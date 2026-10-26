using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
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
        private static readonly Pen KeyBorderPen = new(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1.0);

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

            KeyBorderPen.Freeze();
        }

        public PianoKeyboardControl()
        {
            for (int i = 0; i < 128; i++)
                _activeKeyStrikes[i] = new List<ActiveKeyStrike>(4);

            IsHitTestVisible = false;
            ClipToBounds = true;

            ThemeManager.ThemeChanged += () =>
            {
                Dispatcher.BeginInvoke(InvalidateVisual);
            };
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
            int count = 0;
            for (int m = FirstMidiNote; m < midiPitch; m++)
            {
                if (!IsBlackKey(m)) count++;
            }
            return count;
        }

        public static (double x, double width) GetKeyHorizontalSpan(int midiPitch, double totalWidth)
        {
            double whiteKeyWidth = totalWidth / WhiteKeyCount;
            int clamped = Math.Clamp(midiPitch, FirstMidiNote, LastMidiNote);

            if (!IsBlackKey(clamped))
            {
                int wkIdx = GetWhiteKeyIndexBeforeOrAt(clamped);
                return (wkIdx * whiteKeyWidth, whiteKeyWidth);
            }
            else
            {
                int leftWhiteIdx = GetWhiteKeyIndexBeforeOrAt(clamped - 1);
                double blackW = whiteKeyWidth * 0.62;
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
                double x = (leftWhiteIdx + offsetRatio) * whiteKeyWidth - (blackW * 0.35);
                return (x, blackW);
            }
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
                int lo = 0, hi = notes.Count - 1;
                int startScan = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (notes[mid].StartTimeSeconds <= _currentTimeSec)
                    {
                        startScan = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                int backBound = Math.Max(0, startScan - 220);
                for (int i = backBound; i < notes.Count; i++)
                {
                    var n = notes[i];
                    if (n.StartTimeSeconds > _currentTimeSec + 0.002)
                        break;

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

            // 頂部琴蓋飾條 (與主題色彩連動)
            double feltH = 5.0;
            Color accent = ThemeManager.CurrentAccentColor;
            var topAccentFeltBrush = new SolidColorBrush(Color.FromRgb(
                (byte)(accent.R * 0.72),
                (byte)(accent.G * 0.72),
                (byte)(accent.B * 0.72)));
            dc.DrawRectangle(topAccentFeltBrush, null, new Rect(0, 0, w, feltH));

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
                bool isPressed = activeList.Count > 0;

                Rect keyRect = new(kx, keyTop, Math.Max(1, kw), keyHeight);

                if (!isPressed)
                {
                    dc.DrawRectangle(WhiteKeyDefaultBrush, KeyBorderPen, keyRect);
                }
                else if (_score != null)
                {
                    var strike = activeList[0];
                    var primaryTrack = _score.Tracks[strike.Note.TrackIndex];
                    Color c = primaryTrack.ThemeColor;

                    var pressedBrush = new LinearGradientBrush(
                        Color.FromRgb((byte)Math.Min(255, c.R + 38), (byte)Math.Min(255, c.G + 38), (byte)Math.Min(255, c.B + 38)),
                        c,
                        new Point(0, 0), new Point(0, 1));

                    dc.DrawRectangle(pressedBrush, KeyBorderPen, keyRect);
                }

                if (m % 12 == 0 || m == FirstMidiNote || isPressed)
                {
                    string label = m == 60 ? "C4" : NoteEvent.GetPitchName(m);
                    Brush textBrush = isPressed
                        ? Brushes.Black
                        : (m == 60
                            ? new SolidColorBrush(Color.FromRgb((byte)(accent.R * 0.65), (byte)(accent.G * 0.65), (byte)(accent.B * 0.65)))
                            : new SolidColorBrush(Color.FromRgb(100, 116, 139)));
                    double fontSize = Math.Clamp(kw * 0.45, 7.5, 11.0);
                    var ft = new FormattedText(
                        label,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        LabelTypeface,
                        fontSize,
                        textBrush,
                        dpi);

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
                bool isPressed = activeList.Count > 0;

                Rect blackRect = new(kx, keyTop, Math.Max(1, kw), blackKeyHeight);

                if (!isPressed)
                {
                    dc.DrawRoundedRectangle(BlackKeyDefaultBrush, KeyBorderPen, blackRect, 2.5, 2.5);
                    var highlightRect = new Rect(kx + 1.5, keyTop + 2, Math.Max(1, kw - 3), blackKeyHeight - 7);
                    dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 0.8), highlightRect, 1.5, 1.5);
                }
                else if (_score != null)
                {
                    var strike = activeList[0];
                    var primaryTrack = _score.Tracks[strike.Note.TrackIndex];
                    Color c = primaryTrack.ThemeColor;

                    // 黑鍵按壓底色維持飽滿聲部色調
                    var pressedBrush = new LinearGradientBrush(
                        Color.FromRgb((byte)Math.Min(255, c.R + 45), (byte)Math.Min(255, c.G + 45), (byte)Math.Min(255, c.B + 45)),
                        c,
                        new Point(0, 0), new Point(0, 1));
                    dc.DrawRoundedRectangle(pressedBrush, KeyBorderPen, blackRect, 2.5, 2.5);
                }
            }
        }
    }
}
