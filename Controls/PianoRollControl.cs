using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinMusicXmlPlayer.Models;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer.Controls
{
    public enum PianoRollViewMode
    {
        HorizontalTimeline = 0,
        VerticalFalling = 1
    }

    public class PianoRollControl : FrameworkElement
    {
        private MusicScore? _score;
        private double _currentTimeSec;
        private PianoRollViewMode _viewMode = PianoRollViewMode.HorizontalTimeline;
        // 預設視野縮小 4 級：8.0 * (1.25^4) = 19.53 秒
        private double _visibleWindowSeconds = 19.53;

        // 滑鼠左右拖拉調整時間狀態
        private const double ClickDragThresholdPx = 4.0;
        private bool _isDraggingTime;
        private bool _leftPressPendingClick;
        private bool _clickTogglesPlayPause;
        private bool _suppressClickToggle;
        private Point _dragStartPoint;
        private double _dragStartTimeSec;
        private bool _dragFromTopRuler;

        // A-B 區間循環視覺標記與滑鼠懸停音符狀態
        private double? _loopStartSec;
        private double? _loopEndSec;
        private bool _isLoopEnabled;
        private NoteEvent? _hoveredNote;

        private static readonly Brush LoopRegionFillBrush = CreateFrozenBrush(Color.FromArgb(36, 251, 191, 36));
        private static readonly Brush LoopRulerBadgeBrush = CreateFrozenBrush(Color.FromArgb(220, 217, 119, 6));
        private static readonly Pen LoopBoundaryPen = CreateFrozenPen(Color.FromRgb(251, 191, 36), 1.8);

        private static readonly Typeface UiTypeface = new(new FontFamily("Segoe UI, Microsoft JhengHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        // 深色模式畫筆
        private static readonly Brush DarkBgBrush = new SolidColorBrush(Color.FromRgb(11, 17, 30));
        private static readonly Brush DarkBlackKeyLaneBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42));
        private static readonly Brush DarkTopRulerBrush = new SolidColorBrush(Color.FromRgb(22, 32, 52));
        private static readonly Brush DarkLeftPitchBarBrush = new SolidColorBrush(Color.FromRgb(18, 26, 44));

        // 淺色模式畫筆
        private static readonly Brush LightBgBrush = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        private static readonly Brush LightBlackKeyLaneBrush = new SolidColorBrush(Color.FromRgb(236, 242, 249));
        private static readonly Brush LightTopRulerBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        private static readonly Brush LightLeftPitchBarBrush = new SolidColorBrush(Color.FromRgb(238, 242, 246));

        private static readonly Pen OctaveGridPen = new(new SolidColorBrush(Color.FromArgb(65, 148, 163, 184)), 1.0);
        private static readonly Pen PitchGridPen = new(new SolidColorBrush(Color.FromArgb(25, 148, 163, 184)), 1.0);
        private static readonly Pen MeasureLinePen = new(new SolidColorBrush(Color.FromArgb(120, 148, 163, 184)), 1.2);
        private static readonly Pen BeatLinePen = new(new SolidColorBrush(Color.FromArgb(38, 148, 163, 184)), 1.0);
        private static readonly Pen ActiveNoteBorderPen = new(Brushes.White, 1.6);

        private static readonly Brush DarkRulerLabelBrush = CreateFrozenBrush(Color.FromRgb(203, 213, 225));
        private static readonly Brush LightRulerLabelBrush = CreateFrozenBrush(Color.FromRgb(51, 65, 85));
        private static readonly Brush DarkNormalPitchBrush = CreateFrozenBrush(Color.FromRgb(148, 163, 184));
        private static readonly Brush LightNormalPitchBrush = CreateFrozenBrush(Color.FromRgb(71, 85, 105));
        private static readonly Brush DarkHintBgBrush = CreateFrozenBrush(Color.FromArgb(195, 15, 23, 42));
        private static readonly Brush LightHintBgBrush = CreateFrozenBrush(Color.FromArgb(225, 255, 255, 255));
        private static readonly Pen DarkHintBorderPen = CreateFrozenPen(Color.FromRgb(51, 65, 85), 1.0);
        private static readonly Pen LightHintBorderPen = CreateFrozenPen(Color.FromRgb(203, 213, 225), 1.0);

        // 快取的主題連動 Frozen Brushes / Pens，僅在主題變更時重建
        private Color _cachedBgColor;
        private Color _cachedBlackLaneColor;
        private Color _cachedRulerColor;
        private Color _cachedAccentColor;
        private Brush _rollBgBrush = DarkBgBrush;
        private Brush _blackLaneBrush = DarkBlackKeyLaneBrush;
        private Brush _rulerBrush = DarkTopRulerBrush;
        private Brush _accentBrush = Brushes.DodgerBlue;
        private Brush _playheadGlowBrush = Brushes.Transparent;
        private Pen _playheadPen = new(Brushes.DodgerBlue, 2.4);
        private Pen _scrubBannerBorderPen = new(Brushes.DodgerBlue, 1.5);

        // 快取的 FormattedText，避免每幀重複呼叫 DirectWrite 文字排版
        private double _cachedTextDpi;
        private bool _cachedTextIsDark;
        private Color _cachedTextAccentColor;
        private FormattedText? _cachedHintFt;
        private FormattedText? _cachedEmptyFt;
        private readonly FormattedText?[] _cachedPitchLabels = new FormattedText?[128];

        // 小節標籤與裁剪幾何快取，避免每幀重新配置 FormattedText / RectangleGeometry
        private FormattedText?[] _cachedHorizontalMeasureLabels = Array.Empty<FormattedText?>();
        private FormattedText?[] _cachedVerticalMeasureLabels = Array.Empty<FormattedText?>();
        private RectangleGeometry? _cachedClipGeometry;
        private double _cachedClipWidth = -1.0;
        private double _cachedClipHeight = -1.0;
        private FormattedText? _cachedLoopAFt;
        private FormattedText? _cachedLoopBFt;
        private double _cachedLoopLabelDpi = -1.0;

        private static SolidColorBrush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Pen CreateFrozenPen(Color c, double thickness)
        {
            var p = new Pen(CreateFrozenBrush(c), thickness);
            p.Freeze();
            return p;
        }

        private void EnsureThemeBrushesUpdated()
        {
            Color bg = ThemeManager.CurrentRollBgColor;
            Color lane = ThemeManager.CurrentRollBlackLaneColor;
            Color ruler = ThemeManager.CurrentRollRulerColor;
            Color accent = ThemeManager.CurrentAccentColor;

            if (bg != _cachedBgColor)
            {
                _cachedBgColor = bg;
                _rollBgBrush = CreateFrozenBrush(bg);
            }
            if (lane != _cachedBlackLaneColor)
            {
                _cachedBlackLaneColor = lane;
                _blackLaneBrush = CreateFrozenBrush(lane);
            }
            if (ruler != _cachedRulerColor)
            {
                _cachedRulerColor = ruler;
                _rulerBrush = CreateFrozenBrush(ruler);
            }
            if (accent != _cachedAccentColor)
            {
                _cachedAccentColor = accent;
                _accentBrush = CreateFrozenBrush(accent);
                _playheadGlowBrush = CreateFrozenBrush(Color.FromArgb(46, accent.R, accent.G, accent.B));
                _playheadPen = CreateFrozenPen(accent, 2.4);
                _scrubBannerBorderPen = CreateFrozenPen(accent, 1.5);
            }
        }

        private void EnsureStaticTextsCached(double dpi, bool isDark)
        {
            Color accent = ThemeManager.CurrentAccentColor;
            if (Math.Abs(_cachedTextDpi - dpi) <= 0.001 &&
                _cachedTextIsDark == isDark &&
                _cachedTextAccentColor == accent &&
                _cachedHintFt != null)
            {
                return;
            }

            _cachedTextDpi = dpi;
            _cachedTextIsDark = isDark;
            _cachedTextAccentColor = accent;

            _cachedHintFt = new FormattedText(
                "⏯ 音符區單擊播放/暫停 · ⇄ 左鍵拖曳調時間 · 右鍵設 A-B 循環 · 滾輪縮放 · 點擊琴鍵試聽",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                11.0,
                isDark ? DarkRulerLabelBrush : LightRulerLabelBrush,
                dpi);

            _cachedEmptyFt = new FormattedText(
                "請點擊左上角「開啟 MusicXML」或將樂譜檔案拖曳至此視窗開始播放",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                15.0,
                isDark ? DarkNormalPitchBrush : LightNormalPitchBrush,
                dpi);

            Array.Clear(_cachedPitchLabels, 0, _cachedPitchLabels.Length);
            InvalidateMeasureLabelCache();
        }

        private void InvalidateMeasureLabelCache()
        {
            _cachedHorizontalMeasureLabels = Array.Empty<FormattedText?>();
            _cachedVerticalMeasureLabels = Array.Empty<FormattedText?>();
            _cachedClipGeometry = null;
            _cachedClipWidth = -1.0;
            _cachedClipHeight = -1.0;
            _cachedLoopAFt = null;
            _cachedLoopBFt = null;
            _cachedLoopLabelDpi = -1.0;
        }

        private FormattedText GetCachedMeasureLabel(int index, MeasureMarker measure, bool vertical, Brush brush, double dpi)
        {
            if (vertical)
            {
                if (_cachedVerticalMeasureLabels.Length < _score!.Measures.Count)
                    _cachedVerticalMeasureLabels = new FormattedText?[_score.Measures.Count];

                var existing = _cachedVerticalMeasureLabels[index];
                if (existing != null)
                    return existing;

                string label = string.IsNullOrEmpty(measure.SectionText)
                    ? $"小節 {measure.MeasureNumberText}"
                    : $"小節 {measure.MeasureNumberText} · {measure.SectionText}";
                var created = new FormattedText(
                    label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    UiTypeface,
                    11.0,
                    brush,
                    dpi);
                _cachedVerticalMeasureLabels[index] = created;
                return created;
            }
            else
            {
                if (_cachedHorizontalMeasureLabels.Length < _score!.Measures.Count)
                    _cachedHorizontalMeasureLabels = new FormattedText?[_score.Measures.Count];

                var existing = _cachedHorizontalMeasureLabels[index];
                if (existing != null)
                    return existing;

                string label = string.IsNullOrEmpty(measure.SectionText)
                    ? $"M.{measure.MeasureNumberText}"
                    : $"M.{measure.MeasureNumberText} · {measure.SectionText}";
                var created = new FormattedText(
                    label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    UiTypeface,
                    11.0,
                    brush,
                    dpi);
                _cachedHorizontalMeasureLabels[index] = created;
                return created;
            }
        }

        private FormattedText GetCachedLoopLabel(char label, double dpi)
        {
            if (Math.Abs(_cachedLoopLabelDpi - dpi) > 0.001)
            {
                _cachedLoopAFt = null;
                _cachedLoopBFt = null;
                _cachedLoopLabelDpi = dpi;
            }

            FormattedText? ft = label == 'A' ? _cachedLoopAFt : _cachedLoopBFt;
            if (ft == null)
            {
                ft = new FormattedText(
                    label.ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    UiTypeface,
                    10.5,
                    Brushes.White,
                    dpi);
                if (label == 'A')
                    _cachedLoopAFt = ft;
                else
                    _cachedLoopBFt = ft;
            }
            return ft;
        }

        private FormattedText GetCachedPitchLabel(int pitch, double dpi, bool isDark)
        {
            int clamped = Math.Clamp(pitch, 0, 127);
            var ft = _cachedPitchLabels[clamped];
            if (ft != null)
                return ft;

            Brush normalPitchBrush = isDark ? DarkNormalPitchBrush : LightNormalPitchBrush;
            ft = new FormattedText(
                NoteEvent.GetPitchName(clamped),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                10.0,
                clamped == 60 ? _accentBrush : normalPitchBrush,
                dpi);
            _cachedPitchLabels[clamped] = ft;
            return ft;
        }

        public event Action<double>? TimeScrubbed;
        public event Action? ScrubStarted;
        public event Action? ScrubEnded;
        public event Action? PlayPauseToggleRequested;
        public event Action<double>? LoopPointRightClicked;
        public event Action<double>? VisibleWindowChanged;

        static PianoRollControl()
        {
            DarkBgBrush.Freeze();
            DarkBlackKeyLaneBrush.Freeze();
            DarkTopRulerBrush.Freeze();
            DarkLeftPitchBarBrush.Freeze();
            LightBgBrush.Freeze();
            LightBlackKeyLaneBrush.Freeze();
            LightTopRulerBrush.Freeze();
            LightLeftPitchBarBrush.Freeze();
            OctaveGridPen.Freeze();
            PitchGridPen.Freeze();
            MeasureLinePen.Freeze();
            BeatLinePen.Freeze();
            ActiveNoteBorderPen.Freeze();
        }

        public PianoRollControl()
        {
            ClipToBounds = true;
            Cursor = Cursors.SizeWE;
            Focusable = false;

            ThemeManager.ThemeChanged += () =>
            {
                Dispatcher.BeginInvoke(InvalidateVisual);
            };
        }

        public PianoRollViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                if (_viewMode != value)
                {
                    _viewMode = value;
                    InvalidateVisual();
                }
            }
        }

        public double VisibleWindowSeconds
        {
            get => _visibleWindowSeconds;
            set
            {
                double clamped = Math.Clamp(value, 3.0, 40.0);
                if (Math.Abs(_visibleWindowSeconds - clamped) > 0.01)
                {
                    _visibleWindowSeconds = clamped;
                    VisibleWindowChanged?.Invoke(_visibleWindowSeconds);
                    InvalidateVisual();
                }
            }
        }

        public void SetScore(MusicScore? score)
        {
            _score = score;
            _currentTimeSec = 0;
            _loopStartSec = null;
            _loopEndSec = null;
            _isLoopEnabled = false;
            _hoveredNote = null;
            InvalidateMeasureLabelCache();
            InvalidateVisual();
        }

        public void SetLoopMarkers(double? startSec, double? endSec, bool isEnabled)
        {
            _loopStartSec = startSec;
            _loopEndSec = endSec;
            _isLoopEnabled = isEnabled;
            InvalidateVisual();
        }

        public void UpdatePlaybackTime(double currentTimeSec)
        {
            if (!_isDraggingTime)
            {
                _currentTimeSec = currentTimeSec;
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (_score == null) return;

            _leftPressPendingClick = true;
            _isDraggingTime = false;
            _dragStartPoint = e.GetPosition(this);
            _dragStartTimeSec = _currentTimeSec;
            _dragFromTopRuler = _dragStartPoint.Y <= 30.0 && _viewMode == PianoRollViewMode.HorizontalTimeline;
            _clickTogglesPlayPause = IsInToggleArea(_dragStartPoint);
            _suppressClickToggle = e.ClickCount > 1;

            CaptureMouse();
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            if (_score == null) return;

            Point pt = e.GetPosition(this);
            double clickedSec = ResolveTimeAtPoint(pt);
            LoopPointRightClicked?.Invoke(clickedSec);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_score == null) return;

            Point pt = e.GetPosition(this);
            if (_leftPressPendingClick)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    _leftPressPendingClick = false;
                    _clickTogglesPlayPause = false;
                    _suppressClickToggle = false;
                    return;
                }

                double dx = pt.X - _dragStartPoint.X;
                double dy = pt.Y - _dragStartPoint.Y;
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= ClickDragThresholdPx)
                {
                    _leftPressPendingClick = false;
                    _clickTogglesPlayPause = false;
                    _suppressClickToggle = false;
                    _isDraggingTime = true;
                    _dragStartTimeSec = _currentTimeSec;
                    ScrubStarted?.Invoke();
                    UpdateScrubFromMouse(pt);
                }
                return;
            }

            if (_isDraggingTime)
            {
                UpdateScrubFromMouse(pt);
                return;
            }

            var hitNote = HitTestNoteAtPoint(pt);
            if (!ReferenceEquals(hitNote, _hoveredNote))
            {
                _hoveredNote = hitNote;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredNote != null)
            {
                _hoveredNote = null;
                InvalidateVisual();
            }
        }

        private bool IsInToggleArea(Point pt)
        {
            if (_viewMode == PianoRollViewMode.VerticalFalling)
                return true;

            const double rulerH = 30.0;
            const double pitchBarW = 44.0;
            return pt.X >= pitchBarW && pt.Y > rulerH;
        }

        private double ResolveTimeAtPoint(Point pt)
        {
            if (_score == null) return 0.0;
            double w = Math.Max(100, ActualWidth);
            double h = Math.Max(100, ActualHeight);

            if (_viewMode == PianoRollViewMode.HorizontalTimeline)
            {
                double leftPitchLabelW = 44.0;
                double canvasW = Math.Max(50, w - leftPitchLabelW);
                double playheadX = leftPitchLabelW + canvasW * 0.24;
                double pxPerSec = canvasW / _visibleWindowSeconds;
                double t = _currentTimeSec + (pt.X - playheadX) / pxPerSec;
                return Math.Clamp(t, 0.0, _score.TotalDurationSeconds);
            }
            else
            {
                double lookaheadSec = _visibleWindowSeconds * 0.65;
                double pxPerSec = h / lookaheadSec;
                double t = _currentTimeSec + (h - pt.Y) / pxPerSec;
                return Math.Clamp(t, 0.0, _score.TotalDurationSeconds);
            }
        }

        private NoteEvent? HitTestNoteAtPoint(Point pt)
        {
            if (_score == null || _score.Notes.Count == 0) return null;
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 10 || h <= 10) return null;

            var notes = _score.Notes;
            if (_viewMode == PianoRollViewMode.HorizontalTimeline)
            {
                double rulerH = 28.0;
                double leftPitchLabelW = 44.0;
                if (pt.X < leftPitchLabelW || pt.Y < rulerH) return null;

                double rollH = Math.Max(40, h - rulerH);
                double canvasW = Math.Max(50, w - leftPitchLabelW);
                int minPitch = Math.Max(21, _score.MinMidiPitch);
                int maxPitch = Math.Min(108, _score.MaxMidiPitch);
                if (maxPitch - minPitch < 24)
                {
                    int mid = (minPitch + maxPitch) / 2;
                    minPitch = Math.Max(21, mid - 12);
                    maxPitch = Math.Min(108, mid + 12);
                }
                int pitchSpan = maxPitch - minPitch + 1;
                double rowH = rollH / pitchSpan;
                int hoverPitch = maxPitch - (int)((pt.Y - rulerH) / rowH);
                if (hoverPitch < minPitch || hoverPitch > maxPitch) return null;

                double playheadX = leftPitchLabelW + canvasW * 0.24;
                double pxPerSec = canvasW / _visibleWindowSeconds;
                double hoverTimeSec = _currentTimeSec + (pt.X - playheadX) / pxPerSec;

                int startIdx = _score.FindFirstNoteIndexForWindow(hoverTimeSec - 0.05);
                for (int i = startIdx; i < notes.Count; i++)
                {
                    var n = notes[i];
                    if (n.StartTimeSeconds > hoverTimeSec + 0.02) break;
                    if (n.MidiPitch == hoverPitch &&
                        hoverTimeSec >= n.StartTimeSeconds &&
                        hoverTimeSec <= n.EndTimeSeconds &&
                        _score.Tracks[n.TrackIndex].IsVisibleOnRoll)
                    {
                        return n;
                    }
                }
            }
            else
            {
                double lookaheadSec = _visibleWindowSeconds * 0.65;
                double pxPerSec = h / lookaheadSec;
                double hoverTimeSec = _currentTimeSec + (h - pt.Y) / pxPerSec;

                int startIdx = _score.FindFirstNoteIndexForWindow(hoverTimeSec - 0.05);
                for (int i = startIdx; i < notes.Count; i++)
                {
                    var n = notes[i];
                    if (n.StartTimeSeconds > hoverTimeSec + 0.02) break;
                    if (hoverTimeSec >= n.StartTimeSeconds &&
                        hoverTimeSec <= n.EndTimeSeconds &&
                        _score.Tracks[n.TrackIndex].IsVisibleOnRoll)
                    {
                        var (kx, kw) = PianoKeyboardControl.GetKeyHorizontalSpan(n.MidiPitch, w);
                        if (pt.X >= kx && pt.X <= kx + kw)
                            return n;
                    }
                }
            }
            return null;
        }

        private void UpdateScrubFromMouse(Point currentPoint)
        {
            if (_score == null) return;
            double w = Math.Max(100, ActualWidth);
            double dx = currentPoint.X - _dragStartPoint.X;

            double secondsPerPixel = _visibleWindowSeconds / w;
            double deltaTime;
            if (_viewMode == PianoRollViewMode.HorizontalTimeline && !_dragFromTopRuler)
            {
                deltaTime = -dx * secondsPerPixel * 1.15;
            }
            else
            {
                deltaTime = dx * secondsPerPixel * 1.25;
            }

            double newTime = Math.Clamp(_dragStartTimeSec + deltaTime, 0.0, _score.TotalDurationSeconds);
            _currentTimeSec = newTime;
            TimeScrubbed?.Invoke(newTime);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_leftPressPendingClick)
            {
                bool shouldToggle = _clickTogglesPlayPause && !_suppressClickToggle;
                _leftPressPendingClick = false;
                _clickTogglesPlayPause = false;
                _suppressClickToggle = false;

                if (IsMouseCaptured)
                    ReleaseMouseCapture();

                if (shouldToggle)
                    PlayPauseToggleRequested?.Invoke();

                InvalidateVisual();
                return;
            }

            if (_isDraggingTime)
            {
                _isDraggingTime = false;
                ReleaseMouseCapture();
                ScrubEnded?.Invoke();
                InvalidateVisual();
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);

            _leftPressPendingClick = false;
            _clickTogglesPlayPause = false;
            _suppressClickToggle = false;

            if (_isDraggingTime)
            {
                _isDraggingTime = false;
                ScrubEnded?.Invoke();
            }

            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta > 0)
                VisibleWindowSeconds *= 0.86;
            else if (e.Delta < 0)
                VisibleWindowSeconds *= 1.16;
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 10 || h <= 10) return;

            bool isDark = ThemeManager.IsDarkMode;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            EnsureThemeBrushesUpdated();
            EnsureStaticTextsCached(dpi, isDark);
            dc.DrawRectangle(_rollBgBrush, null, new Rect(0, 0, w, h));

            if (_score == null || _score.Notes.Count == 0)
            {
                DrawEmptyState(dc, w, h);
                return;
            }

            if (_viewMode == PianoRollViewMode.HorizontalTimeline)
            {
                RenderHorizontalTimeline(dc, w, h, dpi, isDark);
            }
            else
            {
                RenderVerticalFalling(dc, w, h, dpi, isDark);
            }

            if (_isDraggingTime)
            {
                DrawScrubOverlayBanner(dc, w, dpi, isDark);
            }
            else if (_hoveredNote != null)
            {
                DrawHoveredNoteInfoBadge(dc, w, dpi, isDark, _hoveredNote);
            }
        }

        private void RenderHorizontalTimeline(DrawingContext dc, double w, double h, double dpi, bool isDark)
        {
            if (_score == null) return;

            double rulerH = 28.0;
            double rollTop = rulerH;
            double rollH = Math.Max(40, h - rulerH);
            double leftPitchLabelW = 44.0;
            double canvasLeft = leftPitchLabelW;
            double canvasW = Math.Max(50, w - leftPitchLabelW);

            int minPitch = Math.Max(21, _score.MinMidiPitch);
            int maxPitch = Math.Min(108, _score.MaxMidiPitch);
            if (maxPitch - minPitch < 24)
            {
                int mid = (minPitch + maxPitch) / 2;
                minPitch = Math.Max(21, mid - 12);
                maxPitch = Math.Min(108, mid + 12);
            }

            int pitchSpan = maxPitch - minPitch + 1;
            double rowH = rollH / pitchSpan;

            double playheadRatio = 0.24;
            double playheadX = canvasLeft + canvasW * playheadRatio;
            double pxPerSec = canvasW / _visibleWindowSeconds;
            double viewStartSec = _currentTimeSec - (_visibleWindowSeconds * playheadRatio);
            double viewEndSec = viewStartSec + _visibleWindowSeconds;

            Brush blackLaneBrush = _blackLaneBrush;
            Brush rulerLabelBrush = isDark ? DarkRulerLabelBrush : LightRulerLabelBrush;

            // 1. 繪製音高橫列背景與八度線
            for (int p = minPitch; p <= maxPitch; p++)
            {
                double y = rollTop + (maxPitch - p) * rowH;
                if (PianoKeyboardControl.IsBlackKey(p))
                {
                    dc.DrawRectangle(blackLaneBrush, null, new Rect(canvasLeft, y, canvasW, rowH));
                }

                if (p % 12 == 0)
                {
                    dc.DrawLine(OctaveGridPen, new Point(0, y + rowH), new Point(w, y + rowH));
                }
                else
                {
                    dc.DrawLine(PitchGridPen, new Point(canvasLeft, y + rowH), new Point(w, y + rowH));
                }
            }

            // 頂部時間尺背景與底部分隔線
            dc.DrawRectangle(_rulerBrush, null, new Rect(0, 0, w, rulerH));
            dc.DrawLine(MeasureLinePen, new Point(0, rollTop), new Point(w, rollTop));

            // 啟用卷軸畫布裁剪區（幾何快取，僅在尺寸變更時重建）
            if (_cachedClipGeometry == null ||
                Math.Abs(_cachedClipWidth - w) > 0.01 ||
                Math.Abs(_cachedClipHeight - h) > 0.01)
            {
                var clipGeometry = new RectangleGeometry(new Rect(canvasLeft, 0, canvasW, h));
                clipGeometry.Freeze();
                _cachedClipGeometry = clipGeometry;
                _cachedClipWidth = w;
                _cachedClipHeight = h;
            }
            dc.PushClip(_cachedClipGeometry);

            // 1B. 若有設定 A-B 循環點，繪製循環區間底色與標記線
            if (_loopStartSec.HasValue)
            {
                double ax = playheadX + (_loopStartSec.Value - _currentTimeSec) * pxPerSec;
                if (_loopEndSec.HasValue && _isLoopEnabled)
                {
                    double bx = playheadX + (_loopEndSec.Value - _currentTimeSec) * pxPerSec;
                    double rx1 = Math.Max(canvasLeft, ax);
                    double rx2 = Math.Min(w, bx);
                    if (rx2 > rx1)
                    {
                        dc.DrawRectangle(LoopRegionFillBrush, null, new Rect(rx1, rollTop, rx2 - rx1, rollH));
                    }
                    if (bx >= canvasLeft && bx <= w)
                    {
                        dc.DrawLine(LoopBoundaryPen, new Point(bx, rollTop), new Point(bx, h));
                        dc.DrawRoundedRectangle(LoopRulerBadgeBrush, null, new Rect(bx - 16, 4, 16, 18), 3, 3);
                        var ftB = GetCachedLoopLabel('B', dpi);
                        dc.DrawText(ftB, new Point(bx - 11, 5));
                    }
                }
                if (ax >= canvasLeft && ax <= w)
                {
                    dc.DrawLine(LoopBoundaryPen, new Point(ax, rollTop), new Point(ax, h));
                    dc.DrawRoundedRectangle(LoopRulerBadgeBrush, null, new Rect(ax, 4, 16, 18), 3, 3);
                    var ftA = GetCachedLoopLabel('A', dpi);
                    dc.DrawText(ftA, new Point(ax + 4, 5));
                }
            }

            // 2. 繪製小節線、拍線與小節編號（垂直線僅繪製於 rollTop 以下，不劃入頂部小節列）
            var measures = _score.Measures;
            double lastLabelRightX = double.NegativeInfinity;
            for (int m = 0; m < measures.Count; m++)
            {
                var meas = measures[m];
                if (meas.EndTimeSeconds < viewStartSec - 4.0) continue;
                if (meas.StartTimeSeconds > viewEndSec) break;

                double mx = playheadX + (meas.StartTimeSeconds - _currentTimeSec) * pxPerSec;
                if (mx >= canvasLeft)
                {
                    dc.DrawLine(MeasureLinePen, new Point(mx, rollTop), new Point(mx, h));
                }

                if (mx >= lastLabelRightX + 8)
                {
                    var ft = GetCachedMeasureLabel(m, meas, vertical: false, rulerLabelBrush, dpi);

                    double textRight = mx + 4 + ft.Width;
                    lastLabelRightX = textRight;

                    if (textRight > canvasLeft && mx < w)
                    {
                        dc.DrawText(ft, new Point(mx + 4, 6));
                    }
                }

                // 小節內拍線
                if (meas.Beats > 1 && meas.DurationSeconds > 0.2)
                {
                    bool hasBeatTimes = meas.BeatLineTimes.Length == meas.Beats;
                    double fallbackBeatSec = meas.DurationSeconds / meas.Beats;
                    for (int b = 1; b < meas.Beats; b++)
                    {
                        double beatOffsetSec = hasBeatTimes ? meas.BeatLineTimes[b] : b * fallbackBeatSec;
                        double bx = mx + beatOffsetSec * pxPerSec;
                        if (bx >= canvasLeft && bx <= w)
                        {
                            dc.DrawLine(BeatLinePen, new Point(bx, rollTop), new Point(bx, h));
                        }
                    }
                }
            }

            // 3. 繪製可見範圍內的音符塊
            bool anySolo = false;
            for (int t = 0; t < _score.Tracks.Count; t++)
            {
                if (_score.Tracks[t].IsSolo) { anySolo = true; break; }
            }

            var notes = _score.Notes;
            int startIdx = _score.FindFirstNoteIndexForWindow(viewStartSec);
            for (int i = startIdx; i < notes.Count; i++)
            {
                var n = notes[i];
                if (n.StartTimeSeconds > viewEndSec) break;
                if (n.EndTimeSeconds < viewStartSec) continue;
                if (n.MidiPitch < minPitch || n.MidiPitch > maxPitch) continue;

                var track = _score.Tracks[n.TrackIndex];
                if (!track.IsVisibleOnRoll) continue;

                bool isAudible = !track.IsMuted && (!anySolo || track.IsSolo);
                double releaseGap = Math.Clamp(n.DurationSeconds * 0.18, 0.028, 0.055);
                double activeEndSec = n.StartTimeSeconds + Math.Max(0.025, n.DurationSeconds - releaseGap);
                bool isActiveNow = n.StartTimeSeconds <= _currentTimeSec && _currentTimeSec <= activeEndSec && isAudible;

                double nx1 = playheadX + (n.StartTimeSeconds - _currentTimeSec) * pxPerSec;
                double nx2 = playheadX + (n.EndTimeSeconds - _currentTimeSec) * pxPerSec;

                if (nx2 <= canvasLeft) continue;

                double spanPx = nx2 - nx1;
                double gapPx = Math.Clamp(spanPx * 0.12, 1.8, 3.5);
                double nw = Math.Max(3.0, spanPx - gapPx);
                double ny = rollTop + (maxPitch - n.MidiPitch) * rowH + 1.0;
                double nh = Math.Max(3.5, rowH - 2.0);

                Rect noteRect = new(nx1, ny, nw, nh);
                Brush fillBrush = isAudible ? track.ThemeBrush : track.MutedThemeBrush;

                dc.DrawRoundedRectangle(fillBrush, isActiveNow ? ActiveNoteBorderPen : null, noteRect, 2.5, 2.5);
            }

            dc.Pop();

            // 4. 繪製左側音高標記欄 (C2 ~ C7，使用快取 FormattedText；垂直分隔線只從 rollTop 畫到底部)
            dc.DrawRectangle(_rulerBrush, null, new Rect(0, rollTop, leftPitchLabelW, rollH));
            dc.DrawLine(MeasureLinePen, new Point(leftPitchLabelW, rollTop), new Point(leftPitchLabelW, h));

            for (int p = minPitch; p <= maxPitch; p++)
            {
                if (p % 12 == 0 || p == minPitch || p == maxPitch)
                {
                    double y = rollTop + (maxPitch - p) * rowH;
                    var ft = GetCachedPitchLabel(p, dpi, isDark);
                    dc.DrawText(ft, new Point(6, y + (rowH - ft.Height) * 0.5));
                }
            }

            // 5. 繪製播放時間線 (Playhead，垂直線從 rollTop 開始，不穿過頂部小節列)
            dc.DrawRectangle(_playheadGlowBrush, null, new Rect(playheadX - 8, rollTop, 16, rollH));
            dc.DrawLine(_playheadPen, new Point(playheadX, rollTop), new Point(playheadX, h));

            DrawBottomLeftHintBadge(dc, canvasLeft + 10, h - 32, isDark);
        }

        private void RenderVerticalFalling(DrawingContext dc, double w, double h, double dpi, bool isDark)
        {
            if (_score == null) return;

            double lookaheadSec = _visibleWindowSeconds * 0.65;
            double pxPerSec = h / lookaheadSec;
            double viewStartSec = _currentTimeSec;
            double viewEndSec = _currentTimeSec + lookaheadSec;

            Brush blackLaneBrush = _blackLaneBrush;
            Brush measureTextBrush = isDark ? DarkRulerLabelBrush : LightRulerLabelBrush;

            // 1. 繪製對齊下方 88 鍵的垂直音軌格線
            for (int m = PianoKeyboardControl.FirstMidiNote; m <= PianoKeyboardControl.LastMidiNote; m++)
            {
                var (kx, kw) = PianoKeyboardControl.GetKeyHorizontalSpan(m, w);
                if (PianoKeyboardControl.IsBlackKey(m))
                {
                    dc.DrawRectangle(blackLaneBrush, null, new Rect(kx, 0, kw, h));
                }
                if (m % 12 == 0)
                {
                    dc.DrawLine(OctaveGridPen, new Point(kx, 0), new Point(kx, h));
                }
            }

            // 2. 繪製水平下落的小節線
            var measures = _score.Measures;
            for (int m = 0; m < measures.Count; m++)
            {
                var meas = measures[m];
                if (meas.StartTimeSeconds < viewStartSec) continue;
                if (meas.StartTimeSeconds > viewEndSec) break;

                double my = h - (meas.StartTimeSeconds - _currentTimeSec) * pxPerSec;
                dc.DrawLine(MeasureLinePen, new Point(0, my), new Point(w, my));

                var ft = GetCachedMeasureLabel(m, meas, vertical: true, measureTextBrush, dpi);
                dc.DrawText(ft, new Point(10, Math.Max(4, my - ft.Height - 2)));
            }

            // 3. 繪製垂直下落的音符磚塊
            bool anySolo = false;
            for (int t = 0; t < _score.Tracks.Count; t++)
            {
                if (_score.Tracks[t].IsSolo) { anySolo = true; break; }
            }

            var notes = _score.Notes;
            int startIdx = _score.FindFirstNoteIndexForWindow(viewStartSec);
            for (int i = startIdx; i < notes.Count; i++)
            {
                var n = notes[i];
                if (n.StartTimeSeconds > viewEndSec) break;
                if (n.EndTimeSeconds < viewStartSec) continue;
                if (n.MidiPitch < PianoKeyboardControl.FirstMidiNote || n.MidiPitch > PianoKeyboardControl.LastMidiNote)
                    continue;

                var track = _score.Tracks[n.TrackIndex];
                if (!track.IsVisibleOnRoll) continue;

                bool isAudible = !track.IsMuted && (!anySolo || track.IsSolo);
                double releaseGap = Math.Clamp(n.DurationSeconds * 0.18, 0.028, 0.055);
                double activeEndSec = n.StartTimeSeconds + Math.Max(0.025, n.DurationSeconds - releaseGap);
                bool isActiveNow = n.StartTimeSeconds <= _currentTimeSec && _currentTimeSec <= activeEndSec && isAudible;

                var (kx, kw) = PianoKeyboardControl.GetKeyHorizontalSpan(n.MidiPitch, w);
                double yBottom = h - Math.Max(0, (n.StartTimeSeconds - _currentTimeSec) * pxPerSec);
                double yTop = h - (n.EndTimeSeconds - _currentTimeSec) * pxPerSec;

                double spanPx = yBottom - yTop;
                double gapPx = Math.Clamp(spanPx * 0.12, 2.0, 4.0);
                double nh = Math.Max(3.5, spanPx - gapPx);
                Rect noteRect = new(kx + 1.0, yTop + gapPx, Math.Max(3.0, kw - 2.0), nh);

                Brush fillBrush = isAudible ? track.ThemeBrush : track.MutedThemeBrush;

                dc.DrawRoundedRectangle(fillBrush, isActiveNow ? ActiveNoteBorderPen : null, noteRect, 3.0, 3.0);
            }

            // 底部觸鍵光暈線 (與主題色彩連動)
            dc.DrawLine(_playheadPen, new Point(0, h - 1), new Point(w, h - 1));

            DrawBottomLeftHintBadge(dc, 12, h - 32, isDark);
        }

        private void DrawBottomLeftHintBadge(DrawingContext dc, double x, double y, bool isDark)
        {
            var hintFt = _cachedHintFt;
            if (hintFt == null) return;

            dc.DrawRoundedRectangle(
                isDark ? DarkHintBgBrush : LightHintBgBrush,
                isDark ? DarkHintBorderPen : LightHintBorderPen,
                new Rect(x, y, hintFt.Width + 16, hintFt.Height + 6),
                5, 5);
            dc.DrawText(hintFt, new Point(x + 8, y + 3));
        }

        private void DrawScrubOverlayBanner(DrawingContext dc, double w, double dpi, bool isDark)
        {
            if (_score == null) return;

            int measNum = _score.FindMeasureAt(_currentTimeSec).NumericMeasureNumber;

            TimeSpan ts = TimeSpan.FromSeconds(_currentTimeSec);
            string text = $"◀ 滑鼠調整時間位置：{ts:mm\\:ss\\.f}  (第 {measNum} 小節) ▶";

            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                13.5,
                isDark ? Brushes.White : LightRulerLabelBrush,
                dpi);

            double bw = ft.Width + 28;
            double bh = ft.Height + 14;
            double bx = (w - bw) * 0.5;
            double by = 36;

            dc.DrawRoundedRectangle(
                isDark ? DarkHintBgBrush : LightHintBgBrush,
                _scrubBannerBorderPen,
                new Rect(bx, by, bw, bh),
                8, 8);
            dc.DrawText(ft, new Point(bx + 14, by + 7));
        }

        private void DrawHoveredNoteInfoBadge(DrawingContext dc, double w, double dpi, bool isDark, NoteEvent note)
        {
            if (_score == null || note.TrackIndex < 0 || note.TrackIndex >= _score.Tracks.Count)
                return;

            var tr = _score.Tracks[note.TrackIndex];
            string dynSym = note.Velocity switch
            {
                < 42 => "ppp",
                < 54 => "pp",
                < 66 => "p",
                < 78 => "mp",
                < 90 => "mf",
                < 104 => "f",
                < 116 => "ff",
                _ => "fff"
            };
            string artPart = string.IsNullOrWhiteSpace(note.ArticulationLabel) ? "" : $"  ｜  奏法：{note.ArticulationLabel}";
            string info = $"🎵 {tr.Name}  ｜  音高：{note.PitchName} (MIDI {note.MidiPitch})  ｜  第 {note.MeasureNumber} 小節  ｜  力度：{note.Velocity} ({dynSym}){artPart}";

            var ft = new FormattedText(
                info,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                11.5,
                isDark ? Brushes.White : LightRulerLabelBrush,
                dpi);

            double bw = ft.Width + 24;
            double bh = ft.Height + 10;
            double bx = Math.Max(52, w - bw - 14);
            double by = _viewMode == PianoRollViewMode.HorizontalTimeline ? 34 : 10;

            dc.DrawRoundedRectangle(
                isDark ? DarkHintBgBrush : LightHintBgBrush,
                new Pen(tr.ThemeBrush, 1.5),
                new Rect(bx, by, bw, bh),
                6, 6);
            dc.DrawText(ft, new Point(bx + 12, by + 5));
        }

        private void DrawEmptyState(DrawingContext dc, double w, double h)
        {
            var ft = _cachedEmptyFt;
            if (ft == null) return;
            dc.DrawText(ft, new Point((w - ft.Width) * 0.5, (h - ft.Height) * 0.5));
        }
    }
}
