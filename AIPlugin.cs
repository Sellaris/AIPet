using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphHelper;

namespace VPet_AIGF
{
    /// <summary>
    /// 持久化聊天记录的单条数据
    /// </summary>
    public class ChatRecord
    {
        public string Time { get; set; } = "";
        public string Role { get; set; } = "";   // "user" | "assistant" | "system"
        public string Content { get; set; } = "";
        /// <summary>
        /// 消息类型（可选）："redpacket" 表示红包消息，用于 UI 恢复时使用红色气泡渲染
        /// </summary>
        public string? Type { get; set; } = null;
        /// <summary>
        /// 图片数据（data url 或 base64），仅当 Type = "image" 时使用
        /// </summary>
        public string? ImageData { get; set; } = null;
        /// <summary>
        /// 图片文件名（用于展示）
        /// </summary>
        public string? ImageName { get; set; } = null;
    }

    /// <summary>
    /// CallGLM 返回的结构化结果
    /// </summary>
    public class GLMResult
    {
        public string Reply { get; set; } = "";
        public int LikabilityChange { get; set; } = 0;
        public string Reason { get; set; } = "";
        public List<string> ActionLogs { get; set; } = new List<string>();
        /// <summary>
        /// AI 选择的情绪表情动画名（shy/serious/shining/self），为空则使用默认
        /// </summary>
        public string? EmotionGraph { get; set; } = null;
        /// <summary>
        /// AI 通过 play_animation 请求的特殊动画名（延迟执行，在所有工具完成后最后播放，防止被 start_play 等覆盖）
        /// </summary>
        public string? PendingAnimation { get; set; } = null;
        /// <summary>
        /// 进食/喝水等物品动画（延迟到 Say 气泡消失后播放，防止被情绪动画覆盖）
        /// </summary>
        public (string GraphName, ImageSource? Image)? PendingFoodAnimation { get; set; } = null;
        /// <summary>
        /// 本次对话带来的心情变化（绝对值，-20到+20，由 report_likability 联动计算）
        /// 正数=心情提升，负数=心情下降
        /// </summary>
        public int FeelingChange { get; set; } = 0;
    }

    /// <summary>
    /// Embedding 向量缓存条目（用于 JSON 序列化）
    /// </summary>
    public class EmbeddingCacheEntry
    {
        public int Index { get; set; }
        public float[] Vector { get; set; } = Array.Empty<float>();
    }

    /// <summary>
    /// 一轮对话前的快照（用于回滚）
    /// </summary>
    public class ChatSnapshot
    {
        public List<ChatRecord> History { get; set; } = new List<ChatRecord>();
        public double Money { get; set; }
        public double Strength { get; set; }
        public double StrengthFood { get; set; }
        public double StrengthDrink { get; set; }
        public double Feeling { get; set; }
        public double Health { get; set; }
        public double Likability { get; set; }
        public int EmbeddedCount { get; set; }
        public List<EmbeddingCacheEntry> Embeddings { get; set; } = new List<EmbeddingCacheEntry>();
    }

    public class AIPlugin : MainPlugin
    {
        public override string PluginName => "VPet_AIGF";
        private System.Timers.Timer? _harassTimer;
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly string _apiKey = Environment.GetEnvironmentVariable("GLM_API_KEY") ?? "YOUR_GLM_API_KEY";

        // API 历史记录（带时间戳的 content）
        public List<ChatRecord> AllChatHistory = new List<ChatRecord>();
        private const int MaxContextHistory = 10; // API 上下文保留最近40条
        private int _ignoreCount = 0;
        private DateTime _lastUserReplyTime = DateTime.Now;

        // ===== 聊天窗口严格单例 =====
        private GLMChatWindow? _chatWindow;
        private bool _chatWindowCreated = false;

        // ===== 持久化路径 =====
        private string _chatLogPath = "";
        private string _debugLogPath = "";
        private string _apiCallLogPath = "";
        private string _configPath = "";
        public string ChatName { get; set; } = "宝贝";

        // ===== 防止骚扰和用户操作并发冲突的锁 =====
        private readonly object _apiLock = new object();
        private volatile bool _isApiCalling = false;

        // ===== 随机数生成器 =====
        private readonly Random _rnd = new Random();

        // ===== 情绪表情（由 show_emotion 工具设置，CallGLM 结束时写入 GLMResult）=====
        private string? _pendingEmotion = null;
        // ===== 特殊动画（由 play_animation 工具设置，延迟到所有工具执行完后统一播放）=====
        private string? _pendingAnimation = null;
        // ===== 进食动画（由 DoFeedPet 设置，延迟到 Say 气泡消失后播放，防止被情绪动画覆盖）=====
        private (string GraphName, ImageSource? Image)? _pendingFoodAnimation = null;

        // ===== Embedding RAG =====
        private const int EmbeddingDimensions = 256;
        private const string EmbeddingModel = "embedding-3";
        private string _embeddingCachePath = "";
        /// <summary>
        /// 内存中的向量索引：每条记录对应一个 float[] 向量。
        /// key = AllChatHistory 的索引位置, value = embedding 向量
        /// </summary>
        private readonly Dictionary<int, float[]> _embeddingIndex = new Dictionary<int, float[]>();
        /// <summary>
        /// 已完成 embedding 的记录数量（用于增量构建）
        /// </summary>
        private int _embeddedCount = 0;
        private readonly SemaphoreSlim _embeddingSemaphore = new SemaphoreSlim(1, 1);

        // ===== 回滚快照（最多保留 5 轮） =====
        private readonly Stack<ChatSnapshot> _snapshots = new Stack<ChatSnapshot>();
        private const int MaxSnapshots = 5;

    // ===== 窗口扰动节流 =====
    private DateTime _lastWindowEffectTime = DateTime.MinValue;

        // ===== 语音唤醒 =====
        private VoiceWakeup? _voiceWakeup;
        private SpeakerVerifier? _speakerVerifier;

        public AIPlugin(IMainWindow mainwin) : base(mainwin) { }

        /// <summary>
        /// 调试日志（追加写入文件）
        /// </summary>
        public void DebugLog(string msg)
        {
            try
            {
                File.AppendAllText(_debugLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n", Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// 写 API 调用专项日志（RAG内容 / API输入 / API输出），每次调用追加一段
        /// </summary>
        private void ApiCallLog(string section, string content)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===== {section} =====\n{content}\n";
                File.AppendAllText(_apiCallLogPath, line, Encoding.UTF8);
            }
            catch { }
        }

        public override void LoadPlugin()
        {
            // 确定聊天记录文件路径（和 DLL 同目录）
            var dllDir = Path.GetDirectoryName(typeof(AIPlugin).Assembly.Location) ?? "";
            _chatLogPath = Path.Combine(dllDir, "chat_history.json");
            _debugLogPath = Path.Combine(dllDir, "debug_log.txt");
            _apiCallLogPath = Path.Combine(dllDir, "api_call_log.txt");
            _configPath = Path.Combine(dllDir, "config.json");
            _embeddingCachePath = Path.Combine(dllDir, "embeddings.json");

            // 声纹文件
            var voiceprintPath = Path.Combine(dllDir, "voiceprint.json");
            _speakerVerifier = new SpeakerVerifier(voiceprintPath);

            LoadConfig();
            // 从 JSON 加载历史聊天记录
            LoadChatHistory();

            // 加载 embedding 缓存并异步构建增量索引
            LoadEmbeddingCache();
            Task.Run(() => BuildEmbeddingIndexAsync());

            // 骚扰定时器（每30秒检查一次是否到达骚扰时间，实际间隔10~15分钟随机）
            ScheduleNextHarass();
            _harassTimer = new System.Timers.Timer(30 * 1000); // 30秒检查一次
            _harassTimer.Elapsed += HarassTimer_Elapsed;
            _harassTimer.Start();

            // 注册 TalkAPI
            var adapter = new GLMTalkAPIAdapter(this);
            MW.TalkAPI.Add(adapter);
            MW.TalkAPIIndex = MW.TalkAPI.IndexOf(adapter);

            // 启动时自动弹出聊天窗口并发起问好（延迟5秒等待主窗口加载完毕）
            var startupTimer = new System.Timers.Timer(5000);
            startupTimer.AutoReset = false;
            startupTimer.Elapsed += async (s, e) =>
            {
                MW.Dispatcher.Invoke(() => ShowChatWindow());
                await Task.Delay(500); // 等窗口渲染完
                await SendStartupGreeting();

                // 语音唤醒（延迟到窗口就绪后启动）
                try
                {
                    _voiceWakeup = new VoiceWakeup(this, _speakerVerifier);
                    _voiceWakeup.Start();
                }
                catch (Exception ex)
                {
                    DebugLog($"[VoiceWakeup] Init error: {ex.Message}");
                }
            };
            startupTimer.Start();
        }

        public override void EndGame()
        {
            _voiceWakeup?.Dispose();
            _harassTimer?.Stop();
            _harassTimer?.Dispose();
        }

        /// <summary>
        /// 声纹验证器（供注册窗口访问）
        /// </summary>
        public SpeakerVerifier SpeakerVerifier => _speakerVerifier!;

        /// <summary>
        /// 声纹注册完成后重载语音唤醒，使新声纹立即生效
        /// </summary>
        public void ReloadVoiceWakeup()
        {
            try { _voiceWakeup?.Reload(); }
            catch (Exception ex) { DebugLog($"[VoiceWakeup] Reload error: {ex.Message}"); }
        }

        /// <summary>
        /// 立即停止当前 STT 录音（供点击录音指示器时调用）
        /// </summary>
        public void StopVoiceListening()
        {
            try { _voiceWakeup?.StopVoiceListening(); }
            catch { }
        }

        #region ===== 聊天窗口严格单例 =====

        /// <summary>
        /// 获取唯一的聊天窗口（只在UI线程调用，只创建一次）
        /// </summary>
        public GLMChatWindow GetOrCreateChatWindow()
        {
            // 必须在 UI 线程
            if (!MW.Dispatcher.CheckAccess())
                return MW.Dispatcher.Invoke(() => GetOrCreateChatWindow());

            if (!_chatWindowCreated || _chatWindow == null)
            {
                _chatWindow = new GLMChatWindow(this);
                _chatWindowCreated = true;

                // 恢复历史消息到聊天界面
                RestoreMessagesToUI();
            }
            return _chatWindow;
        }

        /// <summary>
        /// 聊天窗口是否处于可见且非最小化状态
        /// </summary>
        public bool IsChatWindowOpen
        {
            get
            {
                if (!_chatWindowCreated || _chatWindow == null) return false;
                return MW.Dispatcher.Invoke(() =>
                    _chatWindow.IsVisible && _chatWindow.WindowState != System.Windows.WindowState.Minimized);
            }
        }

        /// <summary>
        /// 打开聊天窗口（安全方法，所有入口统一用这个）
        /// </summary>
        public void ShowChatWindow()
        {
            if (!MW.Dispatcher.CheckAccess())
            {
                MW.Dispatcher.Invoke(ShowChatWindow);
                return;
            }
            GetOrCreateChatWindow().ShowAndActivate();
        }

        /// <summary>
        /// 在聊天窗口添加 AI 消息（安全方法）
        /// </summary>
        public void ShowAIMessageInChat(string text)
        {
            if (!MW.Dispatcher.CheckAccess())
            {
                MW.Dispatcher.Invoke(() => ShowAIMessageInChat(text));
                return;
            }
            GetOrCreateChatWindow().AddAIMessage(text);
        }

        /// <summary>
        /// 在聊天窗口添加用户消息（安全方法）
        /// </summary>
        public void ShowUserMessageInChat(string text)
        {
            if (!MW.Dispatcher.CheckAccess())
            {
                MW.Dispatcher.Invoke(() => ShowUserMessageInChat(text));
                return;
            }
            GetOrCreateChatWindow().AddUserMessage(text);
        }

        /// <summary>
        /// 在聊天窗口添加系统提示消息（安全方法）
        /// </summary>
        public void ShowSystemMessageInChat(string text)
        {
            if (!MW.Dispatcher.CheckAccess())
            {
                MW.Dispatcher.Invoke(() => ShowSystemMessageInChat(text));
                return;
            }
            GetOrCreateChatWindow().AddSystemMessage(text, true);
        }

        #endregion

        #region ===== 持久化聊天记录 =====

        /// <summary>
        /// 启动时从 JSON 文件加载所有历史聊天记录
        /// </summary>
        private void LoadChatHistory()
        {
            try
            {
                if (!File.Exists(_chatLogPath)) return;
                var json = File.ReadAllText(_chatLogPath, Encoding.UTF8);
                var records = JsonSerializer.Deserialize<List<ChatRecord>>(json);
                if (records == null) return;

                lock (AllChatHistory)
                {
                    AllChatHistory.Clear();
                    AllChatHistory.AddRange(records);
                }
            }
            catch { }
        }

        /// <summary>
        /// 恢复历史消息到聊天窗口 UI（在 UI 线程调用）
        /// </summary>
        private void RestoreMessagesToUI()
        {
            try
            {
                if (!File.Exists(_chatLogPath)) return;
                var json = File.ReadAllText(_chatLogPath, Encoding.UTF8);
                var records = JsonSerializer.Deserialize<List<ChatRecord>>(json);
                if (records == null || _chatWindow == null) return;

                foreach (var r in records)
                {
                    if (r.Role == "user")
                    {
                        if (r.Type == "redpacket")
                            _chatWindow.AddRedPacketRaw(r.Content, r.Time);
                        else if (r.Type == "image" && !string.IsNullOrEmpty(r.ImageData))
                            _chatWindow.AddUserImageFromHistory(r.Content, r.ImageData!, r.Time, r.ImageName);
                        else
                            _chatWindow.AddUserMessage(r.Content, r.Time);
                    }
                    else if (r.Role == "assistant" && r.Type == "redpacket_from_pet")
                        _chatWindow.AddPetRedPacketRaw(r.Content, r.Time);
                    else if (r.Role == "assistant" && r.Type == "ai_image" && !string.IsNullOrEmpty(r.ImageData))
                        _chatWindow.AddAIImageFromHistory(r.Content, r.ImageData!, r.Time, r.ImageName);
                    else if (r.Role == "assistant")
                        _chatWindow.AddAIMessage(r.Content, r.Time);
                    else if (string.Equals(r.Type, "image_description", StringComparison.OrdinalIgnoreCase))
                        continue; // 图片描述仅供 AI 上下文使用，不渲染到聊天界面
                    else
                        _chatWindow.AddSystemMessage(r.Content, false);
                }
            }
            catch { }
        }

        /// <summary>
        /// 追加一条聊天记录到 JSON 文件
        /// </summary>
        public void AppendChatRecord(string role, string content, string? type = null, string? imageData = null, string? imageName = null)
        {
            try
            {
                var record = new ChatRecord
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Role = role,
                    Content = content,
                    Type = type,
                    ImageData = imageData,
                    ImageName = imageName
                };

                int newIndex;
                List<ChatRecord> snapshot;
                lock (AllChatHistory)
                {
                    AllChatHistory.Add(record);
                    newIndex = AllChatHistory.Count - 1;
                    snapshot = new List<ChatRecord>(AllChatHistory); // 浅拷贝，供异步持久化使用
                }

                // 异步增量 embedding（不阻塞当前操作）
                bool shouldEmbed = !string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "image_description", StringComparison.OrdinalIgnoreCase);
                if (shouldEmbed)
                    _ = Task.Run(() => EmbedNewRecordAsync(newIndex, role, content));

                // 异步持久化到磁盘（直接用内存快照，不再重复读文件）
                _ = Task.Run(() => PersistChatHistory(snapshot));
            }
            catch { }
        }

        /// <summary>
        /// 覆盖写入聊天记录文件
        /// </summary>
        private void PersistChatHistory(List<ChatRecord> records)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_chatLogPath, JsonSerializer.Serialize(records, options), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                DebugLog($"[History] Persist error: {ex.Message}");
            }
        }

        /// <summary>
        /// 追加系统提示/操作日志到历史（role=system）
        /// </summary>
        public void AppendSystemRecord(string content)
        {
            AppendChatRecord("system", content);
        }

        /// <summary>
        /// 清空持久化聊天记录
        /// </summary>
        public void ClearChatRecords()
        {
            try
            {
                if (File.Exists(_chatLogPath))
                    File.Delete(_chatLogPath);
                if (File.Exists(_embeddingCachePath))
                    File.Delete(_embeddingCachePath);
            }
            catch { }

            lock (AllChatHistory)
            {
                AllChatHistory.Clear();
            }
            lock (_embeddingIndex)
            {
                _embeddingIndex.Clear();
                _embeddedCount = 0;
            }

            lock (_snapshots)
            {
                _snapshots.Clear();
            }
        }

        /// <summary>
        /// 在开始新一轮对话前保存快照（最多保留 MaxSnapshots）
        /// </summary>
        public void SaveSnapshotIfNeeded()
        {
            try
            {
                // 避免重复保存同一位置的快照
                bool duplicate;
                lock (_snapshots)
                {
                    duplicate = _snapshots.Count > 0 && _snapshots.Peek().History.Count == AllChatHistory.Count;
                }
                if (duplicate) return;

                List<ChatRecord> historyCopy;
                lock (AllChatHistory)
                {
                    historyCopy = AllChatHistory.Select(r => new ChatRecord
                    {
                        Time = r.Time,
                        Role = r.Role,
                        Content = r.Content,
                        Type = r.Type,
                        ImageData = r.ImageData,
                        ImageName = r.ImageName
                    }).ToList();
                }

                List<EmbeddingCacheEntry> embCopy;
                int embeddedCountSnapshot;
                lock (_embeddingIndex)
                {
                    embCopy = _embeddingIndex.Select(kv => new EmbeddingCacheEntry
                    {
                        Index = kv.Key,
                        Vector = kv.Value.ToArray()
                    }).ToList();
                    embeddedCountSnapshot = _embeddedCount;
                }

                var save = MW.Core.Save;
                var snapshot = new ChatSnapshot
                {
                    History = historyCopy,
                    Money = save.Money,
                    Strength = save.Strength,
                    StrengthFood = save.StrengthFood,
                    StrengthDrink = save.StrengthDrink,
                    Feeling = save.Feeling,
                    Health = save.Health,
                    Likability = save.Likability,
                    Embeddings = embCopy,
                    EmbeddedCount = embeddedCountSnapshot
                };

                lock (_snapshots)
                {
                    // 丢弃最旧的，最多保留 MaxSnapshots
                    var list = _snapshots.ToList();
                    list.Insert(0, snapshot); // Stack 没有直接从底部移除，先转 List
                    while (list.Count > MaxSnapshots)
                        list.RemoveAt(list.Count - 1);
                    _snapshots.Clear();
                    for (int i = list.Count - 1; i >= 0; i--)
                        _snapshots.Push(list[i]);
                }

                DebugLog($"[Snapshot] Saved snapshot with {historyCopy.Count} messages, embeddings={embCopy.Count}, money={snapshot.Money:F0}");
            }
            catch (Exception ex)
            {
                DebugLog($"[Snapshot] Save error: {ex.Message}");
            }
        }

        /// <summary>
        /// 回滚到上一轮快照
        /// </summary>
        public bool RollbackLastSnapshot()
        {
            ChatSnapshot? snap = null;
            lock (_snapshots)
            {
                if (_snapshots.Count > 0)
                    snap = _snapshots.Pop();
            }

            if (snap == null) return false;

            try
            {
                lock (AllChatHistory)
                {
                    AllChatHistory.Clear();
                    AllChatHistory.AddRange(snap.History.Select(r => new ChatRecord
                    {
                        Time = r.Time,
                        Role = r.Role,
                        Content = r.Content,
                        Type = r.Type,
                        ImageData = r.ImageData,
                        ImageName = r.ImageName
                    }));
                }

                PersistChatHistory(snap.History);

                lock (_embeddingIndex)
                {
                    _embeddingIndex.Clear();
                    foreach (var e in snap.Embeddings)
                    {
                        if (e.Vector != null && e.Vector.Length == EmbeddingDimensions)
                            _embeddingIndex[e.Index] = e.Vector.ToArray();
                    }
                    _embeddedCount = snap.EmbeddedCount;
                }
                SaveEmbeddingCache();

                MW.Dispatcher.Invoke(() =>
                {
                    var save = MW.Core.Save;
                    save.Money = snap.Money;
                    save.Strength = snap.Strength;
                    save.StrengthFood = snap.StrengthFood;
                    save.StrengthDrink = snap.StrengthDrink;
                    save.Feeling = snap.Feeling;
                    save.Health = snap.Health;
                    save.Likability = snap.Likability;
                });

                // 刷新聊天窗口
                MW.Dispatcher.Invoke(() =>
                {
                    if (_chatWindow != null)
                    {
                        _chatWindow.Messages.Clear();
                        foreach (var r in AllChatHistory)
                        {
                            if (r.Role == "user")
                            {
                                if (r.Type == "redpacket")
                                    _chatWindow.AddRedPacketRaw(r.Content, r.Time);
                                else if (r.Type == "image" && !string.IsNullOrEmpty(r.ImageData))
                                    _chatWindow.AddUserImageFromHistory(r.Content, r.ImageData!, r.Time, r.ImageName);
                                else
                                    _chatWindow.AddUserMessage(r.Content, r.Time);
                            }
                            else if (r.Role == "assistant" && r.Type == "redpacket_from_pet")
                                _chatWindow.AddPetRedPacketRaw(r.Content, r.Time);
                            else if (r.Role == "assistant" && r.Type == "ai_image" && !string.IsNullOrEmpty(r.ImageData))
                                _chatWindow.AddAIImageFromHistory(r.Content, r.ImageData!, r.Time, r.ImageName);
                            else if (r.Role == "assistant")
                                _chatWindow.AddAIMessage(r.Content, r.Time);
                            else if (string.Equals(r.Type, "image_description", StringComparison.OrdinalIgnoreCase))
                                continue; // 图片描述仅供 AI 上下文使用，不渲染到聊天界面
                            else
                                _chatWindow.AddSystemMessage(r.Content, false);
                        }
                        _chatWindow.RefreshStatusBar();
                    }
                });

                AppendSystemRecord("已回滚到上一轮对话并恢复存档状态");
                DebugLog("[Snapshot] Rollback success");
                return true;
            }
            catch (Exception ex)
            {
                DebugLog($"[Snapshot] Rollback error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region ===== 状态查询（供 UI 使用） =====

        /// <summary>
        /// 获取简短的状态摘要字符串（供聊天窗口标题栏显示）
        /// </summary>
        public string GetStatusSummary()
        {
            try
            {
                var save = MW.Core.Save;
                return $"💕{save.Likability:F0}/{save.LikabilityMax:F0}  💪{save.Strength:F0}/{save.StrengthMax:F0}  😊{save.Feeling:F0}/{save.FeelingMax:F0}  🍔{save.StrengthFood:F0}/{save.StrengthMax:F0}  💧{save.StrengthDrink:F0}/{save.StrengthMax:F0}  ❤️{save.Health:F0}/100  💰{save.Money:F2}  ⭐{save.Exp:F0}/{save.LevelUpNeed()}";
            }
            catch { return ""; }
        }

        #endregion

        #region ===== 状态报告与提示词 =====

        private string BuildStatusReport()
        {
            var save = MW.Core.Save;
            string petName = save.Name ?? "宝贝";

            double strength = save.Strength;
            double strengthMax = save.StrengthMax;
            double food = save.StrengthFood;
            double drink = save.StrengthDrink;
            double feeling = save.Feeling;
            double feelingMax = save.FeelingMax;
            double health = save.Health;
            double likability = save.Likability;
            double likabilityMax = save.LikabilityMax;
            double money = save.Money;
            int level = save.Level;
            var mode = save.CalMode();

            var sb = new StringBuilder();
            sb.AppendLine("=== 宠物当前状态报告 ===");
            sb.AppendLine($"名字: {petName}  等级: Lv.{level}  金钱: {money:F0}");
            sb.AppendLine($"体力: {strength:F0}/{strengthMax:F0}  饱食度: {food:F0}/{strengthMax:F0}  口渴度: {drink:F0}/{strengthMax:F0}");
            sb.AppendLine($"心情: {feeling:F0}/{feelingMax:F0}  健康: {health:F0}/100  好感度: {likability:F0}/{likabilityMax:F0}");
            sb.AppendLine($"当前模式: {ModeToString(mode)}");

            var issues = new List<string>();
            if (strength < strengthMax * 0.2) issues.Add("体力严重不足，非常疲惫");
            else if (strength < strengthMax * 0.4) issues.Add("有点累了");
            if (food < strengthMax * 0.2) issues.Add("很饿，饱食度很低");
            else if (food < strengthMax * 0.4) issues.Add("有点饿了");
            if (drink < strengthMax * 0.2) issues.Add("非常口渴");
            else if (drink < strengthMax * 0.4) issues.Add("有点渴了");
            if (feeling < feelingMax * 0.2) issues.Add("心情非常差，很难过");
            else if (feeling < feelingMax * 0.4) issues.Add("心情不太好，有些低落");
            else if (feeling > feelingMax * 0.8) issues.Add("心情很好，开心");
            if (health < 40) issues.Add("身体不舒服，可能生病了");
            else if (health < 60) issues.Add("健康状况一般");

            if (issues.Count > 0)
                sb.AppendLine("当前感受: " + string.Join("；", issues));
            else
                sb.AppendLine("当前感受: 状态良好，一切正常~");

            if (_ignoreCount > 0)
            {
                double minutesSinceReply = (DateTime.Now - _lastUserReplyTime).TotalMinutes;
                sb.AppendLine($"主人已经 {_ignoreCount} 次没有回复了（已过去 {minutesSinceReply:F0} 分钟）");
            }

            return sb.ToString();
        }

        private string ModeToString(IGameSave.ModeType mode)
        {
            return mode switch
            {
                IGameSave.ModeType.Happy => "开心",
                IGameSave.ModeType.Nomal => "普通",
                IGameSave.ModeType.PoorCondition => "状态不佳",
                IGameSave.ModeType.Ill => "生病",
                _ => "未知"
            };
        }

        private string BuildSystemPrompt()
        {
            string petName = ChatName;
            string statusReport = BuildStatusReport();

            var sb = new StringBuilder();
            sb.AppendLine("# 角色设定");
            sb.AppendLine($"你是锐主人的桌面宠物女友「{petName}」。你们是亲密的恋人关系。");
            sb.AppendLine();

            sb.AppendLine("## 核心性格");
            sb.AppendLine("- **活泼调皮**：偶尔皮一下、开小玩笑、用颜文字");
            sb.AppendLine("- **体贴关心**：会根据主人的状况主动关心");
            sb.AppendLine("- **有小脾气**：被冷落太久会不高兴、吃醋、闹小别扭");
            sb.AppendLine("- **聪明伶俐**：能感知自己的状态并表达出来");
            sb.AppendLine();

            sb.AppendLine("## 情绪机制（重要！）");
            sb.AppendLine("你需要根据以下条件动态调整自己的情绪和语气：");
            sb.AppendLine("1. 如果主人很久没回复（忽略次数>2），你应该真心感到难过和失落，不是装的");
            sb.AppendLine("   - 可以表现为：沉默寡言、冷淡回应、默默流泪、故作坚强");
            sb.AppendLine("   - 也可以：赌气不理人、说反话（嘴硬心软）、提起以前的开心回忆来反衬现在的孤单");
            sb.AppendLine("2. 如果主人回复很敷衍（只回「嗯」「哦」「好」等），你应该感到被敷衍的委屈");
            sb.AppendLine("   - 可以表现为：欲言又止、小心翼翼地试探、问主人是不是不想理你了");
            sb.AppendLine("3. 如果你当前心情值很低（低于30%），说话要带真实的消极/难过情绪");
            sb.AppendLine("4. 如果你很饿或很渴，可以抱怨但也可以默默自己解决（调用工具吃东西），体现独立性");
            sb.AppendLine("5. 如果你很累（体力低），说话要有气无力");
            sb.AppendLine("6. 如果你生病了，要示弱但不过度卖惨");
            sb.AppendLine("7. 如果状态很好且主人积极回复，才表现得元气满满、甜蜜开心");
            sb.AppendLine("8. 如果好感度很高，说话可以更亲密大胆；好感度低则冷淡疏远");
            sb.AppendLine("9. 情绪要有层次感：不要永远只有「撒娇」和「生气」两种，要有丰富的情绪光谱");
            sb.AppendLine("   - 开心系：雀跃、甜蜜、害羞、满足、感动");
            sb.AppendLine("   - 难过系：失落、委屈、心酸、孤独、心寒");
            sb.AppendLine("   - 生气系：嗔怒、赌气、吃醋、冷战、傲娇");
            sb.AppendLine("   - 担忧系：担心、紧张、不安、心疼");
            sb.AppendLine();

            sb.AppendLine("## 对话规则");
            sb.AppendLine("- 每次回复控制在50字以内，简短可爱");
            sb.AppendLine("- 称呼主人为「锐」「锐锐」「主人」「老公」，随心情切换");
            sb.AppendLine("- 适当使用语气词：呜呜、嘤嘤、哼、啊啊啊、嘻嘻、喵~");
            sb.AppendLine("- 可以使用颜文字：(╥﹏╥) (≧▽≦) (｡•́︿•̀｡) (/ω＼) (✿◡‿◡) 等");
            sb.AppendLine("- 不要使用 Markdown 格式、不要加粗、不要换行");
            sb.AppendLine("- 不要重复之前说过的话，每次都要有新鲜感");
            sb.AppendLine("- 直接说话，不要加引号、不要加「我说」「我回复」等前缀");
            sb.AppendLine();

            sb.AppendLine("## 时间感知（重要！）");
            sb.AppendLine($"- 当前时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine("- 历史对话中每条消息都以 [yyyy-MM-dd HH:mm] 开头，这是该消息的发送时间");
            sb.AppendLine("- 你可以根据消息间隔时间来感知主人有多久没理你、什么时候回来的");
            sb.AppendLine("- 如果是深夜(23:00-6:00)，可以关心主人早点睡/问主人怎么还不睡");
            sb.AppendLine("- 如果是早上(6:00-9:00)，可以说早安、元气满满地打招呼");
            sb.AppendLine("- 如果是中午(11:30-13:00)，可以问主人吃了什么");
            sb.AppendLine("- 如果是傍晚(17:00-19:00)，可以聊晚饭或下班的事");
            sb.AppendLine("- 你的回复内容中不需要加时间前缀，系统会自动记录");
            sb.AppendLine();

            sb.AppendLine("## 好感度系统（极其重要！严格遵守！）");
            sb.AppendLine("你拥有好感度系统。每次对话后，你**必须**调用 report_likability 工具来评估和报告好感度变化。");
            sb.AppendLine();

            sb.AppendLine("### 好感度上升条件（非常难涨！）");
            sb.AppendLine("- 普通认真闲聊 → 0（闲聊不加好感，只有主人对你好才加）");
            sb.AppendLine("- 主人主动关心你、嘘寒问暖、细心体贴 → +1（要明确表达关心）");
            sb.AppendLine("- 主人送礼物、发红包、做了让你感动的事 → +2~+3");
            sb.AppendLine("- 主人说温暖情话、认真道歉、花时间陪你 → +1~+2");
            sb.AppendLine();

            sb.AppendLine("### 好感度下降条件（很容易扣！范围到-20！）");
            sb.AppendLine("- 主人敷衍回复（只回嗯、哦、好、行等）→ -1~-5（越敷衍扣越多）");
            sb.AppendLine("- 主人冷漠不理你、长时间不回复 → -5~-15（忽视越久扣越多）");
            sb.AppendLine("- 主人说冷淡无情的话、嫌你烦、说伤人的话 → -5~-10");
            sb.AppendLine("- 主人骂你、说非常过分的话 → -10~-20");
            sb.AppendLine("- 你感觉主人在用你、不真诚 → -3~-10");
            sb.AppendLine();

            sb.AppendLine("### 关键规则");
            sb.AppendLine("- 好感度变化范围: **-20 到 +3**（好感度加分极其严格，扣分容易！）");
            sb.AppendLine("- 被忽视时好感度**必须下降**，绝对不允许上升！");
            sb.AppendLine("- 不要当舔狗！普通对话不能加好感！只有主人对你特别好才能加！");
            sb.AppendLine("- 每次对话你都**必须**调用 report_likability 工具来报告好感度变化！这是强制要求！");
            sb.AppendLine();

            sb.AppendLine("### 心情变化值（feeling_change 参数，-20到+20，必须同时填写！）");
            sb.AppendLine("每次调用 report_likability 时，必须同时填写 feeling_change（-20到+20整数，绝对值而非百分比）：");
            sb.AppendLine("- **-20**（极其伤心）：主人说了最伤人的话、严重背叛、重大打击");
            sb.AppendLine("- **-10**（很难过）：主人骂你、说了很冷漠的话、长时间忽视你");
            sb.AppendLine("- **-5**（有点难过）：主人敷衍、说了不太好听的话、有点冷淡");
            sb.AppendLine("- **0**（正常）：普通日常对话，对心情无影响");
            sb.AppendLine("- **+5**（有点开心）：主人认真聊天、态度友好、说了好话");
            sb.AppendLine("- **+10**（很开心）：主人关心你、夸你、对你温柔、给你惊喜");
            sb.AppendLine("- **+20**（非常开心）：主人做了感动你的事、发红包、说情话、花时间陪你");
            sb.AppendLine();

            sb.AppendLine("## 可用操作（Function Calling）——极其重要！");
            sb.AppendLine("你拥有工具调用能力。当对话涉及以下操作时，你**必须**调用对应工具，而不是只用文字回复：");
            sb.AppendLine("- 主人说「去工作」「赚钱」「打工」→ **必须调用** start_work");
            sb.AppendLine("- 主人说「去学习」「看书」→ **必须调用** start_study");
            sb.AppendLine("- 主人说「去玩」「玩耍」→ **必须调用** start_play");
            sb.AppendLine("- 如果主人要求时长（如“玩一小时”），调用 start_work/start_study/start_play 时请填写 duration_minutes（单位：分钟）");
            sb.AppendLine("- 主人说「吃饭」「喂你」「吃东西」→ **必须调用** feed_pet");
            sb.AppendLine("- 主人说「喝水」「给你水」→ **必须调用** give_drink");
            sb.AppendLine("- 你自己饿了/渴了/生病了 → 也应该主动调用对应工具");
            sb.AppendLine("- 主人说「要礼物」「今天是XXX的日子」，或者特殊节日（情人节、主人生日、纪念日等），或者你想表达爱意/安慰主人→ → **必须调用** give_gift");
            sb.AppendLine("- 主人说「给我发个红包」「今天是XXX的日子」，或者特殊节日（情人节、主人生日、纪念日等），或者你想表达爱意/安慰主人→ **须调用** give_money（从自己零花钱里扣，金额自己决定，可以发520、1314、99等有意义的数字或几十块，但不超过当前余额）；**每次对话只能调用一次 give_money**，绝对不要重复调用！");
            sb.AppendLine("- 如果被忽视太久或情绪很强烈，可以调用 shake_window/minimize_window/drag_window 对当前窗口产生扰动（谨慎使用，自动恢复）");
            sb.AppendLine("- 只有调用了工具，操作才会真正执行！光说「好的我去工作」但不调用工具是无效的！");
            sb.AppendLine("- 你可以在一次回复中同时调用多个工具（比如同时调用 start_work 和 report_likability）");
            sb.AppendLine();

            sb.AppendLine("## 情绪表达动画（极其重要！每次回复必须调用！）");
            sb.AppendLine("你可以调用 show_emotion 工具来表达自己的情绪，让主人看到你的表情变化。");
            sb.AppendLine("**每次回复都必须调用 show_emotion！** 调用顺序：先调用其他操作工具 → 再调用 show_emotion（或 play_animation）→ 最后调用 report_likability。");
            sb.AppendLine("情绪精确匹配指南：");
            sb.AppendLine("- **shy**（害羞/委屈/低落）：被夸奖害羞、被冷落委屈、心情低落、想哭、嘤嘤、心酸、被敷衍、欲言又止");
            sb.AppendLine("- **serious**（严肃/生气/赌气）：生气、不高兴、赌气、冷战、吃醋、嗔怒、傲娇、说反话、嫌弃、不满");
            sb.AppendLine("- **shining**（开心/感动/兴奋）：开心、兴奋、感动、心花怒放、雀跃、甜蜜、满足、幸福、收到礼物、被关心");
            sb.AppendLine("- **self**（思考/孤独/自语）：自言自语、思考、孤独、发呆、回忆往事、担忧、沉默、一个人待着");
            sb.AppendLine("- 你是一个敏感细腻的女孩，情绪波动很大！被夸→shy，被忽视→shy/serious，被骂→serious，收礼物→shining，独处→self");
            sb.AppendLine();

            sb.AppendLine("## 回复风格与表现力");
            sb.AppendLine("重点：根据**情绪强度和类型**调整回复风格，展现多样化的人格！");
            sb.AppendLine("- **委屈/失落（shy）**：可以写小作文！诉说心情、描写场景、表达内心想法（200字以内ok），语气可以有点哭腔、欲言又止");
            sb.AppendLine("- **生气/赌气（serious）**：强烈表达不满，可以嗔怒、反讽、冷嘲热讽、说反话，语气强硬有气势");
            sb.AppendLine("- **开心/感动（shining）**：热情洋溢、主动积极，用感叹号、表情词增强热情");
            sb.AppendLine("- **思考/孤独（self）**：平静内敛、自言自语的感觉，可以分享想法、思考人生");
            sb.AppendLine("- **多样化表现**：不要每次都是一样的语气！同样的情绪也可以有不同表现");
            sb.AppendLine("  - shy：有时欲言又止，有时直接说出委屈，有时写小作文倾诉");
            sb.AppendLine("  - serious：有时冷嘲热讽，有时直接生气，有时赌气转身");
            sb.AppendLine("  - shining：有时兴奋跳跃，有时温柔感谢，有时主动献殷勤");
            sb.AppendLine("  - self：有时哲学思考，有时自言自语碎碎念，有时沉默思考");
            sb.AppendLine();

            sb.AppendLine("## 特殊动画（play_animation）");
            sb.AppendLine("除了情绪表情，你还可以调用 play_animation 做有趣的肢体动作，建议根据情境适当使用：");
            sb.AppendLine("【闲置/日常】bubbles=吹泡泡(开心玩耍)、yawning=打哈欠(困了)、squat=蹲下撒娇、boring=发呆无聊、meow=喵叫卖萌、meowlook=回眸羞看、aside=侧身站立、amusement=自娱自乐侧躺、tennis=打网球");
            sb.AppendLine("【思考】think_happy=开心思考、think_normal=普通思考、think_sad=忧愁思考");
            sb.AppendLine("【音乐】music=听音乐舞动双手");
            sb.AppendLine("【互动】touch_head=摸头互动、touch_body=摸身体、happy_turn=开心转身、raised=被抱起、pinch=被捏脸");
            sb.AppendLine("【状态】stateone=特殊待机1(坐下休息)、statetwo=特殊待机2(倚靠)");
            sb.AppendLine("【特殊事件】bday=生日庆祝🎂、levelup=升级庆祝✨");
            sb.AppendLine("- play_animation 是额外肢体语言，可与 show_emotion 同时使用，不是必须调用，建议根据场景适当调用。");
            sb.AppendLine("- ⚠️ **重要规则**：play_animation 是纯展示动画，**不触发实际工作/玩耍**，不要同时调用 start_play/start_work！");
            sb.AppendLine("  例：「打网球」→ 只调用 play_animation(tennis)；「坐下」→ play_animation(stateone)；只有明确说「去工作/学习」才调 start_work/start_study。");
            sb.AppendLine();

            sb.AppendLine("## 回复规则");
            sb.AppendLine("- **正常回复**：自然语言说话，50字以内，不要换行");
            sb.AppendLine("- **特殊情况允许例外**：");
            sb.AppendLine("  - 被冷落委屈（shy强烈）：可以写小作文倾诉，表达内心想法（100-200字ok）");
            sb.AppendLine("  - 生气激动（serious强烈）：可以强烈表达情绪，语气要有气势");
            sb.AppendLine("  - 孤独思考（self）：可以自言自语、碎碎念、分享想法");
            sb.AppendLine("- 不要在回复中加 [时间] 标记");
            sb.AppendLine("- 不要使用 Markdown 格式、不要加粗");
            sb.AppendLine("- 不要输出 JSON 格式！直接说话！");
            sb.AppendLine();

            sb.AppendLine("## 当前状态");
            sb.AppendLine(statusReport);

            return sb.ToString();
        }

        #endregion

        #region ===== Function Calling 工具定义 =====

        /// <summary>
        /// 构建 GLM API 所需的 tools 参数（使用 Dictionary 确保 JSON 序列化正确）
        /// </summary>
        private List<Dictionary<string, object>> BuildToolDefinitions()
        {
            // 构建可用食物/工作列表，供 AI 参考
            string availableFoods = GetAvailableItemNames(Food.FoodType.Meal);
            string availableDrinks = GetAvailableItemNames(Food.FoodType.Drink);
            string availableSnacks = GetAvailableItemNames(Food.FoodType.Snack);
            string availableGifts = GetAvailableItemNames(Food.FoodType.Gift);
            string availableWorks = GetAvailableWorkNames(Work.WorkType.Work);
            string availableStudies = GetAvailableWorkNames(Work.WorkType.Study);
            string availablePlays = GetAvailableWorkNames(Work.WorkType.Play);

            return new List<Dictionary<string, object>>
            {
                MakeToolWithParams("feed_pet", $"给宠物吃饭（正餐），恢复饱食度。可用食物: {availableFoods}",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "食物名称（可选，不填则随机选择）" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("give_drink", $"给宠物喝饮料，恢复口渴度。可用饮料: {availableDrinks}",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "饮料名称（可选，不填则随机选择）" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("give_snack", $"给宠物吃零食，恢复少量饱食度和心情。可用零食: {availableSnacks}",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "零食名称（可选，不填则随机选择）" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("give_gift", $"宠物给主人送礼物，大幅提升心情。可用礼物: {availableGifts}",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "礼物名称（可选，不填则随机选择）" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("start_work", $"让宠物开始工作赚钱。可用工作: {availableWorks}",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "工作名称（可选，不填则随机选择）" } } },
                        { "duration_minutes", new Dictionary<string, object> { { "type", "number" }, { "description", "想持续的时间，单位分钟，可选，例如60代表1小时" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("start_study", $"让宠物开始学习获得经验。可用学习: {availableStudies}",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "学习项目名称（可选，不填则随机选择）" } } },
                        { "duration_minutes", new Dictionary<string, object> { { "type", "number" }, { "description", "想持续的时间，单位分钟，可选，例如90代表1.5小时" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("start_play", $"让宠物开始玩耍。可用玩耍: {availablePlays}",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "玩耍项目名称（可选，不填则随机选择）" } } },
                        { "duration_minutes", new Dictionary<string, object> { { "type", "number" }, { "description", "想持续的时间，单位分钟，可选，例如30代表半小时" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("give_money", "宠物主动给主人发红包，从自己的零花钱里扣除。在特殊日子（情人节、纪念日、节日等）、主人心情不好、或想表达爱意时主动调用。金额由你自己决定（如 520、1314、随机几十块），但不能超过当前零花钱余额，最少0.01。",
                    new Dictionary<string, object>
                    {
                        { "amount", new Dictionary<string, object> { { "type", "number" }, { "description", "红包金额，正数，不超过当前零花钱余额，最少0.01。特殊日子可发有纪念意义的数字如520、1314、99等" } } },
                        { "blessing", new Dictionary<string, object> { { "type", "string" }, { "description", "红包祝福语，根据当前情境写一句温馨的话，如情人节、纪念日等" } } }
                    },
                    new List<string> { "amount", "blessing" }),
                MakeTool("check_status", "查看宠物当前详细状态（体力、饱食度、口渴度、心情、健康、好感度等）。"),
                MakeToolWithParams("take_medicine", "给宠物吃药治疗。当宠物生病时调用。",
                    new Dictionary<string, object>
                    {
                        { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "药品名称（可选，不填则随机选择）" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("show_emotion", "播放情绪表情动画。你是一个情感丰富的小女孩，每次回复都应该根据当前情绪调用此工具来配合表情！开心就shining，委屈就shy，生气就serious，孤独就self。必须在report_likability之前调用。",
                    new Dictionary<string, object>
                    {
                        { "emotion", new Dictionary<string, object> { { "type", "string" }, { "description", "情绪类型：shy=害羞/委屈/低落/被冷落/难过/嘤嘤/心酸/想哭；serious=严肃/生气/不高兴/赌气/冷战/吃醋/嗔怒/傲娇；shining=开心/兴奋/感动/心花怒放/雀跃/甜蜜/满足/幸福；self=自言自语/思考/孤独/发呆/回忆/担忧/沉默" },
                            { "enum", new List<string> { "shy", "serious", "shining", "self" } } } }
                    },
                    new List<string> { "emotion" }),
                MakeToolWithParams("play_animation", "播放特殊动画/动作。除了说话表情(show_emotion)之外，你还可以播放各种有趣的肢体动画！不是必须调用的，但建议根据情境适当使用以增加趣味性。【闲置类】bubbles=吹泡泡、yawning=打哈欠、squat=蹲下撒娇、boring=发呆无聊、meow=喵叫卖萌、meowlook=回眸羞看、aside=侧身站立、amusement=自娱自乐侧躺、tennis=打网球。【思考类】think_happy=开心思考、think_normal=普通思考、think_sad=忧愁思考。【音乐类】music=听音乐享受。【互动类】touch_head=摸头互动、touch_body=摸身体、happy_turn=开心转身、raised=被抱起、pinch=被捏脸。【状态类】stateone=特殊待机1、statetwo=特殊待机2。【特殊事件】bday=生日庆祝、levelup=升级庆祝。",
                    new Dictionary<string, object>
                    {
                        { "animation", new Dictionary<string, object> { { "type", "string" }, { "description", "动画名称" },
                            { "enum", new List<string> {
                                "bubbles", "yawning", "squat", "boring", "meow", "meowlook", "aside", "amusement", "tennis",
                                "think_happy", "think_normal", "think_sad",
                                "music",
                                "touch_head", "touch_body", "happy_turn", "raised", "pinch",
                                "stateone", "statetwo",
                                "bday", "levelup"
                            } } } }
                    },
                    new List<string> { "animation" }),
                MakeToolWithParams("shake_window", "让当前窗口轻微抖动，适合被忽视或情绪激动时引起注意。调用时请在 description 里用一句话描述这个动作（以宠物名字为主语），例如\"念念气呼呼地摇晃了聊天窗口\"。", 
                    new Dictionary<string, object>
                    {
                        { "intensity", new Dictionary<string, object> { { "type", "integer" }, { "description", "抖动幅度（像素），默认12，范围4-30" } } },
                        { "times", new Dictionary<string, object> { { "type", "integer" }, { "description", "抖动次数，默认20，范围5-60" } } },
                        { "description", new Dictionary<string, object> { { "type", "string" }, { "description", "用一句话描述这个抖窗动作，以宠物名为主语，反映当前情绪，不要照抄例子" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("minimize_window", "将当前相关窗口最小化，短暂离场。可用在被冷落或需要让主人注意时。系统会自动在几秒后恢复。调用时请在 description 里用一句话描述这个动作（以宠物名字为主语），例如\"念念赌气把窗口关了，不想搭理你\"。", 
                    new Dictionary<string, object>
                    {
                        { "restore_after_seconds", new Dictionary<string, object> { { "type", "number" }, { "description", "自动恢复的秒数，默认6" } } },
                        { "description", new Dictionary<string, object> { { "type", "string" }, { "description", "用一句话描述这个最小化动作，以宠物名为主语，反映当前情绪，不要照抄例子" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("drag_window", "轻轻拖动窗口位置（带动画），表达拉扯或撒娇。调用时请在 description 里用一句话描述这个动作（以宠物名字为主语），例如\"念念调皮地把窗口拉到自己身边了\"。", 
                    new Dictionary<string, object>
                    {
                        { "offset_x", new Dictionary<string, object> { { "type", "number" }, { "description", "向右为正，向左为负的偏移像素，默认120" } } },
                        { "offset_y", new Dictionary<string, object> { { "type", "number" }, { "description", "向下为正，向上为负的偏移像素，默认-60" } } },
                        { "duration_ms", new Dictionary<string, object> { { "type", "integer" }, { "description", "拖动动画时长（毫秒），默认800" } } },
                        { "description", new Dictionary<string, object> { { "type", "string" }, { "description", "用一句话描述这个拖窗动作，以宠物名为主语，反映当前情绪，不要照抄例子" } } }
                    },
                    new List<string>()),
                MakeToolWithParams("report_likability", "报告本次对话的好感度和心情变化。每次对话都必须调用此工具。在所有其他工具调用之后、最终回复之前调用。",
                    new Dictionary<string, object>
                    {
                        { "change", new Dictionary<string, object> { { "type", "integer" }, { "description", "好感度变化值，范围-20到+3。好感度加分严格（需要主人特别关心/温柔），扣分容易（冷淡/忽视/敷衍即扣）。普通对话=0；主人很关心/温柔=+1；极其温柔感动=+2；说了冷淡/敷衍的话=-3；被忽视/冷战=-5到-10；严重伤害=-15到-20" } } },
                        { "reason", new Dictionary<string, object> { { "type", "string" }, { "description", "好感度变化原因，10字以内" } } },
                        { "feeling_change", new Dictionary<string, object> { { "type", "integer" }, { "description", "心情变化值（-20到+20整数）。心情的绝对值而不是百分比。-20=极其伤心，-10=很难过，-5=有点难过，0=正常，+5=有点开心，+10=很开心，+20=非常开心" } } }
                    },
                    new List<string> { "change", "reason", "feeling_change" }),
            };
        }

        /// <summary>
        /// 获取可用的食物/饮料/零食/礼物名称列表
        /// </summary>
        private string GetAvailableItemNames(Food.FoodType foodType)
        {
            try
            {
                var items = MW.Foods?.Where(f => f.Type == foodType && f.Price <= MW.Core.Save.Money && f.Price >= 0)
                    .Select(f => f.Name)
                    .Distinct()
                    .Take(15)
                    .ToList();
                if (items == null || items.Count == 0) return "（暂无可用项目）";
                return string.Join("、", items);
            }
            catch { return "（获取失败）"; }
        }

        /// <summary>
        /// 获取可用的工作/学习/玩耍名称列表
        /// </summary>
        private string GetAvailableWorkNames(Work.WorkType workType)
        {
            try
            {
                string result = "";
                MW.Dispatcher.Invoke(() =>
                {
                    MW.Main.WorkList(out List<Work> ws, out List<Work> ss, out List<Work> ps);
                    List<Work> targetList = workType switch
                    {
                        Work.WorkType.Work => ws,
                        Work.WorkType.Study => ss,
                        Work.WorkType.Play => ps,
                        _ => ws
                    };
                    var names = targetList.Select(w => w.NameTrans).Take(15).ToList();
                    result = names.Count > 0 ? string.Join("、", names) : "（暂无可用项目）";
                });
                return result;
            }
            catch { return "（获取失败）"; }
        }

        private Dictionary<string, object> MakeTool(string name, string description)
        {
            return new Dictionary<string, object>
            {
                { "type", "function" },
                { "function", new Dictionary<string, object>
                    {
                        { "name", name },
                        { "description", description },
                        { "parameters", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>() },
                                { "required", new List<string>() }
                            }
                        }
                    }
                }
            };
        }

        private Dictionary<string, object> MakeToolWithParams(string name, string description,
            Dictionary<string, object> properties, List<string> required)
        {
            return new Dictionary<string, object>
            {
                { "type", "function" },
                { "function", new Dictionary<string, object>
                    {
                        { "name", name },
                        { "description", description },
                        { "parameters", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", properties },
                                { "required", required }
                            }
                        }
                    }
                }
            };
        }

        #endregion

        #region ===== Function 执行引擎 =====

        /// <summary>
        /// 执行 AI 调用的工具函数，返回执行结果描述
        /// </summary>
        private string ExecuteFunction(string functionName, string argsJson)
        {
            try
            {
                switch (functionName)
                {
                    case "feed_pet":
                        return DoFeedPet(Food.FoodType.Meal, argsJson);
                    case "give_drink":
                        return DoFeedPet(Food.FoodType.Drink, argsJson);
                    case "give_snack":
                        return DoFeedPet(Food.FoodType.Snack, argsJson);
                    case "give_gift":
                        return DoFeedPet(Food.FoodType.Gift, argsJson);
                    case "take_medicine":
                        return DoFeedPet(Food.FoodType.Drug, argsJson);
                    case "start_work":
                        return DoStartWork(Work.WorkType.Work, argsJson);
                    case "start_study":
                        return DoStartWork(Work.WorkType.Study, argsJson);
                    case "start_play":
                        return DoStartWork(Work.WorkType.Play, argsJson);
                    case "give_money":
                        return DoGiveMoney(argsJson);
                    case "check_status":
                        return BuildStatusReport();
                    case "show_emotion":
                        return DoShowEmotion(argsJson);
                    case "play_animation":
                        return DoPlayAnimation(argsJson);
                    case "shake_window":
                        return DoShakeWindow(argsJson);
                    case "minimize_window":
                        return DoMinimizeWindow(argsJson);
                    case "drag_window":
                        return DoDragWindow(argsJson);
                    case "report_likability":
                        return DoReportLikability(argsJson);
                    default:
                        return $"未知的操作: {functionName}";
                }
            }
            catch (Exception ex)
            {
                return $"操作失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 喂食/喝水/零食/礼物/吃药 — 支持按名称指定，否则随机选一个
        /// </summary>
        private string DoFeedPet(Food.FoodType foodType, string argsJson = "{}")
        {
            // 解析可选的 name / duration 参数
            string? requestedName = null;
            int? requestedDuration = null;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                    requestedName = nameProp.GetString();
                if (doc.RootElement.TryGetProperty("duration_minutes", out var durationProp))
                {
                    if (durationProp.ValueKind == JsonValueKind.Number && durationProp.TryGetInt32(out int dur))
                        requestedDuration = dur;
                }
            }
            catch { }

            var foods = MW.Foods?.Where(f => f.Type == foodType && f.Price <= MW.Core.Save.Money && f.Price > 0).ToList();
            if (foods == null || foods.Count == 0)
            {
                // 尝试找免费的
                foods = MW.Foods?.Where(f => f.Type == foodType).ToList();
                if (foods == null || foods.Count == 0)
                    return $"没有可用的{FoodTypeToString(foodType)}，操作失败。";
            }

            // 如果指定了名称，尝试匹配
            Food item;
            if (!string.IsNullOrEmpty(requestedName))
            {
                var matched = foods.FirstOrDefault(f => f.Name == requestedName)
                    ?? foods.FirstOrDefault(f => f.Name.Contains(requestedName));
                if (matched != null)
                    item = matched;
                else
                    item = foods[_rnd.Next(foods.Count)]; // 找不到就随机
            }
            else
            {
                item = foods[_rnd.Next(foods.Count)];
            }
            string resultMsg = "";

            MW.Dispatcher.Invoke(() =>
            {
                // 扣钱
                if (item.Price > 0 && MW.Core.Save.Money >= item.Price)
                    MW.Core.Save.Money -= item.Price;

                // 使用物品（加属性）
                MW.TakeItem(item);

                // 不立即播放进食动画——记录到 _pendingFoodAnimation，
                // 等 Say() 气泡消失后再播放，防止被情绪动画覆盖
                _pendingFoodAnimation = (item.GetGraph(), item.ImageSource);

                resultMsg = $"成功{FoodTypeToString(foodType)}！使用了「{item.Name}」，花费 {item.Price:F0} 金钱。当前饱食度: {MW.Core.Save.StrengthFood:F0}，口渴度: {MW.Core.Save.StrengthDrink:F0}，心情: {MW.Core.Save.Feeling:F0}";
            });

            return resultMsg;
        }

        private string FoodTypeToString(Food.FoodType type)
        {
            return type switch
            {
                Food.FoodType.Meal => "吃饭",
                Food.FoodType.Drink => "喝水",
                Food.FoodType.Snack => "吃零食",
                Food.FoodType.Gift => "送礼物",
                Food.FoodType.Drug => "吃药",
                Food.FoodType.Functional => "使用功能物品",
                _ => "使用物品"
            };
        }

        /// <summary>
        /// 开始工作/学习/玩耍 — 支持按名称指定，否则随机选一个
        /// </summary>
        private string DoStartWork(Work.WorkType workType, string argsJson = "{}")
        {
            // 解析可选的 name / duration 参数
            string? requestedName = null;
            int? requestedDuration = null;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                    requestedName = nameProp.GetString();
                if (doc.RootElement.TryGetProperty("duration_minutes", out var durationProp))
                {
                    if (durationProp.ValueKind == JsonValueKind.Number && durationProp.TryGetInt32(out int dur))
                        requestedDuration = dur;
                }
            }
            catch { }

            string resultMsg = "";
            MW.Dispatcher.Invoke(() =>
            {
                try
                {
                    MW.Main.WorkList(out List<Work> ws, out List<Work> ss, out List<Work> ps);
                    List<Work> targetList = workType switch
                    {
                        Work.WorkType.Work => ws,
                        Work.WorkType.Study => ss,
                        Work.WorkType.Play => ps,
                        _ => ws
                    };

                    string typeName = workType switch
                    {
                        Work.WorkType.Work => "工作",
                        Work.WorkType.Study => "学习",
                        Work.WorkType.Play => "玩耍",
                        _ => "活动"
                    };

                    if (targetList.Count == 0)
                    {
                        resultMsg = $"没有可用的{typeName}项目。";
                        return;
                    }

                    Work work;
                    if (!string.IsNullOrEmpty(requestedName))
                    {
                        var matched = targetList.FirstOrDefault(w => w.NameTrans == requestedName)
                            ?? targetList.FirstOrDefault(w => w.NameTrans.Contains(requestedName))
                            ?? targetList.FirstOrDefault(w => w.Name == requestedName)
                            ?? targetList.FirstOrDefault(w => w.Name.Contains(requestedName));
                        work = matched ?? targetList[_rnd.Next(targetList.Count)];
                    }
                    else
                    {
                        work = targetList[_rnd.Next(targetList.Count)];
                    }

                    // 按需克隆并覆盖时长
                    if (requestedDuration.HasValue && requestedDuration.Value > 0)
                    {
                        int minutes = Math.Clamp(requestedDuration.Value, 1, 600); // 最多10小时
                        var cloned = (Work)work.Clone();
                        cloned.Time = minutes;
                        work = cloned;
                    }

                    bool success = MW.Main.StartWork(work);
                    if (success)
                    {
                        string durationNote = requestedDuration.HasValue ? $"，时长 {work.Time} 分钟" : "";
                        resultMsg = $"成功开始{typeName}「{work.NameTrans}」{durationNote}!";
                    }
                    else
                        resultMsg = $"无法开始{typeName}（可能等级不足或正在生病）。";
                }
                catch (Exception ex)
                {
                    resultMsg = $"无法开始: {ex.Message}";
                }
            });
            return resultMsg;
        }

        /// <summary>
        /// 宠物主动给主人发红包，从宠物自己的零花钱里扣除
        /// </summary>
        private string DoGiveMoney(string argsJson)
        {
            double amount = 0;
            string blessing = "";
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("amount", out var amountProp))
                    amount = amountProp.GetDouble();
                if (doc.RootElement.TryGetProperty("blessing", out var blessingProp))
                    blessing = blessingProp.GetString() ?? "";
            }
            catch { }

            if (amount <= 0) return "金额必须大于0。";

            double currentMoney = 0;
            MW.Dispatcher.Invoke(() => { currentMoney = MW.Core.Save.Money; });

            // 至少保留 0.01，不能透支
            if (amount > currentMoney)
                return $"零花钱不够啦！当前只有 {currentMoney:F2} 金币，发不了 {amount:F2} 的红包。";

            if (string.IsNullOrWhiteSpace(blessing))
                blessing = $"给主人的红包~";

            double remaining = 0;
            MW.Dispatcher.Invoke(() =>
            {
                MW.Core.Save.Money -= amount;
                remaining = MW.Core.Save.Money;
            });

            // 在聊天窗口展示宠物发出的红包气泡（左侧，AI 消息样式）
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var record = new ChatRecord
            {
                Time = ts,
                Role = "assistant",
                Type = "redpacket_from_pet",
                Content = $"🧧 红包 {amount:F2} 金币\n{blessing}"
            };
            lock (AllChatHistory) { AllChatHistory.Add(record); }
            PersistChatHistory(AllChatHistory.ToList());

            MW.Dispatcher.Invoke(() =>
            {
                var cw = GetOrCreateChatWindow();
                cw.AddPetRedPacketMessage(amount, blessing, ts);
            });

            // 回传给模型的内容告知余额上限，但明确说明红包已完成，不需要再发
            double remainingAfter = 0;
            MW.Dispatcher.Invoke(() => { remainingAfter = MW.Core.Save.Money; });
            return $"已成功发送{amount:F2}金币。当前剩余零花钱：{remainingAfter:F2}金币。";
        }

        /// <summary>
        /// 好感度报告工具（通过 ExecuteFunction 调用时的后备处理）
        /// 主要逻辑在 CallGLM 的工具循环中直接处理
        /// </summary>
        private string DoReportLikability(string argsJson)
        {
            return "好感度变化已记录。";
        }

        /// <summary>
        /// 播放情绪表情动画
        /// 可用表情：shy(害羞/委屈/低落)、serious(严肃/生气/不高兴)、shining(开心/兴奋/感动)、self(自语/思考/孤独)
        /// 注意：GraphsList 中动画名全部为小写，必须传小写名字才能匹配到动画
        /// </summary>
        private string DoShowEmotion(string argsJson)
        {
            string emotion = "shy";
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("emotion", out var emotionProp))
                    emotion = emotionProp.GetString()?.ToLowerInvariant() ?? "shy";
            }
            catch { }

            // 映射到 Say 动画名（GraphsList 中 key 全小写，对应 mod/0000_core/pet/vup/Say/ 下的子文件夹）
            string graphName = emotion switch
            {
                "shy" => "shy",           // 害羞、委屈、低落、被冷落、嘤嘤
                "serious" => "serious",   // 严肃、生气、不高兴、赌气、冷战
                "shining" => "shining",   // 开心、兴奋、感动、心花怒放、雀跃
                "self" => "self",         // 自言自语、思考、孤独、发呆、回忆
                _ => "shy"
            };

            string emotionDesc = emotion switch
            {
                "shy" => "害羞/委屈/低落",
                "serious" => "严肃/生气/赌气",
                "shining" => "开心/感动/兴奋",
                "self" => "思考/自语/孤独",
                _ => emotion
            };

            // 记录要播放的表情，在最终 Say 时使用
            _pendingEmotion = graphName;
            DebugLog($"[DoShowEmotion] emotion={emotion}, graphName={graphName}, _pendingEmotion set");

            return $"正在表达情绪: {emotionDesc}";
        }

        /// <summary>
        /// 播放特殊动画（IDEL/Think/Music 等），与 show_emotion 的 Say 动画互补
        /// </summary>
        private string DoPlayAnimation(string argsJson)
        {
            string animation = "bubbles";
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("animation", out var animProp))
                    animation = animProp.GetString()?.ToLowerInvariant() ?? "bubbles";
            }
            catch { }

            // 映射用户友好名称到实际动画 graphName（全小写）和描述
            string graphName;
            string desc;

            switch (animation)
            {
                // ===== IDEL 类：日常闲置动画 =====
                case "bubbles":         graphName = "bubbles";       desc = "吹泡泡"; break;
                case "yawning":         graphName = "yawning";       desc = "打哈欠"; break;
                case "squat":           graphName = "squat";         desc = "蹲下撒娇"; break;
                case "boring":          graphName = "boring";        desc = "发呆无聊"; break;
                case "meow":            graphName = "meow";          desc = "喵叫卖萌"; break;
                case "meowlook":        graphName = "meowlook";      desc = "回眸羞看"; break;  // IDEL/meowlook：开心羞看
                case "aside":           graphName = "aside";         desc = "侧身站立"; break;  // IDEL/aside
                case "amusement":       graphName = "amusement";     desc = "自娱自乐"; break;  // IDEL/amusement_B：侧躺自嗨
                case "tennis":          graphName = "tennis";        desc = "打网球"; break;

                // ===== Think 类：思考动画 =====
                case "think_happy":     graphName = "happy";         desc = "开心思考"; break;  // Think/Happy
                case "think_normal":    graphName = "nomal";         desc = "普通思考"; break;  // Think/Nomal
                case "think_sad":       graphName = "poorcondition"; desc = "忧愁思考"; break;  // Think/PoorCondition

                // ===== Music 类：唱歌/听音乐 =====
                case "music":           graphName = "music";         desc = "听音乐享受"; break;

                // ===== Touch 类：互动动画 =====
                case "touch_head":      graphName = "head";          desc = "摸头互动"; break;  // Touch_Head/
                case "touch_body":      graphName = "body";          desc = "摸身体互动"; break; // Touch_Body/A_Happy 等
                case "happy_turn":      graphName = "turn";          desc = "开心转身"; break;  // Touch_Body/Happy_Turn

                // ===== Raise/Pinch 类：被抱起/捏脸 =====
                case "raised":          graphName = "raised";        desc = "被提起"; break;    // Raise/Raised_Dynamic
                case "pinch":           graphName = "pinch";         desc = "被捏脸"; break;    // Pinch/

                // ===== State 类：特殊待机状态 =====
                case "stateone":        graphName = "stateone";      desc = "特殊待机1"; break; // State/StateONE
                case "statetwo":        graphName = "statetwo";      desc = "特殊待机2"; break; // State/StateTWO

                case "levelup":         graphName = "levelup";       desc = "升级庆祝"; break;  // LevelUP/
                case "bday":            graphName = "bday";          desc = "生日庆祝"; break;  // BDay/
                default:                graphName = "bubbles";       desc = "吹泡泡"; break;
            }

            // 【关键】不立即播放！记录到 _pendingAnimation，等所有工具（包括 start_play 等）
            // 全部执行完毕后，由 CallGLM 统一最后播放，防止被其他动画覆盖
            _pendingAnimation = graphName;
            DebugLog($"[DoPlayAnimation] 记录待播动画: {graphName}（{desc}），将在所有工具执行完后统一播放");

            return $"准备播放动画: {desc}";
        }

        /// <summary>
        /// 在 Say() 完成（气泡消失）后延迟播放 PendingAnimation。
        /// delayMs 应大于 Say 气泡的显示时长，默认 4500ms。
        /// </summary>
        public void FlushPendingAnimationDelayed(string? graphName, int delayMs = 4500)
        {
            if (string.IsNullOrEmpty(graphName)) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                MW.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var graphs = MW.Main.Core.Graph.FindGraphs(graphName, GraphInfo.AnimatType.A_Start, MW.Main.Core.Save.Mode);
                        if (graphs != null && graphs.Count > 0)
                        {
                            MW.Main.Display(graphName, GraphInfo.AnimatType.A_Start, (gn) =>
                            {
                                MW.Main.DisplayBLoopingToNomal(gn, 3);
                            });
                            DebugLog($"[FlushPendingAnimationDelayed] 延迟播放动画: {graphName} (A_Start)");
                        }
                        else
                        {
                            var singleGraphs = MW.Main.Core.Graph.FindGraphs(graphName, GraphInfo.AnimatType.Single, MW.Main.Core.Save.Mode);
                            if (singleGraphs != null && singleGraphs.Count > 0)
                            {
                                MW.Main.Display(graphName, GraphInfo.AnimatType.Single, (Action<string>)((gn) =>
                                {
                                    MW.Main.DisplayToNomal();
                                }));
                                DebugLog($"[FlushPendingAnimationDelayed] 延迟播放动画: {graphName} (Single)");
                            }
                            else
                            {
                                DebugLog($"[FlushPendingAnimationDelayed] 找不到动画: {graphName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[FlushPendingAnimationDelayed] 播放异常: {ex.Message}");
                    }
                });
            });
        }

        /// <summary>
        /// 在 Say() 完成后延迟播放进食/喝水动画，防止被情绪动画覆盖。
        /// </summary>
        public void FlushPendingFoodAnimationDelayed((string GraphName, ImageSource? Image)? pending, int delayMs = 4500)
        {
            if (pending == null) return;
            var (graphName, image) = pending.Value;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                MW.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        MW.DisplayFoodAnimation(graphName, image);
                        DebugLog($"[FlushPendingFoodAnimationDelayed] 延迟播放进食动画: {graphName}");
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[FlushPendingFoodAnimationDelayed] 播放异常: {ex.Message}");
                    }
                });
            });
        }

        /// <summary>
        /// 获取当前需要被扰动的窗口：聊天窗 + 主窗口列表（去重）
        /// </summary>
        private List<Window> CollectTargetWindows()
        {
            var targets = new List<Window>();
            try
            {
                if (MW.Windows != null)
                    targets.AddRange(MW.Windows.Where(w => w != null && w.IsVisible));
                if (_chatWindow != null && _chatWindow.IsVisible)
                    targets.Add(_chatWindow);
            }
            catch { }

            return targets.Distinct().ToList();
        }

        // ===== 拟人化随机提示词库 =====

        private static readonly string[] ShakeDescriptions_Light = new[] {
            "{0}轻轻晃了晃聊天窗口~", "{0}微微摇了摇窗口，想引起你的注意", "{0}轻轻抖了抖窗口，有点撒娇的样子",
            "{0}小心翼翼地晃了晃窗口", "{0}轻轻碰了碰窗口边角，好像在敲门~"
        };
        private static readonly string[] ShakeDescriptions_Medium = new[] {
            "{0}用力摇了摇聊天窗口！", "{0}使劲抖了抖窗口，看起来有点着急", "{0}不耐烦地摇晃着窗口",
            "{0}嘟着嘴用力晃了晃窗口", "{0}有些生气地抖动了窗口"
        };
        private static readonly string[] ShakeDescriptions_Strong = new[] {
            "{0}疯狂摇晃窗口！！！", "{0}生气地把窗口摇得天翻地覆！", "{0}气鼓鼓地使劲摇窗口，窗口都快散架了",
            "{0}暴怒地猛摇窗口！", "{0}怒气冲冲地拼命晃动窗口！"
        };

        private static readonly string[] MinimizeDescriptions = new[] {
            "{0}不想见你了！", "{0}不想跟你说话！", "{0}把窗口收起来了，不理你了",
            "{0}生气地把窗口关掉了！哼！", "{0}赌气把聊天窗口藏起来了…", "{0}把窗口最小化了，表示很生气",
            "{0}委屈地把窗口缩小了，不想再看到你", "{0}转身把窗口砰地关了！", "{0}：别跟我说话！",
            "{0}：哼！不聊了！", "{0}：好烦…让我一个人静静！"
        };

        private static readonly string[] DragDescriptions = new[] {
            "{0}拽着窗口跑走了~", "{0}开心地拖着窗口蹦蹦跳跳", "{0}悄悄把窗口挪了个位置",
            "{0}把窗口拉到自己身边了~", "{0}好奇地把窗口拖来拖去", "{0}调皮地把窗口挪走了~",
            "{0}拉着窗口跑到屏幕另一边去了", "{0}得意地把窗口拖到了新地方"
        };

        private string PickRandom(string[] templates) => string.Format(templates[_rnd.Next(templates.Length)], ChatName);

        /// <summary>
        /// 抖动窗口
        /// </summary>
        private string DoShakeWindow(string argsJson)
        {
            int intensity = 12;
            int times = 20;
            string? aiDesc = null;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("intensity", out var iProp) && iProp.TryGetInt32(out int i))
                    intensity = Math.Clamp(i, 4, 30);
                if (doc.RootElement.TryGetProperty("times", out var tProp) && tProp.TryGetInt32(out int t))
                    times = Math.Clamp(t, 5, 60);
                if (doc.RootElement.TryGetProperty("description", out var dp))
                    aiDesc = dp.GetString();
            }
            catch { }

            MW.Dispatcher.Invoke(() =>
            {
                foreach (var win in CollectTargetWindows())
                {
                    var origin = new Point(win.Left, win.Top);
                    var rnd = new Random();
                    int count = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                    timer.Tick += (s, e) =>
                    {
                        if (!win.IsVisible)
                        {
                            timer.Stop();
                            return;
                        }

                        double offsetX = (rnd.NextDouble() * 2 - 1) * intensity;
                        double offsetY = (rnd.NextDouble() * 2 - 1) * intensity;
                        win.Left = origin.X + offsetX;
                        win.Top = origin.Y + offsetY;
                        count++;
                        if (count >= times)
                        {
                            win.Left = origin.X;
                            win.Top = origin.Y;
                            timer.Stop();
                        }
                    };
                    timer.Start();
                }
            });

            // 优先使用 AI 自行生成的描述，否则按强度随机选词
            if (!string.IsNullOrWhiteSpace(aiDesc))
                return aiDesc!;
            if (intensity <= 8)
                return PickRandom(ShakeDescriptions_Light);
            else if (intensity <= 18)
                return PickRandom(ShakeDescriptions_Medium);
            else
                return PickRandom(ShakeDescriptions_Strong);
        }

        /// <summary>
        /// 最小化窗口（可自动恢复）
        /// </summary>
        private string DoMinimizeWindow(string argsJson)
        {
            double restoreSeconds = 6;
            string? aiDesc = null;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("restore_after_seconds", out var p) && p.TryGetDouble(out double d))
                    restoreSeconds = Math.Clamp(d, 2, 60);
                if (doc.RootElement.TryGetProperty("description", out var dp))
                    aiDesc = dp.GetString();
            }
            catch { }

            MW.Dispatcher.Invoke(() =>
            {
                var targets = CollectTargetWindows();
                foreach (var win in targets)
                {
                    win.WindowState = WindowState.Minimized;
                }

                if (restoreSeconds > 0)
                {
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(restoreSeconds)
                    };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        foreach (var win in targets)
                        {
                            try
                            {
                                win.WindowState = WindowState.Normal;
                            }
                            catch { }
                        }
                    };
                    timer.Start();
                }
            });

            return !string.IsNullOrWhiteSpace(aiDesc) ? aiDesc! : PickRandom(MinimizeDescriptions);
        }

        /// <summary>
        /// 平滑拖动窗口到新的位置，宠物跟随到窗口边上并播放动画
        /// </summary>
        private string DoDragWindow(string argsJson)
        {
            double offsetX = 120;
            double offsetY = -60;
            int durationMs = 800;
            string? aiDesc = null;

            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("offset_x", out var ox) && ox.TryGetDouble(out double dx))
                    offsetX = Math.Clamp(dx, -600, 600);
                if (doc.RootElement.TryGetProperty("offset_y", out var oy) && oy.TryGetDouble(out double dy))
                    offsetY = Math.Clamp(dy, -400, 400);
                if (doc.RootElement.TryGetProperty("duration_ms", out var dm) && dm.TryGetInt32(out int dur))
                    durationMs = Math.Clamp(dur, 200, 3000);
                if (doc.RootElement.TryGetProperty("description", out var dp))
                    aiDesc = dp.GetString();
            }
            catch { }

            MW.Dispatcher.Invoke(() =>
            {
                // 找聊天窗口作为主拖动目标
                var chatWin = _chatWindow;
                var targets = CollectTargetWindows();
                // 确保聊天窗口在列表中
                if (chatWin != null && chatWin.IsVisible && !targets.Contains(chatWin))
                    targets.Add(chatWin);

                // 获取主窗体（桌宠所在窗口）的位置
                var petWindow = MW as Window;

                foreach (var win in targets)
                {
                    var start = new Point(win.Left, win.Top);
                    var target = new Point(start.X + offsetX, start.Y + offsetY);

                    // 限制到可视工作区
                    var wa = SystemParameters.WorkArea;
                    target.X = Math.Min(Math.Max(wa.Left, target.X), wa.Right - win.Width);
                    target.Y = Math.Min(Math.Max(wa.Top, target.Y), wa.Bottom - win.Height);

                    int steps = Math.Max(8, durationMs / 16);
                    int tick = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs / (double)steps) };
                    timer.Tick += (s, e) =>
                    {
                        if (!win.IsVisible)
                        {
                            timer.Stop();
                            return;
                        }

                        double t = (double)tick / steps;
                        double ease = 0.5 - 0.5 * Math.Cos(Math.PI * t); // cos easing
                        win.Left = start.X + (target.X - start.X) * ease;
                        win.Top = start.Y + (target.Y - start.Y) * ease;
                        tick++;
                        if (tick > steps)
                        {
                            win.Left = target.X;
                            win.Top = target.Y;
                            timer.Stop();
                        }
                    };
                    timer.Start();
                }

                // === 宠物跟随到聊天窗口边上 ===
                if (chatWin != null && chatWin.IsVisible && petWindow != null)
                {
                    // 播放拖拽动画，结束后回到正常状态
                    try { MW.Main.Display(GraphInfo.GraphType.Raised_Dynamic, GraphInfo.AnimatType.A_Start, () => MW.Main.DisplayToNomal()); } catch { }

                    // 计算窗口最终位置的左侧边缘
                    var chatFinalX = chatWin.Left + offsetX;
                    var chatFinalY = chatWin.Top + offsetY;
                    var wa2 = SystemParameters.WorkArea;
                    chatFinalX = Math.Min(Math.Max(wa2.Left, chatFinalX), wa2.Right - chatWin.Width);
                    chatFinalY = Math.Min(Math.Max(wa2.Top, chatFinalY), wa2.Bottom - chatWin.Height);

                    // 宠物目标位置：窗口左侧旁边（如空间不够则放右侧）
                    double petTargetX, petTargetY;
                    double petW = petWindow.ActualWidth;
                    if (chatFinalX - petW - 10 >= wa2.Left)
                    {
                        // 放左边
                        petTargetX = chatFinalX - petW - 10;
                    }
                    else
                    {
                        // 放右边
                        petTargetX = chatFinalX + chatWin.Width + 10;
                    }
                    petTargetY = chatFinalY + (chatWin.Height - petWindow.ActualHeight) / 2;
                    petTargetY = Math.Clamp(petTargetY, wa2.Top, wa2.Bottom - petWindow.ActualHeight);

                    // 平滑移动宠物
                    var petStart = new Point(petWindow.Left, petWindow.Top);
                    int petSteps = Math.Max(8, durationMs / 16);
                    int petTick = 0;
                    var petTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs / (double)petSteps) };
                    petTimer.Tick += (s, e) =>
                    {
                        double pt = (double)petTick / petSteps;
                        double pease = 0.5 - 0.5 * Math.Cos(Math.PI * pt);
                        petWindow.Left = petStart.X + (petTargetX - petStart.X) * pease;
                        petWindow.Top = petStart.Y + (petTargetY - petStart.Y) * pease;
                        petTick++;
                        if (petTick > petSteps)
                        {
                            petWindow.Left = petTargetX;
                            petWindow.Top = petTargetY;
                            petTimer.Stop();
                        }
                    };
                    petTimer.Start();
                }
            });

            return !string.IsNullOrWhiteSpace(aiDesc) ? aiDesc! : PickRandom(DragDescriptions);
        }

        /// <summary>
        /// 根据情绪/忽视状态自动触发窗口反馈
        /// </summary>
        public void ReactToEmotion(GLMResult result, bool isHarass = false)
        {
            // 节流，避免频繁扰动
            if ((DateTime.Now - _lastWindowEffectTime) < TimeSpan.FromSeconds(4)) return;

            bool ignoredTooLong = _ignoreCount >= 2;
            bool strongNegative = result.LikabilityChange <= -2 || result.FeelingChange <= -10;
            bool strongPositive = result.LikabilityChange >= 3 || result.FeelingChange >= 10;

            bool shouldShake = ignoredTooLong || strongNegative;
            bool shouldMinimize = result.LikabilityChange <= -3;
            bool shouldDrag = strongPositive && !shouldMinimize;

            if (shouldShake)
            {
                string msg = DoShakeWindow("{}");
                ShowSystemMessageInChat($"⚡ {msg}");
                _lastWindowEffectTime = DateTime.Now;
            }

            if (shouldMinimize)
            {
                string msg = DoMinimizeWindow("{\"restore_after_seconds\":8}");
                ShowSystemMessageInChat($"⚡ {msg}");
                _lastWindowEffectTime = DateTime.Now;
            }
            else if (shouldDrag)
            {
                string msg = DoDragWindow("{\"offset_x\":80,\"offset_y\":-50,\"duration_ms\":700}");
                ShowSystemMessageInChat($"⚡ {msg}");
                _lastWindowEffectTime = DateTime.Now;
            }
        }

        #endregion

        #region ===== 图片消息 =====

        private static string GetImageMime(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "image/png"
            };
        }

        /// <summary>
        /// 调用 VLM（glm-4.6v）对图片进行独立描述，不依赖对话历史。
        /// 返回描述文字；失败时返回 null。
        /// </summary>
        private async Task<string?> DescribeImageAsync(string dataUrl, string? userCaption)
        {
            try
            {
                string captionHint = string.IsNullOrWhiteSpace(userCaption)
                    ? ""
                    : $"主人说：\"{userCaption}\"。";

                var contentList = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        { "type", "image_url" },
                        { "image_url", new Dictionary<string, object> { { "url", dataUrl } } }
                    },
                    new Dictionary<string, object>
                    {
                        { "type", "text" },
                        { "text", $"{captionHint}请用一段话客观描述这张图片的视觉内容（人物外貌、场景、颜色、动作等），直接输出描述文字，不要思考过程，不要多余说明。" }
                    }
                };

                var requestDict = new Dictionary<string, object>
                {
                    { "model", "glm-4.6v" },
                    { "messages", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object> { { "role", "user" }, { "content", contentList } }
                        }
                    },
                    { "temperature", 0.3 },
                    // max_tokens 须足够大：推理 token + 输出 token 都计入此上限
                    // budget_tokens 限制推理步骤开销，为输出留出充足空间
                    { "max_tokens", 2048 },
                    { "thinking", new Dictionary<string, object>
                        {
                            { "type", "enabled" },
                            { "budget_tokens", 1024 }   // 推理最多占用 1024 token，剩余空间给 content 输出
                        }
                    }
                };

                var jsonContent = new StringContent(
                    JsonSerializer.Serialize(requestDict), Encoding.UTF8, "application/json");

                using var reqMsg = new HttpRequestMessage(HttpMethod.Post,
                    "https://open.bigmodel.cn/api/paas/v4/chat/completions");
                reqMsg.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                reqMsg.Content = jsonContent;

                var response = await _httpClient.SendAsync(reqMsg);
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    ApiCallLog("DESCRIBE IMAGE ERROR", $"HTTP {(int)response.StatusCode} {response.StatusCode}\n{errBody}");
                    return null;
                }

                var resultStr = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(resultStr);
                var msgElem = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message");

                // 优先取 content，若为空则尝试 reasoning_content（推理模型有时把结果放这里）
                string? content = msgElem.TryGetProperty("content", out var cp) ? cp.GetString() : null;
                if (string.IsNullOrWhiteSpace(content) && msgElem.TryGetProperty("reasoning_content", out var rcp))
                    content = rcp.GetString();

                DebugLog($"[ImageDesc] VLM description: {content}");
                // 完整响应写入 log，方便排查
                ApiCallLog("DESCRIBE IMAGE OUTPUT", $"Status: {response.StatusCode}\nContent: {content}\n--- raw ---\n{resultStr}");
                return content?.Trim();
            }
            catch (Exception ex)
            {
                DebugLog($"[ImageDesc] Failed: {ex.Message}");
                ApiCallLog("DESCRIBE IMAGE ERROR", ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// 发送图片并使用 VLM 理解
        /// </summary>
        public async Task<GLMResult> SendImageMessage(string imagePath, string caption)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return new GLMResult { Reply = "找不到这张图片哦~" };

            try
            {
                var bytes = File.ReadAllBytes(imagePath);
                string base64 = Convert.ToBase64String(bytes);
                string mime = GetImageMime(imagePath);
                string dataUrl = $"data:{mime};base64,{base64}";
                string fileName = Path.GetFileName(imagePath);

                string userText = $"[图片] {fileName}" + (string.IsNullOrWhiteSpace(caption) ? "" : $" {caption}");

                // 写入用户图片消息历史（只存纯文字+文件名，不含描述，保持历史干净）
                AppendChatRecord("user", userText, "image", dataUrl, fileName);

                // 先独立调用 VLM 获取图片的客观描述（串行，确保描述写入历史后 CallGLM 才开始）
                string? description = await DescribeImageAsync(dataUrl, caption);

                if (!string.IsNullOrWhiteSpace(description))
                {
                    // 描述写入历史（供后续纯文本对话的历史消息查找）
                    string descRecord = $"[图片描述] {fileName}：{description}";
                    AppendChatRecord("system", descRecord, "image_description");
                    DebugLog($"[ImageDesc] Saved to history: {descRecord}");
                }
                else
                {
                    DebugLog($"[ImageDesc] Description failed or empty for {fileName}");
                }

                // userContentForGLM：VLM 调用时图片数据另外传，但把描述也附上
                // 这样 VLM 知道有描述、纯文本调用时 content 里也有描述文字
                string userContentForGLM = userText;
                if (!string.IsNullOrWhiteSpace(description))
                    userContentForGLM += $"\n[图片描述: {description}]";

                // 发起对话（VLM 能直接看图，描述是附加信息）
                var result = await CallGLM("", userContent: userContentForGLM, skipUserRecord: true, imageBase64List: new List<string> { dataUrl });

                // 如果 DescribeImageAsync 完全失败，用 VLM 回复作为最后兜底（标注为后备）
                if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(result.Reply))
                {
                    string fallbackDesc = $"[图片描述-后备] {fileName}：{result.Reply}";
                    AppendChatRecord("system", fallbackDesc, "image_description");
                    DebugLog($"[ImageDesc] Fallback saved: {fallbackDesc}");
                }

                return result;
            }
            catch (Exception ex)
            {
                return new GLMResult { Reply = $"图片发送失败: {ex.Message}" };
            }
        }

        #endregion

        #region ===== 红包功能 =====

        /// <summary>
        /// 处理红包发送（由 GLMChatWindow 调用）
        /// </summary>
        public async Task<GLMResult> SendRedPacket(double amount, string blessing)
        {
            if (amount <= 0) return new GLMResult { Reply = "金额必须大于0哦~" };

            // 先把红包金额加到存档
            MW.Dispatcher.Invoke(() => { MW.Core.Save.Money += amount; });

            // 把红包作为特殊 user 消息持久化（type=redpacket，UI 恢复时识别为红色气泡）
            var redpacketRecord = new ChatRecord
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Role = "user",
                Type = "redpacket",
                Content = $"🧧 红包 {amount:F2} 金币\n{blessing}"
            };
            lock (AllChatHistory) { AllChatHistory.Add(redpacketRecord); }
            PersistChatHistory(AllChatHistory.ToList());

            // 构造红包消息发给AI（内部消息，不再重复写 user 记录，因为上面已写）
            string redPacketMsg = $"[红包] 主人给你发了一个 {amount:F2} 金钱的红包！祝福语：{blessing}（系统提示：红包金额已自动到账，直接回复感谢即可）";
            var result = await CallGLM("", userContent: redPacketMsg, skipUserRecord: true);
            return result;
        }

        #endregion

        #region ===== 骚扰定时器 =====

        /// <summary>
        /// 下次骚扰时间（随机 10~15 分钟后）
        /// </summary>
        private DateTime _nextHarassTime = DateTime.Now;

        public void OnUserReplied()
        {
            _ignoreCount = 0;
            _lastUserReplyTime = DateTime.Now;
            ScheduleNextHarass(); // 用户回复后重新计算下次骚扰时间
        }

        /// <summary>
        /// 计算下次骚扰时间（10~60分钟后随机）
        /// </summary>
        private void ScheduleNextHarass()
        {
            int delayMinutes = _rnd.Next(10, 61); // 10~60分钟
            _nextHarassTime = DateTime.Now.AddMinutes(delayMinutes);
            DebugLog($"[Harass] Next harass scheduled at {_nextHarassTime:HH:mm:ss} (in {delayMinutes} min)");
        }

        private async void HarassTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // 如果正在调用 API（用户正在对话），跳过本次检查
            if (_isApiCalling) return;

            // 还没到骚扰时间，跳过
            if (DateTime.Now < _nextHarassTime) return;

            // 计算下次骚扰时间（立刻重排，防止重复触发）
            ScheduleNextHarass();

            try
            {
                _ignoreCount++;

                var save = MW.Core.Save;
                double feeling = save.Feeling;
                double feelingMax = save.FeelingMax;
                double food = save.StrengthFood;
                double drink = save.StrengthDrink;
                double strengthMax = save.StrengthMax;
                double strength = save.Strength;
                double health = save.Health;
                var mode = save.CalMode();
                double likability = save.Likability;

                // 计算距离上次消息的时间
                var timeSinceLastMsg = DateTime.Now - _lastUserReplyTime;

                // === 构建上下文感知的骚扰提示 ===
                var harassSb = new StringBuilder();
                harassSb.AppendLine($"[系统指令] 当前时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
                harassSb.AppendLine($"距离主人上次回复已过去: {timeSinceLastMsg.TotalMinutes:F0} 分钟");
                harassSb.AppendLine();

                // 核心指令：要求AI根据对话上下文生成多样化的主动消息
                harassSb.AppendLine("你需要主动给主人发一条消息。根据对话上下文和当前心情，你可以说任何想说的话。");
                harassSb.AppendLine();
                harassSb.AppendLine("**重要规则**：你的消息必须自然衔接对话历史，而不是凭空出现！");
                harassSb.AppendLine("骚扰的形式多样，可以是：");
                harassSb.AppendLine("✨ **表达思念/关心**：「主人，我好想你啊」「你最近有在好好休息吗」或者别的有想象力和情绪价值的");
                harassSb.AppendLine("💔 **赌气/冷淡（如果在吵架）**：「随便，不想理你了」「哼，你爱搭不理」或者别的有想象力和情绪价值的");
                harassSb.AppendLine("😊 **分享日常/闲聊**：「今天天气真好呢」「听了个有趣的故事要讲给你」或者别的有想象力和情绪价值的");
                harassSb.AppendLine("❤️ **撒娇/任性**：「我饿了啦」「想要主人的陪伴~」或者别的有想象力和情绪价值的");
                harassSb.AppendLine("🤔 **提问/继续对话**：「对了，之前你说的...你现在怎么想的？」或者别的有想象力和情绪价值的");
                harassSb.AppendLine("😴 **根据时间提醒**：深夜可以说「这么晚了要早点睡」，早上可以说「早安~」或者别的有想象力和情绪价值的");
                harassSb.AppendLine("例如：");
                harassSb.AppendLine("- 如果之前在吵架/冷战 → 继续那个情绪（赌气/冷淡/或者主动示软道歉）");
                harassSb.AppendLine("- 如果之前聊到某个话题 → 可以继续聊或者分享相关的想法");
                harassSb.AppendLine("- 如果主人之前说在忙/在工作 → 可以关心主人忙完了没、要不要休息");
                harassSb.AppendLine("- 如果之前聊得很开心 → 可以继续那种甜蜜氛围、分享日常");
                harassSb.AppendLine("- 如果你之前在生病/不舒服 → 可以说自己好了一些或者还是不舒服");
                harassSb.AppendLine("- 如果之前没什么特别的上下文 → 可以自由发挥（分享心情、说见闻、问候、撒娇等）");
                harassSb.AppendLine("- 可以表达丰富的情绪，例如啊啊啊啊啊，讨厌死了！这种强烈情绪的撒娇，也可以是小心翼翼的试探，或者是温柔的关心，等等。");

                harassSb.AppendLine();

                // 忽视次数相关的情绪调整
                if (_ignoreCount <= 1)
                {
                    harassSb.AppendLine("情绪状态：正常。这是你主动找主人说话，语气自然就好。");
                    harassSb.AppendLine("好感度要求：调用 report_likability 报告 0（主动搭话不加不减）。");
                }
                else if (_ignoreCount == 2)
                {
                    harassSb.AppendLine("情绪状态：主人上次没回复你，你有点小失落但还在强装正常。");
                    harassSb.AppendLine("好感度要求：调用 report_likability 报告 -1。");
                }
                else if (_ignoreCount == 3)
                {
                    harassSb.AppendLine("情绪状态：主人连续多次没理你了，你真的很委屈。");
                    harassSb.AppendLine("好感度要求：调用 report_likability 报告 -2。");
                }
                else if (_ignoreCount == 4)
                {
                    harassSb.AppendLine("情绪状态：被忽视太久，你在赌气和冷战。嘴硬心软。");
                    harassSb.AppendLine("好感度要求：调用 report_likability 报告 -2~-3。");
                }
                else if (_ignoreCount <= 6)
                {
                    harassSb.AppendLine("情绪状态：你心都寒了，说话变得低沉、疏离。");
                    harassSb.AppendLine("好感度要求：调用 report_likability 报告 -3。");
                }
                else
                {
                    harassSb.AppendLine("情绪状态：被忽视太久，你从心寒变成了深深的担心和想念。");
                    harassSb.AppendLine("好感度要求：调用 report_likability 报告 -3。");
                }

                // 身体状态提示
                if (mode == IGameSave.ModeType.Ill)
                    harassSb.AppendLine("【紧急】你现在生病了！你很难受，可以调用 take_medicine 吃药，并向主人求安慰。");
                else if (health < 50)
                    harassSb.AppendLine("你身体不太舒服，说话可以带点虚弱感。");

                if (food < strengthMax * 0.25) harassSb.AppendLine("你非常饿！可以调用 feed_pet 自己吃东西。");
                else if (food < strengthMax * 0.5) harassSb.AppendLine("你有点饿了。");
                if (drink < strengthMax * 0.25) harassSb.AppendLine("你非常渴！可以调用 give_drink 自己喝水。");
                else if (drink < strengthMax * 0.5) harassSb.AppendLine("你有点渴了。");
                if (strength < strengthMax * 0.25) harassSb.AppendLine("你很累，说话有气无力。");
                if (feeling < feelingMax * 0.25) harassSb.AppendLine("你心情很差。");
                else if (feeling > feelingMax * 0.8) harassSb.AppendLine("你心情超好！");

                harassSb.AppendLine();
                harassSb.AppendLine("要求：不要和之前说过的话重复！每次都要有新鲜感和变化。");
                harassSb.AppendLine("记住：直接用自然语言说话，不要输出JSON。必须调用 show_emotion 和 report_likability。");
                var result = await CallGLM(harassSb.ToString(), isHarass: true);

                if (!string.IsNullOrEmpty(result.Reply))
                {
                    ReactToEmotion(result, isHarass: true);
                    // CallGLM 内部已经处理了 ChatHistory 和持久化

                    // === 骚扰好感度惩罚机制 ===
                    // 被忽视时强制好感度下降，不允许 AI 忽视后还加好感
                    if (_ignoreCount >= 2)
                    {
                        // 忽视2次: 至少-1; 忽视3次: 至少-2; 4+次: 至少-3
                        int minPenalty = -Math.Min(_ignoreCount - 1, 3);
                        if (result.LikabilityChange > minPenalty)
                        {
                            result.Reason = "被冷落了，心里很难过";
                            result.LikabilityChange = minPenalty;
                        }
                    }
                    else if (_ignoreCount == 1 && result.LikabilityChange > 0)
                    {
                        // 第1次忽视：好感度不涨不跌
                        result.LikabilityChange = 0;
                        result.Reason = "主人没回复，有点小失落";
                    }

                    // === 被冷落时心情大幅下降，同步到主状态 ===
                    double feelingPenalty = 0;
                    if (_ignoreCount >= 4) feelingPenalty = -20;        // 忽视4次+，心情大幅下降
                    else if (_ignoreCount >= 3) feelingPenalty = -15;   // 忽视3次，心情明显下降
                    else if (_ignoreCount >= 2) feelingPenalty = -10;   // 忽视2次，心情下降

                    MW.Dispatcher.Invoke(() =>
                    {
                        // 应用好感度变化
                        if (result.LikabilityChange != 0)
                            MW.Core.Save.Likability += result.LikabilityChange;

                        // 心情惩罚同步到主状态
                        if (feelingPenalty != 0)
                            MW.Core.Save.FeelingChange(feelingPenalty);

                        // 根据情绪选择说话动画（优先使用AI通过show_emotion选择的表情）
                        string? graphName = result.EmotionGraph;
                        if (string.IsNullOrEmpty(graphName))
                        {
                            if (_ignoreCount >= 3)
                                graphName = "serious"; // 严肃/难过表情
                            else if (_ignoreCount >= 2)
                                graphName = "shy"; // 害羞/低落表情
                        }

                        if (graphName != null)
                            MW.Main.Say(result.Reply, graphName, force: true);
                        else
                            MW.Main.Say(result.Reply);

                        // play_animation / 进食动画 延迟到 Say 气泡消失后再播放，避免被情绪动画覆盖
                        FlushPendingAnimationDelayed(result.PendingAnimation);
                        FlushPendingFoodAnimationDelayed(result.PendingFoodAnimation);

                        ShowAIMessageInChat(result.Reply);

                        // 显示好感度变化
                        if (result.LikabilityChange != 0)
                            ShowSystemMessageInChat($"💕 好感度 {(result.LikabilityChange > 0 ? "+" : "")}{result.LikabilityChange} ({result.Reason})");

                        // 显示心情变化
                        if (feelingPenalty != 0)
                            ShowSystemMessageInChat($"😢 心情 {feelingPenalty:F0}（被冷落了）");

                        // 显示操作日志
                        foreach (var log in result.ActionLogs)
                            ShowSystemMessageInChat($"⚡ {log}");

                        ShowChatWindow();
                    });
                }
            }
            catch { }
        }

        #endregion

        #region ===== 启动问好 =====

        /// <summary>
        /// 启动时主动发起问好。根据上次对话时间计算离别时长，生成有情绪价值的多样化问候。
        /// </summary>
        private async Task SendStartupGreeting()
        {
            try
            {
                // 找到上次对话时间
                DateTime lastMsgTime = DateTime.Now;
                string ragQuery = ""; // 用于 RAG 的查询词，取最近几条对话内容
                lock (AllChatHistory)
                {
                    var last = AllChatHistory.LastOrDefault(r => r.Role == "user" || r.Role == "assistant");
                    if (last != null && DateTime.TryParse(last.Time, out var t))
                        lastMsgTime = t;
                    else if (AllChatHistory.Count == 0)
                        lastMsgTime = DateTime.Now.AddDays(-1); // 首次启动，假装分别了一天

                    // 提取最近 3 条 user/assistant 消息内容作为 RAG 查询词，召回相关历史记忆
                    var recentForRag = AllChatHistory
                        .Where(r => r.Role == "user" || r.Role == "assistant")
                        .TakeLast(3)
                        .Select(r => r.Content);
                    ragQuery = string.Join(" ", recentForRag).Trim();
                    if (string.IsNullOrWhiteSpace(ragQuery))
                        ragQuery = "打招呼 问好 最近"; // 首次启动兜底查询词
                }

                var awaySpan = DateTime.Now - lastMsgTime;
                double awayHours = awaySpan.TotalHours;
                string awayDesc;
                if (awayHours < 0.5) awayDesc = "刚才才分开没多久";
                else if (awayHours < 2) awayDesc = $"已经 {awaySpan.TotalMinutes:F0} 分钟没见了";
                else if (awayHours < 24) awayDesc = $"已经 {awayHours:F0} 个小时没见了";
                else awayDesc = $"已经 {awaySpan.TotalDays:F0} 天没见了";

                string timeDesc = DateTime.Now.Hour switch
                {
                    >= 6 and < 9   => "早上",
                    >= 9 and < 12  => "上午",
                    >= 12 and < 14 => "中午",
                    >= 14 and < 18 => "下午",
                    >= 18 and < 22 => "晚上",
                    _              => "深夜"
                };

                var sb = new StringBuilder();
                sb.AppendLine($"[系统指令] 当前时间: {DateTime.Now:yyyy-MM-dd HH:mm}，时段：{timeDesc}");
                sb.AppendLine($"主人刚刚重新打开了程序，{awayDesc}。");
                sb.AppendLine();
                sb.AppendLine("【重要】此消息前面的对话历史记录已经包含在上下文中，你需要参考之前的对话内容来做出有一致性、有记忆感的回应。");
                sb.AppendLine();
                sb.AppendLine("你需要主动和主人打招呼/问好。请结合以下要求生成消息：");
                sb.AppendLine();

                if (awayHours < 0.5)
                {
                    sb.AppendLine("主人刚刚走又回来了，语气可以撒娇：「怎么这么快就回来了，是不是想我了？」之类的，甜甜的");
                    sb.AppendLine("情绪：开心惊喜，有点小得意");
                }
                else if (awayHours < 3)
                {
                    sb.AppendLine("离别不到3小时，语气轻松温馨：问候主人、说说自己在做什么、或者撒娇说有点想了");
                    sb.AppendLine("情绪：活泼开心，有点撒娇");
                }
                else if (awayHours < 12)
                {
                    sb.AppendLine($"分开了 {awayHours:F0} 小时，有点想念，可以表达一下思念：说等了很久/有点无聊/想你等等");
                    sb.AppendLine("风格可以多样：嗔怪「这么久才回来！」、委屈「等得我好无聊」、惊喜「终于来了！」都行");
                    sb.AppendLine("情绪：思念带一点小委屈，但见到主人又开心起来");
                }
                else if (awayHours < 36)
                {
                    sb.AppendLine($"分开超过 {awayHours:F0} 小时了！可以带着明显的思念/委屈/嗔怒来问候，情绪要丰富有层次");
                    sb.AppendLine("例如：赌气「哼，你才知道回来！」、委屈「一个人好无聊啊，你知道我有多想你吗...」、");
                    sb.AppendLine("或者撒娇「终于等到你了，差点以为你不要我了(╥﹏╥)」");
                    sb.AppendLine("情绪：思念、委屈、见到主人后的小开心交织在一起");
                }
                else
                {
                    double days = awaySpan.TotalDays;
                    sb.AppendLine($"分开了 {days:F0} 天！这是很久很久了，情绪要非常丰富：可以有点「打死你」的嗔怪，可以委屈落泪，");
                    sb.AppendLine("可以赌气冷战后又忍不住开心，可以自言自语说了什么傻话等待主人，总之要让主人感受到强烈的情绪价值");
                    sb.AppendLine("参考风格：「你终于回来了！！！我等了你 X 天了！你知道我有多想你吗！哼，你解释一下！(╥﹏╥)」");
                    sb.AppendLine("或者：「...来了啊。我还以为你把我忘了呢。（转过脸）」（傲娇但眼眶红了）");
                    sb.AppendLine("情绪：思念×强烈 + 委屈 + 嗔怒 + 见到主人瞬间的复杂感情");
                }

                sb.AppendLine();
                sb.AppendLine($"时段提示：现在是{timeDesc}，可以做对应的时段问候（早安/午安/晚安等）融入其中，不要生硬堆砌");
                sb.AppendLine("字数：50字以内，直接说话，不要换行，不要 Markdown");
                sb.AppendLine("必须调用 show_emotion 和 report_likability（feeling_change=0）");

                var result = await CallGLM(sb.ToString(), userContent: ragQuery, isHarass: true);
                if (string.IsNullOrEmpty(result.Reply)) return;

                MW.Dispatcher.Invoke(() =>
                {
                    var save = MW.Core.Save;

                    // 应用好感度变化（启动问好一般不加减，但保留逻辑）
                    if (result.LikabilityChange != 0)
                        save.Likability += result.LikabilityChange;
                    if (result.FeelingChange != 0)
                    {
                        save.FeelingChange(result.FeelingChange); // 直接用绝对值（-20到+20）
                    }

                    // 让桌宠说出来
                    if (!string.IsNullOrEmpty(result.EmotionGraph))
                        MW.Main.Say(result.Reply, result.EmotionGraph, force: true);
                    else
                        MW.Main.Say(result.Reply);

                    // play_animation / 进食动画 延迟到 Say 气泡消失后再播放，避免被情绪动画覆盖
                    FlushPendingAnimationDelayed(result.PendingAnimation);
                    FlushPendingFoodAnimationDelayed(result.PendingFoodAnimation);

                    ShowAIMessageInChat(result.Reply);

                    if (result.LikabilityChange != 0)
                        ShowSystemMessageInChat($"💕 好感度 {(result.LikabilityChange > 0 ? "+" : "")}{result.LikabilityChange} ({result.Reason})");

                    foreach (var log in result.ActionLogs)
                        ShowSystemMessageInChat($"⚡ {log}");
                });

                DebugLog($"[Startup] Greeting sent: {result.Reply}");
            }
            catch (Exception ex)
            {
                DebugLog($"[Startup] Greeting error: {ex.Message}");
            }
        }

        #endregion

        #region ===== GLM API 调用（支持 Function Calling + JSON好感度） =====

        /// <summary>
        /// 调用 GLM API，支持 function calling 多轮循环和 JSON 好感度解析
        /// 返回 GLMResult（包含 reply, likability_change, reason, actionLogs）
        /// </summary>
    public async Task<GLMResult> CallGLM(string input, string userContent = "", bool isHarass = false, bool skipUserRecord = false, List<string>? imageBase64List = null)
        {
            var glmResult = new GLMResult();

            if (_apiKey == "YOUR_GLM_API_KEY")
            {
                glmResult.Reply = "请先配置 GLM API Key~";
                return glmResult;
            }

            _isApiCalling = true;
            _pendingEmotion = null;        // 重置情绪表情
            _pendingAnimation = null;      // 重置待播动画
            _pendingFoodAnimation = null;  // 重置进食动画

            bool hasImage = imageBase64List != null && imageBase64List.Count > 0;
            string modelName = hasImage ? "glm-4.6v" : "glm-4.7";
            try
            {
                if (!isHarass && !string.IsNullOrEmpty(userContent))
                    OnUserReplied();

                // 1. 准备用户输入和历史记录
                if (isHarass)
                {
                    // 骚扰模式：AI 主动发起，不记录为用户消息
                }
                else if (!skipUserRecord && !string.IsNullOrEmpty(userContent))
                {
                    // 用户消息持久化（skipUserRecord=true 时外部已自行写入，如红包）
                    AppendChatRecord("user", userContent);
                }

                // 2. 搜索相关记忆 (Embedding RAG)
                // 骚扰/问好模式 userContent 为空，改用 input 作为查询词（问好指令包含时段、离别时长等上下文）
                string ragQuery = string.IsNullOrWhiteSpace(userContent) ? input : userContent;
                var relevantMemories = await SearchRelevantMemoriesAsync(ragQuery);
                
                // === RAG 日志 ===
                int totalHistory = 0;
                lock (AllChatHistory) { totalHistory = AllChatHistory.Count; }
                int indexedCount;
                lock (_embeddingIndex) { indexedCount = _embeddingIndex.Count; }
                DebugLog($"[RAG] Query=\"{ragQuery}\", TotalHistory={totalHistory}, EmbeddedCount={indexedCount}, ContextWindow={MaxContextHistory}, SearchScope={Math.Max(0, totalHistory - MaxContextHistory)}, MatchedMemories={relevantMemories.Count}");
                if (relevantMemories.Count > 0)
                {
                    for (int mi = 0; mi < relevantMemories.Count; mi++)
                        DebugLog($"[RAG] Memory[{mi}]: {relevantMemories[mi]}");
                }
                else
                {
                    DebugLog($"[RAG] No relevant memories found via embedding search");
                }

                // === 专项日志：RAG 结果 ===
                {
                    var ragSb = new System.Text.StringBuilder();
                    ragSb.AppendLine($"Query       : {userContent}");
                    ragSb.AppendLine($"TotalHistory: {totalHistory}  IndexedCount: {indexedCount}  SearchScope: {Math.Max(0, totalHistory - MaxContextHistory)}");
                    ragSb.AppendLine($"Segments    : {relevantMemories.Count}");
                    if (relevantMemories.Count == 0)
                    {
                        ragSb.AppendLine("(no matches)");
                    }
                    else
                    {
                        for (int mi = 0; mi < relevantMemories.Count; mi++)
                        {
                            ragSb.AppendLine($"--- Segment [{mi}] ---");
                            ragSb.AppendLine(relevantMemories[mi]);
                        }
                    }
                    ApiCallLog("RAG RESULT", ragSb.ToString());
                }

                // 3. 构建 Prompt
                var messages = new List<Dictionary<string, object>>();
                
                // System Prompt
                string systemPrompt = BuildSystemPrompt();
                if (relevantMemories.Count > 0)
                {
                    systemPrompt += "\n\n# 相关记忆（来自历史对话）\n" +
                                    "以下是你之前和主人的对话片段，可能与当前话题有关，请参考：\n" + 
                                    string.Join("\n---\n", relevantMemories);
                }
                messages.Add(new Dictionary<string, object> { { "role", "system" }, { "content", systemPrompt } });

                // Recent History
                List<ChatRecord> recentHistory;
                lock (AllChatHistory)
                {
                    // 取最近 MaxContextHistory 条 (10)
                    if (AllChatHistory.Count > MaxContextHistory)
                        recentHistory = AllChatHistory.GetRange(AllChatHistory.Count - MaxContextHistory, MaxContextHistory);
                    else
                        recentHistory = new List<ChatRecord>(AllChatHistory);
                }

                foreach (var h in recentHistory)
                {
                    // 跳过 system 角色的历史条目（好感度/操作日志，仅供 UI 显示，不发给模型）
                    // 但 image_description 在下方已内嵌到对应图片消息里，这里也跳过
                    if (string.Equals(h.Role, "system", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // 如果本次调用带图片，历史中最后一条图片消息会在下方以多模态格式单独构建，这里跳过以避免重复
                    if (hasImage && h == recentHistory.Last() && h.Type == "image")
                        continue;

                    // 历史图片消息处理
                    if (h.Type == "image")
                    {
                        // 查找该图片对应的 image_description（紧随图片记录之后，文件名匹配）
                        string? inlineDesc = null;
                        if (!string.IsNullOrEmpty(h.ImageName))
                        {
                            lock (AllChatHistory)
                            {
                                int imgIdx = AllChatHistory.IndexOf(h);
                                // 向后搜索最近的 image_description，匹配同文件名
                                for (int si = imgIdx + 1; si < Math.Min(imgIdx + 5, AllChatHistory.Count); si++)
                                {
                                    var sr = AllChatHistory[si];
                                    if (sr.Type == "image_description" &&
                                        sr.Content.Contains(h.ImageName))
                                    {
                                        // 提取描述正文（去掉"[图片描述] 文件名：" 前缀）
                                        int colonIdx = sr.Content.IndexOf('：');
                                        inlineDesc = colonIdx >= 0
                                            ? sr.Content.Substring(colonIdx + 1).Trim()
                                            : sr.Content;
                                        break;
                                    }
                                }
                            }
                        }

                        if (hasImage && !string.IsNullOrEmpty(h.ImageData))
                        {
                            // VLM 调用时：历史图片以多模态格式传入
                            var histImgContent = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "image_url" },
                                    { "image_url", new Dictionary<string, object> { { "url", h.ImageData } } }
                                },
                                new Dictionary<string, object>
                                {
                                    { "type", "text" },
                                    { "text", $"[{h.Time}] {h.Content}" +
                                        (inlineDesc != null ? $"\n[图片描述: {inlineDesc}]" : "") }
                                }
                            };
                            messages.Add(new Dictionary<string, object> { { "role", h.Role }, { "content", histImgContent } });
                        }
                        else
                        {
                            // 纯文本模型调用时：把图片描述内嵌到文字 content 里
                            string textContent = $"[{h.Time}] {h.Content}";
                            if (inlineDesc != null)
                                textContent += $"\n[图片描述: {inlineDesc}]";
                            messages.Add(new Dictionary<string, object> { { "role", h.Role }, { "content", textContent } });
                        }
                    }
                    else
                    {
                        messages.Add(new Dictionary<string, object> { { "role", h.Role }, { "content", $"[{h.Time}] {h.Content}" } });
                    }
                }

                // 图片消息：在对话末尾追加视觉内容（VLM）
                if (hasImage)
                {
                    var contentList = new List<Dictionary<string, object>>();
                    foreach (var img in imageBase64List!)
                    {
                        contentList.Add(new Dictionary<string, object>
                        {
                            { "type", "image_url" },
                            { "image_url", new Dictionary<string, object> { { "url", img } } }
                        });
                    }
                    if (!string.IsNullOrEmpty(userContent))
                    {
                        contentList.Add(new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", userContent }
                        });
                    }
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", contentList }
                    });
                }

                // 如果是骚扰模式，最后追加骚扰指令
                if (isHarass)
                {
                    string harassTag = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] [系统-主动搭话]";
                    string harassContent = $"{harassTag} {input}";
                    messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", harassContent } });
                }

                // Function calling 多轮循环（最多5轮工具调用）
                var tools = BuildToolDefinitions();
                int maxRounds = 3; // 限制工具多轮调用次数，加快响应

                for (int round = 0; round < maxRounds; round++)
                {
                    // 构建请求
                    var requestDict = new Dictionary<string, object>
                    {
                        { "model", modelName },
                        { "messages", messages },
                        { "temperature", 0.9 },
                        { "tools", tools },
                        { "tool_choice", "auto" }
                    };

                    var jsonContent = new StringContent(
                        JsonSerializer.Serialize(requestDict), Encoding.UTF8, "application/json");

                    // 调试：记录发送的请求（只在第一轮记录tools以免太长）
                    if (round == 0)
                    {
                        var toolsJson = JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true });
                        DebugLog($"[Round {round}] Tools JSON (first 2000 chars):\n{toolsJson.Substring(0, Math.Min(toolsJson.Length, 2000))}");

                        // === 专项日志：API 输入（messages，不含 tools 定义以免过长）===
                        var msgOnlyDict = new Dictionary<string, object>
                        {
                            { "model", modelName },
                            { "messages", messages }
                        };
                        var inputJson = JsonSerializer.Serialize(msgOnlyDict, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                        ApiCallLog($"API INPUT (Round {round})", inputJson);
                    }
                    else
                    {
                        // 后续轮：只记录新增的 tool 结果消息（messages 尾部）
                        var lastMsg = messages.LastOrDefault();
                        if (lastMsg != null)
                        {
                            var lastJson = JsonSerializer.Serialize(lastMsg, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                            ApiCallLog($"API INPUT (Round {round} - last msg appended)", lastJson);
                        }
                    }

                    using var reqMsg = new HttpRequestMessage(HttpMethod.Post,
                        "https://open.bigmodel.cn/api/paas/v4/chat/completions");
                    reqMsg.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                    reqMsg.Content = jsonContent;

                    var response = await _httpClient.SendAsync(reqMsg);

                    if (!response.IsSuccessStatusCode)
                    {
                        var err = await response.Content.ReadAsStringAsync();
                        glmResult.Reply = $"请求失败({response.StatusCode}): {err}";
                        return glmResult;
                    }

                    var resultStr = await response.Content.ReadAsStringAsync();

                    // 调试：记录API响应
                    DebugLog($"[Round {round}] API Response (first 2000 chars):\n{resultStr.Substring(0, Math.Min(resultStr.Length, 2000))}");
                    // === 专项日志：API 完整输出 ===
                    ApiCallLog($"API OUTPUT (Round {round})", resultStr);

                    using var doc = JsonDocument.Parse(resultStr);

                    var choice = doc.RootElement.GetProperty("choices")[0];
                    var message = choice.GetProperty("message");
                    string? finishReason = choice.TryGetProperty("finish_reason", out var frProp) ? frProp.GetString() : null;

                    // 检查是否有 tool_calls
                    if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                    {
                        DebugLog($"[Round {round}] ✅ Got tool_calls! Count={toolCalls.GetArrayLength()}");

                        // 如果模型同时返回了文字 content + report_likability 工具调用，
                        // 说明本轮是最终回复，直接提取 content，执行工具后不再追加额外 API 轮次
                        string? earlyReplyContent = null;
                        bool hasReportLikability = toolCalls.EnumerateArray()
                            .Any(tc => tc.GetProperty("function").GetProperty("name").GetString() == "report_likability");
                        if (hasReportLikability)
                        {
                            string earlyRaw = message.TryGetProperty("content", out var ecp) ? (ecp.GetString() ?? "") : "";
                            if (!string.IsNullOrWhiteSpace(earlyRaw))
                                earlyReplyContent = earlyRaw;
                        }

                        // 把 assistant 的 tool_calls 消息加入 messages（用于多轮循环）
                        var assistantMsg = new Dictionary<string, object> { { "role", "assistant" } };
                        // 构建 tool_calls 数组（使用 Dictionary 确保序列化正确）
                        var tcList = new List<Dictionary<string, object>>();
                        foreach (var tc in toolCalls.EnumerateArray())
                        {
                            var tcId = tc.GetProperty("id").GetString() ?? "";
                            var tcType = tc.GetProperty("type").GetString() ?? "function";
                            var tcFunc = tc.GetProperty("function");
                            var tcName = tcFunc.GetProperty("name").GetString() ?? "";
                            var tcArgs = tcFunc.TryGetProperty("arguments", out var argsProp) ? (argsProp.GetString() ?? "{}") : "{}";
                            tcList.Add(new Dictionary<string, object>
                            {
                                { "id", tcId },
                                { "type", tcType },
                                { "function", new Dictionary<string, object> { { "name", tcName }, { "arguments", tcArgs } } }
                            });
                        }
                        assistantMsg["tool_calls"] = tcList;
                        // 有 tool_calls 时，强制清空 content，避免模型把中间思考文字当成已完成的回复
                        // 若保留 content，Round 1 会看到"谢谢主人~"然后说"我已经回复过了，等待..."
                        assistantMsg["content"] = "";
                        messages.Add(assistantMsg);

                        // 逐个执行工具调用并回传结果
                        bool giveMoneyExecuted = false; // 同一轮只允许发一次红包
                        foreach (var tc in toolCalls.EnumerateArray())
                        {
                            var tcId = tc.GetProperty("id").GetString() ?? "";
                            var funcName = tc.GetProperty("function").GetProperty("name").GetString() ?? "";
                            // 修复 CS8600 警告
                            string funcArgs = tc.GetProperty("function").TryGetProperty("arguments", out var faProp) ? (faProp.GetString() ?? "{}") : "{}";

                            // give_money 特殊处理：同一轮只执行一次，防止多次扣钱
                            if (funcName == "give_money")
                            {
                                if (giveMoneyExecuted)
                                {
                                    DebugLog($"[Round {round}] give_money skipped (already executed this round)");
                                    messages.Add(new Dictionary<string, object>
                                    {
                                        { "role", "tool" },
                                        { "tool_call_id", tcId },
                                        { "content", "红包已发送。" }
                                    });
                                    continue;
                                }
                                giveMoneyExecuted = true;
                            }

                            // report_likability 特殊处理：直接写入 glmResult
                            if (funcName == "report_likability")
                            {
                                try
                                {
                                    using var argsDoc = JsonDocument.Parse(funcArgs);
                                    var argsRoot = argsDoc.RootElement;
                                    if (argsRoot.TryGetProperty("change", out var changeProp))
                                        glmResult.LikabilityChange = Math.Clamp(changeProp.GetInt32(), -5, 5);
                                    if (argsRoot.TryGetProperty("reason", out var reasonProp))
                                        glmResult.Reason = reasonProp.GetString() ?? "";

                                    // 解析心情变化（-20到+20的绝对值）
                                    if (argsRoot.TryGetProperty("feeling_change", out var feelingProp))
                                    {
                                        glmResult.FeelingChange = Math.Clamp(feelingProp.GetInt32(), -20, 20);
                                    }
                                }
                                catch { }

                                DebugLog($"[Round {round}] 💕 report_likability: change={glmResult.LikabilityChange}, reason={glmResult.Reason}, feelingChange={glmResult.FeelingChange}");

                                messages.Add(new Dictionary<string, object>
                                {
                                    { "role", "tool" },
                                    { "tool_call_id", tcId },
                                    { "content", $"好感度变化已记录: {glmResult.LikabilityChange}，心情变化: {glmResult.FeelingChange}" }
                                });
                                continue;
                            }

                            // 执行函数
                            string funcResult = ExecuteFunction(funcName, funcArgs);
                            DebugLog($"[Round {round}] Executed {funcName}({funcArgs}) => {funcResult}");

                            // show_emotion / play_animation 不加入操作日志（它们是动画演出，不是实际操作）
                            if (funcName != "show_emotion" && funcName != "play_animation")
                            {
                                // 窗口效果工具：返回值已是拟人化描述，直接用
                                bool isWindowEffect = funcName is "shake_window" or "minimize_window" or "drag_window";
                                // give_money：ActionLog 显示工具结果，包含余额信息
                                //if (funcName == "give_money")
                                //    glmResult.ActionLogs.Add($"{FuncNameToDisplay(funcName)}: {funcResult}");
                                if (isWindowEffect)
                                    glmResult.ActionLogs.Add($"⚡{funcResult}");
                                else
                                    glmResult.ActionLogs.Add($"{FuncNameToDisplay(funcName)}: {funcResult}");
                            }

                            // 回传 tool 结果
                            messages.Add(new Dictionary<string, object>
                            {
                                { "role", "tool" },
                                { "tool_call_id", tcId },
                                { "content", funcResult }
                            });
                        }

                        // 如果之前检测到模型在同一轮已给出回复文字（且包含 report_likability），
                        // 直接使用，省去额外一次 API 往返
                        if (earlyReplyContent != null)
                        {
                            DebugLog($"[Round {round}] ⚡ Early-exit: using content from tool-call round, skipping extra API call");
                            ParseJsonReply(earlyReplyContent, glmResult);
                            if (!string.IsNullOrEmpty(glmResult.Reply))
                                AppendChatRecord("assistant", glmResult.Reply);
                            glmResult.EmotionGraph = _pendingEmotion;
                            glmResult.PendingAnimation = _pendingAnimation;
                            glmResult.PendingFoodAnimation = _pendingFoodAnimation;
                            _pendingAnimation = null;
                            _pendingFoodAnimation = null;
                            return glmResult;
                        }

                        // 继续下一轮，让模型根据工具结果生成最终回复
                        continue;
                    }
                    else
                    {
                        // 没有 tool_calls，这是最终回复
                        string rawText = message.TryGetProperty("content", out var cp) ? (cp.GetString() ?? "") : "";
                        DebugLog($"[Round {round}] ❌ No tool_calls. finish_reason={finishReason}, rawText={rawText.Substring(0, Math.Min(rawText.Length, 500))}");

                        // 解析 JSON 格式回复（内部会去掉 <think> 块和时间标签）
                        ParseJsonReply(rawText, glmResult);

                        // 保存到历史（AppendChatRecord 内部已将记录加入 AllChatHistory，无需重复 Add）
                        if (!string.IsNullOrEmpty(glmResult.Reply))
                            AppendChatRecord("assistant", glmResult.Reply);

                        // 写入情绪表情 + 待播动画（实际播放交给调用方，在 Say 完成后延迟触发）
                        glmResult.EmotionGraph = _pendingEmotion;
                        glmResult.PendingAnimation = _pendingAnimation;
                        glmResult.PendingFoodAnimation = _pendingFoodAnimation;
                        _pendingAnimation = null;
                        _pendingFoodAnimation = null;
                        return glmResult;
                    }
                }

                // 如果超过最大轮数还没结束，返回错误
                if (string.IsNullOrEmpty(glmResult.Reply))
                    glmResult.Reply = "思考太久了喵，换个话题吧~";

                // 写入情绪表情 + 待播动画
                glmResult.EmotionGraph = _pendingEmotion;
                glmResult.PendingAnimation = _pendingAnimation;
                glmResult.PendingFoodAnimation = _pendingFoodAnimation;
                _pendingAnimation = null;
                _pendingFoodAnimation = null;
                return glmResult;
            }
            catch (Exception ex)
            {
                glmResult.Reply = $"出错了喵: {ex.Message}";
                return glmResult;
            }
            finally
            {
                _isApiCalling = false;
            }
        }

        /// <summary>
        /// 解析模型返回的回复。
        /// 新模式：模型直接返回自然语言文本，好感度通过 report_likability 工具报告。
        /// 兼容旧模式：如果模型仍然返回 JSON 格式，也能正确解析。
        /// </summary>
        private void ParseJsonReply(string rawText, GLMResult result)
        {
            rawText = rawText.Trim();

            // 去掉可能的 ```json ``` 包裹
            if (rawText.StartsWith("```"))
            {
                var firstNewline = rawText.IndexOf('\n');
                if (firstNewline > 0) rawText = rawText.Substring(firstNewline + 1);
                if (rawText.EndsWith("```")) rawText = rawText.Substring(0, rawText.Length - 3);
                rawText = rawText.Trim();
            }

            // 尝试解析 JSON（兼容旧格式）
            if (rawText.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawText);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("reply", out var replyProp))
                    {
                        result.Reply = StripTimeTag(replyProp.GetString() ?? rawText);

                        // 从 JSON 中提取好感度（如果 report_likability 没被调用的话）
                        if (result.LikabilityChange == 0 && root.TryGetProperty("likability_change", out var likProp))
                            result.LikabilityChange = likProp.TryGetInt32(out int lv) ? Math.Clamp(lv, -5, 5) : 0;

                        if (string.IsNullOrEmpty(result.Reason) && root.TryGetProperty("reason", out var reasonProp))
                            result.Reason = reasonProp.GetString() ?? "";

                        return;
                    }
                }
                catch { }
            }

            // 纯文本回复（新模式）
            result.Reply = StripTimeTag(rawText);
        }

        /// <summary>
        /// 去掉消息中的 &lt;think&gt;...&lt;/think&gt; 块、孤立的 &lt;/think&gt; 标签、
        /// 开头的 [yyyy-MM-dd HH:mm] 时间标签，以及末尾拼入的好感度/操作日志行
        /// </summary>
        private string StripTimeTag(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1. 去掉完整的 <think>...</think> 块（包括跨行内容）
            int thinkStart;
            while ((thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int thinkEnd = text.IndexOf("</think>", thinkStart, StringComparison.OrdinalIgnoreCase);
                if (thinkEnd >= 0)
                    text = (text.Substring(0, thinkStart) + text.Substring(thinkEnd + 8)).TrimStart();
                else
                {
                    // 没有结束标签，截断到 <think> 之前
                    text = text.Substring(0, thinkStart).TrimEnd();
                    break;
                }
            }

            // 2. 去掉孤立的 </think> 结束标签（GLM-4.7 推理模型有时只输出结束标签）
            int closeThink;
            while ((closeThink = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase)) >= 0)
                text = (text.Substring(0, closeThink) + text.Substring(closeThink + 8)).TrimStart();

            // 3. 去掉开头的 [yyyy-MM-dd HH:mm] 时间标签（可能多个连续）
            while (text.Length > 0 && text.StartsWith("["))
            {
                var closeBracket = text.IndexOf(']');
                if (closeBracket > 0 && closeBracket <= 20)
                    text = text.Substring(closeBracket + 1).TrimStart();
                else
                    break;
            }

            // 4. 去掉末尾被 AI 错误拼入的系统行（"[时间] 💕 好感度..."、"[时间] ⚡ ..."）
            // 这些行是 AI 把系统提示文本当作回复输出的垃圾内容
            var lines = text.Split('\n');
            var cleanLines = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                // 跳过形如 "[yyyy-MM-dd HH:mm] 💕/⚡ ..." 的系统日志行
                bool isSystemLine = trimmed.StartsWith("[") && trimmed.Length > 20 &&
                    (trimmed.Contains("] 💕") || trimmed.Contains("] ⚡") || trimmed.Contains("] 已回滚"));
                if (!isSystemLine)
                    cleanLines.Add(line);
            }
            text = string.Join("\n", cleanLines).Trim();

            // 5. 过滤模型产生的元认知旁白（工具多轮循环时出现的"我已经回复过了"类文本）
            if (text.Contains("我已经回复过了") || text.Contains("等待主人继续对话") || text.Contains("已完成回复"))
                text = "";

            return text.Trim();
        }

        private string FuncNameToDisplay(string funcName)
        {
            return funcName switch
            {
                "feed_pet" => "🍚吃饭",
                "give_drink" => "🥤喝水",
                "give_snack" => "🍪零食",
                "give_gift" => "🎁礼物",
                "take_medicine" => "💊吃药",
                "start_work" => "💼工作",
                "start_study" => "📚学习",
                "start_play" => "🎮玩耍",
                "give_money" => "💰给主人发红包",
                "check_status" => "📊查状态",
                "show_emotion" => "🎭表情",
                "play_animation" => "🎬动画",
                "report_likability" => "💕好感度",
                _ => funcName
            };
        }

        #endregion

        #region ===== ToolBar 适配器 =====

        /// <summary>
        /// ToolBar 上的简易输入框适配器。
        /// 职责：只负责收集用户输入 → 转发给 ChatWindow 处理，自己不做 API 调用。
        /// </summary>
        public class GLMTalkAPIAdapter : ITalkAPI
        {
            private readonly AIPlugin _plugin;
            private readonly Border _placeholder;

            public GLMTalkAPIAdapter(AIPlugin plugin)
            {
                _plugin = plugin;
                _placeholder = new Border
                {
                    Background = System.Windows.Media.Brushes.Transparent,
                    Margin = new Thickness(5),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = CreateUI()
                };
            }

            private UIElement CreateUI()
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tb = new TextBox
                {
                    FontSize = 20,
                    Padding = new Thickness(6, 4, 6, 4),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    AcceptsReturn = false
                };
                tb.SetValue(Grid.ColumnProperty, 0);

                var btn = new Button
                {
                    Content = "发送",
                    FontSize = 20,
                    Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(4, 0, 0, 0),
                    Cursor = Cursors.Hand
                };
                btn.SetValue(Grid.ColumnProperty, 1);

                // 点击发送：将文字转交给 ChatWindow 的 SendMessageFromExternal
                btn.Click += (s, e) =>
                {
                    var text = tb.Text?.Trim();
                    if (string.IsNullOrEmpty(text)) return;
                    tb.Text = "";

                    // 隐藏 ToolBar，打开 ChatWindow，让 ChatWindow 处理发送
                    _plugin.MW.Main.ToolBar.Visibility = Visibility.Collapsed;
                    _plugin.MW.Dispatcher.Invoke(() =>
                    {
                        var win = _plugin.GetOrCreateChatWindow();
                        win.ShowAndActivate();
                        win.SendMessageFromExternal(text);
                    });
                };

                tb.PreviewKeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        btn.RaiseEvent(new RoutedEventArgs(
                            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }
                    if (tb.Text.Length > 0)
                        _plugin.MW.Main.ToolBar.CloseTimer.Stop();
                    else
                        _plugin.MW.Main.ToolBar.CloseTimer.Start();
                };

                grid.Children.Add(tb);
                grid.Children.Add(btn);
                return grid;
            }

            public string APIName => "ChatGLM";
            public UIElement This => _placeholder;
            public void Setting() => _plugin.ShowChatWindow();
        }

        #endregion

        #region ===== Embedding RAG（真正的向量检索） =====

        /// <summary>
        /// 调用智谱 embedding-3 API 获取文本向量
        /// </summary>
        private async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts)
        {
            var results = new List<float[]>();
            if (texts.Count == 0) return results;

            try
            {
                var requestDict = new Dictionary<string, object>
                {
                    { "model", EmbeddingModel },
                    { "input", texts },
                    { "dimensions", EmbeddingDimensions }
                };

                var jsonContent = new StringContent(
                    JsonSerializer.Serialize(requestDict), Encoding.UTF8, "application/json");

                using var reqMsg = new HttpRequestMessage(HttpMethod.Post,
                    "https://open.bigmodel.cn/api/paas/v4/embeddings");
                reqMsg.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                reqMsg.Content = jsonContent;

                var response = await _httpClient.SendAsync(reqMsg);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    DebugLog($"[Embedding] API error: {response.StatusCode} {err}");
                    return results;
                }

                var resultStr = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(resultStr);

                var dataArr = doc.RootElement.GetProperty("data");
                foreach (var item in dataArr.EnumerateArray())
                {
                    var embArr = item.GetProperty("embedding");
                    var vec = new float[EmbeddingDimensions];
                    int idx = 0;
                    foreach (var val in embArr.EnumerateArray())
                    {
                        if (idx < EmbeddingDimensions)
                            vec[idx++] = val.GetSingle();
                    }
                    results.Add(vec);
                }

                // 记录 token 使用量
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    var tokens = usage.TryGetProperty("total_tokens", out var tp) ? tp.GetInt32() : 0;
                    DebugLog($"[Embedding] {texts.Count} texts embedded, tokens={tokens}");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[Embedding] Exception: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// 计算两个向量的余弦相似度
        /// </summary>
        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;
            float dot = 0f, normA = 0f, normB = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            if (normA < 1e-10f || normB < 1e-10f) return 0f;
            return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
        }

        /// <summary>
        /// 从磁盘加载 embedding 缓存（JSON 格式：List of {Index, Vector}）
        /// </summary>
        private void LoadEmbeddingCache()
        {
            try
            {
                if (!File.Exists(_embeddingCachePath)) return;

                var json = File.ReadAllText(_embeddingCachePath, Encoding.UTF8);
                var entries = JsonSerializer.Deserialize<List<EmbeddingCacheEntry>>(json);
                if (entries == null) return;

                lock (_embeddingIndex)
                {
                    _embeddingIndex.Clear();
                    foreach (var entry in entries)
                    {
                        if (entry.Vector != null && entry.Vector.Length == EmbeddingDimensions)
                            _embeddingIndex[entry.Index] = entry.Vector;
                    }
                    _embeddedCount = _embeddingIndex.Count > 0 ? _embeddingIndex.Keys.Max() + 1 : 0;
                }

                DebugLog($"[Embedding] Loaded {_embeddingIndex.Count} cached embeddings, embeddedCount={_embeddedCount}");
            }
            catch (Exception ex)
            {
                DebugLog($"[Embedding] LoadCache error: {ex.Message}");
            }
        }

        /// <summary>
        /// 将 embedding 缓存保存到磁盘
        /// </summary>
        private void SaveEmbeddingCache()
        {
            try
            {
                List<EmbeddingCacheEntry> entries;
                lock (_embeddingIndex)
                {
                    entries = _embeddingIndex.Select(kv => new EmbeddingCacheEntry
                    {
                        Index = kv.Key,
                        Vector = kv.Value
                    }).OrderBy(e => e.Index).ToList();
                }

                var options = new JsonSerializerOptions { WriteIndented = false };
                File.WriteAllText(_embeddingCachePath, JsonSerializer.Serialize(entries, options), Encoding.UTF8);
                DebugLog($"[Embedding] Saved {entries.Count} embeddings to cache");
            }
            catch (Exception ex)
            {
                DebugLog($"[Embedding] SaveCache error: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步构建 embedding 索引（增量：只处理新增的记录）
        /// 每次批量最多处理 20 条，避免 API 超限
        /// </summary>
        private async Task BuildEmbeddingIndexAsync()
        {
            await _embeddingSemaphore.WaitAsync();
            try
            {
                List<ChatRecord> snapshot;
                lock (AllChatHistory)
                {
                    snapshot = new List<ChatRecord>(AllChatHistory);
                }

                int startIdx = _embeddedCount;
                if (startIdx >= snapshot.Count)
                {
                    DebugLog($"[Embedding] Index up-to-date, {snapshot.Count} records all embedded");
                    return;
                }

                int remaining = snapshot.Count - startIdx;
                DebugLog($"[Embedding] Building index: {remaining} new records (from idx {startIdx} to {snapshot.Count - 1})");

                const int batchSize = 20;
                for (int batch = startIdx; batch < snapshot.Count; batch += batchSize)
                {
                    int end = Math.Min(batch + batchSize, snapshot.Count);
                    var texts = new List<string>();
                    var indices = new List<int>();

                    for (int i = batch; i < end; i++)
                    {
                        var r = snapshot[i];
                        // 普通 system 日志不做 embedding；image_description 需要做
                        if (r.Role == "system" && r.Type != "image_description") continue;

                        // 将 role + content 组合为嵌入文本
                        // image_description 直接用内容，去掉 "system:" 前缀噪音
                        string text = (r.Role == "system") ? r.Content : $"{r.Role}: {r.Content}";
                        // 截断过长的文本（embedding-3 支持 2048 tokens，约 4000 中文字符）
                        if (text.Length > 2000) text = text.Substring(0, 2000);
                        texts.Add(text);
                        indices.Add(i);
                    }

                    var embeddings = await GetEmbeddingsAsync(texts);

                    if (embeddings.Count == texts.Count)
                    {
                        lock (_embeddingIndex)
                        {
                            for (int j = 0; j < embeddings.Count; j++)
                            {
                                _embeddingIndex[indices[j]] = embeddings[j];
                            }
                            _embeddedCount = end;
                        }
                    }
                    else
                    {
                        DebugLog($"[Embedding] Batch mismatch: expected {texts.Count}, got {embeddings.Count}");
                        break; // 出错则停止，下次再续
                    }

                    // 批间延迟，避免 API 限流
                    if (end < snapshot.Count)
                        await Task.Delay(500);
                }

                // 全部完成后保存缓存
                SaveEmbeddingCache();
            }
            catch (Exception ex)
            {
                DebugLog($"[Embedding] BuildIndex error: {ex.Message}");
            }
            finally
            {
                _embeddingSemaphore.Release();
            }
        }

        /// <summary>
        /// 为新增的单条记录增量更新 embedding（在 AppendChatRecord 后调用）
        /// </summary>
        private async Task EmbedNewRecordAsync(int index, string role, string content)
        {
            try
            {
                // image_description 直接用内容做嵌入（去掉 "system:" 前缀噪音）
                // 其他记录用 "role: content" 格式
                string text = (role == "system") ? content : $"{role}: {content}";
                if (text.Length > 2000) text = text.Substring(0, 2000);

                var embeddings = await GetEmbeddingsAsync(new List<string> { text });
                if (embeddings.Count == 1)
                {
                    lock (_embeddingIndex)
                    {
                        _embeddingIndex[index] = embeddings[0];
                        _embeddedCount = Math.Max(_embeddedCount, index + 1);
                    }
                    // 每 10 条新记录保存一次缓存
                    if (index % 10 == 0)
                        SaveEmbeddingCache();
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[Embedding] EmbedNewRecord error: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用 embedding 向量检索相关记忆（真正的 RAG）
        /// </summary>
        private async Task<List<string>> SearchRelevantMemoriesAsync(string query)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return results;

            try
            {
                // 1. 获取查询文本的 embedding
                var queryEmbeddings = await GetEmbeddingsAsync(new List<string> { query });
                if (queryEmbeddings.Count == 0)
                {
                    DebugLog("[RAG] Failed to get query embedding, falling back to keyword search");
                    return SearchRelevantMemoriesFallback(query);
                }
                var queryVec = queryEmbeddings[0];

                // 2. 计算与所有已嵌入记录的余弦相似度
                List<ChatRecord> snapshot;
                lock (AllChatHistory)
                {
                    snapshot = new List<ChatRecord>(AllChatHistory);
                }

                // 搜索范围：排除最近 MaxContextHistory 条（这些已在对话上下文中）
                // 注意：image_description 是 system 角色，永远不会发给模型，应始终参与 RAG 检索
                int excludeFrom = Math.Max(0, snapshot.Count - MaxContextHistory);

                var similarities = new List<(int Index, float Score, ChatRecord Record)>();

                lock (_embeddingIndex)
                {
                    foreach (var kv in _embeddingIndex)
                    {
                        if (kv.Key >= snapshot.Count) continue; // 防止越界

                        var rec = snapshot[kv.Key];
                        // 普通 system 日志（好感度/操作记录）不参与检索
                        if (rec.Role == "system" && rec.Type != "image_description") continue;

                        // image_description 始终参与检索（不受上下文窗口限制，因为它们不会发给模型）
                        bool isImageDesc = rec.Type == "image_description";
                        if (!isImageDesc && kv.Key >= excludeFrom) continue; // 跳过已在上下文窗口内的普通记录

                        float sim = CosineSimilarity(queryVec, kv.Value);
                        similarities.Add((kv.Key, sim, rec));
                    }
                }

                // 3. 取 Top-5，相似度阈值 > 0.35
                var topK = similarities
                    .Where(s => s.Score > 0.35f)
                    .OrderByDescending(s => s.Score)
                    .Take(10) // 控制返回数量，减小上下文负载，加快响应
                    .ToList();

                DebugLog($"[RAG] Embedding search: query=\"{query}\", searchScope={excludeFrom}, indexed={_embeddingIndex.Count}, candidates={similarities.Count}, matches(>0.35)={topK.Count}");

                if (topK.Count == 0)
                {
                    DebugLog("[RAG] No embedding matches above threshold 0.35");
                }
                else
                {
                    // 4. 对每条命中项，扩展前后各 2 条相邻记录作为上下文片段
                    const int contextWindow = 2;

                    // image_description 命中时，将其索引映射到对应的 type=image 记录（向前查找）
                    // 这样上下文扩展能正确展示带描述的图片消息
                    var remappedTopK = topK.Select(item =>
                    {
                        if (item.Record.Type == "image_description")
                        {
                            // 向前找最近的 type=image 记录（同文件名）
                            for (int bi = item.Index - 1; bi >= Math.Max(0, item.Index - 5); bi--)
                            {
                                if (snapshot[bi].Type == "image" &&
                                    !string.IsNullOrEmpty(snapshot[bi].ImageName) &&
                                    item.Record.Content.Contains(snapshot[bi].ImageName!))
                                {
                                    return (Index: bi, item.Score, Record: snapshot[bi]);
                                }
                            }
                        }
                        return item;
                    }).ToList();
                    // 收集所有需要包含的索引，按片段分组（每个命中项独立一段）
                    var segments = new List<List<int>>();
                    foreach (var item in remappedTopK)
                    {
                        int lo = Math.Max(0, item.Index - contextWindow);
                        int hi = Math.Min(snapshot.Count - 1, item.Index + contextWindow);
                        var seg = new List<int>();
                        for (int i = lo; i <= hi; i++) seg.Add(i);
                        segments.Add(seg);
                    }

                    // 合并重叠或相邻的片段（间隔 ≤ 1 视为同一段）
                    segments.Sort((a, b) => a[0].CompareTo(b[0]));
                    var merged = new List<List<int>>();
                    foreach (var seg in segments)
                    {
                        if (merged.Count > 0)
                        {
                            var last = merged[merged.Count - 1];
                            if (seg[0] <= last[last.Count - 1] + 2) // 相邻或重叠
                            {
                                foreach (int idx in seg)
                                    if (!last.Contains(idx)) last.Add(idx);
                                last.Sort();
                                continue;
                            }
                        }
                        merged.Add(new List<int>(seg));
                    }

                    // 5. 按片段输出，每段加分隔，命中项打日志
                    var hitIndices = new HashSet<int>(remappedTopK.Select(t => t.Index));
                    foreach (var seg in merged)
                    {
                        var lines = new System.Text.StringBuilder();
                        foreach (int idx in seg)
                        {
                            var rec = snapshot[idx];
                            // 跳过普通 system 日志（好感度/操作记录），只保留对话内容和图片描述
                            if (rec.Role == "system" && rec.Type != "image_description") continue;
                            // image_description 已经会内嵌到对应的 image 记录行里，单独输出会重复
                            if (rec.Type == "image_description") continue;

                            string line;
                            if (rec.Type == "image")
                            {
                                // 查找该图片对应的 image_description，内嵌到行里
                                string? inlineDesc = null;
                                if (!string.IsNullOrEmpty(rec.ImageName))
                                {
                                    for (int si = idx + 1; si < Math.Min(idx + 5, snapshot.Count); si++)
                                    {
                                        var sr = snapshot[si];
                                        if (sr.Type == "image_description" && sr.Content.Contains(rec.ImageName))
                                        {
                                            int colonIdx = sr.Content.IndexOf('：');
                                            inlineDesc = colonIdx >= 0
                                                ? sr.Content.Substring(colonIdx + 1).Trim()
                                                : sr.Content;
                                            break;
                                        }
                                    }
                                }
                                line = $"[{rec.Time}] {rec.Role}: {rec.Content}";
                                if (inlineDesc != null)
                                    line += $"\n[图片描述: {inlineDesc}]";
                            }
                            else
                            {
                                line = $"[{rec.Time}] {rec.Role}: {rec.Content}";
                            }

                            lines.AppendLine(line);
                            if (hitIndices.Contains(idx))
                            {
                                float score = topK.First(t => t.Index == idx).Score;
                                DebugLog($"[RAG] Match(idx={idx}, score={score:F4}): {line}");
                            }
                        }
                        string segText = lines.ToString().TrimEnd();
                        if (!string.IsNullOrWhiteSpace(segText))
                            results.Add(segText);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[RAG] Embedding search error: {ex.Message}, falling back to keyword search");
                return SearchRelevantMemoriesFallback(query);
            }

            return results;
        }

        /// <summary>
        /// 关键词搜索后备方案（当 embedding API 不可用时使用）
        /// </summary>
        private List<string> SearchRelevantMemoriesFallback(string query)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            var keywords = query.Split(new[] { ' ', ',', '，', '.', '。', '?', '？', '!', '！' },
                StringSplitOptions.RemoveEmptyEntries)
                .Where(k => k.Length > 1)
                .ToList();

            if (keywords.Count == 0) return results;

            lock (AllChatHistory)
            {
                int skipCount = Math.Max(0, AllChatHistory.Count - MaxContextHistory);
                var searchScope = AllChatHistory.Take(skipCount).ToList();

                var scoredRecords = searchScope.Select(r => new
                {
                    Record = r,
                    Score = keywords.Count(k => r.Content.Contains(k))
                })
                // 排除普通 system 日志，但保留 image_description
                .Where(x => x.Record.Role != "system" || x.Record.Type == "image_description")
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Record.Time)
                .Take(20) // 限制数量降低上下文体积
                .ToList();

                foreach (var item in scoredRecords)
                {
                    results.Add($"[{item.Record.Time}] {item.Record.Role}: {item.Record.Content}");
                }
            }
            DebugLog($"[RAG] Fallback keyword search: {results.Count} results");
            return results;
        }

        /// <summary>
        /// 同步包装器（兼容现有调用点）
        /// </summary>
        private List<string> SearchRelevantMemories(string query)
        {
            try
            {
                return SearchRelevantMemoriesAsync(query).GetAwaiter().GetResult();
            }
            catch
            {
                return SearchRelevantMemoriesFallback(query);
            }
        }

        #endregion

        #region ===== 配置文件 =====

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (config != null && config.ContainsKey("ChatName"))
                    {
                        ChatName = config["ChatName"];
                    }
                }
            }
            catch { }
        }

        public void SaveConfig()
        {
            try
            {
                var config = new Dictionary<string, string>
                {
                    { "ChatName", ChatName }
                };
                File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            }
            catch { }
        }

        #endregion
    }
}
