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
    public class PianoKeyboardControl : FrameworkElement
    {
        public const int FirstMidiNote = 21; // A0
        public const int LastMidiNote = 108; // C8
        public const int WhiteKeyCount = 52;

        private struct ActiveKeyStrike
        {
            public NoteEvent Note;
            public double AttackRatio; // 1.0 = 剛敲擊瞬間, 0.0 = 持續按住階段
        }

        private MusicScore? _score;
        private double _currentTimeSec;
        private readonly List<ActiveKeyStrike>[] _activeKeyStrikes = new List<ActiveKeyStrike>[128];

        private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI, Microsoft JhengHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Brush WhiteKeyDefaultBrush;
        private static readonly Brush BlackKeyDefaultBrush;
        private static readonly Brush NormalKeyLabelBrush;
        private static readonly Pen KeyBorderPen = new(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1.0);
        private static readonly Pen BlackKeyHighlightPen;

        // 預先算好的 88 鍵正規化座標查表 (0..127)，將 O(N) 迴圈降為 O(1) 陣列存取
        private static readonly int[] WhiteKeyIndexTable = new int[128];
        private static readonly int[] WhiteKeyToMidiTable = new int[WhiteKeyCount];
        private static readonly (double normX, double normW)[] KeySpanTable = new (double normX, double normW)[128];

        private Color _cachedAccentColor;
        private SolidColorBrush _topAccentFeltBrush = Brushes.DodgerBlue;
        private SolidColorBrush _c4AccentLabelBrush = Brushes.DodgerBlue;
        private LinearGradientBrush _accentPressedKeyBrush = null!;

        // 滑鼠點擊 / 刮奏試聽琴鍵狀態
        private int _mousePressedPitch = -1;
        public event Action<int, bool>? KeyPreviewChanged;

        // 快取 88 鍵未按壓/按壓狀態的 FormattedText，避免每幀重建 DirectWrite 排版
        private double _cachedLabelDpi;
        private double _cachedLabelFontSize;
        private Color _cachedLabelAccentColor;
        private readonly FormattedText?[] _unpressedKeyLabels = new FormattedText?[128];
        private readonly FormattedText?[] _pressedKeyLabels = new FormattedText?[128];

        static PianoKeyboardControl()
        {
            var wkGrad = new LinearGradientBrush(
                Color.FromRgb(238, 242, 246),
                Color.FromRgb(255, 255, 255),
                new Point(0, 0), new Point(0, 1));
            wkGrad.Freeze();
            WhiteKeyDefaultBrush = wkGrad;

            var bkGrad = new LinearGradientBrush(
                Color.FromRgb(38, 43, 54),
                Color.FromRgb(15, 18, 26),
                new Point(0, 0), new Point(0, 1));
            bkGrad.Freeze();
            BlackKeyDefaultBrush = bkGrad;

            var normalLbl = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            normalLbl.Freeze();
            NormalKeyLabelBrush = normalLbl;

            KeyBorderPen.Freeze();

            var hlBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
            hlBrush.Freeze();
            var hlPen = new Pen(hlBrush, 0.8);
            hlPen.Freeze();
            BlackKeyHighlightPen = hlPen;

            // 初始化白鍵索引表與正規化寬度表
            int wkCount = 0;
            for (int m = 0; m < 128; m++)
            {
                WhiteKeyIndexTable[m] = wkCount;
                if (m >= FirstMidiNote && m <= LastMidiNote && !IsBlackKey(m))
                {
                    if (wkCount < WhiteKeyCount)
                        WhiteKeyToMidiTable[wkCount] = m;
                    wkCount++;
                }
            }

            double normWhiteW = 1.0 / WhiteKeyCount;
            for (int m = 0; m < 128; m++)
            {
                int clamped = Math.Clamp(m, FirstMidiNote, LastMidiNote);
                if (!IsBlackKey(clamped))
                {
                    int wkIdx = WhiteKeyIndexTable[clamped];
                    KeySpanTable[m] = (wkIdx * normWhiteW, normWhiteW);
                }
                else
                {
                    int leftWhiteIdx = WhiteKeyIndexTable[clamped - 1];
                    double blackW = normWhiteW * 0.62;
                    int pc = clamped % 12;
                    double offsetRatio = pc switch
                    {
                        1 => 0.64,  // C#
                        3 => 0.72,  // D#
                        6 => 0.62,  // F#
                        8 => 0.68,  // G#
                        10 => 0.74, // A#
                        _ => 0.68
                    };
                    double x = (leftWhiteIdx + offsetRatio) * normWhiteW - (blackW * 0.35);
                    KeySpanTable[m] = (x, blackW);
                }
            }
        }

        public PianoKeyboardControl()
        {
            for (int i = 0; i < 128; i++)
                _activeKeyStrikes[i] = new List<ActiveKeyStrike>(4);

            IsHitTestVisible = true;
            Cursor = Cursors.Hand;
            ClipToBounds = true;

            ThemeManager.ThemeChanged += () =>
            {
                Dispatcher.BeginInvoke(InvalidateVisual);
            };
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            CaptureMouse();
            int hit = HitTestPitch(e.GetPosition(this));
            SetMousePressedPitch(hit);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured)
            {
                int hit = HitTestPitch(e.GetPosition(this));
                SetMousePressedPitch(hit);
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            SetMousePressedPitch(-1);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            SetMousePressedPitch(-1);
        }

        private void SetMousePressedPitch(int newPitch)
        {
            if (_mousePressedPitch == newPitch) return;
            if (_mousePressedPitch >= FirstMidiNote && _mousePressedPitch <= LastMidiNote)
            {
                KeyPreviewChanged?.Invoke(_mousePressedPitch, false);
            }
            _mousePressedPitch = newPitch;
            if (_mousePressedPitch >= FirstMidiNote && _mousePressedPitch <= LastMidiNote)
            {
                KeyPreviewChanged?.Invoke(_mousePressedPitch, true);
            }
            InvalidateVisual();
        }

        private int HitTestPitch(Point pt)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 10 || h <= 10 || pt.X < 0 || pt.X >= w || pt.Y < 0 || pt.Y >= h)
                return -1;

            double feltH = 5.0;
            double keyTop = feltH;
            double keyHeight = Math.Max(20, h - feltH);
            double blackKeyHeight = keyHeight * 0.63;

            // 先檢查上層的黑鍵
            if (pt.Y >= keyTop && pt.Y <= keyTop + blackKeyHeight)
            {
                for (int m = FirstMidiNote; m <= LastMidiNote; m++)
                {
                    if (!IsBlackKey(m)) continue;
                    var (kx, kw) = GetKeyHorizontalSpan(m, w);
                    if (pt.X >= kx && pt.X <= kx + kw)
                        return m;
                }
            }

            // 否則對應白鍵
            int wkIdx = Math.Clamp((int)(pt.X / (w / WhiteKeyCount)), 0, WhiteKeyCount - 1);
            return WhiteKeyToMidiTable[wkIdx];
        }

        public void SetScore(MusicScore? score)
        {
            _score = score;
            InvalidateVisual();
        }

        public void UpdatePlaybackTime(double currentTimeSec)
        {
            _currentTimeSec = currentTimeSec;
            InvalidateVisual();
        }

        public static bool IsBlackKey(int midiPitch)
        {
            int pc = ((midiPitch % 12) + 12) % 12;
            return pc is 1 or 3 or 6 or 8 or 10;
        }

        public static int GetWhiteKeyIndexBeforeOrAt(int midiPitch)
        {
            return WhiteKeyIndexTable[Math.Clamp(midiPitch, 0, 127)];
        }

        public static (double x, double width) GetKeyHorizontalSpan(int midiPitch, double totalWidth)
        {
            var (nx, nw) = KeySpanTable[Math.Clamp(midiPitch, FirstMidiNote, LastMidiNote)];
            return (nx * totalWidth, nw * totalWidth);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 10 || h <= 10) return;

            for (int i = FirstMidiNote; i <= LastMidiNote; i++)
                _activeKeyStrikes[i].Clear();

            if (_score != null && _score.Notes.Count > 0)
            {
                bool anySolo = false;
                for (int t = 0; t < _score.Tracks.Count; t++)
                {
                    if (_score.Tracks[t].IsSolo)
                    {
                        anySolo = true;
                        break;
                    }
                }

                var notes = _score.Notes;
                int backBound = _score.FindFirstNoteIndexForWindow(_currentTimeSec);
                for (int i = backBound; i < notes.Count; i++)
                {
                    var n = notes[i];
                    if (n.StartTimeSeconds > _currentTimeSec + 0.002)
                        break;
                    if (n.EndTimeSeconds < _currentTimeSec)
                        continue;

                    if (n.MidiPitch < FirstMidiNote || n.MidiPitch > LastMidiNote ||
                        n.TrackIndex < 0 || n.TrackIndex >= _score.Tracks.Count)
                        continue;

                    var tr = _score.Tracks[n.TrackIndex];
                    if (!tr.IsVisibleOnRoll || tr.IsMuted || (anySolo && !tr.IsSolo))
                        continue;

                    // 預留回彈間隙 (Re-articulation Gap)，確保同音連續敲擊時琴鍵先彈起再按壓
                    double rawDur = Math.Min(n.DurationSeconds, Math.Max(n.DurationSeconds * 0.88, n.AudioDurationSeconds));
                    double releaseGap = Math.Clamp(n.DurationSeconds * 0.22, 0.035, 0.068);
                    double visualHoldDur = Math.Max(0.025, Math.Min(rawDur, n.DurationSeconds - releaseGap));
                    double visualEndSec = n.StartTimeSeconds + visualHoldDur;

                    if (_currentTimeSec >= n.StartTimeSeconds && _currentTimeSec <= visualEndSec)
                    {
                        double age = _currentTimeSec - n.StartTimeSeconds;
                        // 敲擊瞬間產生局部圓形閃光動畫
                        double attackWindow = Math.Clamp(n.DurationSeconds * 0.40, 0.055, 0.115);
                        double attackRatio = Math.Clamp(1.0 - (age / attackWindow), 0.0, 1.0);

                        var list = _activeKeyStrikes[n.MidiPitch];
                        if (list.Count > 0 && n.StartTimeSeconds >= list[0].Note.StartTimeSeconds)
                        {
                            list.Insert(0, new ActiveKeyStrike { Note = n, AttackRatio = attackRatio });
                        }
                        else
                        {
                            list.Add(new ActiveKeyStrike { Note = n, AttackRatio = attackRatio });
                        }
                    }
                }
            }

            // 頂部琴蓋飾條 (與主題色彩連動，快取 Frozen Brush)
            double feltH = 5.0;
            Color accent = ThemeManager.CurrentAccentColor;
            if (accent != _cachedAccentColor)
            {
                _cachedAccentColor = accent;
                var felt = new SolidColorBrush(Color.FromRgb(
                    (byte)(accent.R * 0.72),
                    (byte)(accent.G * 0.72),
                    (byte)(accent.B * 0.72)));
                felt.Freeze();
                _topAccentFeltBrush = felt;

                var c4Lbl = new SolidColorBrush(Color.FromRgb(
                    (byte)(accent.R * 0.65),
                    (byte)(accent.G * 0.65),
                    (byte)(accent.B * 0.65)));
                c4Lbl.Freeze();
                _c4AccentLabelBrush = c4Lbl;

                var accGrad = new LinearGradientBrush(
                    Color.FromRgb(
                        (byte)Math.Min(255, accent.R + 40),
                        (byte)Math.Min(255, accent.G + 40),
                        (byte)Math.Min(255, accent.B + 40)),
                    accent,
                    new Point(0, 0),
                    new Point(0, 1));
                accGrad.Freeze();
                _accentPressedKeyBrush = accGrad;
            }
            dc.DrawRectangle(_topAccentFeltBrush, null, new Rect(0, 0, w, feltH));

            double keyTop = feltH;
            double keyHeight = Math.Max(20, h - feltH);
            double blackKeyHeight = keyHeight * 0.63;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // 2. 先繪製 52 個白鍵
            for (int m = FirstMidiNote; m <= LastMidiNote; m++)
            {
                if (IsBlackKey(m)) continue;

                var (kx, kw) = GetKeyHorizontalSpan(m, w);
                var activeList = _activeKeyStrikes[m];
                bool isMouseDown = (m == _mousePressedPitch);
                bool isPressed = activeList.Count > 0 || isMouseDown;

                Rect keyRect = new(kx, keyTop, Math.Max(1, kw), keyHeight);

                if (!isPressed)
                {
                    dc.DrawRectangle(WhiteKeyDefaultBrush, KeyBorderPen, keyRect);
                }
                else if (activeList.Count > 0 && _score != null)
                {
                    var strike = activeList[0];
                    var primaryTrack = _score.Tracks[strike.Note.TrackIndex];
                    dc.DrawRectangle(primaryTrack.WhitePressedKeyBrush, KeyBorderPen, keyRect);
                }
                else
                {
                    dc.DrawRectangle(_accentPressedKeyBrush, KeyBorderPen, keyRect);
                }

                if (m % 12 == 0 || m == FirstMidiNote || isPressed)
                {
                    double fontSize = Math.Round(Math.Clamp(kw * 0.45, 7.5, 11.0), 1);
                    var ft = GetCachedKeyLabel(m, isPressed, fontSize, dpi, accent);

                    double tx = kx + (kw - ft.Width) * 0.5;
                    double ty = keyTop + keyHeight - ft.Height - 3;
                    if (tx >= kx && ft.Width <= kw + 2)
                    {
                        dc.DrawText(ft, new Point(tx, ty));
                    }
                }
            }

            // 3. 繪製 36 個黑鍵
            for (int m = FirstMidiNote; m <= LastMidiNote; m++)
            {
                if (!IsBlackKey(m)) continue;

                var (kx, kw) = GetKeyHorizontalSpan(m, w);
                var activeList = _activeKeyStrikes[m];
                bool isMouseDown = (m == _mousePressedPitch);
                bool isPressed = activeList.Count > 0 || isMouseDown;

                Rect blackRect = new(kx, keyTop, Math.Max(1, kw), blackKeyHeight);

                if (!isPressed)
                {
                    dc.DrawRoundedRectangle(BlackKeyDefaultBrush, KeyBorderPen, blackRect, 2.5, 2.5);
                    var highlightRect = new Rect(kx + 1.5, keyTop + 2, Math.Max(1, kw - 3), blackKeyHeight - 7);
                    dc.DrawRoundedRectangle(null, BlackKeyHighlightPen, highlightRect, 1.5, 1.5);
                }
                else if (activeList.Count > 0 && _score != null)
                {
                    var strike = activeList[0];
                    var primaryTrack = _score.Tracks[strike.Note.TrackIndex];
                    dc.DrawRoundedRectangle(primaryTrack.BlackPressedKeyBrush, KeyBorderPen, blackRect, 2.5, 2.5);
                }
                else
                {
                    dc.DrawRoundedRectangle(_accentPressedKeyBrush, KeyBorderPen, blackRect, 2.5, 2.5);
                }
            }
        }

        private FormattedText GetCachedKeyLabel(int midiPitch, bool isPressed, double fontSize, double dpi, Color accent)
        {
            if (Math.Abs(_cachedLabelDpi - dpi) > 0.001 ||
                Math.Abs(_cachedLabelFontSize - fontSize) > 0.05 ||
                _cachedLabelAccentColor != accent)
            {
                _cachedLabelDpi = dpi;
                _cachedLabelFontSize = fontSize;
                _cachedLabelAccentColor = accent;
                Array.Clear(_unpressedKeyLabels, 0, _unpressedKeyLabels.Length);
                Array.Clear(_pressedKeyLabels, 0, _pressedKeyLabels.Length);
            }

            var table = isPressed ? _pressedKeyLabels : _unpressedKeyLabels;
            var ft = table[midiPitch];
            if (ft != null)
                return ft;

            string label = midiPitch == 60 ? "C4" : NoteEvent.GetPitchName(midiPitch);
            Brush textBrush = isPressed
                ? Brushes.Black
                : (midiPitch == 60 ? _c4AccentLabelBrush : NormalKeyLabelBrush);

            ft = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                fontSize,
                textBrush,
                dpi);
            table[midiPitch] = ft;
            return ft;
        }
    }
}
