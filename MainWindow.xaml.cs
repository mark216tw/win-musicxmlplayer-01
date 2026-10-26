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
        private DispatcherTimer? _toastTimer;

        public List<GmInstrumentItem> GmInstrumentList => SoundFontManager.GmInstruments;

        public MainWindow()
        {
            ThemeManager.Initialize();
            InitializeComponent();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowDwmStyle(this);
            FitWindowToWorkArea();
            DataContext = this;

            // 綁定鋼琴卷軸滑鼠左右滑動調整時間事件
            PianoRoll.ScrubStarted += OnPianoRollScrubStarted;
            PianoRoll.TimeScrubbed += OnPianoRollTimeScrubbed;
            PianoRoll.ScrubEnded += OnPianoRollScrubEnded;

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

            if (!string.IsNullOrEmpty(sf2Path) && _audioEngine.LoadSoundFont(sf2Path, out string _))
            {
                if (!string.IsNullOrWhiteSpace(ThemeManager.PianoSoundFontPath) && File.Exists(ThemeManager.PianoSoundFontPath))
                {
                    _audioEngine.LoadPianoSoundFont(ThemeManager.PianoSoundFontPath, out string _);
                }
                TxtSoundFontStatus.Text = _audioEngine.GetStatusSummaryText();
            }
            else
            {
                TxtSoundFontStatus.Text = "目前音源：尚未載入 .sf2 音源檔（請點擊右上角設定音源）";
            }

            UpdateViewModeButtonVisual(PianoRoll.ViewMode);

            // 2. 預設不載入任何 MusicXML 樂譜，啟動 60FPS 畫面同步迴圈
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _audioEngine.Dispose();
        }

        public void LoadMusicXmlFile(string filePath, bool autoPlay = false)
        {
            try
            {
                _audioEngine.Stop();
                BtnPlayPause.Content = "▶ 播放";

                bool expandRepeats = ChkExpandRepeats.IsChecked == true;
                var score = MusicXmlLoader.LoadFromFile(filePath, expandRepeats);

                _currentScore = score;
                _currentFilePath = filePath;
                ThemeManager.SetLastOpenedDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)));

                TxtScoreTitle.Text = string.IsNullOrWhiteSpace(score.Composer)
                    ? score.Title
                    : $"{score.Title} — {score.Composer}";

                string repeatTag = score.HasRepeats ? (expandRepeats ? "已展開反覆記號" : "不展開反覆") : "無反覆記號";
                TxtScoreSubtitle.Text = $"共 {score.Tracks.Count} 個可操作聲部 · {score.Measures.Count} 小節 · 總音符 {score.Notes.Count} · {repeatTag}";

                TxtTrackSummary.Text = $"共 {score.Tracks.Count} 個聲部軌道（可獨立靜音、獨奏、換樂器、調音量）";

                TracksItemsControl.ItemsSource = null;
                TracksItemsControl.ItemsSource = score.Tracks;

                // 有聲部時才顯示「重置」按鈕，無聲部時隱藏
                BtnResetTracks.Visibility = score.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                PianoRoll.SetScore(score);
                PianoKeyboard.SetScore(score);
                _audioEngine.LoadScore(score);

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

            double curSec = _audioEngine.CurrentTimeSeconds;

            PianoRoll.UpdatePlaybackTime(curSec);
            PianoKeyboard.UpdatePlaybackTime(curSec);

            if (!_isSliderDragging)
            {
                SliderTimeline.Value = Math.Min(SliderTimeline.Maximum, curSec);
            }

            TxtCurrentTime.Text = FormatTime(curSec);
            UpdateMusicalTelemetry(curSec);
            UpdateTrackActivityMeters(curSec);
        }

        private void UpdateMusicalTelemetry(double curSec)
        {
            if (_currentScore == null || _currentScore.Measures.Count == 0) return;

            var measures = _currentScore.Measures;
            MeasureMarker currentMeas = measures[0];

            for (int i = 0; i < measures.Count; i++)
            {
                if (measures[i].StartTimeSeconds <= curSec + 0.005)
                    currentMeas = measures[i];
                else
                    break;
            }

            TxtMeasureStatus.Text = $"小節：{currentMeas.MeasureNumberText} / {measures[^1].NumericMeasureNumber}";
            TxtTimeSigStatus.Text = $"拍號：{currentMeas.Beats}/{currentMeas.BeatType}";
            TxtKeySigStatus.Text = $"調號：{currentMeas.KeySignatureName}";
            TxtTempoStatus.Text = $"速度：{Math.Round(currentMeas.TempoBpm * _audioEngine.PlaybackSpeed)} BPM";
        }

        private void UpdateTrackActivityMeters(double curSec)
        {
            if (_currentScore == null) return;

            bool anySolo = _currentScore.Tracks.Any(t => t.IsSolo);
            double[] maxLevels = new double[_currentScore.Tracks.Count];

            if (_audioEngine.IsPlaying)
            {
                var notes = _currentScore.Notes;
                int lo = 0, hi = notes.Count - 1, idx = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (notes[mid].StartTimeSeconds <= curSec)
                    {
                        idx = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                int start = Math.Max(0, idx - 180);
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
                _currentScore.Tracks[t].CurrentActivity = Math.Clamp(smoothed, 0.0, 1.0);
            }
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
            else if (mods == ModifierKeys.None && key == Key.F1)
            {
                e.Handled = true;
                MenuAbout_Click(this, new RoutedEventArgs());
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MenuSoundFontSettings_Click(object sender, RoutedEventArgs e)
        {
            CloseOtherTopMenus(null);
            var dlg = new SoundFontWindow(_audioEngine, () =>
            {
                TxtSoundFontStatus.Text = _audioEngine.GetStatusSummaryText();
            })
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        private void MenuPreferences_Click(object sender, RoutedEventArgs e)
        {
            CloseOtherTopMenus(null);
            var dlg = new PreferencesWindow
            {
                Owner = this
            };
            dlg.ShowDialog();
            UpdateViewModeButtonVisual(PianoRoll.ViewMode);
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
            SetPianoRollViewMode(next);
        }

        private void SetPianoRollViewMode(PianoRollViewMode mode)
        {
            if (PianoRoll == null) return;
            PianoRoll.ViewMode = mode;
            UpdateViewModeButtonVisual(mode);
        }

        private void UpdateViewModeButtonVisual(PianoRollViewMode mode)
        {
            if (BtnToggleViewMode == null) return;
            BtnToggleViewMode.Content = mode == PianoRollViewMode.HorizontalTimeline
                ? "⇄ 水平時間軸卷軸"
                : "⬇ 垂直下落瀑布流";
        }

        private void BtnLoadSoundFont_Click(object sender, RoutedEventArgs e)
        {
            MenuSoundFontSettings_Click(sender, e);
        }

        // ==================== 左側聲部控制事件與 Toast 浮動訊息 ====================

        private void TrackMuteOrSolo_Click(object sender, RoutedEventArgs e)
        {
            _audioEngine.SyncTrackSettings();
            PianoRoll.InvalidateVisual();
            PianoKeyboard.InvalidateVisual();
        }

        private void TrackInstrument_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _audioEngine.SyncTrackSettings();
        }

        private void TrackVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _audioEngine.SyncTrackSettings();
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
            _audioEngine.MasterVolume = e.NewValue;
            TxtMasterVolume.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
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
                        TxtSoundFontStatus.Text = $"目前音源：{_audioEngine.CurrentSoundFontName} (真實樂器取樣)";
                    else
                        MessageBox.Show(this, err, "音源載入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
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
