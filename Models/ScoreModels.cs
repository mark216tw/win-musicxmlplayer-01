using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WinMusicXmlPlayer.Models
{
    public class MusicScore
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Composer { get; set; } = string.Empty;
        public double InitialTempoBpm { get; set; } = 120.0;
        public int InitialBeats { get; set; } = 4;
        public int InitialBeatType { get; set; } = 4;
        public int InitialFifths { get; set; } = 0;
        public bool HasRepeats { get; set; }

        public List<ScorePartTrack> Tracks { get; set; } = new();
        public List<NoteEvent> Notes { get; set; } = new();
        public List<MeasureMarker> Measures { get; set; } = new();

        /// <summary>
        /// 單調遞增的前綴最大結束時間陣列：MaxEndTimePrefix[i] = max(0..i 之 EndTimeSeconds)
        /// 用於 O(log N) 精準定位視窗內第一個尚未結束的音符，避免長音遺漏與硬編碼回推
        /// </summary>
        public double[] MaxEndTimePrefix { get; set; } = Array.Empty<double>();

        public double TotalDurationSeconds { get; set; }
        public int MinMidiPitch { get; set; } = 48;
        public int MaxMidiPitch { get; set; } = 84;

        public int FindFirstNoteIndexForWindow(double windowStartSec)
        {
            var prefix = MaxEndTimePrefix;
            if (prefix.Length == 0 || Notes.Count == 0)
                return 0;

            int lo = 0;
            int hi = prefix.Length - 1;
            int ans = prefix.Length;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (prefix[mid] >= windowStartSec)
                {
                    ans = mid;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            return ans < Notes.Count ? ans : Notes.Count;
        }

        /// <summary>
        /// 以 O(log M) 二分搜尋定位指定秒數所在的小節標記
        /// </summary>
        public MeasureMarker FindMeasureAt(double timeSec)
        {
            int count = Measures.Count;
            if (count == 0)
                return new MeasureMarker();

            int lo = 0;
            int hi = count - 1;
            int found = 0;
            double target = timeSec + 0.005;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Measures[mid].StartTimeSeconds <= target)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return Measures[found];
        }
    }

    public class ScorePartTrack : INotifyPropertyChanged
    {
        private string _trackId = string.Empty;
        private string _partId = string.Empty;
        private string _name = string.Empty;
        private string _originalPartName = string.Empty;
        private int _trackIndex;
        private int _staffNumber = 1;
        private int? _voiceFilter;
        private int _midiChannel;
        private int _gmProgram;
        private int? _initialGmProgram;
        private int _pan = 64; // 0..127 (64 = center)
        private double _volume = 1.0; // 0.0 .. 1.5
        private bool _isMuted;
        private bool _isSolo;
        private bool _isVisibleOnRoll = true;
        private string _colorHex = "#38BDF8";
        private Color _themeColor = Color.FromRgb(56, 189, 248);
        private SolidColorBrush _themeBrush = CreateFrozenSolidBrush(Color.FromRgb(56, 189, 248));
        private SolidColorBrush _mutedThemeBrush = CreateFrozenSolidBrush(Color.FromArgb(65, 56, 189, 248));
        private LinearGradientBrush _whitePressedKeyBrush = CreateFrozenKeyGradient(Color.FromRgb(56, 189, 248), 38);
        private LinearGradientBrush _blackPressedKeyBrush = CreateFrozenKeyGradient(Color.FromRgb(56, 189, 248), 45);
        private int _noteCount;
        private double _currentActivity;

        private static SolidColorBrush CreateFrozenSolidBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static LinearGradientBrush CreateFrozenKeyGradient(Color c, int highlightBoost)
        {
            var g = new LinearGradientBrush(
                Color.FromRgb(
                    (byte)Math.Min(255, c.R + highlightBoost),
                    (byte)Math.Min(255, c.G + highlightBoost),
                    (byte)Math.Min(255, c.B + highlightBoost)),
                c,
                new System.Windows.Point(0, 0),
                new System.Windows.Point(0, 1));
            g.Freeze();
            return g;
        }

        public string TrackId
        {
            get => _trackId;
            set { _trackId = value; OnPropertyChanged(); }
        }

        public string PartId
        {
            get => _partId;
            set { _partId = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string OriginalPartName
        {
            get => _originalPartName;
            set { _originalPartName = value; OnPropertyChanged(); }
        }

        public int TrackIndex
        {
            get => _trackIndex;
            set { _trackIndex = value; OnPropertyChanged(); }
        }

        public int StaffNumber
        {
            get => _staffNumber;
            set { _staffNumber = value; OnPropertyChanged(); }
        }

        public int? VoiceFilter
        {
            get => _voiceFilter;
            set { _voiceFilter = value; OnPropertyChanged(); }
        }

        public int MidiChannel
        {
            get => _midiChannel;
            set { _midiChannel = value; OnPropertyChanged(); }
        }

        public int GmProgram
        {
            get => _gmProgram;
            set
            {
                int clamped = Math.Clamp(value, 0, 127);
                _initialGmProgram ??= clamped;
                if (_gmProgram != clamped)
                {
                    _gmProgram = clamped;
                    OnPropertyChanged();
                }
            }
        }

        public int InitialGmProgram => _initialGmProgram ?? _gmProgram;

        public int Pan
        {
            get => _pan;
            set
            {
                if (_pan != value)
                {
                    _pan = Math.Clamp(value, 0, 127);
                    OnPropertyChanged();
                }
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                // 以 5% (0.05) 為單位對齊
                double clamped = Math.Round(Math.Clamp(value, 0.0, 1.5) * 20.0) / 20.0;
                if (Math.Abs(_volume - clamped) > 0.0001)
                {
                    _volume = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VolumePercentText));
                }
            }
        }

        public string VolumePercentText => $"{(int)Math.Round(_volume * 100)}%";

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted != value)
                {
                    _isMuted = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSolo
        {
            get => _isSolo;
            set
            {
                if (_isSolo != value)
                {
                    _isSolo = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsVisibleOnRoll
        {
            get => _isVisibleOnRoll;
            set
            {
                if (_isVisibleOnRoll != value)
                {
                    _isVisibleOnRoll = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ColorHex
        {
            get => _colorHex;
            set
            {
                _colorHex = value;
                try
                {
                    _themeColor = (Color)ColorConverter.ConvertFromString(value);
                    _themeBrush = CreateFrozenSolidBrush(_themeColor);
                    _mutedThemeBrush = CreateFrozenSolidBrush(Color.FromArgb(65, _themeColor.R, _themeColor.G, _themeColor.B));
                    _whitePressedKeyBrush = CreateFrozenKeyGradient(_themeColor, 38);
                    _blackPressedKeyBrush = CreateFrozenKeyGradient(_themeColor, 45);
                }
                catch
                {
                    // fallback
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeColor));
                OnPropertyChanged(nameof(ThemeBrush));
                OnPropertyChanged(nameof(MutedThemeBrush));
            }
        }

        public Color ThemeColor => _themeColor;
        public SolidColorBrush ThemeBrush => _themeBrush;
        public SolidColorBrush MutedThemeBrush => _mutedThemeBrush;
        public LinearGradientBrush WhitePressedKeyBrush => _whitePressedKeyBrush;
        public LinearGradientBrush BlackPressedKeyBrush => _blackPressedKeyBrush;

        public int NoteCount
        {
            get => _noteCount;
            set { _noteCount = value; OnPropertyChanged(); }
        }

        public double CurrentActivity
        {
            get => _currentActivity;
            set
            {
                if (Math.Abs(_currentActivity - value) > 0.002)
                {
                    _currentActivity = Math.Clamp(value, 0.0, 1.0);
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public class NoteEvent
    {
        public int TrackIndex { get; set; }
        public string TrackId { get; set; } = string.Empty;
        public int MidiChannel { get; set; }
        public int MidiPitch { get; set; }
        public int Velocity { get; set; } = 80;

        /// <summary>
        /// 音符開始時間（秒）
        /// </summary>
        public double StartTimeSeconds { get; set; }

        /// <summary>
        /// 視覺顯示時長（秒，供鋼琴卷軸繪製）
        /// </summary>
        public double DurationSeconds { get; set; }

        /// <summary>
        /// 實際音訊發聲時長（秒，反映斷奏/連奏/踏板延音）
        /// </summary>
        public double AudioDurationSeconds { get; set; }

        public double EndTimeSeconds => StartTimeSeconds + DurationSeconds;
        public double AudioEndTimeSeconds => StartTimeSeconds + AudioDurationSeconds;

        public int MeasureNumber { get; set; }
        public int Staff { get; set; } = 1;
        public int Voice { get; set; } = 1;
        public bool IsOrnamentSubNote { get; set; }
        public bool IsSfpDecay { get; set; }
        public string ArticulationLabel { get; set; } = string.Empty;

        public string PitchName => GetPitchName(MidiPitch);

        public static string GetPitchName(int midiPitch)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int note = ((midiPitch % 12) + 12) % 12;
            int octave = (midiPitch / 12) - 1;
            return $"{names[note]}{octave}";
        }
    }

    public class MeasureMarker
    {
        public int TimelineIndex { get; set; }
        public string MeasureNumberText { get; set; } = "1";
        public int NumericMeasureNumber { get; set; } = 1;
        public int RepeatPass { get; set; } = 1;
        public double StartTimeSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public double EndTimeSeconds => StartTimeSeconds + DurationSeconds;
        public int Beats { get; set; } = 4;
        public int BeatType { get; set; } = 4;
        public int Fifths { get; set; } = 0;
        public double TempoBpm { get; set; } = 120.0;
        public string SectionText { get; set; } = string.Empty;

        public string KeySignatureName => Fifths switch
        {
            -7 => "Cb 大調",
            -6 => "Gb 大調",
            -5 => "Db 大調",
            -4 => "Ab 大調",
            -3 => "Eb 大調 / C 小調",
            -2 => "Bb 大調 / G 小調",
            -1 => "F 大調 / D 小調",
            0 => "C 大調 / A 小調",
            1 => "G 大調 / E 小調",
            2 => "D 大調 / B 小調",
            3 => "A 大調 / F# 小調",
            4 => "E 大調 / C# 小調",
            5 => "B 大調 / G# 小調",
            6 => "F# 大調",
            7 => "C# 大調",
            _ => "C 大調"
        };
    }

    public class GmInstrumentItem
    {
        public int Program { get; set; }
        public string Category { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;

        public override string ToString() => $"{Program + 1:000} - {DisplayName}";
    }
}
