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
        private bool _isDraggingTime;
        private Point _dragStartPoint;
        private double _dragStartTimeSec;
        private bool _dragFromTopRuler;

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

        public event Action<double>? TimeScrubbed;
        public event Action? ScrubStarted;
        public event Action? ScrubEnded;

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
                    InvalidateVisual();
                }
            }
        }

        public void SetScore(MusicScore? score)
        {
            _score = score;
            _currentTimeSec = 0;
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

            _isDraggingTime = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartTimeSec = _currentTimeSec;
            _dragFromTopRuler = _dragStartPoint.Y <= 30.0 && _viewMode == PianoRollViewMode.HorizontalTimeline;

            CaptureMouse();
            ScrubStarted?.Invoke();

            if (_dragFromTopRuler)
            {
                UpdateScrubFromMouse(_dragStartPoint);
            }
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_isDraggingTime || _score == null) return;

            Point pt = e.GetPosition(this);
            UpdateScrubFromMouse(pt);
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
            if (_isDraggingTime)
            {
                _isDraggingTime = false;
                ReleaseMouseCapture();
                ScrubEnded?.Invoke();
                InvalidateVisual();
            }
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
            dc.DrawRectangle(new SolidColorBrush(ThemeManager.CurrentRollBgColor), null, new Rect(0, 0, w, h));

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (_score == null || _score.Notes.Count == 0)
            {
                DrawEmptyState(dc, w, h, dpi, isDark);
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

            Brush blackLaneBrush = new SolidColorBrush(ThemeManager.CurrentRollBlackLaneColor);
            Brush rulerLabelBrush = isDark
                ? new SolidColorBrush(Color.FromRgb(203, 213, 225))
                : new SolidColorBrush(Color.FromRgb(51, 65, 85));

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

            // 頂部時間尺背景
            dc.DrawRectangle(new SolidColorBrush(ThemeManager.CurrentRollRulerColor), null, new Rect(0, 0, w, rulerH));

            // 啟用卷軸畫布裁剪區
            dc.PushClip(new RectangleGeometry(new Rect(canvasLeft, 0, canvasW, h)));

            // 2. 繪製小節線、拍線與小節編號
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
                    dc.DrawLine(MeasureLinePen, new Point(mx, 0), new Point(mx, h));
                }

                if (mx >= lastLabelRightX + 8)
                {
                    string mLabel = string.IsNullOrEmpty(meas.SectionText)
                        ? $"M.{meas.MeasureNumberText}"
                        : $"M.{meas.MeasureNumberText} · {meas.SectionText}";

                    var ft = new FormattedText(
                        mLabel,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        UiTypeface,
                        11.0,
                        rulerLabelBrush,
                        dpi);

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
                    double beatSec = meas.DurationSeconds / meas.Beats;
                    for (int b = 1; b < meas.Beats; b++)
                    {
                        double bx = mx + b * beatSec * pxPerSec;
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
            int startIdx = FindFirstNoteIndexBefore(notes, viewStartSec);
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
                Brush fillBrush = track.ThemeBrush;

                if (!isAudible)
                {
                    var mutedColor = Color.FromArgb(65, track.ThemeColor.R, track.ThemeColor.G, track.ThemeColor.B);
                    fillBrush = new SolidColorBrush(mutedColor);
                }

                dc.DrawRoundedRectangle(fillBrush, isActiveNow ? ActiveNoteBorderPen : null, noteRect, 2.5, 2.5);
            }

            dc.Pop();

            // 4. 繪製左側音高標記欄 (C2 ~ C7)
            dc.DrawRectangle(new SolidColorBrush(ThemeManager.CurrentRollRulerColor), null, new Rect(0, rollTop, leftPitchLabelW, rollH));
            dc.DrawLine(MeasureLinePen, new Point(leftPitchLabelW, 0), new Point(leftPitchLabelW, h));

            Brush accentBrush = new SolidColorBrush(ThemeManager.CurrentAccentColor);
            Brush normalPitchBrush = isDark
                ? new SolidColorBrush(Color.FromRgb(148, 163, 184))
                : new SolidColorBrush(Color.FromRgb(71, 85, 105));

            for (int p = minPitch; p <= maxPitch; p++)
            {
                if (p % 12 == 0 || p == minPitch || p == maxPitch)
                {
                    double y = rollTop + (maxPitch - p) * rowH;
                    var ft = new FormattedText(
                        NoteEvent.GetPitchName(p),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        UiTypeface,
                        10.0,
                        p == 60 ? accentBrush : normalPitchBrush,
                        dpi);
                    dc.DrawText(ft, new Point(6, y + (rowH - ft.Height) * 0.5));
                }
            }

            // 5. 繪製播放時間線 (Playhead，與主題色彩連動)
            Color accent = ThemeManager.CurrentAccentColor;
            var playheadGlow = new SolidColorBrush(Color.FromArgb(46, accent.R, accent.G, accent.B));
            var playheadPen = new Pen(new SolidColorBrush(accent), 2.4);
            dc.DrawRectangle(playheadGlow, null, new Rect(playheadX - 8, rollTop, 16, rollH));
            dc.DrawLine(playheadPen, new Point(playheadX, 0), new Point(playheadX, h));

            DrawBottomLeftHintBadge(dc, canvasLeft + 10, h - 32, dpi, isDark);
        }

        private void RenderVerticalFalling(DrawingContext dc, double w, double h, double dpi, bool isDark)
        {
            if (_score == null) return;

            double lookaheadSec = _visibleWindowSeconds * 0.65;
            double pxPerSec = h / lookaheadSec;
            double viewStartSec = _currentTimeSec;
            double viewEndSec = _currentTimeSec + lookaheadSec;

            Brush blackLaneBrush = new SolidColorBrush(ThemeManager.CurrentRollBlackLaneColor);
            Brush measureTextBrush = isDark
                ? new SolidColorBrush(Color.FromRgb(203, 213, 225))
                : new SolidColorBrush(Color.FromRgb(51, 65, 85));

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

                string mLabel = string.IsNullOrEmpty(meas.SectionText)
                    ? $"小節 {meas.MeasureNumberText}"
                    : $"小節 {meas.MeasureNumberText} · {meas.SectionText}";

                var ft = new FormattedText(
                    mLabel,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    UiTypeface,
                    11.0,
                    measureTextBrush,
                    dpi);
                dc.DrawText(ft, new Point(10, Math.Max(4, my - ft.Height - 2)));
            }

            // 3. 繪製垂直下落的音符磚塊
            bool anySolo = false;
            for (int t = 0; t < _score.Tracks.Count; t++)
            {
                if (_score.Tracks[t].IsSolo) { anySolo = true; break; }
            }

            var notes = _score.Notes;
            int startIdx = FindFirstNoteIndexBefore(notes, viewStartSec);
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

                Brush fillBrush = track.ThemeBrush;
                if (!isAudible)
                {
                    var mutedColor = Color.FromArgb(65, track.ThemeColor.R, track.ThemeColor.G, track.ThemeColor.B);
                    fillBrush = new SolidColorBrush(mutedColor);
                }

                dc.DrawRoundedRectangle(fillBrush, isActiveNow ? ActiveNoteBorderPen : null, noteRect, 3.0, 3.0);
            }

            // 底部觸鍵光暈線 (與主題色彩連動)
            var playheadPen = new Pen(new SolidColorBrush(ThemeManager.CurrentAccentColor), 2.4);
            dc.DrawLine(playheadPen, new Point(0, h - 1), new Point(w, h - 1));

            DrawBottomLeftHintBadge(dc, 12, h - 32, dpi, isDark);
        }

        private static void DrawBottomLeftHintBadge(DrawingContext dc, double x, double y, double dpi, bool isDark)
        {
            var hintFt = new FormattedText(
                "⇄ 按住滑鼠左鍵左右滑動調整時間 · 滾輪縮放視野",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                11.0,
                isDark ? new SolidColorBrush(Color.FromRgb(203, 213, 225)) : new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                dpi);

            dc.DrawRoundedRectangle(
                isDark ? new SolidColorBrush(Color.FromArgb(195, 15, 23, 42)) : new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
                new Pen(isDark ? new SolidColorBrush(Color.FromRgb(51, 65, 85)) : new SolidColorBrush(Color.FromRgb(203, 213, 225)), 1.0),
                new Rect(x, y, hintFt.Width + 16, hintFt.Height + 6),
                5, 5);
            dc.DrawText(hintFt, new Point(x + 8, y + 3));
        }

        private void DrawScrubOverlayBanner(DrawingContext dc, double w, double dpi, bool isDark)
        {
            if (_score == null) return;

            int measNum = 1;
            foreach (var m in _score.Measures)
            {
                if (m.StartTimeSeconds <= _currentTimeSec)
                    measNum = m.NumericMeasureNumber;
                else
                    break;
            }

            TimeSpan ts = TimeSpan.FromSeconds(_currentTimeSec);
            string text = $"◀ 滑鼠調整時間位置：{ts:mm\\:ss\\.f}  (第 {measNum} 小節) ▶";

            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                13.5,
                isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                dpi);

            double bw = ft.Width + 28;
            double bh = ft.Height + 14;
            double bx = (w - bw) * 0.5;
            double by = 36;

            dc.DrawRoundedRectangle(
                isDark ? new SolidColorBrush(Color.FromArgb(225, 15, 23, 42)) : new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                new Pen(new SolidColorBrush(ThemeManager.CurrentAccentColor), 1.5),
                new Rect(bx, by, bw, bh),
                8, 8);
            dc.DrawText(ft, new Point(bx + 14, by + 7));
        }

        private static void DrawEmptyState(DrawingContext dc, double w, double h, double dpi, bool isDark)
        {
            var ft = new FormattedText(
                "請點擊左上角「開啟 MusicXML」或將樂譜檔案拖曳至此視窗開始播放",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                15.0,
                isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                dpi);
            dc.DrawText(ft, new Point((w - ft.Width) * 0.5, (h - ft.Height) * 0.5));
        }

        private static int FindFirstNoteIndexBefore(List<NoteEvent> notes, double targetSec)
        {
            int lo = 0, hi = notes.Count - 1;
            int idx = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (notes[mid].StartTimeSeconds <= targetSec)
                {
                    idx = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return Math.Max(0, idx - 300);
        }
    }
}
