using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NAudio.Wave;

namespace VPet_AIGF
{
    /// <summary>
    /// 说话人声纹验证模块。
    /// 
    /// 原理：
    ///   录音 PCM → 预加重 → 分帧加窗 → FFT → Mel 滤波器组 → 对数 → DCT → MFCC 帧序列
    ///   注册时：对多段录音提取 MFCC，合并后取每维均值作为声纹模板
    ///   验证时：提取待验证音频 MFCC 均值向量，与模板做余弦相似度比对
    /// 
    /// 完全本地，无需网络，无需模型文件。
    /// </summary>
    public class SpeakerVerifier
    {
        // ────────── 超参数 ──────────
        private const int SampleRate = 16000;       // 采样率 16kHz
        private const int FrameSize = 400;          // 帧长 25ms @ 16kHz
        private const int FrameStep = 160;          // 帧移 10ms @ 16kHz
        private const int NumMelFilters = 26;       // Mel 滤波器数
        private const int NumMfcc = 13;             // 保留前 13 维 MFCC
        private const double PreEmphasis = 0.97;    // 预加重系数
        private const double VerifyThreshold = 0.82; // 余弦相似度阈值（可调）

        // ────────── 状态 ──────────
        private double[]? _voiceprint;              // 已注册的声纹模板（13 维均值向量）
        private readonly string _savePath;

        public bool IsEnrolled => _voiceprint != null;

        public SpeakerVerifier(string savePath)
        {
            _savePath = savePath;
            TryLoad();
        }

        // ══════════════════════════════════════════════
        //  公开 API
        // ══════════════════════════════════════════════

        /// <summary>
        /// 注册声纹：传入多段 PCM 样本（每段约 2~4 秒，16kHz，16bit，单声道）
        /// </summary>
        public void Enroll(IEnumerable<short[]> pcmSamples)
        {
            var allFrames = new List<double[]>();
            foreach (var pcm in pcmSamples)
            {
                var frames = ExtractMfccFrames(pcm);
                allFrames.AddRange(frames);
            }
            if (allFrames.Count == 0)
                throw new InvalidOperationException("没有有效音频帧，请检查录音质量");

            _voiceprint = MeanVector(allFrames);
            Save();
        }

        /// <summary>
        /// 验证说话人：传入一段 PCM，返回是否匹配
        /// </summary>
        public bool Verify(short[] pcm, out double similarity)
        {
            similarity = 0;
            if (_voiceprint == null) return true; // 未注册则放行

            var frames = ExtractMfccFrames(pcm);
            if (frames.Count == 0) return false;

            var queryVec = MeanVector(frames);
            similarity = CosineSimilarity(queryVec, _voiceprint);
            return similarity >= VerifyThreshold;
        }

        /// <summary>
        /// 清除已注册声纹（重新注册）
        /// </summary>
        public void Clear()
        {
            _voiceprint = null;
            if (File.Exists(_savePath))
                File.Delete(_savePath);
        }

        // ══════════════════════════════════════════════
        //  持久化
        // ══════════════════════════════════════════════

        private void Save()
        {
            if (_voiceprint == null) return;
            File.WriteAllText(_savePath,
                JsonSerializer.Serialize(_voiceprint));
        }

        private void TryLoad()
        {
            try
            {
                if (!File.Exists(_savePath)) return;
                _voiceprint = JsonSerializer.Deserialize<double[]>(
                    File.ReadAllText(_savePath));
            }
            catch { _voiceprint = null; }
        }

        // ══════════════════════════════════════════════
        //  MFCC 特征提取
        // ══════════════════════════════════════════════

        private static List<double[]> ExtractMfccFrames(short[] pcm)
        {
            var result = new List<double[]>();

            // 1. 转 double + 预加重
            double[] signal = new double[pcm.Length];
            signal[0] = pcm[0] / 32768.0;
            for (int i = 1; i < pcm.Length; i++)
                signal[i] = (pcm[i] / 32768.0) - PreEmphasis * (pcm[i - 1] / 32768.0);

            // 2. 分帧 + Hamming 窗
            var melFilters = BuildMelFilterBank(FrameSize, SampleRate, NumMelFilters);

            for (int start = 0; start + FrameSize <= signal.Length; start += FrameStep)
            {
                double[] frame = new double[FrameSize];
                for (int i = 0; i < FrameSize; i++)
                    frame[i] = signal[start + i] * HammingWindow(i, FrameSize);

                // 3. FFT → 功率谱
                double[] powerSpec = PowerSpectrum(frame);

                // 4. Mel 滤波 + 对数
                double[] melEnergy = new double[NumMelFilters];
                int specLen = Math.Min(powerSpec.Length, melFilters[0].Length);
                for (int m = 0; m < NumMelFilters; m++)
                {
                    double sum = 0;
                    for (int k = 0; k < specLen; k++)
                        sum += melFilters[m][k] * powerSpec[k];
                    melEnergy[m] = Math.Log(Math.Max(sum, 1e-10));
                }

                // 5. DCT → MFCC
                double[] mfcc = new double[NumMfcc];
                for (int n = 0; n < NumMfcc; n++)
                {
                    double val = 0;
                    for (int m = 0; m < NumMelFilters; m++)
                        val += melEnergy[m] * Math.Cos(Math.PI * n * (2 * m + 1) / (2 * NumMelFilters));
                    mfcc[n] = val;
                }

                result.Add(mfcc);
            }

            return result;
        }

        // ── Mel 滤波器组 ──

        private static double FreqToMel(double freq) => 2595 * Math.Log10(1 + freq / 700);
        private static double MelToFreq(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);

        private static double[][] BuildMelFilterBank(int frameSize, int sr, int numFilters)
        {
            int nfft = NextPow2(frameSize);          // FFT 补零后实际点数 = 512
            int fftSize = nfft / 2 + 1;              // 功率谱长度 = 257
            double melMin = FreqToMel(0);
            double melMax = FreqToMel(sr / 2.0);
            double[] melPoints = new double[numFilters + 2];
            for (int i = 0; i < melPoints.Length; i++)
                melPoints[i] = melMin + i * (melMax - melMin) / (numFilters + 1);

            double[] freqPoints = melPoints.Select(m => MelToFreq(m)).ToArray();
            // 映射到 FFT bin（用 nfft 而不是 frameSize）
            int[] bins = freqPoints.Select(f =>
                (int)Math.Floor((nfft + 1) * f / sr)).ToArray();

            double[][] filters = new double[numFilters][];
            for (int m = 0; m < numFilters; m++)
            {
                filters[m] = new double[fftSize];
                int lo = Math.Max(0, bins[m]);
                int mid = Math.Min(bins[m + 1], fftSize - 1);
                int hi = Math.Min(bins[m + 2], fftSize - 1);
                // 上升沿
                if (mid > lo)
                    for (int k = lo; k < mid; k++)
                        filters[m][k] = (double)(k - lo) / (mid - lo);
                // 下降沿
                if (hi > mid)
                    for (int k = mid; k <= hi; k++)
                        filters[m][k] = (double)(hi - k) / (hi - mid);
            }
            return filters;
        }

        // ── FFT（Cooley-Tukey 递归，补零到2的幂次） ──

        private static double[] PowerSpectrum(double[] frame)
        {
            int n = NextPow2(frame.Length);
            double[] re = new double[n];
            double[] im = new double[n];
            Array.Copy(frame, re, frame.Length);

            FFT(re, im, n);

            int half = n / 2 + 1;
            double[] power = new double[half];
            for (int i = 0; i < half; i++)
                power[i] = re[i] * re[i] + im[i] * im[i];
            return power;
        }

        private static void FFT(double[] re, double[] im, int n)
        {
            // Bit-reversal
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            // Butterfly
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double curRe = 1, curIm = 0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int u = i + j, v = u + len / 2;
                        double tRe = curRe * re[v] - curIm * im[v];
                        double tIm = curRe * im[v] + curIm * re[v];
                        re[v] = re[u] - tRe; im[v] = im[u] - tIm;
                        re[u] += tRe;        im[u] += tIm;
                        (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                    }
                }
            }
        }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        private static double HammingWindow(int n, int N) =>
            0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (N - 1));

        // ── 向量运算 ──

        private static double[] MeanVector(List<double[]> frames)
        {
            int dim = frames[0].Length;
            double[] mean = new double[dim];
            foreach (var f in frames)
                for (int i = 0; i < dim; i++)
                    mean[i] += f[i];
            for (int i = 0; i < dim; i++)
                mean[i] /= frames.Count;
            return mean;
        }

        private static double CosineSimilarity(double[] a, double[] b)
        {
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            double denom = Math.Sqrt(na) * Math.Sqrt(nb);
            return denom < 1e-10 ? 0 : dot / denom;
        }

        // ══════════════════════════════════════════════
        //  NAudio 录音工具（静态工具方法，供注册窗口调用）
        // ══════════════════════════════════════════════

        /// <summary>
        /// 录制指定毫秒的音频，返回 16kHz 16bit 单声道 PCM 样本数组。
        /// 必须在非 UI 线程调用（会阻塞直到录音结束）。
        /// </summary>
        public static short[] RecordPcm(int durationMs, Action<float>? levelCallback = null)
        {
            var targetFormat = new WaveFormat(SampleRate, 16, 1);
            var buffer = new List<short>();

            using var capture = new WaveInEvent
            {
                WaveFormat = targetFormat,
                BufferMilliseconds = 50
            };

            capture.DataAvailable += (_, e) =>
            {
                int count = e.BytesRecorded / 2;
                float maxLevel = 0;
                for (int i = 0; i < count; i++)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                    buffer.Add(sample);
                    float abs = Math.Abs(sample / 32768f);
                    if (abs > maxLevel) maxLevel = abs;
                }
                levelCallback?.Invoke(maxLevel);
            };

            capture.StartRecording();
            System.Threading.Thread.Sleep(durationMs);
            capture.StopRecording();

            return buffer.ToArray();
        }

        /// <summary>
        /// 检测 PCM 样本的有效音量（RMS），用于判断是否有效发声
        /// </summary>
        public static double ComputeRms(short[] pcm)
        {
            if (pcm.Length == 0) return 0;
            double sum = 0;
            foreach (var s in pcm)
                sum += (s / 32768.0) * (s / 32768.0);
            return Math.Sqrt(sum / pcm.Length);
        }

        /// <summary>
        /// 录制音频并同时从 WaveInEvent 中捕获（供 VoiceWakeUp 在唤醒词触发前后缓冲使用）
        /// </summary>
        public static RollingPcmBuffer CreateRollingBuffer(int bufferSeconds = 3)
            => new RollingPcmBuffer(SampleRate, bufferSeconds);
    }

    /// <summary>
    /// 滚动 PCM 缓冲区：持续接收 NAudio WaveInEvent 数据，
    /// 随时可取出最近 N 秒的 PCM 用于声纹验证。
    /// </summary>
    public class RollingPcmBuffer
    {
        private readonly int _maxSamples;
        private readonly short[] _ring;
        private int _writePos = 0;
        private int _count = 0;
        private readonly object _lock = new();

        public int SampleRate { get; }

        public RollingPcmBuffer(int sampleRate, int bufferSeconds)
        {
            SampleRate = sampleRate;
            _maxSamples = sampleRate * bufferSeconds;
            _ring = new short[_maxSamples];
        }

        /// <summary>
        /// 写入新采样（从 WaveInEvent.DataAvailable 调用）
        /// </summary>
        public void Write(byte[] buffer, int bytesRecorded)
        {
            lock (_lock)
            {
                int count = bytesRecorded / 2;
                for (int i = 0; i < count; i++)
                {
                    _ring[_writePos] = BitConverter.ToInt16(buffer, i * 2);
                    _writePos = (_writePos + 1) % _maxSamples;
                    if (_count < _maxSamples) _count++;
                }
            }
        }

        /// <summary>
        /// 读出最近 seconds 秒的 PCM（不足则取全部已有数据）
        /// </summary>
        public short[] Read(int seconds)
        {
            lock (_lock)
            {
                int want = Math.Min(SampleRate * seconds, _count);
                short[] result = new short[want];
                int start = (_writePos - want + _maxSamples) % _maxSamples;
                for (int i = 0; i < want; i++)
                    result[i] = _ring[(start + i) % _maxSamples];
                return result;
            }
        }
    }
}
