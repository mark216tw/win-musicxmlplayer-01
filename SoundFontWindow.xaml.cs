using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer
{
    public partial class SoundFontWindow : Window
    {
        private readonly AudioEngine _audioEngine;
        private readonly Action _onSoundFontChanged;
        private bool _isSyncingRecentUi;
        private bool _isLoadingSoundFont;

        public SoundFontWindow(AudioEngine audioEngine, Action onSoundFontChanged)
        {
            _audioEngine = audioEngine;
            _onSoundFontChanged = onSoundFontChanged;

            InitializeComponent();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowDwmStyle(this);

            RefreshSlotStatusUi();
            RefreshRecentSoundFontsUi();
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

        private void RefreshRecentSoundFontsUi()
        {
            _isSyncingRecentUi = true;
            try
            {
                PopulateRecentCombo(CmbRecentPianoSf, BtnDeleteRecentPianoSf, BtnClearRecentPianoSf, SoundFontSlot.Piano);
                PopulateRecentCombo(CmbRecentGeneralSf, BtnDeleteRecentGeneralSf, BtnClearRecentGeneralSf, SoundFontSlot.General);
            }
            finally
            {
                _isSyncingRecentUi = false;
            }
        }

        private static void PopulateRecentCombo(
            ComboBox combo,
            Button deleteButton,
            Button clearButton,
            SoundFontSlot slot)
        {
            var recents = ThemeManager.GetRecentSoundFonts(slot);
            combo.Items.Clear();
            deleteButton.IsEnabled = false;
            clearButton.IsEnabled = recents.Count > 0;
            combo.IsEnabled = recents.Count > 0;

            if (recents.Count == 0)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = "(尚無最近音源)",
                    IsEnabled = false
                });
                combo.SelectedIndex = 0;
                return;
            }

            foreach (string path in recents)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = Path.GetFileName(path),
                    Tag = path,
                    ToolTip = path
                });
            }

            combo.SelectedIndex = -1;
        }

        private async Task LoadSoundFontAsync(SoundFontSlot slot, string selectedFile)
        {
            if (_isLoadingSoundFont) return;

            string label = slot == SoundFontSlot.Piano ? "鋼琴專用音源" : "通用樂器音源";
            _isLoadingSoundFont = true;
            TxtActionFeedback.Text = $"⏳ 正在背景載入{label}：{Path.GetFileName(selectedFile)}...";

            try
            {
                string err = string.Empty;
                bool ok;
                if (slot == SoundFontSlot.Piano)
                {
                    ok = await Task.Run(() => _audioEngine.LoadPianoSoundFont(selectedFile, out err));
                }
                else
                {
                    ok = await Task.Run(() => _audioEngine.LoadSoundFont(selectedFile, out err));
                }

                if (ok)
                {
                    if (slot == SoundFontSlot.Piano)
                    {
                        ThemeManager.SetPianoSoundFontPath(selectedFile);
                    }
                    else
                    {
                        ThemeManager.SetGeneralSoundFontPath(selectedFile);
                    }

                    ThemeManager.AddRecentSoundFont(slot, selectedFile);
                    RefreshSlotStatusUi();
                    RefreshRecentSoundFontsUi();
                    _onSoundFontChanged?.Invoke();
                    TxtActionFeedback.Text = $"✓ 已成功載入{label}：{Path.GetFileName(selectedFile)}";
                }
                else
                {
                    TxtActionFeedback.Text = string.Empty;
                    MessageBox.Show(this, err, $"{label}載入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                _isLoadingSoundFont = false;
            }
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
                RememberSelectedSoundFontDirectory(dlg.FileName);
                await LoadSoundFontAsync(SoundFontSlot.Piano, dlg.FileName);
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
                RememberSelectedSoundFontDirectory(dlg.FileName);
                await LoadSoundFontAsync(SoundFontSlot.General, dlg.FileName);
            }
        }

        private async void BtnResetGeneralSf_Click(object sender, RoutedEventArgs e)
        {
            string? defaultSf = SoundFontManager.FindBuiltInTimGmSoundFontPath() ?? SoundFontManager.FindDefaultSoundFontPath();
            if (!string.IsNullOrEmpty(defaultSf))
            {
                await LoadSoundFontAsync(SoundFontSlot.General, defaultSf);
                return;
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

        private async void CmbRecentPianoSf_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingRecentUi) return;
            BtnDeleteRecentPianoSf.IsEnabled = CmbRecentPianoSf.SelectedItem is ComboBoxItem { Tag: string };
            await HandleRecentSelectionAsync(CmbRecentPianoSf, SoundFontSlot.Piano);
        }

        private async void CmbRecentGeneralSf_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingRecentUi) return;
            BtnDeleteRecentGeneralSf.IsEnabled = CmbRecentGeneralSf.SelectedItem is ComboBoxItem { Tag: string };
            await HandleRecentSelectionAsync(CmbRecentGeneralSf, SoundFontSlot.General);
        }

        private async Task HandleRecentSelectionAsync(ComboBox combo, SoundFontSlot slot)
        {
            if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string path)
                return;

            if (!File.Exists(path))
            {
                ThemeManager.RemoveRecentSoundFont(slot, path);
                RefreshRecentSoundFontsUi();
                MessageBox.Show(this, $"找不到音源檔案：{path}", "音源不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadSoundFontAsync(slot, path);
        }

        private void BtnDeleteRecentPianoSf_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedRecent(CmbRecentPianoSf, SoundFontSlot.Piano);
        }

        private void BtnDeleteRecentGeneralSf_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedRecent(CmbRecentGeneralSf, SoundFontSlot.General);
        }

        private void DeleteSelectedRecent(ComboBox combo, SoundFontSlot slot)
        {
            if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string path)
                return;

            ThemeManager.RemoveRecentSoundFont(slot, path);
            RefreshRecentSoundFontsUi();
            TxtActionFeedback.Text = $"✓ 已從最近音源移除：{Path.GetFileName(path)}";
        }

        private void BtnClearRecentPianoSf_Click(object sender, RoutedEventArgs e)
        {
            ClearRecentSoundFonts(SoundFontSlot.Piano, "鋼琴專用音源");
        }

        private void BtnClearRecentGeneralSf_Click(object sender, RoutedEventArgs e)
        {
            ClearRecentSoundFonts(SoundFontSlot.General, "通用樂器音源");
        }

        private void ClearRecentSoundFonts(SoundFontSlot slot, string label)
        {
            ThemeManager.ClearRecentSoundFonts(slot);
            RefreshRecentSoundFontsUi();
            TxtActionFeedback.Text = $"✓ 已清除{label}的最近音源紀錄";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
