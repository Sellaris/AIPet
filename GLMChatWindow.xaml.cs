using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VPet_AIGF
{
    /// <summary>
    /// 空->Collapsed 转换
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 字符串为空则 Collapsed
    /// </summary>
    public class StringNullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null) return Visibility.Collapsed;
            var str = value.ToString();
            return string.IsNullOrWhiteSpace(str) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 聊天消息 ViewModel
    /// </summary>
    public class ChatMessage : INotifyPropertyChanged
    {
        public string Text { get; set; } = "";
        public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        public Brush Background { get; set; } = Brushes.White;
        public Brush Foreground { get; set; } = Brushes.Black;
        public HorizontalAlignment Alignment { get; set; } = HorizontalAlignment.Left;
        public ImageSource? Image { get; set; } = null;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 独立的 GLM 聊天窗口（严格单例，由 AIPlugin 管理生命周期）
    /// </summary>
    public partial class GLMChatWindow : Window
    {
        private readonly AIPlugin _plugin;
        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();

        private bool _isSending = false; // 防止重复发送
        private DispatcherTimer? _statusTimer; // 状态栏刷新定时器
        private string? _pendingImagePath;
        private ImageSource? _pendingImageSource;
        private bool _isTempImage = false; // 剪贴板粘贴产生的临时文件，发送后需删除

        public GLMChatWindow(AIPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            icMessages.ItemsSource = Messages;
            tbTitle.Text = $"💕 和{_plugin.ChatName}聊天";

            // 窗口关闭时只隐藏，不销毁（单例模式）
            Closing += (s, e) =>
            {
                e.Cancel = true;
                Hide();
            };

            // 状态栏定时刷新（每2秒）
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (s, e) => RefreshStatus();
            _statusTimer.Start();
            RefreshStatus(); // 初始化一次
        }

        /// <summary>
        /// 刷新状态栏显示
        /// </summary>
        private void RefreshStatus()
        {
            try
            {
                tbStatus.Text = _plugin.GetStatusSummary();
            }
            catch { }
        }

        #region ===== 公开方法：添加消息到 UI =====

        /// <summary>
        /// 添加 AI 消息到界面（可指定时间戳，用于恢复历史）
        /// </summary>
        public void AddAIMessage(string text, string? timestamp = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddAIMessage(text, timestamp));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = $"🐾 {text}",
                Timestamp = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Background = new SolidColorBrush(Color.FromRgb(240, 230, 250)),
                Foreground = new SolidColorBrush(Color.FromRgb(80, 50, 100)),
                Alignment = HorizontalAlignment.Left
            });
            ScrollToBottom();
        }

        /// <summary>
        /// 添加用户消息到界面（可指定时间戳，用于恢复历史）
        /// </summary>
        public void AddUserMessage(string text, string? timestamp = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddUserMessage(text, timestamp));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = text,
                Timestamp = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Background = new SolidColorBrush(Color.FromRgb(200, 230, 255)),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 80)),
                Alignment = HorizontalAlignment.Right
            });
            ScrollToBottom();
        }

        /// <summary>
        /// 添加系统提示消息（好感度变化、操作日志等）
        /// </summary>
        public void AddSystemMessage(string text, bool saveToHistory = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddSystemMessage(text, saveToHistory));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = text,
                Timestamp = "",
                Background = new SolidColorBrush(Color.FromRgb(255, 250, 230)),
                Foreground = new SolidColorBrush(Color.FromRgb(160, 130, 60)),
                Alignment = HorizontalAlignment.Center
            });
            ScrollToBottom();

            if (saveToHistory)
                _plugin.AppendSystemRecord(text);
        }

        /// <summary>
        /// 添加红包消息到界面
        /// </summary>
        public void AddRedPacketMessage(double amount, string blessing)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddRedPacketMessage(amount, blessing));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = $"🧧 红包 {amount:F2} 金币\n{blessing}",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Background = new SolidColorBrush(Color.FromRgb(255, 220, 220)),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 50, 50)),
                Alignment = HorizontalAlignment.Right
            });
            ScrollToBottom();
        }

        /// <summary>
        /// 恢复历史记录时用已格式化文本渲染红包气泡（保持红色样式）
        /// </summary>
        public void AddRedPacketRaw(string rawText, string? timestamp = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddRedPacketRaw(rawText, timestamp));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = rawText,
                Timestamp = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Background = new SolidColorBrush(Color.FromRgb(255, 220, 220)),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 50, 50)),
                Alignment = HorizontalAlignment.Right
            });
            ScrollToBottom();
        }

        /// <summary>
        /// 宠物主动给主人发红包气泡（左侧，AI 侧，粉红色）
        /// </summary>
        public void AddPetRedPacketMessage(double amount, string blessing, string? timestamp = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddPetRedPacketMessage(amount, blessing, timestamp));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = $"🧧 红包 {amount:F2} 金币\n{blessing}",
                Timestamp = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Background = new SolidColorBrush(Color.FromRgb(255, 210, 230)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 40, 80)),
                Alignment = HorizontalAlignment.Left
            });
            ScrollToBottom();
        }

        /// <summary>
        /// 恢复历史时渲染宠物发出的红包气泡（左侧）
        /// </summary>
        public void AddPetRedPacketRaw(string rawText, string? timestamp = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddPetRedPacketRaw(rawText, timestamp));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = rawText,
                Timestamp = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Background = new SolidColorBrush(Color.FromRgb(255, 210, 230)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 40, 80)),
                Alignment = HorizontalAlignment.Left
            });
            ScrollToBottom();
        }

        /// <summary>
        /// 从 ToolBar 转发过来的消息，统一在这里处理发送流程
        /// </summary>
        public async void SendMessageFromExternal(string text)
        {
            if (string.IsNullOrEmpty(text) || _isSending) return;
            await DoSendMessage(text);
        }

        public void RefreshStatusBar()
        {
            RefreshStatus();
        }

        #endregion

        #region ===== UI 事件 =====

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            var text = tbInput.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _isSending) return;
            tbInput.Text = "";
            await DoSendMessage(text);
        }

        private async void tbInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                var text = tbInput.Text?.Trim();
                if (string.IsNullOrEmpty(text) || _isSending) return;
                tbInput.Text = "";
                await DoSendMessage(text);
            }
            // Ctrl+V：优先尝试粘贴图片，成功则阻止文本粘贴
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (TrySetPendingImageFromClipboard())
                    e.Handled = true; // 有图片则拦截，不把图片路径粘成文字
            }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要清空聊天记录并删除记忆吗？此操作不可撤销。", "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            Messages.Clear();
            _plugin.ClearChatRecords();
            AddSystemMessage("已清空聊天记录", true);
        }

        private void Rollback_Click(object sender, RoutedEventArgs e)
        {
            var ok = _plugin.RollbackLastSnapshot();
            if (ok)
                AddSystemMessage("已回滚到上一轮对话", false);
            else
                AddSystemMessage("没有可回滚的记录", false);
        }

        private void BtnVoiceEnroll_Click(object sender, RoutedEventArgs e)
        {
            var win = new VoiceEnrollWindow(_plugin.SpeakerVerifier);
            win.Owner = this;
            win.ShowDialog();
            // 注册完成后重载语音唤醒（使新声纹生效）
            _plugin.ReloadVoiceWakeup();
        }

        private void RecordingIndicator_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 点击录音指示器立即停止 STT
            _plugin.StopVoiceListening();
        }

        private async void RedPacket_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending) return;
            await ShowRedPacketDialog();
        }

        private void SendImage_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending) return;

            var ofd = new OpenFileDialog
            {
                Title = "选择要发送的图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*"
            };

            if (ofd.ShowDialog() != true) return;

            SetPendingImage(ofd.FileName);
        }

        private void SetPendingImage(string imagePath, bool isTempFile = false)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath);
                bitmap.DecodePixelWidth = 512;
                bitmap.EndInit();
                bitmap.Freeze();

                _pendingImagePath = imagePath;
                _pendingImageSource = bitmap;
                _isTempImage = isTempFile;
                imgPreview.Source = bitmap;
                tbPreviewName.Text = isTempFile ? "📋 粘贴的图片" : System.IO.Path.GetFileName(imagePath);
                previewPanel.Visibility = Visibility.Visible;
                tbInput.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法加载图片: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ClearPreview_Click(object sender, RoutedEventArgs e)
        {
            ClearPendingImage();
        }

        /// <summary>
        /// 尝试从剪贴板读取图片，保存为临时 PNG 文件后挂起为待发送图片。
        /// 返回 true 表示成功读取到图片。
        /// </summary>
        private bool TrySetPendingImageFromClipboard()
        {
            try
            {
                BitmapSource? bmp = null;

                // 优先尝试 Bitmap（截图、QQ图片等来源）
                if (Clipboard.ContainsImage())
                {
                    bmp = Clipboard.GetImage();
                }
                // 其次尝试文件拖放（文件管理器复制的图片文件）
                else if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    foreach (string f in files)
                    {
                        var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif")
                        {
                            SetPendingImage(f, isTempFile: false);
                            return true;
                        }
                    }
                    return false;
                }

                if (bmp == null) return false;

                // 写到系统临时目录
                string tempDir = System.IO.Path.GetTempPath();
                string tempFile = System.IO.Path.Combine(tempDir, $"vpet_paste_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    encoder.Save(fs);
                }

                SetPendingImage(tempFile, isTempFile: true);
                return true;
            }
            catch { return false; }
        }

        private void tbTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var inputDialog = new InputDialog("请输入新的称呼", _plugin.ChatName);
            if (inputDialog.ShowDialog() == true)
            {
                _plugin.ChatName = inputDialog.InputText;
                _plugin.SaveConfig();
                tbTitle.Text = $"💕 和{_plugin.ChatName}聊天";
            }
        }

        /// <summary>
        /// 窗口级 Ctrl+V 拦截：焦点在任意控件时均可粘贴图片。
        /// 仅当剪贴板确实含有图片时才处理，否则让文本框正常粘贴文字。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 只处理 Ctrl+V，且当焦点不在输入框内时（输入框已在 tbInput_KeyDown 处理）
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control
                && !tbInput.IsFocused)
            {
                if (TrySetPendingImageFromClipboard())
                    e.Handled = true;
            }
        }

        #endregion

        #region ===== 红包弹窗 =====

        private async Task ShowRedPacketDialog()
        {
            // 创建红包弹窗
            var dialog = new Window
            {
                Title = "🧧 发红包",
                Width = 320,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(255, 245, 240)),
                Topmost = true
            };

            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };

            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "红包不扣钱，随意发~",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 100, 100)),
                Margin = new Thickness(0, 0, 0, 10)
            });

            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "💰 金额", FontSize = 14, FontWeight = FontWeights.Bold });
            var tbAmount = new System.Windows.Controls.TextBox
            {
                FontSize = 16,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 4, 0, 12),
                Text = "100"
            };
            sp.Children.Add(tbAmount);

            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "💌 祝福语", FontSize = 14, FontWeight = FontWeights.Bold });
            var tbBlessing = new System.Windows.Controls.TextBox
            {
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 4, 0, 16),
                Text = "给宝贝的红包~"
            };
            sp.Children.Add(tbBlessing);

            var btnConfirm = new System.Windows.Controls.Button
            {
                Content = "🧧 发送红包",
                FontSize = 16,
                Padding = new Thickness(16, 8, 16, 8),
                Background = new SolidColorBrush(Color.FromRgb(230, 80, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            sp.Children.Add(btnConfirm);

            dialog.Content = sp;

            double amount = 0;
            string blessing = "";
            bool confirmed = false;

            btnConfirm.Click += (s, ev) =>
            {
                if (!double.TryParse(tbAmount.Text, out amount) || amount <= 0)
                {
                    MessageBox.Show("请输入有效的金额！", "提示");
                    return;
                }
                blessing = tbBlessing.Text?.Trim() ?? "红包";
                confirmed = true;
                dialog.Close();
            };

            dialog.ShowDialog();

            if (!confirmed) return;

            // 发送红包
            _isSending = true;
            btnSend.IsEnabled = false;
            tbInput.IsEnabled = false;
            btnRedPacket.IsEnabled = false;

            try
            {
                _plugin.SaveSnapshotIfNeeded();

                // 1. 显示红包消息
                AddRedPacketMessage(amount, blessing);

                // 2. 显示思考中
                var thinkMsg = new ChatMessage
                {
                    Text = "🐾 拆红包中...",
                    Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                    Foreground = Brushes.Gray,
                    Alignment = HorizontalAlignment.Left
                };
                Messages.Add(thinkMsg);
                ScrollToBottom();

                // 3. 调用红包API，带重试机制
                GLMResult result = new GLMResult();
                int maxRetries = 3;
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    var attemptText = attempt == 1 ? "🐾 拆红包中..." : $"🐾 拆红包中...（重试 {attempt}/{maxRetries}）";
                    int thinkIdx = Messages.IndexOf(thinkMsg);
                    if (thinkIdx >= 0)
                        Messages[thinkIdx].Text = attemptText;

                    try
                    {
                        result = await Task.Run(() => _plugin.SendRedPacket(amount, blessing));
                    }
                    catch (Exception ex)
                    {
                        result = new GLMResult { Reply = $"出错了喵: {ex.Message}" };
                    }

                    bool isErrorReply = string.IsNullOrEmpty(result.Reply)
                        || result.Reply.Contains("出错了喵")
                        || result.Reply.Contains("思考太久")
                        || result.Reply.Contains("思考太久了");

                    if (!isErrorReply)
                        break;

                    if (attempt < maxRetries)
                        await Task.Delay(800);
                }

                // 4. 替换思考消息为实际回复
                var idx = Messages.IndexOf(thinkMsg);
                if (idx >= 0)
                {
                    Messages[idx] = new ChatMessage
                    {
                        Text = $"🐾 {result.Reply}",
                        Background = new SolidColorBrush(Color.FromRgb(240, 230, 250)),
                        Foreground = new SolidColorBrush(Color.FromRgb(80, 50, 100)),
                        Alignment = HorizontalAlignment.Left
                    };
                }

                // 情绪触发窗口反馈
                _plugin.ReactToEmotion(result);

                // 5. 应用好感度 + 心情变化
                _plugin.MW.Dispatcher.Invoke(() =>
                {
                    var save = _plugin.MW.Core.Save;
                    if (result.LikabilityChange != 0)
                        save.Likability += result.LikabilityChange;
                    if (result.FeelingChange != 0)
                        save.FeelingChange(result.FeelingChange); // 直接用绝对值（-20到+20）
                });

                if (result.LikabilityChange != 0)
                    AddSystemMessage($"💕 好感度 {(result.LikabilityChange > 0 ? "+" : "")}{result.LikabilityChange} ({result.Reason})", true);

                if (result.FeelingChange != 0)
                {
                    string sign = result.FeelingChange > 0 ? "+" : "";
                    AddSystemMessage($"😊 心情 {sign}{result.FeelingChange}", true);
                }

                // 6. 显示操作日志
                foreach (var log in result.ActionLogs)
                    AddSystemMessage($"⚡ {log}", true);

                // 7. 红包扣款提示
                //AddSystemMessage($"💰 已增加 {amount:F0} 金币余额，剩余 {_plugin.MW.Core.Save.Money:F0}");

                // 8. 刷新状态栏
                RefreshStatus();

                // 9. 让桌宠说出来（带情绪表情）
                try
                {
                    _plugin.MW.Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrEmpty(result.EmotionGraph))
                            _plugin.MW.Main.Say(result.Reply, result.EmotionGraph, true);
                        else
                            _plugin.MW.Main.SayRnd(result.Reply, true);
                    });
                }
                catch { }
            }
            finally
            {
                _isSending = false;
                btnSend.IsEnabled = true;
                tbInput.IsEnabled = true;
                btnRedPacket.IsEnabled = true;
                tbInput.Focus();
                ScrollToBottom();
            }
        }

        #endregion

        #region ===== 核心发送逻辑（唯一入口） =====

        private void ApplyResultToUI(GLMResult result, ChatMessage placeholder)
        {
            // 替换占位的思考消息
            var idx = Messages.IndexOf(placeholder);
            var aiMsg = new ChatMessage
            {
                Text = $"🐾 {result.Reply}",
                Background = new SolidColorBrush(Color.FromRgb(240, 230, 250)),
                Foreground = new SolidColorBrush(Color.FromRgb(80, 50, 100)),
                Alignment = HorizontalAlignment.Left
            };
            if (idx >= 0)
                Messages[idx] = aiMsg;
            else
                Messages.Add(aiMsg);

            // 情绪触发窗口反馈
            _plugin.ReactToEmotion(result);

            // 应用好感度 + 心情变化并显示
            try
            {
                _plugin.MW.Dispatcher.Invoke(() =>
                {
                    var save = _plugin.MW.Core.Save;

                    if (result.LikabilityChange != 0)
                        save.Likability += result.LikabilityChange;

                    if (result.FeelingChange != 0)
                        save.FeelingChange(result.FeelingChange); // 直接用绝对值（-20到+20）
                });
            }
            catch { }

            if (result.LikabilityChange != 0)
                AddSystemMessage($"💕 好感度 {(result.LikabilityChange > 0 ? "+" : "")}{result.LikabilityChange} ({result.Reason})", true);

            if (result.FeelingChange != 0)
            {
                string feelingDesc = result.FeelingChange switch
                {
                    <= -15 => "心情极差，很伤心",
                    <= -10 => "心情变差了",
                    <= -5  => "有点不开心",
                    < 0    => "略微有点低落",
                    >= 15  => "心情大好！",
                    >= 10  => "心情变好了~",
                    _      => "心情好了一点"
                };
                string sign = result.FeelingChange > 0 ? "+" : "";
                AddSystemMessage($"😊 心情 {sign}{result.FeelingChange} ({feelingDesc})", true);
            }

            foreach (var log in result.ActionLogs)
                AddSystemMessage($"⚡ {log}", true);

            RefreshStatus();

            try
            {
                _plugin.MW.Dispatcher.Invoke(() =>
                {
                    if (!string.IsNullOrEmpty(result.EmotionGraph))
                        _plugin.MW.Main.Say(result.Reply, result.EmotionGraph, true);
                    else
                        _plugin.MW.Main.SayRnd(result.Reply, true);
                });

                // Say() 里的情绪动画（如 shy）会用 DisplayBLoopingForce 无限播放，
                // 必须在气泡消失之后再播放 play_animation / 进食动画，否则会被 Say 动画覆盖。
                _plugin.FlushPendingAnimationDelayed(result.PendingAnimation);
                _plugin.FlushPendingFoodAnimationDelayed(result.PendingFoodAnimation);
            }
            catch { }
        }

        private async Task DoSendMessage(string userText)
        {
            // 如果有待发送图片，则走图片+文本通道
            if (!string.IsNullOrEmpty(_pendingImagePath))
            {
                await DoSendImageMessage(_pendingImagePath, userText, _pendingImageSource);
                return;
            }

            _isSending = true;
            btnSend.IsEnabled = false;
            tbInput.IsEnabled = false;
            btnRedPacket.IsEnabled = false;
            btnSendImage.IsEnabled = false;

            try
            {
                _plugin.SaveSnapshotIfNeeded();

                // 1. 显示用户消息
                AddUserMessage(userText);

                // 2. 显示思考中
                var thinkMsg = new ChatMessage
                {
                    Text = "🐾 思考中...",
                    Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                    Foreground = Brushes.Gray,
                    Alignment = HorizontalAlignment.Left
                };
                Messages.Add(thinkMsg);
                ScrollToBottom();

                // 3. 调用 API
                GLMResult result = new GLMResult();
                int maxRetries = 3;
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    var attemptText = attempt == 1 ? "🐾 思考中..." : $"🐾 思考中...（重试 {attempt}/{maxRetries}）";
                    int thinkIdx = Messages.IndexOf(thinkMsg);
                    if (thinkIdx >= 0)
                        Messages[thinkIdx].Text = attemptText;

                    try
                    {
                        result = await Task.Run(() => _plugin.CallGLM("", userContent: userText));
                    }
                    catch (Exception ex)
                    {
                        result = new GLMResult { Reply = $"出错了喵: {ex.Message}" };
                    }

                    bool isErrorReply = string.IsNullOrEmpty(result.Reply)
                        || result.Reply.Contains("出错了喵")
                        || result.Reply.Contains("思考太久")
                        || result.Reply.Contains("思考太久了");

                    if (!isErrorReply)
                        break;

                    if (attempt < maxRetries)
                        await Task.Delay(800);
                }

                ApplyResultToUI(result, thinkMsg);
            }
            finally
            {
                _isSending = false;
                btnSend.IsEnabled = true;
                tbInput.IsEnabled = true;
                btnRedPacket.IsEnabled = true;
                btnSendImage.IsEnabled = true;
                tbInput.Focus();
                ScrollToBottom();
            }
        }

        private async Task DoSendImageMessage(string imagePath, string caption, ImageSource? imageSource = null)
        {
            _isSending = true;
            btnSend.IsEnabled = false;
            tbInput.IsEnabled = false;
            btnRedPacket.IsEnabled = false;
            btnSendImage.IsEnabled = false;

            try
            {
                _plugin.SaveSnapshotIfNeeded();

                // 1. 显示用户图片消息
                var bitmap = imageSource ?? LoadBitmap(imagePath);
                string displayCaption = string.IsNullOrWhiteSpace(caption) ? "(无描述)" : caption;
                AddUserImageMessage(displayCaption, bitmap, null, System.IO.Path.GetFileName(imagePath));

                // 2. 显示思考中
                var thinkMsg = new ChatMessage
                {
                    Text = "🐾 看看图片...",
                    Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                    Foreground = Brushes.Gray,
                    Alignment = HorizontalAlignment.Left
                };
                Messages.Add(thinkMsg);
                ScrollToBottom();

                var result = await _plugin.SendImageMessage(imagePath, caption ?? "");
                ApplyResultToUI(result, thinkMsg);
            }
            finally
            {
                _isSending = false;
                btnSend.IsEnabled = true;
                tbInput.IsEnabled = true;
                btnRedPacket.IsEnabled = true;
                btnSendImage.IsEnabled = true;
                ClearPendingImage();
                tbInput.Focus();
                ScrollToBottom();
            }
        }

        #endregion

        private void ClearPendingImage()
        {
            // 如果是剪贴板粘贴产生的临时文件，发送后删除
            if (_isTempImage && !string.IsNullOrEmpty(_pendingImagePath))
            {
                try { File.Delete(_pendingImagePath); } catch { }
            }
            _pendingImagePath = null;
            _pendingImageSource = null;
            _isTempImage = false;
            imgPreview.Source = null;
            tbPreviewName.Text = "";
            previewPanel.Visibility = Visibility.Collapsed;
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 512;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        public void AddUserImageFromHistory(string text, string dataUrl, string? timestamp = null, string? imageName = null)
        {
            try
            {
                var bmp = LoadBitmapFromDataUrl(dataUrl);
                // 历史记录中 text 已包含 "[图片] xxx.png ..." 完整信息，
                // 不再传 imageName 避免 AddUserImageMessage 再拼一遍文件名。
                AddUserImageMessage(string.IsNullOrWhiteSpace(text) ? "(无描述)" : text, bmp, timestamp, imageName: null);
            }
            catch { }
        }

        public void AddAIImageFromHistory(string text, string dataUrl, string? timestamp = null, string? imageName = null)
        {
            try
            {
                var bmp = LoadBitmapFromDataUrl(dataUrl);
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => AddAIImageFromHistory(text, dataUrl, timestamp, imageName));
                    return;
                }
                Messages.Add(new ChatMessage
                {
                    Text = text,
                    Image = bmp,
                    Timestamp = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Background = new SolidColorBrush(Color.FromRgb(240, 230, 250)),
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 50, 100)),
                    Alignment = HorizontalAlignment.Left
                });
                ScrollToBottom();
            }
            catch { }
        }

        private void AddUserImageMessage(string text, ImageSource image, string? timestamp = null, string? imageName = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddUserImageMessage(text, image, timestamp, imageName));
                return;
            }

            Messages.Add(new ChatMessage
            {
                Text = string.IsNullOrWhiteSpace(imageName) ? text : $"🖼 {imageName}\n{text}",
                Image = image,
                Timestamp = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Background = new SolidColorBrush(Color.FromRgb(200, 230, 255)),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 80)),
                Alignment = HorizontalAlignment.Right
            });
            ScrollToBottom();
        }

        private static BitmapImage LoadBitmapFromDataUrl(string dataUrl)
        {
            // data:[mime];base64,xxxx
            var commaIdx = dataUrl.IndexOf(',');
            string b64 = commaIdx >= 0 ? dataUrl[(commaIdx + 1)..] : dataUrl;
            byte[] bytes = Convert.FromBase64String(b64);
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.DecodePixelWidth = 512;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void ScrollToBottom()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                svChat.ScrollToEnd();
            });
        }

        public void ShowAndActivate()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ShowAndActivate);
                return;
            }

            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            tbInput.Focus();
        }

        /// <summary>
        /// 聚焦到输入框（供外部模块调用）
        /// </summary>
        public void FocusInput()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(FocusInput);
                return;
            }
            tbInput.Focus();
        }

        #region ===== 语音录入：输入框文字 + 录音指示器 =====

        private DispatcherTimer? _recordingDotTimer;

        /// <summary>
        /// 显示录音指示器（输入栏旁的闪烁红点）
        /// </summary>
        public void ShowRecordingIndicator()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ShowRecordingIndicator); return; }

            tbRecordingHint.Text = "录音中";
            recordingIndicator.Visibility = Visibility.Visible;
            recordingDot.Opacity = 1;

            // 闪烁动画
            _recordingDotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            bool visible = true;
            _recordingDotTimer.Tick += (_, _) =>
            {
                visible = !visible;
                recordingDot.Opacity = visible ? 1.0 : 0.15;
            };
            _recordingDotTimer.Start();
        }

        /// <summary>
        /// 隐藏录音指示器
        /// </summary>
        public void HideRecordingIndicator()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(HideRecordingIndicator); return; }

            _recordingDotTimer?.Stop();
            _recordingDotTimer = null;
            recordingIndicator.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 更新录音指示器旁的临时假设文字（不写输入框）
        /// </summary>
        public void UpdateRecordingHint(string hint)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdateRecordingHint(hint)); return; }
            tbRecordingHint.Text = string.IsNullOrWhiteSpace(hint) ? "录音中" : hint;
        }

        /// <summary>
        /// 设置输入框文字（仅写入已确认识别段落，线程安全）
        /// </summary>
        public void SetInputText(string text)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetInputText(text)); return; }

            tbInput.Text = text;
            tbInput.CaretIndex = text.Length; // 光标移到末尾
        }

        #endregion
    }
}
