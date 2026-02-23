using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;
using VPet_Simulator.Core;

namespace VPet_AIGF
{
    /// <summary>
    /// 语音唤醒模块：
    ///   - 关键词检测：System.Speech（轻量 hotword）
    ///   - STT 录入：Whisper.net（ggml-large-v3，本地离线）
    ///   - 声纹验证：SpeakerVerifier（MFCC 余弦相似度）
    /// </summary>
    public class VoiceWakeup : IDisposable
    {
        private readonly AIPlugin _plugin;
        private readonly SpeakerVerifier _verifier;

        // ── 关键词引擎 ──
        private SpeechRecognitionEngine? _engine;

        // ── 声纹采样滚动缓冲 ──
        private WaveInEvent? _waveIn;
        private RollingPcmBuffer? _rollingBuffer;

        // ── Whisper 模型（延迟加载，复用） ──
        private WhisperFactory? _whisperFactory;
        private readonly string _whisperModelPath;
        private const string ModelFileName = "ggml-tiny.bin";
        private const GgmlType ModelType = GgmlType.Tiny;

        // ── 模型加载状态 ──
        private Task? _modelLoadTask;
        private readonly SemaphoreSlim _modelLoadLock = new SemaphoreSlim(1, 1);

        // ── STT 取消令牌 ──
        private CancellationTokenSource? _sttCts;

        private bool _disposed = false;
        private DateTime _lastTriggerTime = DateTime.MinValue;
        private const double CooldownSeconds = 5;

        public bool IsRunning { get; private set; }

        public VoiceWakeup(AIPlugin plugin, SpeakerVerifier verifier)
        {
            _plugin = plugin;
            _verifier = verifier;
            var dllDir = Path.GetDirectoryName(typeof(AIPlugin).Assembly.Location) ?? "";
            _whisperModelPath = Path.Combine(dllDir, ModelFileName);

            // 把插件目录加入原生库解析路径，确保 whisper.dll / ggml-*.dll 能被找到
            NativeLibrary.SetDllImportResolver(typeof(WhisperFactory).Assembly, (libName, assembly, searchPath) =>
            {
                // 先尝试从插件目录加载
                var candidate = Path.Combine(dllDir, libName + ".dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
                // 回退到默认搜索
                NativeLibrary.TryLoad(libName, assembly, searchPath, out var defaultHandle);
                return defaultHandle;
            });
        }

        // ══════════════════════════════════════════════
        //  启动 / 停止
        // ══════════════════════════════════════════════

        /// <summary>
        /// 启动语音唤醒监听。重复调用安全。
        /// </summary>
        public void Start()
        {
            if (IsRunning) return;

            try
            {
                // 1. 滚动 PCM 缓冲（声纹）
                _rollingBuffer = SpeakerVerifier.CreateRollingBuffer(bufferSeconds: 4);
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 50
                };
                _waveIn.DataAvailable += (_, e) =>
                    _rollingBuffer.Write(e.Buffer, e.BytesRecorded);
                _waveIn.StartRecording();

                // 2. System.Speech 关键词识别
                _engine = new SpeechRecognitionEngine(new CultureInfo("zh-CN"));

                string petName = _plugin.MW.Core.Save.Name ?? _plugin.ChatName;
                if (string.IsNullOrWhiteSpace(petName)) petName = "宝贝";

                var choices = new Choices();
                choices.Add(petName);
                if (!string.IsNullOrWhiteSpace(_plugin.ChatName)
                    && !string.Equals(petName, _plugin.ChatName, StringComparison.OrdinalIgnoreCase))
                    choices.Add(_plugin.ChatName);

                var gb = new GrammarBuilder { Culture = new CultureInfo("zh-CN") };
                gb.Append(choices);
                _engine.LoadGrammar(new Grammar(gb));
                _engine.SpeechRecognized += OnSpeechRecognized;
                _engine.SetInputToDefaultAudioDevice();
                _engine.RecognizeAsync(RecognizeMode.Multiple);

                // 3. 后台预加载 Whisper 模型（启动时即开始）
                _modelLoadTask = Task.Run(EnsureWhisperFactoryAsync);

                IsRunning = true;
                _plugin.DebugLog($"[VoiceWakeup] Started. Hotword: \"{petName}\" | Model: {ModelFileName}" +
                    (_verifier.IsEnrolled ? " [声纹验证已启用]" : " [声纹未注册，开放唤醒]"));
            }
            catch (Exception ex)
            {
                _plugin.DebugLog($"[VoiceWakeup] Failed to start: {ex.Message}");
                StopInternal();
                IsRunning = false;
            }
        }

        /// <summary>
        /// 停止语音唤醒监听。
        /// </summary>
        public void Stop()
        {
            if (!IsRunning) return;
            StopInternal();
            IsRunning = false;
            _plugin.DebugLog("[VoiceWakeup] Stopped");
        }

        private void StopInternal()
        {
            _sttCts?.Cancel();

            try { _engine?.RecognizeAsyncCancel(); } catch { }
            try { if (_engine != null) _engine.SpeechRecognized -= OnSpeechRecognized; } catch { }
            try { _engine?.Dispose(); } catch { }
            _engine = null;

            try { _waveIn?.StopRecording(); } catch { }
            try { _waveIn?.Dispose(); } catch { }
            _waveIn = null;
            _rollingBuffer = null;
        }

        /// <summary>
        /// 改名后重新加载关键词。
        /// </summary>
        public void Reload()
        {
            Stop();
            Start();
        }

        // ══════════════════════════════════════════════
        //  Whisper 工厂加载（线程安全延迟初始化）
        // ══════════════════════════════════════════════

        private async Task<WhisperFactory> EnsureWhisperFactoryAsync()
        {
            await _modelLoadLock.WaitAsync();
            try
            {
                if (_whisperFactory != null) return _whisperFactory;

                // 确保目录存在
                var dir = Path.GetDirectoryName(_whisperModelPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 若模型文件不存在则自动下载
                if (!File.Exists(_whisperModelPath))
                {
                    _plugin.DebugLog($"[Whisper] 模型文件未找到，开始下载: {ModelFileName}");
                    try
                    {
                        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ModelType);
                        using var fileStream = File.OpenWrite(_whisperModelPath);
                        await modelStream.CopyToAsync(fileStream);
                        _plugin.DebugLog($"[Whisper] 模型下载完成: {_whisperModelPath}");
                    }
                    catch (Exception ex)
                    {
                        _plugin.DebugLog($"[Whisper] 自动下载失败: {ex.Message}，请手动下载到: {_whisperModelPath}");
                        throw new InvalidOperationException(
                            $"模型文件下载失败。请手动下载到:\n{_whisperModelPath}\n\n" +
                            $"下载链接（任选其一）:\n" +
                            $"1. https://huggingface.co/ggerganov/whisper.cpp/blob/main/models/ggml-large-v3.bin\n" +
                            $"2. https://github.com/ggerganov/whisper.cpp/releases\n" +
                            $"3. 使用国内镜像加速下载", ex);
                    }
                }

                _plugin.DebugLog($"[Whisper] 加载模型: {_whisperModelPath}");
                _whisperFactory = WhisperFactory.FromPath(_whisperModelPath);
                _plugin.DebugLog($"[Whisper] 模型加载完毕: {ModelFileName}");
                return _whisperFactory;
            }
            finally
            {
                _modelLoadLock.Release();
            }
        }

        // ══════════════════════════════════════════════
        //  关键词识别回调
        // ══════════════════════════════════════════════

        private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            if (e.Result.Confidence < 0.5f) return;
            if ((DateTime.Now - _lastTriggerTime).TotalSeconds < CooldownSeconds) return;

            _plugin.DebugLog($"[VoiceWakeup] Recognized: \"{e.Result.Text}\" conf={e.Result.Confidence:F2}");

            // ── 只有聊天窗口最小化或关闭（不可见）时才触发唤醒 ──
            if (_plugin.IsChatWindowOpen)
            {
                _plugin.DebugLog("[VoiceWakeup] 聊天窗口已打开，忽略唤醒");
                return;
            }

            // ── 声纹验证 ──
            if (_verifier.IsEnrolled && _rollingBuffer != null)
            {
                var pcm = _rollingBuffer.Read(seconds: 3);
                bool pass = _verifier.Verify(pcm, out double similarity);
                _plugin.DebugLog($"[VoiceWakeup] Speaker verify: similarity={similarity:F3}, pass={pass}");

                if (!pass)
                {
                    _plugin.DebugLog("[VoiceWakeup] 声纹不匹配，忽略本次唤醒");
                    return;
                }
            }

            _lastTriggerTime = DateTime.Now;

            // ── 切到 UI 线程执行窗口操作 ──
            _plugin.MW.Dispatcher.BeginInvoke(new Action(() =>
            {
                _plugin.DebugLog("[VoiceWakeup] UI thread: begin trigger actions");

                // 1. 弹出聊天窗口 + 抖动 + 聚焦
                try
                {
                    _plugin.ShowChatWindow();
                    var cw = _plugin.GetOrCreateChatWindow();
                    ShakeWindow(cw);
                    cw.FocusInput();
                }
                catch (Exception ex) { _plugin.DebugLog($"[VoiceWakeup] Window error: {ex.Message}"); }

                // 2. 播放摸头动画
                try
                {
                    _plugin.MW.Main.Display(GraphInfo.GraphType.Touch_Head,
                        GraphInfo.AnimatType.A_Start,
                        () => _plugin.MW.Main.DisplayToNomal());
                }
                catch
                {
                    try { _plugin.MW.Main.DisplayToNomal(); } catch { }
                }

                // 3. 系统提示（不调用 AI）
                try
                {
                    var cw = _plugin.GetOrCreateChatWindow();
                    cw.AddSystemMessage("🎤 语音唤醒触发", false);
                }
                catch (Exception ex) { _plugin.DebugLog($"[VoiceWakeup] System msg error: {ex.Message}"); }

                // 4. 用 Task.Run 在线程池上启动 STT（绝不阻塞 UI 线程）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StartVoiceListeningAsync();
                    }
                    catch (Exception ex)
                    {
                        _plugin.DebugLog($"[VoiceWakeup] Voice listening error: {ex.Message}");
                    }
                });
            }));
        }

        // ══════════════════════════════════════════════
        //  Whisper 伪流式 STT（滚动累积识别）
        // ══════════════════════════════════════════════

        /// <summary>点击录音指示器时调用，立即停止 STT 录入</summary>
        public void StopVoiceListening() => _sttCts?.Cancel();

        private async Task StartVoiceListeningAsync()
        {
            var cw = _plugin.GetOrCreateChatWindow();

            // ── 先显示加载提示 ──
            cw.ShowRecordingIndicator();
            cw.UpdateRecordingHint($"模型加载中（{ModelFileName}）");

            // ── 等待模型就绪 ──
            WhisperFactory factory;
            try
            {
                factory = await EnsureWhisperFactoryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _plugin.DebugLog($"[Whisper] model error: {ex.Message}");
                _plugin.MW.Dispatcher.BeginInvoke(new Action(() =>
                {
                    cw.HideRecordingIndicator();
                    cw.AddSystemMessage($"⚠️ Whisper 模型加载失败: {ex.Message}", false);
                }));
                return;
            }

            // ── 从这里开始已脱离 UI 线程（ConfigureAwait(false)），所有 UI 操作必须用 Dispatcher ──
            _plugin.MW.Dispatcher.BeginInvoke(new Action(() =>
                cw.UpdateRecordingHint("🎙 录音中，请说话…")));

            _sttCts?.Cancel();
            _sttCts = new CancellationTokenSource();
            var ct = _sttCts.Token;

            _plugin.DebugLog("[Whisper] Streaming STT started");

            // ── 录音缓冲（线程安全） ──
            var pcmLock = new object();
            var pcmBuffer = new System.Collections.Generic.List<float>();

            bool stopped = false;
            var stopLock = new object();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            DateTime lastSoundTime = DateTime.Now;
            string recognizedText = "";

            WaveInEvent? mic = null;
            System.Timers.Timer? silenceTimer = null;

            void StopRecording(string reason)
            {
                lock (stopLock)
                {
                    if (stopped) return;
                    stopped = true;
                }
                _plugin.DebugLog($"[Whisper] stop recording: {reason}");
                try { silenceTimer?.Stop(); silenceTimer?.Dispose(); silenceTimer = null; } catch { }
                try
                {
                    if (mic != null)
                    {
                        mic.StopRecording();
                        mic.Dispose();
                        mic = null;
                    }
                }
                catch { }
                tcs.TrySetResult(true);
            }

            ct.Register(() => StopRecording("user cancelled"));

            // ── 后台增量识别线程：每2秒对累积音频做一次完整识别 ──
            var recognizeTask = Task.Run(async () =>
            {
                try
                {
                    int lastSampleCount = 0;
                    while (!stopped && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        if (stopped || ct.IsCancellationRequested) break;

                        // 取当前全部累积音频的快照
                        float[] snapshot;
                        lock (pcmLock)
                        {
                            if (pcmBuffer.Count == lastSampleCount || pcmBuffer.Count < 8000) // 至少 0.5s
                                continue;
                            snapshot = pcmBuffer.ToArray();
                            lastSampleCount = pcmBuffer.Count;
                        }

                        _plugin.DebugLog($"[Whisper] partial recognize: {snapshot.Length / 16000.0:F1}s audio");

                        // 对全部累积音频做识别
                        var proc = factory.CreateBuilder()
                            .WithLanguage("zh")
                            .Build();
                        try
                        {
                            using var wavStream = CreateWavStream(snapshot, 16000);
                            string partial = "";
                            await foreach (var seg in proc.ProcessAsync(wavStream, ct).ConfigureAwait(false))
                            {
                                if (!string.IsNullOrWhiteSpace(seg.Text))
                                    partial += seg.Text.Trim();
                            }

                            if (!string.IsNullOrWhiteSpace(partial))
                            {
                                recognizedText = partial;  // 替换（不是追加，因为每次是全量识别）
                                var textCopy = partial;
                                _plugin.MW.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    cw.SetInputText(textCopy);
                                    cw.UpdateRecordingHint($"🎙 {textCopy}");
                                }));
                                _plugin.DebugLog($"[Whisper] partial: \"{partial}\"");
                            }
                        }
                        finally
                        {
                            try
                            {
                                if (proc is IAsyncDisposable ad)
                                    await ad.DisposeAsync().ConfigureAwait(false);
                                else
                                    proc?.Dispose();
                            }
                            catch { }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _plugin.DebugLog($"[Whisper] recognize error: {ex.Message}"); }
            }, ct);

            // ── 麦克风录音 ──
            try
            {
                mic = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100
                };

                mic.DataAvailable += (_, e) =>
                {
                    if (stopped) return;

                    // PCM Int16 → Float32，追加到累积缓冲
                    int sampleCount = e.BytesRecorded / 2;
                    var samples = new float[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                        samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;

                    lock (pcmLock)
                        pcmBuffer.AddRange(samples);

                    // 音量检测（静音判断）
                    float maxLevel = 0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        float abs = Math.Abs(samples[i]);
                        if (abs > maxLevel) maxLevel = abs;
                    }
                    if (maxLevel > 0.015f) lastSoundTime = DateTime.Now;
                };

                mic.RecordingStopped += (_, _) => StopRecording("mic stopped");
                mic.StartRecording();

                _plugin.DebugLog("[Whisper] mic started, waiting for audio...");

                // 静音 3s 自动停止
                silenceTimer = new System.Timers.Timer(300);
                silenceTimer.Elapsed += (_, _) =>
                {
                    if ((DateTime.Now - lastSoundTime).TotalSeconds > 3.0)
                        StopRecording("silence");
                };
                silenceTimer.Start();

                // 最长 30 秒
                _ = Task.Delay(30000, ct).ContinueWith(_ => StopRecording("max duration"),
                    TaskContinuationOptions.None);

                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _plugin.DebugLog($"[Whisper] record error: {ex.Message}"); }
            finally
            {
                try { mic?.Dispose(); mic = null; } catch { }
                try { silenceTimer?.Stop(); silenceTimer?.Dispose(); } catch { }
            }

            // ── 录音结束，等待后台识别线程退出 ──
            _plugin.DebugLog("[Whisper] recording stopped, waiting for recognizer...");
            try { await recognizeTask.ConfigureAwait(false); } catch { }

            if (ct.IsCancellationRequested)
            {
                _plugin.MW.Dispatcher.BeginInvoke(new Action(() => cw.HideRecordingIndicator()));
                return;
            }

            // ── 最终识别：对全部录音做一次最终识别 ──
            float[] finalAudio;
            lock (pcmLock) { finalAudio = pcmBuffer.ToArray(); }
            _plugin.DebugLog($"[Whisper] Final transcribe: {finalAudio.Length / 16000.0:F1}s");

            _plugin.MW.Dispatcher.BeginInvoke(new Action(() =>
                cw.UpdateRecordingHint("最终识别中…")));

            if (finalAudio.Length > 4800) // 至少 0.3s
            {
                var proc = factory.CreateBuilder()
                    .WithLanguage("zh")
                    .Build();
                try
                {
                    using var wavStream = CreateWavStream(finalAudio, 16000);
                    string finalText = "";
                    await foreach (var seg in proc.ProcessAsync(wavStream, ct).ConfigureAwait(false))
                    {
                        if (!string.IsNullOrWhiteSpace(seg.Text))
                            finalText += seg.Text.Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(finalText))
                        recognizedText = finalText;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _plugin.DebugLog($"[Whisper] final error: {ex.Message}"); }
                finally
                {
                    try
                    {
                        if (proc is IAsyncDisposable ad)
                            await ad.DisposeAsync().ConfigureAwait(false);
                        else
                            proc?.Dispose();
                    }
                    catch { }
                }
            }

            // ── 结果填入输入框（切回 UI 线程） ──
            var finalResult = recognizedText;
            _plugin.MW.Dispatcher.BeginInvoke(new Action(() =>
            {
                cw.HideRecordingIndicator();
                if (!string.IsNullOrWhiteSpace(finalResult))
                {
                    cw.SetInputText(finalResult);
                    cw.FocusInput();
                }
                else
                {
                    cw.AddSystemMessage("🎤 未识别到语音内容", false);
                }
            }));

            if (!string.IsNullOrWhiteSpace(recognizedText))
                _plugin.DebugLog($"[Whisper] final input: \"{recognizedText}\"");
            else
                _plugin.DebugLog("[Whisper] no speech detected");
        }

        // ══════════════════════════════════════════════
        //  工具：将 float[] PCM 打包为 WAV MemoryStream
        // ══════════════════════════════════════════════

        private static MemoryStream CreateWavStream(float[] samples, int sampleRate)
        {
            var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            int byteCount = samples.Length * 2; // 16-bit
            // RIFF header
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + byteCount);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            // fmt chunk
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);          // chunk size
            writer.Write((short)1);    // PCM
            writer.Write((short)1);    // mono
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);    // block align
            writer.Write((short)16);   // bits per sample
            // data chunk
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(byteCount);
            foreach (var s in samples)
            {
                short val = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)(s * 32768f)));
                writer.Write(val);
            }

            ms.Position = 0;
            return ms;
        }

        // ══════════════════════════════════════════════
        //  窗口抖动动画
        // ══════════════════════════════════════════════

        private static void ShakeWindow(Window window)
        {
            if (window == null) return;

            double originalLeft = window.Left;
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                FillBehavior = FillBehavior.Stop
            };

            double offset = 8;
            double[] offsets = { offset, -offset, offset, -offset, offset / 2, -offset / 2, 0 };
            double step = 400.0 / offsets.Length;

            for (int i = 0; i < offsets.Length; i++)
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(
                    originalLeft + offsets[i],
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(step * (i + 1)))));

            anim.Completed += (_, _) => window.Left = originalLeft;
            window.BeginAnimation(Window.LeftProperty, anim);
        }

        // ══════════════════════════════════════════════
        //  IDisposable
        // ══════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _whisperFactory?.Dispose();
            _whisperFactory = null;
        }
    }
}

