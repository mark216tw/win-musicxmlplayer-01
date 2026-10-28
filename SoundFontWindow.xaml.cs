using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer
{
    public partial class SoundFontWindow : Window
    {
        private readonly AudioEngine _audioEngine;
        private readonly Action _onSoundFontChanged;

        public SoundFontWindow(AudioEngine audioEngine, Action onSoundFontChanged)
        {
            _audioEngine = audioEngine;
            _onSoundFontChanged = onSoundFontChanged;

            InitializeComponent();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowDwmStyle(this);

            RefreshSlotStatusUi();
        }

        private void RefreshSlotStatusUi()
        {
            if (_audioEngine.HasDedicatedPianoSoundFont)
            {
                TxtPianoSfStatus.Text = $"已啟用：{_audioEngine.PianoSoundFontName} ({_audioEngine.PianoSoundFontPath})";
                BtnClearPianoSf.IsEnabled = true;
            }
            else
            {
                TxtPianoSfStatus.Text = "目前狀態：未指定（自動使用下方通用音源內的鋼琴音色）";
                BtnClearPianoSf.IsEnabled = false;
            }

            if (!string.IsNullOrEmpty(_audioEngine.CurrentSoundFontPath))
            {
                TxtGeneralSfStatus.Text = $"使用中：{_audioEngine.CurrentSoundFontName} ({_audioEngine.CurrentSoundFontPath})";
            }
            else
            {
                TxtGeneralSfStatus.Text = "目前狀態：尚未載入通用音源";
            }
        }

        private static string GetBuiltInSf2Directory()
        {
            string localSfDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "SoundFonts");
            if (Directory.Exists(localSfDir))
                return localSfDir;
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private string GetInitialSf2Directory()
        {
            string lastDirectory = ThemeManager.LastSoundFontDirectory;
            if (!string.IsNullOrWhiteSpace(lastDirectory) && Directory.Exists(lastDirectory))
                return lastDirectory;

            return GetBuiltInSf2Directory();
        }

        private static void RememberSelectedSoundFontDirectory(string selectedFile)
        {
            string? directory = Path.GetDirectoryName(selectedFile);
            if (!string.IsNullOrWhiteSpace(directory))
                ThemeManager.SetLastSoundFontDirectory(directory);
        }

        private async void BtnSelectPianoSf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "選擇「鋼琴專用」SoundFont 2 (.sf2) 音源檔",
                InitialDirectory = GetInitialSf2Directory(),
                Filter = "SoundFont 2 音源檔 (*.sf2)|*.sf2|所有檔案 (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                string selectedFile = dlg.FileName;
                RememberSelectedSoundFontDirectory(selectedFile);
                TxtActionFeedback.Text = $"⏳ 正在背景載入鋼琴音源：{Path.GetFileName(selectedFile)}...";
                var btn = sender as UIElement;
                if (btn != null) btn.IsEnabled = false;
                try
                {
                    string err = string.Empty;
                    bool ok = await Task.Run(() => _audioEngine.LoadPianoSoundFont(selectedFile, out err));
                    if (ok)
                    {
                        ThemeManager.SetPianoSoundFontPath(selectedFile);
                        RefreshSlotStatusUi();
                        _onSoundFontChanged?.Invoke();
                        TxtActionFeedback.Text = $"✓ 已成功載入鋼琴專用音源：{_audioEngine.PianoSoundFontName}";
                    }
                    else
                    {
                        TxtActionFeedback.Text = string.Empty;
                        MessageBox.Show(this, err, "鋼琴音源載入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    if (btn != null) btn.IsEnabled = true;
                }
            }
        }

        private void BtnClearPianoSf_Click(object sender, RoutedEventArgs e)
        {
            _audioEngine.ClearPianoSoundFont();
            ThemeManager.SetPianoSoundFontPath(string.Empty);
            RefreshSlotStatusUi();
            _onSoundFontChanged?.Invoke();
            TxtActionFeedback.Text = "✓ 已停用獨立鋼琴音源，鋼琴聲部已回歸使用通用樂器音源";
        }

        private async void BtnSelectGeneralSf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "選擇「通用樂器」SoundFont 2 (.sf2) 音源檔",
                InitialDirectory = GetInitialSf2Directory(),
                Filter = "SoundFont 2 音源檔 (*.sf2)|*.sf2|所有檔案 (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                string selectedFile = dlg.FileName;
                RememberSelectedSoundFontDirectory(selectedFile);
                TxtActionFeedback.Text = $"⏳ 正在背景載入通用音源：{Path.GetFileName(selectedFile)}...";
                var btn = sender as UIElement;
                if (btn != null) btn.IsEnabled = false;
                try
                {
                    string err = string.Empty;
                    bool ok = await Task.Run(() => _audioEngine.LoadSoundFont(selectedFile, out err));
                    if (ok)
                    {
                        ThemeManager.SetGeneralSoundFontPath(selectedFile);
                        RefreshSlotStatusUi();
                        _onSoundFontChanged?.Invoke();
                        TxtActionFeedback.Text = $"✓ 已成功切換通用樂器音源：{_audioEngine.CurrentSoundFontName}";
                    }
                    else
                    {
                        TxtActionFeedback.Text = string.Empty;
                        MessageBox.Show(this, err, "通用音源載入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    if (btn != null) btn.IsEnabled = true;
                }
            }
        }

        private async void BtnResetGeneralSf_Click(object sender, RoutedEventArgs e)
        {
            string? defaultSf = SoundFontManager.FindBuiltInTimGmSoundFontPath() ?? SoundFontManager.FindDefaultSoundFontPath();
            if (!string.IsNullOrEmpty(defaultSf))
            {
                TxtActionFeedback.Text = "⏳ 正在還原預設通用音源...";
                string err = string.Empty;
                bool ok = await Task.Run(() => _audioEngine.LoadSoundFont(defaultSf, out err));
                if (ok)
                {
                    ThemeManager.SetGeneralSoundFontPath(defaultSf);
                    RefreshSlotStatusUi();
                    _onSoundFontChanged?.Invoke();
                    TxtActionFeedback.Text = $"✓ 已還原為預設通用音源：{_audioEngine.CurrentSoundFontName}";
                    return;
                }
            }
            MessageBox.Show(this, "找不到內建 TimGM6mb.sf2 音源檔。", "還原失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void BtnOpenSoundFontFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = GetBuiltInSf2Directory();
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"無法開啟資料夾：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
