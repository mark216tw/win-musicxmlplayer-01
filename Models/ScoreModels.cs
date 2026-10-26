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

        public double TotalDurationSeconds { get; set; }
        public int MinMidiPitch { get; set; } = 48;
        public int MaxMidiPitch { get; set; } = 84;
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
        private SolidColorBrush _themeBrush = new(Color.FromRgb(56, 189, 248));
        private int _noteCount;
        private double _currentActivity;

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
                double clamped = Math.Clamp(value, 0.0, 1.5);
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
                    var brush = new SolidColorBrush(_themeColor);
                    brush.Freeze();
                    _themeBrush = brush;
                }
                catch
                {
                    // fallback
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeColor));
                OnPropertyChanged(nameof(ThemeBrush));
            }
        }

        public Color ThemeColor => _themeColor;
        public SolidColorBrush ThemeBrush => _themeBrush;

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
