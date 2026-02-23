using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VPet_AIGF
{
    /// <summary>
    /// 声纹注册向导窗口。
    /// 引导用户依次朗读三句提示语，每句录制 3 秒，
    /// 完成后将 MFCC 模板写入文件供 SpeakerVerifier 使用。
    /// </summary>
    public partial class VoiceEnrollWindow : Window
    {
        // ── 提示语（用户朗读内容）──
        // 用普通汉语短句，避免太短或太相似
        private static readonly string[] Prompts =
        {
            "今天天气真好，我来叫一叫你",
            "你好，我是你的主人，请认识我",
            "叫一声宠物名，测试一下音色"
        };

        private const int RecordDurationMs = 3000; // 每句录音时长
        private const int CountdownMs = 500;        // 录音前短暂提示时间

        private readonly SpeakerVerifier _verifier;
        private readonly List<short[]> _collectedSamples = new();

        private int _currentStep = 0;   // 0~2 三步；3 = 完成
        private bool _isRecording = false;
        private DispatcherTimer? _countdownTimer;
        private int _countdownSec;

        public VoiceEnrollWindow(SpeakerVerifier verifier)
        {
            InitializeComponent();
            _verifier = verifier;
            // 若已注册，直接显示完成界面
            if (_verifier.IsEnrolled)
                _currentStep = 3;
            UpdateUI();
        }

        // ══════════════════════════════════════════════
        //  UI 状态同步
        // ══════════════════════════════════════════════

        private void UpdateUI()
        {
            var stepColors = new[] { "#B39DDB", "#D1C4E9", "#D1C4E9" };
            var active = "#7E57C2";

            dotStep1.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_currentStep >= 0 ? active : stepColors[0]));
            dotStep2.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_currentStep >= 1 ? active : stepColors[1]));
            dotStep3.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_currentStep >= 2 ? active : stepColors[2]));

            if (_currentStep < 3)
            {
                tbStep.Text = $"第 {_currentStep + 1} / 3 句";
                tbPrompt.Text = $"「{Prompts[_currentStep]}」";
                btnRecord.Content = $"🎙️ 开始录制第 {_currentStep + 1} 句";
                btnRecord.IsEnabled = !_isRecording;
            }
            else
            {
                tbStep.Text = "✅ 声纹注册完成！";
                tbPrompt.Text = "现在只有你的声音才能唤醒宠物了~";
                btnRecord.IsEnabled = false;
                btnRecord.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#A5D6A7"));
                btnRecord.Content = "✅ 注册成功";
            }
        }

        private void SetStatus(string text, string color = "#666")
        {
            Dispatcher.Invoke(() =>
            {
                tbStatus.Text = text;
                tbStatus.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(color));
            });
        }

        private void SetLevelBar(float level)
        {
            Dispatcher.Invoke(() =>
            {
                double maxWidth = levelBar.Parent is FrameworkElement parent
                    ? parent.ActualWidth : 400;
                levelBar.Width = Math.Min(level * maxWidth * 2, maxWidth);
            });
        }

        // ══════════════════════════════════════════════
        //  按钮事件
        // ══════════════════════════════════════════════

        private async void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording || _currentStep >= 3) return;

            _isRecording = true;
            btnRecord.IsEnabled = false;
            btnClear.IsEnabled = false;
            levelBar.Width = 0;

            await RunCountdown();
            await RecordOneStep();
        }

        private async Task RunCountdown()
        {
            for (int i = 3; i >= 1; i--)
            {
                SetStatus($"⏳ {i} 秒后开始录音，请准备朗读：「{Prompts[_currentStep]}」", "#F57C00");
                await Task.Delay(1000);
            }
            SetStatus($"🔴 录音中…  请朗读：「{Prompts[_currentStep]}」", "#C62828");
        }

        private async Task RecordOneStep()
        {
            short[]? sample = null;

            // 在后台线程录音，避免阻塞 UI
            await Task.Run(() =>
            {
                sample = SpeakerVerifier.RecordPcm(RecordDurationMs, level =>
                {
                    SetLevelBar(level);
                });
            });

            if (sample == null || sample.Length == 0)
            {
                SetStatus("❌ 录音失败，请重试", "#C62828");
                _isRecording = false;
                btnRecord.IsEnabled = true;
                btnClear.IsEnabled = true;
                return;
            }

            double rms = SpeakerVerifier.ComputeRms(sample);
            if (rms < 0.005)
            {
                SetStatus("⚠️ 未检测到有效声音，请靠近麦克风后重试", "#E65100");
                _isRecording = false;
                btnRecord.IsEnabled = true;
                btnClear.IsEnabled = true;
                SetLevelBar(0);
                return;
            }

            _collectedSamples.Add(sample);
            _currentStep++;

            if (_currentStep < 3)
            {
                SetStatus($"✔ 第 {_currentStep} 句录制成功！继续下一句", "#2E7D32");
            }
            else
            {
                // 三句全录完，提取声纹
                SetStatus("⚙️ 正在提取声纹特征，请稍候…", "#1565C0");
                await Task.Run(() =>
                {
                    try
                    {
                        _verifier.Enroll(_collectedSamples);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                            SetStatus($"❌ 声纹提取失败：{ex.Message}", "#C62828"));
                        return;
                    }
                });

                if (_verifier.IsEnrolled)
                    SetStatus("🎉 声纹注册完成！下次叫宠物名只有你的声音才能唤醒~", "#2E7D32");
            }

            _isRecording = false;
            SetLevelBar(0);
            Dispatcher.Invoke(() =>
            {
                UpdateUI();
                btnClear.IsEnabled = true;
            });
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("确定要清除已注册声纹并重新录制吗？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            _verifier.Clear();
            _collectedSamples.Clear();
            _currentStep = 0;
            _isRecording = false;
            SetLevelBar(0);
            SetStatus("已清除，请重新录制三句话。");
            UpdateUI();
        }
    }
}
