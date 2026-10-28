using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using WinMusicXmlPlayer.Controls;
using WinMusicXmlPlayer.Models;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer
{
    public partial class MainWindow : Window
    {
        private readonly AudioEngine _audioEngine = new();
        private MusicScore? _currentScore;
        private string? _currentFilePath;
        private bool _isSliderDragging;
        private bool _wasPlayingBeforeScrub;
        private double _lastRenderedTimeSec = -1.0;
        private int _lastDisplayedTenths = -1;
        private int _lastTelemetryTimelineIdx = -1;
        private int _lastTelemetrySpeedBpm = -1;
        private bool _metersActive;
        private bool _isMasterMuted;
        private double _masterVolumeBeforeMute = 1.25;
        private int _loopState;
        private DispatcherTimer? _toastTimer;

        public MainWindow()
        {
            ThemeManager.Initialize();
            InitializeComponent();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowDwmStyle(this);
            FitWindowToWorkArea();
            DataContext = this;
            _audioEngine.UseInstrumentSpatialProfile = ThemeManager.InstrumentSpatialProfile;

            // 綁定鋼琴卷軸滑鼠左右滑動調整時間、右鍵設定 A-B 循環、縮放記憶事件
            PianoRoll.ScrubStarted += OnPianoRollScrubStarted;
            PianoRoll.TimeScrubbed += OnPianoRollTimeScrubbed;
            PianoRoll.ScrubEnded += OnPianoRollScrubEnded;
            PianoRoll.LoopPointRightClicked += OnPianoRollLoopPointRightClicked;
            PianoRoll.VisibleWindowChanged += s => ThemeManager.SetLastVisibleWindowSeconds(s);

            // 綁定底部 88 鍵鋼琴鍵盤滑鼠點擊與滑奏試聽事件
            PianoKeyboard.KeyPreviewChanged += OnPianoKeyboardPreviewChanged;

            _audioEngine.PlaybackCompleted += () =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    BtnPlayPause.Content = "▶ 播放";
                });
            };

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void FitWindowToWorkArea()
        {
            Rect workArea = SystemParameters.WorkArea;
            if (workArea.Width > 400 && workArea.Height > 300)
            {
                double targetW = Math.Min(1160.0, Math.Floor(workArea.Width * 0.88));
                double targetH = Math.Min(680.0, Math.Floor(workArea.Height * 0.85));

                Width = Math.Max(MinWidth, targetW);
                Height = Math.Max(MinHeight, targetH);

                Left = workArea.Left + Math.Max(0, (workArea.Width - Width) * 0.5);
                Top = workArea.Top + Math.Max(0, (workArea.Height - Height) * 0.5);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. 載入通用樂器 SoundFont (.sf2) 與選用的鋼琴專用 SoundFont (.sf2)
            string? sf2Path = !string.IsNullOrWhiteSpace(ThemeManager.GeneralSoundFontPath) && File.Exists(ThemeManager.GeneralSoundFontPath)
                ? ThemeManager.GeneralSoundFontPath
                : SoundFontManager.FindDefaultSoundFontPath();

            string generalError = string.Empty;
            bool generalLoaded = false;

            if (!string.IsNullOrWhiteSpace(sf2Path))
            {
                generalLoaded = _audioEngine.LoadSoundFont(sf2Path, out generalError);
            }

            if (!generalLoaded)
            {
                string? fallbackPath = SoundFontManager.FindBuiltInTimGmSoundFontPath();
                if (string.IsNullOrEmpty(fallbackPath) ||
                    string.Equals(fallbackPath, sf2Path, StringComparison.OrdinalIgnoreCase))
                {
                    fallbackPath = SoundFontManager.FindDefaultSoundFontPath();
                }

                if (!string.IsNullOrEmpty(fallbackPath) &&
                    !string.Equals(fallbackPath, sf2Path, StringComparison.OrdinalIgnoreCase))
                {
                    generalLoaded = _audioEngine.LoadSoundFont(fallbackPath, out _);
                    if (generalLoaded)
                    {
                        ThemeManager.SetGeneralSoundFontPath(fallbackPath);
                        ShowToast($"原通用音源無效，已自動改用：{Path.GetFileName(fallbackPath)}");
                    }
                }
            }

            if (generalLoaded)
            {
                string pianoError = string.Empty;
                if (!string.IsNullOrWhiteSpace(ThemeManager.PianoSoundFontPath) && File.Exists(ThemeManager.PianoSoundFontPath))
                {
                    if (!_audioEngine.LoadPianoSoundFont(ThemeManager.PianoSoundFontPath, out pianoError))
                    {
                        ThemeManager.SetPianoSoundFontPath(string.Empty);
                    }
                }

                string status = _audioEngine.GetStatusSummaryText();
                if (!string.IsNullOrEmpty(generalError))
                {
                    status += " ｜ ⚠ 原音源無效，已自動替換";
                    ShowToast($"通用音源載入失敗：{generalError}");
                }
                if (!string.IsNullOrEmpty(pianoError))
                {
                    status += " ｜ ⚠ 鋼琴音源已停用";
                    ShowToast($"鋼琴音源載入失敗：{pianoError}");
                }

                TxtSoundFontStatus.Text = status;
            }
            else
            {
                TxtSoundFontStatus.Text = "目前音源：尚未載入可用的 .sf2 音源檔（請點擊右上角設定音源）";
                if (!string.IsNullOrEmpty(generalError))
                {
                    ShowToast($"通用音源載入失敗：{generalError}");
                }
            }

            RefreshTrackSoundFontRouting();

            PianoRoll.VisibleWindowSeconds = ThemeManager.LastVisibleWindowSeconds;
            PianoRollViewMode savedMode = ThemeManager.LastViewMode == 1
                ? PianoRollViewMode.VerticalFalling
                : PianoRollViewMode.HorizontalTimeline;
            SetPianoRollViewMode(savedMode, savePref: false);
            RefreshRecentFilesMenu();

            // 2. 預設不載入任何 MusicXML 樂譜，啟動 60FPS 畫面同步迴圈
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            ThemeManager.FlushPreferences();
            _audioEngine.Dispose();
        }

        public void LoadMusicXmlFile(string filePath, bool autoPlay = false)
        {
            try
            {
                _audioEngine.Stop();
                BtnPlayPause.Content = "▶ 播放";

                bool expandRepeats = ChkExpandRepeats.IsChecked == true;
                var score = MusicXmlLoader.LoadFromFile(filePath, expandRepeats, ThemeManager.CreatePerformanceOptions());

                _currentScore = score;
                _currentFilePath = filePath;
                ThemeManager.SetLastOpenedDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)));
                ThemeManager.AddRecentFile(filePath);
                RefreshRecentFilesMenu();

                TxtScoreTitle.Text = string.IsNullOrWhiteSpace(score.Composer)
                    ? score.Title
                    : $"{score.Title} — {score.Composer}";

                string repeatTag = score.HasRepeats ? (expandRepeats ? "已展開反覆記號" : "不展開反覆") : "無反覆記號";
                TxtScoreSubtitle.Text = $"共 {score.Tracks.Count} 個可操作聲部 · {score.Measures.Count} 小節 · 總音符 {score.Notes.Count} · {repeatTag}";

                TxtTrackSummary.Text = $"共 {score.Tracks.Count} 個聲部軌道（可獨立靜音、獨奏、換樂器、調音量）";

                foreach (var track in score.Tracks)
                {
                    InitializeTrackInstrumentOptions(track);
                }
                RefreshAllTrackInstrumentOptions();

                TracksItemsControl.ItemsSource = null;
                TracksItemsControl.ItemsSource = score.Tracks;

                // 有聲部時才顯示「重置」按鈕，無聲部時隱藏
                BtnResetTracks.Visibility = score.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                PianoRoll.SetScore(score);
                PianoKeyboard.SetScore(score);
                _audioEngine.LoadScore(score);
                RefreshTrackSoundFontRouting();
                _loopState = 0;
                UpdateLoopButtonUi();
                _lastRenderedTimeSec = -1.0;
                _lastDisplayedTenths = -1;
                _lastTelemetryTimelineIdx = -1;
                _lastTelemetrySpeedBpm = -1;
                _metersActive = true;

                SliderTimeline.Maximum = Math.Max(1.0, score.TotalDurationSeconds);
                SliderTimeline.Value = 0.0;
                TxtCurrentTime.Text = "00:00.0";
                TxtTotalTime.Text = FormatTime(score.TotalDurationSeconds);

                UpdateMusicalTelemetry(0.0);

                if (autoPlay)
                {
                    _audioEngine.Play();
                    BtnPlayPause.Content = "⏸ 暫停";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"讀取 MusicXML 檔案失敗：\n{ex.Message}", "載入錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCompositionTargetRendering(object? sender, EventArgs e)
        {
            if (_currentScore == null) return;

            double curSec = _audioEngine.GetVisualTimeSeconds(ThemeManager.VisualSyncOffsetMs / 1000.0);
            bool isPlaying = _audioEngine.IsPlaying;
            bool timeChanged = Math.Abs(curSec - _lastRenderedTimeSec) > 0.0005;

            if (!isPlaying && !timeChanged && !_metersActive)
                return;

            if (timeChanged)
            {
                _lastRenderedTimeSec = curSec;
                PianoRoll.UpdatePlaybackTime(curSec);
                PianoKeyboard.UpdatePlaybackTime(curSec);

                if (!_isSliderDragging)
                {
                    SliderTimeline.Value = Math.Min(SliderTimeline.Maximum, curSec);
                }

                int tenths = (int)(curSec * 10.0);
                if (tenths != _lastDisplayedTenths)
                {
                    _lastDisplayedTenths = tenths;
                    TxtCurrentTime.Text = FormatTime(curSec);
                }

                UpdateMusicalTelemetry(curSec);
            }

            _metersActive = UpdateTrackActivityMeters(curSec);
        }

        private void UpdateMusicalTelemetry(double curSec)
        {
            if (_currentScore == null || _currentScore.Measures.Count == 0) return;

            var measures = _currentScore.Measures;
            MeasureMarker currentMeas = _currentScore.FindMeasureAt(curSec);
            int speedBpm = (int)Math.Round(currentMeas.TempoBpm * _audioEngine.PlaybackSpeed);

            if (currentMeas.TimelineIndex == _lastTelemetryTimelineIdx && speedBpm == _lastTelemetrySpeedBpm)
                return;

            _lastTelemetryTimelineIdx = currentMeas.TimelineIndex;
            _lastTelemetrySpeedBpm = speedBpm;

            TxtMeasureStatus.Text = $"小節：{currentMeas.MeasureNumberText} / {measures[^1].NumericMeasureNumber}";
            TxtTimeSigStatus.Text = $"拍號：{currentMeas.Beats}/{currentMeas.BeatType}";
            TxtKeySigStatus.Text = $"調號：{currentMeas.KeySignatureName}";
            TxtTempoStatus.Text = $"速度：{speedBpm} BPM";
        }

        private bool UpdateTrackActivityMeters(double curSec)
        {
            if (_currentScore == null) return false;

            bool anySolo = _currentScore.Tracks.Any(t => t.IsSolo);
            double[] maxLevels = new double[_currentScore.Tracks.Count];

            if (_audioEngine.IsPlaying)
            {
                var notes = _currentScore.Notes;
                int start = _currentScore.FindFirstNoteIndexForWindow(curSec);
                for (int i = start; i < notes.Count; i++)
                {
                    var n = notes[i];
                    if (n.StartTimeSeconds > curSec) break;
                    if (n.EndTimeSeconds >= curSec && n.TrackIndex >= 0 && n.TrackIndex < maxLevels.Length)
                    {
                        var tr = _currentScore.Tracks[n.TrackIndex];
                        if (!tr.IsMuted && (!anySolo || tr.IsSolo))
                        {
                            double env = 1.0 - Math.Min(0.65, (curSec - n.StartTimeSeconds) * 0.8);
                            double level = (n.Velocity / 127.0) * env * Math.Min(1.2, tr.Volume);
                            if (level > maxLevels[n.TrackIndex])
                                maxLevels[n.TrackIndex] = level;
                        }
                    }
                }
            }

            bool anyActive = false;
            for (int t = 0; t < _currentScore.Tracks.Count; t++)
            {
                double prev = _currentScore.Tracks[t].CurrentActivity;
                double target = maxLevels[t] * 0.75;
                double smoothed;
                if (target >= prev)
                {
                    smoothed = prev + (target - prev) * 0.65;
                }
                else
                {
                    smoothed = Math.Max(target, prev * 0.82 - 0.018);
                }
                double clamped = Math.Clamp(smoothed, 0.0, 1.0);
                _currentScore.Tracks[t].CurrentActivity = clamped;
                if (clamped > 0.002)
                    anyActive = true;
            }
            return anyActive;
        }

        // ==================== 鋼琴卷軸滑鼠左右滑動調整時間事件 ====================

        private void OnPianoRollScrubStarted()
        {
            _wasPlayingBeforeScrub = _audioEngine.IsPlaying;
            if (_wasPlayingBeforeScrub)
            {
                _audioEngine.Pause();
            }
        }

        private void OnPianoRollTimeScrubbed(double newTimeSec)
        {
            _audioEngine.Seek(newTimeSec);
            PianoKeyboard.UpdatePlaybackTime(newTimeSec);
            if (!_isSliderDragging)
            {
                SliderTimeline.Value = Math.Min(SliderTimeline.Maximum, newTimeSec);
            }
            TxtCurrentTime.Text = FormatTime(newTimeSec);
            UpdateMusicalTelemetry(newTimeSec);
        }

        private void OnPianoRollScrubEnded()
        {
            if (_wasPlayingBeforeScrub)
            {
                _audioEngine.Play();
                BtnPlayPause.Content = "⏸ 暫停";
            }
        }

        // ==================== 頂部系統選單與快捷鍵操作 ====================

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var inMenu = FindAncestor<Menu>(src) != null || FindAncestor<MenuItem>(src) != null;
                if (!inMenu)
                {
                    CloseOtherTopMenus(null);
                }
            }
        }

        private void TopMenuItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is MenuItem topItem && e.OriginalSource is DependencyObject src)
            {
                // 若點擊的是子選單項目本身，交由子選單 Click 處理
                var clickedMenuItem = FindAncestor<MenuItem>(src);
                if (clickedMenuItem != null && clickedMenuItem != topItem)
                {
                    return;
                }

                // 點擊頂層系統選單標題時立即自動展開
                CloseOtherTopMenus(topItem);
                topItem.IsSubmenuOpen = !topItem.IsSubmenuOpen;
                e.Handled = true;
            }
        }

        private void TopMenuItem_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is MenuItem topItem)
            {
                bool anyOpen = MenuFile.IsSubmenuOpen || MenuSettings.IsSubmenuOpen || MenuHelp.IsSubmenuOpen;
                if (anyOpen && !topItem.IsSubmenuOpen)
                {
                    CloseOtherTopMenus(topItem);
                    topItem.IsSubmenuOpen = true;
                }
            }
        }

        private void CloseOtherTopMenus(MenuItem? activeItem)
        {
            if (MenuFile != activeItem) MenuFile.IsSubmenuOpen = false;
            if (MenuSettings != activeItem) MenuSettings.IsSubmenuOpen = false;
            if (MenuHelp != activeItem) MenuHelp.IsSubmenuOpen = false;
        }

        /// <summary>
        /// 安全向上尋找父元素（同時支援 Visual / Visual3D 與 ContentElement / FrameworkContentElement 如 Run）
        /// </summary>
        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                    return match;

                if (current is Visual or Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return null;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys mods = Keyboard.Modifiers;

            if (mods == ModifierKeys.Control)
            {
                if (key == Key.O)
                {
                    e.Handled = true;
                    BtnOpenFile_Click(this, new RoutedEventArgs());
                    return;
                }
                if (key == Key.E)
                {
                    e.Handled = true;
                    MenuExportWav_Click(this, new RoutedEventArgs());
                    return;
                }
                if (key == Key.Q)
                {
                    e.Handled = true;
                    MenuExit_Click(this, new RoutedEventArgs());
                    return;
                }
                if (key == Key.M)
                {
                    e.Handled = true;
                    MenuSoundFontSettings_Click(this, new RoutedEventArgs());
                    return;
                }
                if (key == Key.P || key == Key.OemComma)
                {
                    e.Handled = true;
                    MenuPreferences_Click(this, new RoutedEventArgs());
                    return;
                }
                if (key == Key.G)
                {
                    e.Handled = true;
                    OpenGotoMeasureInput();
                    return;
                }
            }
            else if (mods == ModifierKeys.Alt)
            {
                if (key == Key.F)
                {
                    CloseOtherTopMenus(MenuFile);
                    MenuFile.IsSubmenuOpen = true;
                    e.Handled = true;
                    return;
                }
                if (key == Key.S)
                {
                    CloseOtherTopMenus(MenuSettings);
                    MenuSettings.IsSubmenuOpen = true;
                    e.Handled = true;
                    return;
                }
                if (key == Key.H)
                {
                    CloseOtherTopMenus(MenuHelp);
                    MenuHelp.IsSubmenuOpen = true;
                    e.Handled = true;
                    return;
                }
            }
            else if (mods == ModifierKeys.None)
            {
                if (key == Key.F1)
                {
                    e.Handled = true;
                    MenuAbout_Click(this, new RoutedEventArgs());
                    return;
                }

                // 若焦點不在下拉選單或輸入框內，支援 Space 播放/暫停、左右方向鍵快退/快進 5 秒、Home 回到開頭、M 靜音切換
                if (Keyboard.FocusedElement is not ComboBox and not ComboBoxItem and not TextBox)
                {
                    if (key == Key.Space && _currentScore != null)
                    {
                        e.Handled = true;
                        BtnPlayPause_Click(this, new RoutedEventArgs());
                        return;
                    }
                    if (key == Key.Left && _currentScore != null)
                    {
                        e.Handled = true;
                        BtnStepBack_Click(this, new RoutedEventArgs());
                        return;
                    }
                    if (key == Key.Right && _currentScore != null)
                    {
                        e.Handled = true;
                        BtnStepForward_Click(this, new RoutedEventArgs());
                        return;
                    }
                    if (key == Key.Home && _currentScore != null)
                    {
                        e.Handled = true;
                        _audioEngine.Seek(0.0);
                        return;
                    }
                    if (key == Key.M)
                    {
                        e.Handled = true;
                        ToggleMasterMute();
                        return;
                    }
                }
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void MenuExportWav_Click(object sender, RoutedEventArgs e)
        {
            CloseOtherTopMenus(null);
            if (_currentScore == null)
            {
                ShowToast("請先開啟一份 MusicXML 樂譜再匯出 WAV");
                return;
            }

            string defaultFileName = Path.GetFileNameWithoutExtension(_currentScore.FileName) + ".wav";
            var dlg = new SaveFileDialog
            {
                Title = "匯出 WAV 音訊檔 (44.1kHz 16-bit 立體聲)",
                InitialDirectory = ThemeManager.GetInitialMusicXmlDirectory(),
                FileName = defaultFileName,
                DefaultExt = ".wav",
                Filter = "WAV 音訊檔案 (*.wav)|*.wav"
            };

            if (dlg.ShowDialog(this) == true)
            {
                string outPath = dlg.FileName;
                ShowToast("⏳ 正在背景高速合成 WAV 音訊檔...");
                string err = string.Empty;
                bool ok = await System.Threading.Tasks.Task.Run(() => _audioEngine.ExportToWavFile(outPath, out err));
                if (ok)
                {
                    ShowToast($"✓ 已匯出 WAV：{Path.GetFileName(outPath)}");
                }
                else
                {
                    MessageBox.Show(this, $"匯出 WAV 失敗：\n{err}", "匯出錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void MenuSoundFontSettings_Click(object sender, RoutedEventArgs e)
        {
            CloseOtherTopMenus(null);
            var dlg = new SoundFontWindow(_audioEngine, () =>
            {
                TxtSoundFontStatus.Text = _audioEngine.GetStatusSummaryText();
                RefreshTrackSoundFontRouting();
                RefreshAllTrackInstrumentOptions();
            })
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        private void MenuPreferences_Click(object sender, RoutedEventArgs e)
        {
            CloseOtherTopMenus(null);
            var before = ThemeManager.CreatePerformanceOptions();
            var dlg = new PreferencesWindow
            {
                Owner = this
            };
            dlg.ShowDialog();

            var after = ThemeManager.CreatePerformanceOptions();
            _audioEngine.UseInstrumentSpatialProfile = after.InstrumentSpatialProfile;
            UpdateViewModeButtonVisual(PianoRoll.ViewMode);
            _audioEngine.ReverbAmount = ThemeManager.ReverbAmount;
            _audioEngine.ChorusAmount = ThemeManager.ChorusAmount;
            _audioEngine.UpdateEffectsSends();

            if (!string.IsNullOrEmpty(_currentFilePath) && before.RequiresScoreReload(after))
            {
                ReloadCurrentScoreWithPerformanceSettings();
            }
        }

        private void ReloadCurrentScoreWithPerformanceSettings()
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath))
                return;

            double position = _audioEngine.IsPlaying
                ? _audioEngine.GetAudiblePlaybackSeconds()
                : _audioEngine.CurrentTimeSeconds;
            bool wasPlaying = _audioEngine.IsPlaying;
            var loopA = _audioEngine.LoopStartSeconds;
            var loopB = _audioEngine.LoopEndSeconds;
            bool loopEnabled = _audioEngine.IsLoopEnabled;

            LoadMusicXmlFile(_currentFilePath, autoPlay: false);

            double target = Math.Min(position, _currentScore?.TotalDurationSeconds ?? position);
            _audioEngine.Seek(target);
            PianoRoll.UpdatePlaybackTime(target);
            PianoKeyboard.UpdatePlaybackTime(target);
            SliderTimeline.Value = Math.Min(SliderTimeline.Maximum, target);
            TxtCurrentTime.Text = FormatTime(target);
            _lastRenderedTimeSec = target;
            _lastDisplayedTenths = (int)(target * 10.0);
            UpdateMusicalTelemetry(target);

            if (loopA.HasValue && loopB.HasValue && loopEnabled)
            {
                _audioEngine.SetLoopPointA(loopA.Value);
                _audioEngine.SetLoopPointB(loopB.Value);
                _loopState = _audioEngine.IsLoopEnabled ? 2 : 0;
            }
            else
            {
                _loopState = 0;
            }
            UpdateLoopButtonUi();

            if (wasPlaying)
            {
                _audioEngine.Play();
                BtnPlayPause.Content = "⏸ 暫停";
            }

            ShowToast("演奏設定已套用");
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            CloseOtherTopMenus(null);
            var dlg = new AboutWindow(_audioEngine.CurrentSoundFontName, _audioEngine.CurrentSoundFontPath)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            PromptAndLoadMusicXml(autoPlay: false);
        }

        private bool PromptAndLoadMusicXml(bool autoPlay)
        {
            CloseOtherTopMenus(null);
            var dlg = new OpenFileDialog
            {
                Title = "開啟 MusicXML 樂譜檔案",
                InitialDirectory = ThemeManager.GetInitialMusicXmlDirectory(),
                Filter = "MusicXML 樂譜檔案 (*.musicxml;*.xml;*.mxl)|*.musicxml;*.xml;*.mxl|所有檔案 (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                LoadMusicXmlFile(dlg.FileName, autoPlay: autoPlay);
                return _currentScore != null;
            }
            return false;
        }

        private void ChkExpandRepeats_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || string.IsNullOrEmpty(_currentFilePath)) return;
            bool wasPlaying = _audioEngine.IsPlaying;
            LoadMusicXmlFile(_currentFilePath, autoPlay: wasPlaying);
        }

        // ==================== 鋼琴卷軸單一按鈕點擊切換模式 ====================

        private void BtnToggleViewMode_Click(object sender, RoutedEventArgs e)
        {
            var next = PianoRoll.ViewMode == PianoRollViewMode.HorizontalTimeline
                ? PianoRollViewMode.VerticalFalling
                : PianoRollViewMode.HorizontalTimeline;
            SetPianoRollViewMode(next, savePref: true);
        }

        private void SetPianoRollViewMode(PianoRollViewMode mode, bool savePref = true)
        {
            if (PianoRoll == null) return;
            PianoRoll.ViewMode = mode;
            if (savePref)
            {
                ThemeManager.SetLastViewMode(mode == PianoRollViewMode.VerticalFalling ? 1 : 0);
            }
            UpdateViewModeButtonVisual(mode);
        }

        private void UpdateViewModeButtonVisual(PianoRollViewMode mode)
        {
            if (BtnToggleViewMode == null) return;
            BtnToggleViewMode.Content = mode == PianoRollViewMode.HorizontalTimeline
                ? "⇄ 水平時間軸卷軸"
                : "⬇ 垂直下落瀑布流";
        }

        // ==================== 最近開啟的樂譜選單 ====================

        private void RefreshRecentFilesMenu()
        {
            if (MenuRecentFiles == null) return;
            MenuRecentFiles.Items.Clear();

            var recents = ThemeManager.RecentFiles;
            var menuStyle = TryFindResource("SystemMenuItemStyle") as Style;
            var sepStyle = TryFindResource("SystemMenuSeparatorStyle") as Style;

            if (recents.Count == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = "(尚無最近開啟紀錄)",
                    IsEnabled = false,
                    Style = menuStyle
                };
                MenuRecentFiles.Items.Add(emptyItem);
                return;
            }

            for (int i = 0; i < recents.Count; i++)
            {
                string path = recents[i];
                string fileName = Path.GetFileName(path);
                var item = new MenuItem
                {
                    Header = $"{i + 1}. {fileName}",
                    ToolTip = path,
                    Style = menuStyle
                };
                item.Click += (_, _) =>
                {
                    CloseOtherTopMenus(null);
                    if (File.Exists(path))
                    {
                        LoadMusicXmlFile(path, autoPlay: false);
                    }
                    else
                    {
                        ShowToast("檔案已不存在或已被移動");
                        RefreshRecentFilesMenu();
                    }
                };
                MenuRecentFiles.Items.Add(item);
            }

            MenuRecentFiles.Items.Add(new Separator { Style = sepStyle });
            var clearItem = new MenuItem
            {
                Header = "清除最近開啟紀錄",
                Style = menuStyle
            };
            clearItem.Click += (_, _) =>
            {
                CloseOtherTopMenus(null);
                ThemeManager.ClearRecentFiles();
                RefreshRecentFilesMenu();
                ShowToast("已清除最近開啟紀錄");
            };
            MenuRecentFiles.Items.Add(clearItem);
        }

        // ==================== 88 鍵鋼琴鍵盤點擊試聽與 A-B 區間循環 ====================

        private void OnPianoKeyboardPreviewChanged(int midiPitch, bool isNoteOn)
        {
            if (isNoteOn)
            {
                _audioEngine.PreviewNoteOn(midiPitch, 98);
            }
            else
            {
                _audioEngine.PreviewNoteOff(midiPitch);
            }
        }

        private void BtnLoopAB_Click(object sender, RoutedEventArgs e)
        {
            AdvanceLoopStateAtTime(_audioEngine.GetVisualTimeSeconds(ThemeManager.VisualSyncOffsetMs / 1000.0));
        }

        private void OnPianoRollLoopPointRightClicked(double clickedTimeSec)
        {
            AdvanceLoopStateAtTime(clickedTimeSec);
        }

        private void AdvanceLoopStateAtTime(double timeSec)
        {
            if (_currentScore == null)
            {
                ShowToast("請先開啟一份 MusicXML 樂譜再設定 A-B 循環");
                return;
            }

            double clamped = Math.Clamp(timeSec, 0.0, _currentScore.TotalDurationSeconds);
            if (_loopState == 0)
            {
                _audioEngine.SetLoopPointA(clamped);
                _loopState = 1;
                UpdateLoopButtonUi();
                ShowToast($"📍 已設定循環起點 A：{FormatTime(clamped)}（再次點擊設定終點 B）");
            }
            else if (_loopState == 1)
            {
                double startA = _audioEngine.LoopStartSeconds ?? 0.0;
                if (Math.Abs(clamped - startA) < 0.15)
                {
                    clamped = Math.Min(_currentScore.TotalDurationSeconds, startA + 2.0);
                }
                _audioEngine.SetLoopPointB(clamped);
                _loopState = _audioEngine.IsLoopEnabled ? 2 : 0;
                UpdateLoopButtonUi();
                if (_audioEngine.IsLoopEnabled)
                {
                    ShowToast($"🔁 已啟用 A-B 循環：{FormatTime(_audioEngine.LoopStartSeconds ?? 0)} ～ {FormatTime(_audioEngine.LoopEndSeconds ?? 0)}");
                }
            }
            else
            {
                _audioEngine.ClearLoop();
                _loopState = 0;
                UpdateLoopButtonUi();
                ShowToast("已取消 A-B 區間循環");
            }
        }

        private void UpdateLoopButtonUi()
        {
            if (BtnLoopAB == null) return;
            PianoRoll.SetLoopMarkers(_audioEngine.LoopStartSeconds, _audioEngine.LoopEndSeconds, _audioEngine.IsLoopEnabled);

            if (_loopState == 1 && _audioEngine.LoopStartSeconds.HasValue)
            {
                BtnLoopAB.Content = $"📍 A: {FormatTime(_audioEngine.LoopStartSeconds.Value)} → 設 B";
            }
            else if (_loopState == 2 && _audioEngine.IsLoopEnabled)
            {
                BtnLoopAB.Content = "🔁 清除 A-B";
            }
            else
            {
                BtnLoopAB.Content = "🔁 A-B 循環";
            }
        }

        // ==================== 小節號點擊跳轉 (Go to Measure) ====================

        private void MenuGotoMeasure_Click(object sender, RoutedEventArgs e)
        {
            CloseOtherTopMenus(null);
            OpenGotoMeasureInput();
        }

        private void MeasureBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenGotoMeasureInput();
        }

        private void OpenGotoMeasureInput()
        {
            if (_currentScore == null || _currentScore.Measures.Count == 0)
            {
                ShowToast("請先開啟一份 MusicXML 樂譜再跳轉小節");
                return;
            }

            MeasureMarker cur = _currentScore.FindMeasureAt(_audioEngine.CurrentTimeSeconds);
            TxtGotoMeasureInput.Text = cur.NumericMeasureNumber.ToString();
            TxtGotoMeasureInput.Visibility = Visibility.Visible;
            TxtGotoMeasureInput.Focus();
            TxtGotoMeasureInput.SelectAll();
        }

        private void CommitGotoMeasureInput()
        {
            if (TxtGotoMeasureInput.Visibility != Visibility.Visible) return;
            string raw = TxtGotoMeasureInput.Text.Trim();
            TxtGotoMeasureInput.Visibility = Visibility.Collapsed;

            if (_currentScore == null || _currentScore.Measures.Count == 0) return;

            if (int.TryParse(raw, out int targetMeasureNum))
            {
                int maxMeas = _currentScore.Measures[^1].NumericMeasureNumber;
                targetMeasureNum = Math.Clamp(targetMeasureNum, 1, Math.Max(1, maxMeas));
                var match = _currentScore.Measures.FirstOrDefault(m => m.NumericMeasureNumber >= targetMeasureNum)
                            ?? _currentScore.Measures[^1];

                double targetSec = match.StartTimeSeconds;
                _audioEngine.Seek(targetSec);
                PianoRoll.UpdatePlaybackTime(targetSec);
                PianoKeyboard.UpdatePlaybackTime(targetSec);
                SliderTimeline.Value = Math.Min(SliderTimeline.Maximum, targetSec);
                TxtCurrentTime.Text = FormatTime(targetSec);
                _lastTelemetryTimelineIdx = -1;
                UpdateMusicalTelemetry(targetSec);
                ShowToast($"已跳轉至第 {match.MeasureNumberText} 小節");
            }
        }

        private void CancelGotoMeasureInput()
        {
            TxtGotoMeasureInput.Visibility = Visibility.Collapsed;
        }

        private void TxtGotoMeasureInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitGotoMeasureInput();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelGotoMeasureInput();
            }
        }

        private void TxtGotoMeasureInput_LostFocus(object sender, RoutedEventArgs e)
        {
            CancelGotoMeasureInput();
        }

        private void BtnLoadSoundFont_Click(object sender, RoutedEventArgs e)
        {
            MenuSoundFontSettings_Click(sender, e);
        }

        // ==================== 左側聲部控制事件與 Toast 浮動訊息 ====================

        private static void InitializeTrackInstrumentOptions(ScorePartTrack track)
        {
            if (track.InstrumentOptions.Count > 0) return;

            foreach (var source in SoundFontManager.GmInstruments)
            {
                track.InstrumentOptions.Add(new GmInstrumentItem
                {
                    Program = source.Program,
                    Category = source.Category,
                    DisplayName = source.DisplayName
                });
            }
        }

        private void RefreshAllTrackInstrumentOptions()
        {
            if (_currentScore == null) return;

            var generalNames = _audioEngine.GetGeneralSoundFontProgramNames();
            var pianoNames = _audioEngine.GetPianoSoundFontProgramNames();
            bool hasGeneralSoundFont = !string.IsNullOrEmpty(_audioEngine.CurrentSoundFontPath);

            foreach (var track in _currentScore.Tracks)
            {
                RefreshTrackInstrumentOptions(track, generalNames, pianoNames, hasGeneralSoundFont);
            }
        }

        private void RefreshTrackInstrumentOptions(ScorePartTrack track)
        {
            var generalNames = _audioEngine.GetGeneralSoundFontProgramNames();
            var pianoNames = _audioEngine.GetPianoSoundFontProgramNames();
            bool hasGeneralSoundFont = !string.IsNullOrEmpty(_audioEngine.CurrentSoundFontPath);

            RefreshTrackInstrumentOptions(track, generalNames, pianoNames, hasGeneralSoundFont);
        }

        private void RefreshTrackInstrumentOptions(
            ScorePartTrack track,
            Dictionary<int, string> generalNames,
            Dictionary<int, string> pianoNames,
            bool hasGeneralSoundFont)
        {
            InitializeTrackInstrumentOptions(track);

            bool usesDedicatedPiano = _audioEngine.IsTrackUsingDedicatedPiano(track);
            bool hasDedicatedPiano = _audioEngine.HasDedicatedPianoSoundFont;

            for (int program = 0; program < 128 && program < track.InstrumentOptions.Count; program++)
            {
                var item = track.InstrumentOptions[program];
                string fallbackName = SoundFontManager.GmInstruments[program].DisplayName;
                string zhName = ExtractChineseInstrumentName(fallbackName);

                if (usesDedicatedPiano && program <= 5)
                {
                    if (pianoNames.TryGetValue(program, out string? pianoPresetName) &&
                        !string.IsNullOrWhiteSpace(pianoPresetName))
                    {
                        item.DisplayName = $"{zhName} ｜ 獨立鋼琴：{pianoPresetName}";
                    }
                    else
                    {
                        item.DisplayName = $"{zhName} ｜ 獨立鋼琴：{_audioEngine.PianoSoundFontName}";
                    }

                    item.IsAvailable = true;
                    continue;
                }

                // 一般聲部選到 GM 001–006 時會切換至獨立鋼琴，因此保留可選並提示。
                if (!usesDedicatedPiano && program <= 5 && hasDedicatedPiano)
                {
                    if (generalNames.TryGetValue(program, out string? generalPianoName) &&
                        !string.IsNullOrWhiteSpace(generalPianoName))
                    {
                        item.DisplayName = $"{zhName} ｜ {generalPianoName}（切換至獨立鋼琴）";
                    }
                    else
                    {
                        item.DisplayName = $"{zhName}（切換至獨立鋼琴）";
                    }

                    item.IsAvailable = true;
                    continue;
                }

                if (!hasGeneralSoundFont)
                {
                    item.DisplayName = fallbackName;
                    item.IsAvailable = true;
                    continue;
                }

                if (generalNames.TryGetValue(program, out string? presetName) &&
                    !string.IsNullOrWhiteSpace(presetName))
                {
                    item.DisplayName = $"{zhName} ｜ {presetName}";
                    item.IsAvailable = true;
                }
                else
                {
                    item.DisplayName = $"{zhName}（音源未提供，將使用替代音色）";
                    item.IsAvailable = false;
                }
            }
        }

        private static string ExtractChineseInstrumentName(string displayName)
        {
            int separatorIndex = displayName.IndexOf(" (", StringComparison.Ordinal);
            return separatorIndex > 0 ? displayName[..separatorIndex] : displayName;
        }

        private void RefreshTrackSoundFontRouting()
        {
            if (_currentScore == null) return;

            foreach (var track in _currentScore.Tracks)
            {
                track.UsesDedicatedPianoSynth = _audioEngine.IsTrackUsingDedicatedPiano(track);
                track.SoundFontRouteText = _audioEngine.GetTrackSoundFontRouteText(track);
            }
        }

        private void TrackMuteOrSolo_Click(object sender, RoutedEventArgs e)
        {
            _audioEngine.SyncTrackSettings();
            PianoRoll.InvalidateVisual();
            PianoKeyboard.InvalidateVisual();
        }

        private void TrackInstrument_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _audioEngine.SyncTrackSettings();
            RefreshTrackSoundFontRouting();

            if (sender is ComboBox comboBox && comboBox.DataContext is ScorePartTrack track)
            {
                RefreshTrackInstrumentOptions(track);
            }
            else
            {
                RefreshAllTrackInstrumentOptions();
            }
        }

        private void TrackVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _audioEngine.UpdateTrackVolumesOnly();
        }

        private void TrackVolumeSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Slider slider && slider.DataContext is ScorePartTrack track)
            {
                e.Handled = true;
                track.Volume = 1.0;
                _audioEngine.UpdateTrackVolumesOnly();
            }
        }

        private void TrackVisibility_Changed(object sender, RoutedEventArgs e)
        {
            PianoRoll?.InvalidateVisual();
            PianoKeyboard?.InvalidateVisual();
        }

        private void BtnResetTracks_Click(object sender, RoutedEventArgs e)
        {
            if (_currentScore == null || _currentScore.Tracks.Count == 0) return;
            foreach (var tr in _currentScore.Tracks)
            {
                tr.IsMuted = false;
                tr.IsSolo = false;
                tr.IsVisibleOnRoll = true;
                tr.Volume = 1.0;
                tr.GmProgram = tr.InitialGmProgram;
            }
            _audioEngine.SyncTrackSettings();
            RefreshTrackSoundFontRouting();
            RefreshAllTrackInstrumentOptions();
            PianoRoll.InvalidateVisual();
            PianoKeyboard.InvalidateVisual();

            ShowToast("全部聲部已重置");
        }

        public void ShowToast(string message)
        {
            TxtToastMessage.Text = message;
            ToastBanner.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160));
            ToastBanner.BeginAnimation(OpacityProperty, fadeIn);

            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1800)
                };
                _toastTimer.Tick += (_, _) =>
                {
                    _toastTimer.Stop();
                    var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(240));
                    fadeOut.Completed += (_, _) =>
                    {
                        if (ToastBanner.Opacity <= 0.05)
                        {
                            ToastBanner.Visibility = Visibility.Collapsed;
                        }
                    };
                    ToastBanner.BeginAnimation(OpacityProperty, fadeOut);
                };
            }

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ==================== 播放與時間軸控制 ====================

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_currentScore == null)
            {
                PromptAndLoadMusicXml(autoPlay: true);
                return;
            }

            if (_audioEngine.IsPlaying)
            {
                _audioEngine.Pause();
                BtnPlayPause.Content = "▶ 播放";
            }
            else
            {
                _audioEngine.Play();
                BtnPlayPause.Content = "⏸ 暫停";
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _audioEngine.Stop();
            BtnPlayPause.Content = "▶ 播放";
            PianoRoll.UpdatePlaybackTime(0);
            PianoKeyboard.UpdatePlaybackTime(0);
        }

        private void BtnStepBack_Click(object sender, RoutedEventArgs e)
        {
            _audioEngine.Seek(_audioEngine.CurrentTimeSeconds - 5.0);
        }

        private void BtnStepForward_Click(object sender, RoutedEventArgs e)
        {
            _audioEngine.Seek(_audioEngine.CurrentTimeSeconds + 5.0);
        }

        private void SliderTimeline_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSliderDragging = true;
        }

        private void SliderTimeline_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSliderDragging)
            {
                _isSliderDragging = false;
                _audioEngine.Seek(SliderTimeline.Value);
            }
        }

        private void SliderTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSliderDragging)
            {
                _audioEngine.Seek(e.NewValue);
                PianoRoll.UpdatePlaybackTime(e.NewValue);
                PianoKeyboard.UpdatePlaybackTime(e.NewValue);
            }
        }

        private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_audioEngine == null || TxtSpeedValue == null) return;
            _audioEngine.PlaybackSpeed = e.NewValue;
            TxtSpeedValue.Text = $"{e.NewValue:0.00}x";
        }

        private void BtnResetSpeed_Click(object sender, RoutedEventArgs e)
        {
            SliderSpeed.Value = 1.0;
        }

        private void SliderMasterVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_audioEngine == null || TxtMasterVolume == null) return;
            double snapped = Math.Round(e.NewValue * 20.0) / 20.0;
            if (Math.Abs(SliderMasterVolume.Value - snapped) > 0.001)
            {
                SliderMasterVolume.Value = snapped;
                return;
            }

            _audioEngine.MasterVolume = snapped;
            TxtMasterVolume.Text = $"{(int)Math.Round(snapped * 100)}%";

            if (snapped > 0.001)
            {
                _isMasterMuted = false;
                _masterVolumeBeforeMute = snapped;
                if (TxtMasterVolumeLabel != null)
                    TxtMasterVolumeLabel.Text = "🔊 主音量：";
            }
            else
            {
                _isMasterMuted = true;
                if (TxtMasterVolumeLabel != null)
                    TxtMasterVolumeLabel.Text = "🔇 已靜音：";
            }
        }

        private void SliderMasterVolume_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SliderMasterVolume.Value = 1.0;
        }

        private void TxtMasterVolumeLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleMasterMute();
        }

        private void ToggleMasterMute()
        {
            if (_isMasterMuted || SliderMasterVolume.Value <= 0.001)
            {
                double restore = _masterVolumeBeforeMute > 0.05 ? _masterVolumeBeforeMute : 1.0;
                SliderMasterVolume.Value = Math.Round(restore * 20.0) / 20.0;
                ShowToast($"🔊 已恢復主音量 ({TxtMasterVolume.Text})");
            }
            else
            {
                _masterVolumeBeforeMute = SliderMasterVolume.Value;
                SliderMasterVolume.Value = 0.0;
                ShowToast("🔇 主音量已靜音 (按 M 可恢復)");
            }
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            PianoRoll.VisibleWindowSeconds *= 0.8;
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            PianoRoll.VisibleWindowSeconds *= 1.25;
        }

        // ==================== 檔案拖放支援 ====================

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0)
            {
                string f = files[0];
                if (f.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase))
                {
                    if (_audioEngine.LoadSoundFont(f, out string err))
                    {
                        TxtSoundFontStatus.Text = _audioEngine.GetStatusSummaryText();
            _audioEngine.ReverbAmount = ThemeManager.ReverbAmount;
            _audioEngine.ChorusAmount = ThemeManager.ChorusAmount;

            RefreshTrackSoundFontRouting();
                        RefreshAllTrackInstrumentOptions();
                    }
                    else
                    {
                        MessageBox.Show(this, err, "音源載入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    LoadMusicXmlFile(f, autoPlay: false);
                }
            }
        }

        public void CaptureScreenshotAt(string musicFilePath, double seekTimeSec, PianoRollViewMode mode, string outPngPath)
        {
            LoadMusicXmlFile(musicFilePath, autoPlay: false);
            SetPianoRollViewMode(mode);

            _audioEngine.Seek(seekTimeSec);
            PianoRoll.UpdatePlaybackTime(seekTimeSec);
            PianoKeyboard.UpdatePlaybackTime(seekTimeSec);
            SliderTimeline.Value = Math.Min(SliderTimeline.Maximum, seekTimeSec);
            TxtCurrentTime.Text = FormatTime(seekTimeSec);
            UpdateMusicalTelemetry(seekTimeSec);

            if (_currentScore != null)
            {
                for (int i = 0; i < _currentScore.Tracks.Count; i++)
                {
                    _currentScore.Tracks[i].CurrentActivity = 0.45 + (i % 3) * 0.18;
                }
            }

            UpdateLayout();
            PianoRoll.InvalidateVisual();
            PianoKeyboard.InvalidateVisual();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            int width = (int)Math.Max(900, ActualWidth > 0 ? ActualWidth : Width);
            int height = (int)Math.Max(540, ActualHeight > 0 ? ActualHeight : Height);

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(outPngPath)!);
            using var fs = new FileStream(outPngPath, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        public void RunUiSelfTest(string sampleMusicXmlFile)
        {
            UpdateLayout();

            // 1. 初始無聲部時「重置」按鈕應隱藏
            if (BtnResetTracks.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Expected BtnResetTracks to be Collapsed before loading score.");

            // 2. 測試點擊頂層系統選單自動展開與 ContentElement (Run) 父節點查找不崩潰
            var dummyRun = new System.Windows.Documents.Run("檔案(F)");
            _ = FindAncestor<MenuItem>(dummyRun);

            MenuFile.IsSubmenuOpen = true;
            UpdateLayout();
            MenuFile.IsSubmenuOpen = false;

            MenuSettings.IsSubmenuOpen = true;
            UpdateLayout();
            MenuSettings.IsSubmenuOpen = false;

            MenuHelp.IsSubmenuOpen = true;
            UpdateLayout();
            MenuHelp.IsSubmenuOpen = false;

            // 3. 測試單一按鈕切換鋼琴卷軸模式
            SetPianoRollViewMode(PianoRollViewMode.HorizontalTimeline, savePref: false);
            BtnToggleViewMode_Click(this, new RoutedEventArgs());
            if (PianoRoll.ViewMode != PianoRollViewMode.VerticalFalling)
                throw new InvalidOperationException("Expected VerticalFalling after first toggle.");
            BtnToggleViewMode_Click(this, new RoutedEventArgs());
            if (PianoRoll.ViewMode != PianoRollViewMode.HorizontalTimeline)
                throw new InvalidOperationException("Expected HorizontalTimeline after second toggle.");

            // 4. 載入樂譜後「重置」按鈕應顯示，點擊後顯示「全部聲部已重置」Toast
            LoadMusicXmlFile(sampleMusicXmlFile, autoPlay: false);
            if (BtnResetTracks.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Expected BtnResetTracks to be Visible after loading score.");

            BtnResetTracks_Click(this, new RoutedEventArgs());
            if (ToastBanner.Visibility != Visibility.Visible || TxtToastMessage.Text != "全部聲部已重置")
                throw new InvalidOperationException("Expected ToastBanner with '全部聲部已重置' to be Visible.");
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        }
    }
}
