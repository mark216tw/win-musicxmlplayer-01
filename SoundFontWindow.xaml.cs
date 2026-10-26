using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer
{
    public sealed class SoundFontDownloadSuggestion
    {
        public string CategoryBadge { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string SizeAndLicense { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
    }

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

            RecommendationsList.ItemsSource = BuildRecommendations();
            RefreshSlotStatusUi();
        }

        private static List<SoundFontDownloadSuggestion> BuildRecommendations()
        {
            return new List<SoundFontDownloadSuggestion>
            {
                new()
                {
                    CategoryBadge = "[🎹 鋼琴專用推薦]",
                    Title = "Upright Piano KW / Salamander Grand Piano (FreePats)",
                    SizeAndLicense = "約 14 MB ~ 200 MB · CC0 / CC-BY 開源授權",
                    Description = "FreePats 官方收錄之高品質平台鋼琴與直立式鋼琴 .sf2 音源，木質共鳴自然、動態層次細膩，極適合掛載於上方「鋼琴專用音源」槽位。",
                    Url = "https://freepats.zenvoid.org/Piano/acoustic-grand-piano.html"
                },
                new()
                {
                    CategoryBadge = "[🎹 鋼琴專用推薦]",
                    Title = "Musical Artifacts — 免費鋼琴 SoundFont 專區",
                    SizeAndLicense = "多款 10 MB ~ 500 MB · 開源社群授權",
                    Description = "匯集 Steinway Model D、Yamaha C5、Chateau Grand、Equinox Grand Pianos 等數十款免費鋼琴 .sf2，可直接依評價下載。",
                    Url = "https://musical-artifacts.com/artifacts?formats=sf2&tags=piano"
                },
                new()
                {
                    CategoryBadge = "[🎻 通用全樂器推薦]",
                    Title = "GeneralUser GS (by S. Christian Collins)",
                    SizeAndLicense = "約 30 MB · 免費授權",
                    Description = "全球最受推崇的輕量級全樂器 General MIDI 音源之一，128 種管弦樂、木管、銅管與打擊樂音量平衡極佳，適合掛載於「通用樂器音源」槽位。",
                    Url = "https://schristiancollins.com/generaluser.php"
                },
                new()
                {
                    CategoryBadge = "[🎻 通用全樂器推薦]",
                    Title = "FluidR3_GM.sf2 / MuseScore 推薦音源庫總覽",
                    SizeAndLicense = "35 MB ~ 500 MB · MIT / 開源授權",
                    Description = "MuseScore 官方手冊整理的各類 GM 管弦樂與專業分段音源（包含 FluidR3_GM、MuseScore_General、Sonatina Symphonic Orchestra）。",
                    Url = "https://musescore.org/en/handbook/3/soundfonts-and-sfz-files"
                },
                new()
                {
                    CategoryBadge = "[🔍 綜合音源搜尋庫]",
                    Title = "Musical Artifacts — 全球最大開源 .sf2 音源庫",
                    SizeAndLicense = "數千款免費 .sf2 音源",
                    Description = "可自由搜尋 Violin (小提琴)、Cello (大提琴)、Orchestra (管弦樂團)、Harpsichord (大鍵琴) 等各式獨立或通用 .sf2 音源。",
                    Url = "https://musical-artifacts.com/artifacts?formats=sf2"
                }
            };
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

        private string GetInitialSf2Directory()
        {
            string localSfDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "SoundFonts");
            if (Directory.Exists(localSfDir))
                return localSfDir;
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void BtnSelectPianoSf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "選擇「鋼琴專用」SoundFont 2 (.sf2) 音源檔",
                InitialDirectory = GetInitialSf2Directory(),
                Filter = "SoundFont 2 音源檔 (*.sf2)|*.sf2|所有檔案 (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                if (_audioEngine.LoadPianoSoundFont(dlg.FileName, out string err))
                {
                    ThemeManager.SetPianoSoundFontPath(dlg.FileName);
                    RefreshSlotStatusUi();
                    _onSoundFontChanged?.Invoke();
                    TxtActionFeedback.Text = $"✓ 已成功載入鋼琴專用音源：{_audioEngine.PianoSoundFontName}";
                }
                else
                {
                    MessageBox.Show(this, err, "鋼琴音源載入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private void BtnSelectGeneralSf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "選擇「通用樂器」SoundFont 2 (.sf2) 音源檔",
                InitialDirectory = GetInitialSf2Directory(),
                Filter = "SoundFont 2 音源檔 (*.sf2)|*.sf2|所有檔案 (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                if (_audioEngine.LoadSoundFont(dlg.FileName, out string err))
                {
                    ThemeManager.SetGeneralSoundFontPath(dlg.FileName);
                    RefreshSlotStatusUi();
                    _onSoundFontChanged?.Invoke();
                    TxtActionFeedback.Text = $"✓ 已成功切換通用樂器音源：{_audioEngine.CurrentSoundFontName}";
                }
                else
                {
                    MessageBox.Show(this, err, "通用音源載入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void BtnResetGeneralSf_Click(object sender, RoutedEventArgs e)
        {
            string? defaultSf = SoundFontManager.FindBuiltInTimGmSoundFontPath() ?? SoundFontManager.FindDefaultSoundFontPath();
            if (!string.IsNullOrEmpty(defaultSf) && _audioEngine.LoadSoundFont(defaultSf, out string err))
            {
                ThemeManager.SetGeneralSoundFontPath(defaultSf);
                RefreshSlotStatusUi();
                _onSoundFontChanged?.Invoke();
                TxtActionFeedback.Text = $"✓ 已還原為預設通用音源：{_audioEngine.CurrentSoundFontName}";
            }
            else
            {
                MessageBox.Show(this, "找不到內建 TimGM6mb.sf2 音源檔。", "還原失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnOpenSoundFontFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = GetInitialSf2Directory();
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

        private void BtnOpenDownloadUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    TxtActionFeedback.Text = $"✓ 已在瀏覽器開啟下載頁面：{url}";
                }
                catch
                {
                    Clipboard.SetText(url);
                    TxtActionFeedback.Text = $"已將下載網址複製到剪貼簿：{url}";
                }
            }
        }

        private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                Clipboard.SetText(url);
                TxtActionFeedback.Text = $"✓ 已複製下載網址至剪貼簿：{url}";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
