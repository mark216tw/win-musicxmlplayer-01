using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MeltySynth;
using NAudio.Wave;
using WinMusicXmlPlayer.Models;

namespace WinMusicXmlPlayer.Services
{
    public sealed class AudioEngine : ISampleProvider, IDisposable
    {
        private readonly object _syncLock = new();
        private readonly int _sampleRate = 44100;
        private readonly WaveFormat _waveFormat;

        public const float MuseScorePianoFilterCutoffFloorHz = 4000F;

        // 通用樂器音源 (General MIDI SoundFont)
        private SoundFont? _soundFont;
        private Synthesizer? _synth;
        private string _currentSoundFontPath = string.Empty;

        // 鋼琴專用音源 (Dedicated Piano SoundFont - 方案 A)
        private SoundFont? _pianoSoundFont;
        private Synthesizer? _pianoSynth;
        private string _pianoSoundFontPath = string.Empty;

        private IWavePlayer? _waveOut;
        private IWavePosition? _wavePosition;
        private long _totalBytesWritten;

        private MusicScore? _score;
        private List<NoteEvent> _notes = new();
        private int _nextNoteIndex = 0;

        // 追蹤目前正在發聲的音符以便準時送出 NoteOff 至對應的合成器
        private struct ActiveNote
        {
            public bool IsPianoSynth;
            public int Channel;
            public int Pitch;
            public double OffTimeSec;
            public int TrackIndex;
        }

        private readonly List<ActiveNote> _activeNotes = new(128);

        private bool _isPlaying;
        private double _currentTimeSec;
        private double _playbackSpeed = 1.0;
        private double _masterVolume = 1.0;

        // A-B 區間循環狀態
        private double? _loopStartSec;
        private double? _loopEndSec;
        private bool _isLoopEnabled;

        // Interleaved Render 緩衝區 (通用合成器 + 鋼琴專用合成器混音緩衝)
        private readonly float[] _blockLeft = new float[64];
        private readonly float[] _blockRight = new float[64];
        private readonly float[] _pianoLeft = new float[64];
        private readonly float[] _pianoRight = new float[64];

        // 紀錄各 MIDI Channel 已套用的 (Bank, Patch)，避免每次音量或靜音變動都重送 Program Change (0xC0)
        private readonly (int bank, int patch)[] _appliedGeneralPresets = new (int, int)[16];
        private readonly (int bank, int patch)[] _appliedPianoPresets = new (int, int)[16];
        private readonly int[] _generalActiveProgram = new int[16];
        private readonly int[] _pianoActiveProgram = new int[16];
        private readonly Dictionary<int, int> _lastExpressionValues = new();
        private readonly Dictionary<int, int> _lastModulationValues = new();

        public bool UseInstrumentSpatialProfile { get; set; } = true;

        private int _reverbAmount = 30;
        private int _chorusAmount = 15;

        // ===== 影音同步時鐘 (A/V Sync Clock) =====
        // Read() 每次會「預先」合成一整段即將送進音效卡緩衝區的聲音，_currentTimeSec 因此跳躍式前進，
        // 且代表的是「尚未被聽見」的未來時間。畫面改用以下錨點 + Stopwatch 內插的平滑時鐘，
        // 扣除輸出緩衝延遲得到「目前實際聽到的位置」，再加上畫面管線提前量，讓卷軸與琴鍵與耳朵聽到的聲音對齊。
        private readonly object _clockLock = new();
        private double _clockAnchorMediaSec;
        private long _clockAnchorTicks;
        private bool _clockAnchorValid;
        private double _lastVisualTimeSec;
        private double _outputLatencySec = 0.035;

        // 每次音訊回呼結束時，實測裝置尚未播放的音訊量（秒）。
        // 以「已寫入位元組 - 裝置已播放位元組」計算，避免硬編碼延遲造成影音偏移。
        private double _clockAnchorBufferedAudioSec = 0.035;

        public WaveFormat WaveFormat => _waveFormat;

        public bool IsPlaying => _isPlaying;
        public double CurrentTimeSeconds => _currentTimeSec;
        public double TotalDurationSeconds => _score?.TotalDurationSeconds ?? 0.0;

        /// <summary>
        /// 目前估計的音訊輸出延遲（秒）：WASAPI 共享模式緩衝或 WaveOut 緩衝長度。
        /// </summary>
        public double OutputLatencySeconds => _outputLatencySec;

        /// <summary>
        /// 取得供畫面（卷軸、琴鍵、小節資訊）使用的平滑播放時間。
        /// 以最後一次音訊合成的錨點時間，加上 Stopwatch 經過時間內插，扣除輸出緩衝延遲（= 目前實際聽到的位置），
        /// 再加上 <paramref name="visualLeadSeconds"/> 補償 WPF 繪製 → DWM 合成 → 螢幕顯示的管線延遲。
        /// 暫停 / 停止時回傳精確的目前時間。
        /// </summary>
        public double GetVisualTimeSeconds(double visualLeadSeconds)
        {
            double total = _score?.TotalDurationSeconds ?? 0.0;
            lock (_clockLock)
            {
                if (!_isPlaying || !_clockAnchorValid)
                {
                    _lastVisualTimeSec = _currentTimeSec;
                    return _currentTimeSec;
                }

                double speed = _playbackSpeed;
                double audibleSec = ComputeAudibleSecLocked();
                double visual = audibleSec + visualLeadSeconds * speed;

                // 播放中保持單調遞增，消除 Read 回呼時間抖動造成的畫面前後跳動（大幅倒退視為 A-B 循環跳轉，允許）
                if (visual < _lastVisualTimeSec && _lastVisualTimeSec - visual < 0.25)
                    visual = _lastVisualTimeSec;

                visual = Math.Clamp(visual, 0.0, Math.Max(0.0, total));
                _lastVisualTimeSec = visual;
                return visual;
            }
        }

        private double ComputeAudibleSecLocked()
        {
            double speed = _playbackSpeed;
            double elapsedSec = (Stopwatch.GetTimestamp() - _clockAnchorTicks) / (double)Stopwatch.Frequency;
            double audibleSec = _clockAnchorMediaSec - _clockAnchorBufferedAudioSec * speed + elapsedSec * speed;

            // 允許少量調度抖動裕度，避免音訊執行緒短暫調度延遲時畫面凍結幀率
            double maxAhead = _clockAnchorMediaSec + 0.080 * speed;
            if (audibleSec > maxAhead)
                audibleSec = maxAhead;
            return Math.Max(0.0, audibleSec);
        }

        /// <summary>
        /// 取得目前「實際聽到」的播放位置（不含畫面提前量，不做單調化）。
        /// </summary>
        public double GetAudiblePlaybackSeconds()
        {
            lock (_clockLock)
            {
                if (!_isPlaying || !_clockAnchorValid)
                    return _currentTimeSec;
                return ComputeAudibleSecLocked();
            }
        }

        private void ResetVisualClock(double timeSec)
        {
            lock (_clockLock)
            {
                _clockAnchorValid = false;
                _clockAnchorMediaSec = timeSec;
                _lastVisualTimeSec = timeSec;
            }
        }

        /// <summary>
        /// 以裝置實際播放位置量測目前尚在緩衝中的音訊量，並更新時鐘錨點。
        /// 必須在持有 _syncLock 時呼叫。
        /// </summary>
        private void UpdateClockAnchorLocked()
        {
            double bufferedAudioSec = _outputLatencySec;

            var positionProvider = _wavePosition;
            if (positionProvider != null && _totalBytesWritten > 0)
            {
                try
                {
                    long playedBytes = positionProvider.GetPosition();
                    long bufferedBytes = _totalBytesWritten - playedBytes;
                    double bufferedSec = bufferedBytes / (double)_waveFormat.AverageBytesPerSecond;
                    // 合理範圍才採用，避免裝置剛啟動或停止時取得無效位置
                    if (bufferedSec >= 0.0 && bufferedSec <= 5.0)
                    {
                        bufferedAudioSec = bufferedSec;
                    }
                }
                catch
                {
                    // 裝置不支援播放位置查詢時退回估計值
                }
            }

            lock (_clockLock)
            {
                if (_isPlaying && _score != null)
                {
                    _clockAnchorMediaSec = _currentTimeSec;
                    _clockAnchorTicks = Stopwatch.GetTimestamp();
                    _clockAnchorBufferedAudioSec = bufferedAudioSec;
                    _clockAnchorValid = true;
                }
                else
                {
                    _clockAnchorValid = false;
                }
            }
        }

        public double? LoopStartSeconds => _loopStartSec;
        public double? LoopEndSeconds => _loopEndSec;
        public bool IsLoopEnabled => _isLoopEnabled;

        public string CurrentSoundFontPath => _currentSoundFontPath;
        public string CurrentSoundFontName => string.IsNullOrEmpty(_currentSoundFontPath) ? "未載入音源" : Path.GetFileName(_currentSoundFontPath);

        public string PianoSoundFontPath => _pianoSoundFontPath;
        public string PianoSoundFontName => string.IsNullOrEmpty(_pianoSoundFontPath) ? "未指定 (使用通用音源)" : Path.GetFileName(_pianoSoundFontPath);
        public bool HasDedicatedPianoSoundFont => _pianoSynth != null && !string.IsNullOrEmpty(_pianoSoundFontPath);

        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set => _playbackSpeed = Math.Clamp(value, 0.25, 2.0);
        }

        public double MasterVolume
        {
            get => _masterVolume;
            set
            {
                _masterVolume = Math.Clamp(value, 0.0, 2.0);
                lock (_syncLock)
                {
                    if (_synth != null)
                        _synth.MasterVolume = (float)(1.05 * _masterVolume);
                    if (_pianoSynth != null)
                        _pianoSynth.MasterVolume = (float)(1.05 * _masterVolume);
                }
            }
        }

        public int ReverbAmount
        {
            get => _reverbAmount;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (_reverbAmount == clamped) return;
                _reverbAmount = clamped;
                UpdateEffectsSends();
            }
        }

        public int ChorusAmount
        {
            get => _chorusAmount;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (_chorusAmount == clamped) return;
                _chorusAmount = clamped;
                UpdateEffectsSends();
            }
        }

        public event Action? PlaybackCompleted;

        public AudioEngine()
        {
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2);
            ResetAppliedPresetCacheLocked();
        }

        private void ResetAppliedPresetCacheLocked()
        {
            Array.Fill(_appliedGeneralPresets, (-1, -1));
            Array.Fill(_appliedPianoPresets, (-1, -1));
            Array.Fill(_generalActiveProgram, -1);
            Array.Fill(_pianoActiveProgram, -1);
            _lastExpressionValues.Clear();
        }

        public string GetStatusSummaryText()
        {
            if (HasDedicatedPianoSoundFont)
            {
                return $"鋼琴：{PianoSoundFontName} ｜ 其他樂器：{CurrentSoundFontName}";
            }
            return $"目前音源：{CurrentSoundFontName} (真實樂器取樣)";
        }

        /// <summary>
        /// 取得目前通用音源在 Bank 0、GM Program 0..127 的實際 Preset 名稱。
        /// </summary>
        public Dictionary<int, string> GetGeneralSoundFontProgramNames()
        {
            lock (_syncLock)
            {
                var names = new Dictionary<int, string>();
                if (_soundFont == null)
                    return names;

                foreach (var preset in _soundFont.Presets)
                {
                    if (preset.BankNumber == 0 &&
                        preset.PatchNumber is >= 0 and < 128 &&
                        !string.IsNullOrWhiteSpace(preset.Name))
                    {
                        names.TryAdd(preset.PatchNumber, preset.Name.Trim());
                    }
                }

                return names;
            }
        }

        /// <summary>
        /// 取得鋼琴專用音源對 GM 0..5 實際會使用的 Preset 名稱。
        /// </summary>
        public Dictionary<int, string> GetPianoSoundFontProgramNames()
        {
            lock (_syncLock)
            {
                var names = new Dictionary<int, string>();
                if (_pianoSoundFont == null || _pianoSoundFont.Presets.Count == 0)
                    return names;

                for (int program = 0; program <= 5; program++)
                {
                    var (bank, patch) = ResolvePresetForSoundFont(_pianoSoundFont, program);
                    string? name = null;

                    foreach (var preset in _pianoSoundFont.Presets)
                    {
                        if (preset.BankNumber == bank && preset.PatchNumber == patch)
                        {
                            name = preset.Name;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(name))
                        name = _pianoSoundFont.Presets[0].Name;

                    if (!string.IsNullOrWhiteSpace(name))
                        names[program] = name.Trim();
                }

                return names;
            }
        }

        /// <summary>
        /// 供 UI 查詢指定聲部目前是否會路由至「鋼琴專用合成器」。
        /// </summary>
        public bool IsTrackUsingDedicatedPiano(ScorePartTrack track)
        {
            lock (_syncLock)
            {
                return ShouldRouteToPianoSynthLocked(track);
            }
        }

        /// <summary>
        /// 供聲部卡片顯示目前實際使用的音源名稱，避免與 GM 樂器名稱混淆。
        /// </summary>
        public string GetTrackSoundFontRouteText(ScorePartTrack track)
        {
            lock (_syncLock)
            {
                if (ShouldRouteToPianoSynthLocked(track))
                    return $"🎹 獨立鋼琴：{PianoSoundFontName}";

                if (_synth != null)
                    return $"🎻 通用音源：{CurrentSoundFontName}";

                return "⚠ 尚未載入通用音源";
            }
        }

        public static bool IsMuseScoreSoundFont(SoundFont soundFont, string? path = null)
        {
            if (soundFont.Info.BankName.Contains("MuseScore", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrEmpty(path))
                return false;

            string fileName = Path.GetFileName(path);
            return fileName.Contains("MuseScore", StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith("MS_Basic", StringComparison.OrdinalIgnoreCase);
        }

        private SynthesizerSettings CreateSynthesizerSettings(SoundFont soundFont, string? path)
        {
            bool isMuseScore = IsMuseScoreSoundFont(soundFont, path);
            return new SynthesizerSettings(_sampleRate)
            {
                EnableReverbAndChorus = true,
                MaximumPolyphony = 128,
                MinimumFilterCutoffHz = isMuseScore ? MuseScorePianoFilterCutoffFloorHz : 0F,
                FilterCutoffFloorMaxProgram = isMuseScore ? 5 : 127
            };
        }

        public bool LoadSoundFont(string sf2Path, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                if (!SoundFontManager.TryValidateSoundFontFile(sf2Path, out string validationError))
                {
                    errorMessage = validationError;
                    return false;
                }

                var newSf = new SoundFont(sf2Path);
                if (!ValidateLoadedSoundFont(newSf, out string loadedError))
                {
                    errorMessage = loadedError;
                    return false;
                }

                var settings = CreateSynthesizerSettings(newSf, sf2Path);
                var newSynth = new Synthesizer(newSf, settings)
                {
                    MasterVolume = (float)(1.05 * _masterVolume)
                };

                lock (_syncLock)
                {
                    _synth?.NoteOffAll(true);
                    _pianoSynth?.NoteOffAll(true);
                    _activeNotes.Clear();
                    _soundFont = newSf;
                    _synth = newSynth;
                    _currentSoundFontPath = sf2Path;
                    Array.Fill(_appliedGeneralPresets, (-1, -1));
                    Array.Fill(_generalActiveProgram, -1);
                    _lastExpressionValues.Clear();
                    ApplyTrackProgramsAndControllersLocked();
                }

                EnsureWavePlayerInitialized();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"載入通用 SF2 音源失敗：{ex.Message}";
                return false;
            }
        }

        public bool LoadPianoSoundFont(string sf2Path, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                if (!SoundFontManager.TryValidateSoundFontFile(sf2Path, out string validationError))
                {
                    errorMessage = validationError;
                    return false;
                }

                var newSf = new SoundFont(sf2Path);
                if (!ValidateLoadedSoundFont(newSf, out string loadedError))
                {
                    errorMessage = loadedError;
                    return false;
                }

                var settings = CreateSynthesizerSettings(newSf, sf2Path);
                var newSynth = new Synthesizer(newSf, settings)
                {
                    MasterVolume = (float)(1.05 * _masterVolume)
                };

                lock (_syncLock)
                {
                    _synth?.NoteOffAll(true);
                    _pianoSynth?.NoteOffAll(true);
                    _activeNotes.Clear();
                    _pianoSoundFont = newSf;
                    _pianoSynth = newSynth;
                    _pianoSoundFontPath = sf2Path;
                    Array.Fill(_appliedPianoPresets, (-1, -1));
                    Array.Fill(_pianoActiveProgram, -1);
                    _lastExpressionValues.Clear();
                    ApplyTrackProgramsAndControllersLocked();
                }

                EnsureWavePlayerInitialized();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"載入鋼琴專用 SF2 音源失敗：{ex.Message}";
                return false;
            }
        }

        private static bool ValidateLoadedSoundFont(SoundFont soundFont, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (soundFont.Presets.Count == 0 || soundFont.Instruments.Count == 0)
            {
                errorMessage = "此 .sf2 檔案不包含任何 Preset 樂器定義，無法發出聲音。";
                return false;
            }

            ReadOnlySpan<short> waveData = soundFont.WaveData;
            if (waveData.IsEmpty || waveData.IndexOfAnyExcept((short)0) < 0)
            {
                errorMessage = "此 .sf2 檔案的全部取樣資料均為靜音 (0)，可能下載損毀或來源檔案不完整，請重新下載有效的音源檔。";
                return false;
            }

            return true;
        }

        public void ClearPianoSoundFont()
        {
            lock (_syncLock)
            {
                _synth?.NoteOffAll(true);
                _pianoSynth?.NoteOffAll(true);
                _activeNotes.Clear();
                _pianoSoundFont = null;
                _pianoSynth = null;
                _pianoSoundFontPath = string.Empty;
                Array.Fill(_appliedPianoPresets, (-1, -1));
                Array.Fill(_pianoActiveProgram, -1);
                _lastExpressionValues.Clear();
                ApplyTrackProgramsAndControllersLocked();
            }
        }

        /// <summary>
        /// 判斷指定聲部是否應路由至「鋼琴專用合成器」
        /// (GM Program 0..5 為傳統鋼琴/電鋼琴家族)
        /// </summary>
        private bool ShouldRouteToPianoSynthLocked(ScorePartTrack track)
        {
            return _pianoSynth != null && _pianoSoundFont != null && track.GmProgram is >= 0 and <= 5;
        }

        /// <summary>
        /// 針對專用音源進行智慧 Preset 映射：若該 .sf2 僅含單一鋼琴 Preset (如 Bank 0, Patch 0)，自動 Fallback 至第一個可用 Preset
        /// </summary>
        private static (int bank, int patch) ResolvePresetForSoundFont(SoundFont? sf, int requestedProgram)
        {
            int targetProg = Math.Clamp(requestedProgram, 0, 127);
            if (sf?.Presets == null)
                return (0, targetProg);

            bool hasBank0Patch0 = false;
            Preset? firstPreset = null;

            foreach (var p in sf.Presets)
            {
                firstPreset ??= p;
                if (p.BankNumber == 0 && p.PatchNumber == targetProg)
                    return (0, targetProg);
                if (p.BankNumber == 0 && p.PatchNumber == 0)
                    hasBank0Patch0 = true;
            }

            if (hasBank0Patch0)
                return (0, 0);

            if (firstPreset != null)
                return (firstPreset.BankNumber, firstPreset.PatchNumber);

            return (0, targetProg);
        }

        private void EnsureWavePlayerInitialized()
        {
            if (_waveOut != null) return;
            try
            {
                const int wasapiLatencyMs = 35;
                var wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, wasapiLatencyMs);
                wasapi.Init(this);
                wasapi.Play();
                _waveOut = wasapi;
                _wavePosition = wasapi;
                // 共享模式實際延遲 ≈ 緩衝長度 + 音訊引擎週期 (~10ms)
                _outputLatencySec = (wasapiLatencyMs + 10) / 1000.0;
            }
            catch
            {
                try
                {
                    var waveOut = new WaveOutEvent { DesiredLatency = 55 };
                    waveOut.Init(this);
                    waveOut.Play();
                    _waveOut = waveOut;
                    _wavePosition = waveOut;
                    _outputLatencySec = 0.055;
                }
                catch
                {
                    // 無音訊輸出裝置時仍維持時間推進
                }
            }
        }

        public void LoadScore(MusicScore score)
        {
            lock (_syncLock)
            {
                _isPlaying = false;
                _synth?.NoteOffAll(false);
                _pianoSynth?.NoteOffAll(false);
                _activeNotes.Clear();

                _score = score;
                _notes = score.Notes;
                _currentTimeSec = 0.0;
                ResetVisualClock(0.0);
                _nextNoteIndex = 0;
                _loopStartSec = null;
                _loopEndSec = null;
                _isLoopEnabled = false;

                ResetAppliedPresetCacheLocked();
                ApplyTrackProgramsAndControllersLocked();
            }
        }

        public void SetLoopPointA(double timeSec)
        {
            lock (_syncLock)
            {
                double maxDur = _score?.TotalDurationSeconds ?? 0.0;
                _loopStartSec = Math.Clamp(timeSec, 0.0, maxDur);
                _loopEndSec = null;
                _isLoopEnabled = false;
            }
        }

        public void SetLoopPointB(double timeSec)
        {
            lock (_syncLock)
            {
                if (!_loopStartSec.HasValue)
                {
                    SetLoopPointA(timeSec);
                    return;
                }

                double maxDur = _score?.TotalDurationSeconds ?? 0.0;
                double b = Math.Clamp(timeSec, 0.0, maxDur);
                double a = _loopStartSec.Value;
                if (b < a)
                {
                    (a, b) = (b, a);
                }

                if (b - a >= 0.2)
                {
                    _loopStartSec = a;
                    _loopEndSec = b;
                    _isLoopEnabled = true;
                }
            }
        }

        public void ClearLoop()
        {
            lock (_syncLock)
            {
                _loopStartSec = null;
                _loopEndSec = null;
                _isLoopEnabled = false;
            }
        }

        /// <summary>
        /// 供下方 88 鍵鋼琴鍵盤滑鼠點擊或滑過時即時試聽單音
        /// </summary>
        public void PreviewNoteOn(int midiPitch, int velocity = 96)
        {
            EnsureWavePlayerInitialized();
            lock (_syncLock)
            {
                var targetSynth = _pianoSynth ?? _synth;
                if (targetSynth == null) return;

                int pitch = Math.Clamp(midiPitch, 21, 108);
                if (_score == null || _score.Tracks.Count == 0)
                {
                    var sf = _pianoSynth != null ? _pianoSoundFont : _soundFont;
                    var (bank, patch) = ResolvePresetForSoundFont(sf, 0);
                    targetSynth.ProcessMidiMessage(0, 0xB0, 0x00, bank);
                    targetSynth.ProcessMidiMessage(0, 0xB0, 0x20, 0);
                    targetSynth.ProcessMidiMessage(0, 0xC0, patch, 0);
                    targetSynth.ProcessMidiMessage(0, 0xB0, 7, 115);
                }

                targetSynth.NoteOff(0, pitch);
                targetSynth.NoteOn(0, pitch, Math.Clamp(velocity, 1, 127));
            }
        }

        public void PreviewNoteOff(int midiPitch)
        {
            lock (_syncLock)
            {
                int pitch = Math.Clamp(midiPitch, 21, 108);
                _pianoSynth?.NoteOff(0, pitch);
                _synth?.NoteOff(0, pitch);
            }
        }

        /// <summary>
        /// 僅更新各聲部音量 CC7，避免拖拉音量滑桿時重刷全部 Program Change 與爭搶合成器狀態
        /// </summary>
        public void UpdateTrackVolumesOnly()
        {
            lock (_syncLock)
            {
                if (_score == null) return;

                foreach (var track in _score.Tracks)
                {
                    int ch = Math.Clamp(track.MidiChannel, 0, 15);
                    int ccVol = (int)Math.Clamp(Math.Round(115.0 * track.Volume), 0, 127);
                    _synth?.ProcessMidiMessage(ch, 0xB0, 7, ccVol);
                    _pianoSynth?.ProcessMidiMessage(ch, 0xB0, 7, ccVol);
                }

                for (int i = _activeNotes.Count - 1; i >= 0; i--)
                {
                    var an = _activeNotes[i];
                    if (an.TrackIndex >= 0 && an.TrackIndex < _score.Tracks.Count &&
                        _score.Tracks[an.TrackIndex].Volume <= 0.001)
                    {
                        var targetSynth = an.IsPianoSynth ? _pianoSynth : _synth;
                        targetSynth?.NoteOff(an.Channel, an.Pitch);
                        _activeNotes.RemoveAt(i);
                    }
                }
            }
        }

        public void SyncTrackSettings()
        {
            lock (_syncLock)
            {
                ApplyTrackProgramsAndControllersLocked();

                if (_score == null) return;
                bool anySolo = _score.Tracks.Any(t => t.IsSolo);

                for (int i = _activeNotes.Count - 1; i >= 0; i--)
                {
                    var an = _activeNotes[i];
                    if (an.TrackIndex >= 0 && an.TrackIndex < _score.Tracks.Count)
                    {
                        var tr = _score.Tracks[an.TrackIndex];
                        bool audible = !tr.IsMuted && (!anySolo || tr.IsSolo) && tr.Volume > 0.001;
                        bool routeChanged = ShouldRouteToPianoSynthLocked(tr) != an.IsPianoSynth;
                        if (!audible || routeChanged)
                        {
                            var targetSynth = an.IsPianoSynth ? _pianoSynth : _synth;
                            targetSynth?.NoteOff(an.Channel, an.Pitch);
                            _activeNotes.RemoveAt(i);
                        }
                    }
                }
            }
        }

        private int ReverbCc => (int)Math.Clamp(Math.Round(_reverbAmount * 127.0 / 100.0), 0, 127);
        private int ChorusCc => (int)Math.Clamp(Math.Round(_chorusAmount * 127.0 / 100.0), 0, 127);

        private void ApplyTrackProgramsAndControllersLocked()
        {
            if (_score == null) return;

            foreach (var track in _score.Tracks)
            {
                int ch = Math.Clamp(track.MidiChannel, 0, 15);
                int ccVol = (int)Math.Clamp(Math.Round(115.0 * track.Volume), 0, 127);
                var (reverb, chorus) = GetSendLevels(track);

                if (_synth != null)
                {
                    var preset = ResolvePresetForSoundFont(_soundFont, track.GmProgram);
                    bool presetChanged = _appliedGeneralPresets[ch] != preset;
                    bool programChanged = _generalActiveProgram[ch] != track.GmProgram;
                    if (presetChanged || programChanged)
                    {
                        _appliedGeneralPresets[ch] = preset;
                        _generalActiveProgram[ch] = track.GmProgram;
                        _synth.ProcessMidiMessage(ch, 0xB0, 0x00, preset.bank);
                        _synth.ProcessMidiMessage(ch, 0xB0, 0x20, 0);
                        _synth.ProcessMidiMessage(ch, 0xC0, preset.patch, 0);
                        _synth.ProcessMidiMessage(ch, 0xB0, 11, 127);
                        _synth.ProcessMidiMessage(ch, 0xB0, 91, reverb);
                        _synth.ProcessMidiMessage(ch, 0xB0, 93, chorus);
                        _lastExpressionValues.Remove(track.TrackIndex);
                    }
                    _synth.ProcessMidiMessage(ch, 0xB0, 10, Math.Clamp(track.Pan, 0, 127));
                    _synth.ProcessMidiMessage(ch, 0xB0, 7, ccVol);
                }

                if (_pianoSynth != null)
                {
                    var pPreset = ResolvePresetForSoundFont(_pianoSoundFont, track.GmProgram);
                    bool pianoPresetChanged = _appliedPianoPresets[ch] != pPreset;
                    bool pianoProgramChanged = _pianoActiveProgram[ch] != track.GmProgram;
                    if (pianoPresetChanged || pianoProgramChanged)
                    {
                        _appliedPianoPresets[ch] = pPreset;
                        _pianoActiveProgram[ch] = track.GmProgram;
                        _pianoSynth.ProcessMidiMessage(ch, 0xB0, 0x00, pPreset.bank);
                        _pianoSynth.ProcessMidiMessage(ch, 0xB0, 0x20, 0);
                        _pianoSynth.ProcessMidiMessage(ch, 0xC0, pPreset.patch, 0);
                        _pianoSynth.ProcessMidiMessage(ch, 0xB0, 11, 127);
                        _pianoSynth.ProcessMidiMessage(ch, 0xB0, 91, reverb);
                        _pianoSynth.ProcessMidiMessage(ch, 0xB0, 93, chorus);
                        _lastExpressionValues.Remove(track.TrackIndex);
                    }
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 10, Math.Clamp(track.Pan, 0, 127));
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 7, ccVol);
                }
            }

            ResetModulationLocked();
        }

        private (int reverb, int chorus) GetSendLevels(ScorePartTrack track)
        {
            int reverb = ReverbCc;
            int chorus = ChorusCc;
            if (!UseInstrumentSpatialProfile)
                return (reverb, chorus);

            double reverbMultiplier = 1.0;
            double chorusMultiplier = 0.4;
            int program = track.GmProgram;

            if (track.MidiChannel == 9 || program == 47)
            {
                reverbMultiplier = 0.60;
                chorusMultiplier = 0.0;
            }
            else if (program <= 5)
            {
                reverbMultiplier = 0.90;
                chorusMultiplier = 0.30;
            }
            else if (program <= 15)
            {
                reverbMultiplier = 0.80;
                chorusMultiplier = 0.20;
            }
            else if (program <= 23)
            {
                reverbMultiplier = 1.20;
                chorusMultiplier = 1.10;
            }
            else if (program <= 31)
            {
                reverbMultiplier = 1.00;
                chorusMultiplier = 0.40;
            }
            else if (program <= 39)
            {
                reverbMultiplier = 0.75;
                chorusMultiplier = 0.20;
            }
            else if (program <= 51)
            {
                reverbMultiplier = 1.15;
                chorusMultiplier = 0.90;
            }
            else if (program <= 55)
            {
                reverbMultiplier = 1.20;
                chorusMultiplier = 1.00;
            }
            else if (program <= 63)
            {
                reverbMultiplier = 1.00;
                chorusMultiplier = 0.40;
            }
            else if (program <= 79)
            {
                reverbMultiplier = 1.05;
                chorusMultiplier = 0.50;
            }
            else if (program <= 95)
            {
                reverbMultiplier = 1.20;
                chorusMultiplier = 0.80;
            }
            else
            {
                reverbMultiplier = 0.90;
                chorusMultiplier = 0.40;
            }

            return (
                Math.Clamp((int)Math.Round(reverb * reverbMultiplier), 0, 127),
                Math.Clamp((int)Math.Round(chorus * chorusMultiplier), 0, 127));
        }

        public void UpdateEffectsSends()
        {
            lock (_syncLock)
            {
                if (_score == null) return;

                foreach (var track in _score.Tracks)
                {
                    int ch = Math.Clamp(track.MidiChannel, 0, 15);
                    var (reverb, chorus) = GetSendLevels(track);
                    _synth?.ProcessMidiMessage(ch, 0xB0, 91, reverb);
                    _synth?.ProcessMidiMessage(ch, 0xB0, 93, chorus);
                    _pianoSynth?.ProcessMidiMessage(ch, 0xB0, 91, reverb);
                    _pianoSynth?.ProcessMidiMessage(ch, 0xB0, 93, chorus);
                }
            }
        }

        private void ResetModulationLocked()
        {
            _lastModulationValues.Clear();
            if (_synth == null)
                return;

            for (int ch = 0; ch < 16; ch++)
                _synth.ProcessMidiMessage(ch, 0xB0, 1, 0);
        }

        public void Play()
        {
            EnsureWavePlayerInitialized();
            lock (_syncLock)
            {
                if (_score == null) return;
                if (_currentTimeSec >= _score.TotalDurationSeconds - 0.02)
                {
                    SeekInternalLocked(0.0);
                }
                ApplyTrackProgramsAndControllersLocked();
                ResetVisualClock(_currentTimeSec);
                _isPlaying = true;
            }
        }

        public void Pause()
        {
            lock (_syncLock)
            {
                // 暫停時回到「實際已聽到」的位置，避免畫面跳到尚在緩衝區、未被聽見的時間點
                double audibleSec = _isPlaying ? GetAudiblePlaybackSeconds() : _currentTimeSec;
                _isPlaying = false;
                _synth?.NoteOffAll(false);
                _pianoSynth?.NoteOffAll(false);
                _activeNotes.Clear();
                ResetModulationLocked();
                if (_score != null && audibleSec < _currentTimeSec)
                    SeekInternalLocked(audibleSec);
                else
                    ResetVisualClock(_currentTimeSec);
            }
        }

        public void Stop()
        {
            lock (_syncLock)
            {
                _isPlaying = false;
                _synth?.NoteOffAll(false);
                _pianoSynth?.NoteOffAll(false);
                _activeNotes.Clear();
                SeekInternalLocked(0.0);
            }
        }

        public void Seek(double timeSeconds)
        {
            lock (_syncLock)
            {
                SeekInternalLocked(timeSeconds);
            }
        }

        private void SeekInternalLocked(double timeSeconds)
        {
            double maxDur = _score?.TotalDurationSeconds ?? 0.0;
            _currentTimeSec = Math.Clamp(timeSeconds, 0.0, maxDur);
            ResetVisualClock(_currentTimeSec);

            _synth?.NoteOffAll(false);
            _pianoSynth?.NoteOffAll(false);
            _activeNotes.Clear();
            _lastExpressionValues.Clear();
            ResetModulationLocked();

            if (_notes.Count == 0)
            {
                _nextNoteIndex = 0;
                return;
            }

            int lo = 0;
            int hi = _notes.Count - 1;
            int found = _notes.Count;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_notes[mid].StartTimeSeconds >= _currentTimeSec - 0.002)
                {
                    found = mid;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            _nextNoteIndex = found;
        }

        private void ApplyExpressionAutomationLocked(double blockEndTime)
        {
            if (_synth == null || _score == null) return;

            foreach (var track in _score.Tracks)
            {
                if (track.ExpressionPoints.Count == 0)
                    continue;
                if (ShouldRouteToPianoSynthLocked(track))
                    continue;

                int value = EvaluateExpression(track.ExpressionPoints, blockEndTime);
                if (!_lastExpressionValues.TryGetValue(track.TrackIndex, out int previous) || previous != value)
                {
                    _lastExpressionValues[track.TrackIndex] = value;
                    int ch = Math.Clamp(track.MidiChannel, 0, 15);
                    _synth.ProcessMidiMessage(ch, 0xB0, 11, value);
                }
            }
        }

        private void ApplyModulationAutomationLocked(double blockEndTime)
        {
            if (_synth == null || _score == null) return;

            foreach (var pair in _score.ModulationPointsByChannel)
            {
                var points = pair.Value;
                if (points.Count == 0)
                    continue;

                int value = EvaluateAutomation(points, blockEndTime, 0);
                if (!_lastModulationValues.TryGetValue(pair.Key, out int previous) || previous != value)
                {
                    _lastModulationValues[pair.Key] = value;
                    _synth.ProcessMidiMessage(Math.Clamp(pair.Key, 0, 15), 0xB0, 1, value);
                }
            }
        }

        private static int EvaluateExpression(List<ExpressionPoint> points, double timeSeconds)
        {
            return EvaluateAutomation(points, timeSeconds, 127);
        }

        private static int EvaluateAutomation(List<ExpressionPoint> points, double timeSeconds, int defaultValue)
        {
            if (points.Count == 0 || timeSeconds < points[0].TimeSeconds)
                return defaultValue;

            int lo = 0;
            int hi = points.Count - 1;
            int answer = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (points[mid].TimeSeconds <= timeSeconds)
                {
                    answer = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return points[answer].Value;
        }

        private static bool HasExactBank0ProgramLocked(SoundFont? soundFont, int program)
        {
            if (soundFont == null)
                return false;

            foreach (var preset in soundFont.Presets)
            {
                if (preset.BankNumber == 0 && preset.PatchNumber == program)
                    return true;
            }

            return false;
        }

        private static void ApplyNoteProgramLocked(
            Synthesizer synth,
            SoundFont? soundFont,
            int channel,
            int program,
            int[] activePrograms)
        {
            if (soundFont == null || activePrograms[channel] == program)
                return;

            var (bank, patch) = ResolvePresetForSoundFont(soundFont, program);
            synth.ProcessMidiMessage(channel, 0xB0, 0x00, bank);
            synth.ProcessMidiMessage(channel, 0xB0, 0x20, 0);
            synth.ProcessMidiMessage(channel, 0xC0, patch, 0);
            activePrograms[channel] = program;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int totalFrames = count / 2;
            int framesRemaining = totalFrames;
            int outIndex = offset;
            bool reachedEnd = false;

            lock (_syncLock)
            {
                if (_synth == null && _pianoSynth == null)
                {
                    Array.Clear(buffer, offset, count);
                    _totalBytesWritten += (long)count * sizeof(float);
                    return count;
                }

                while (framesRemaining > 0)
                {
                    int blockFrames = Math.Min(64, framesRemaining);

                    if (_isPlaying && _score != null)
                    {
                        double deltaSec = (blockFrames / (double)_sampleRate) * _playbackSpeed;
                        double blockEndTime = _currentTimeSec + deltaSec;

                        // A-B 區間循環檢查：到達 B 點時無縫跳回 A 點
                        if (_isLoopEnabled && _loopStartSec.HasValue && _loopEndSec.HasValue &&
                            _loopEndSec.Value > _loopStartSec.Value + 0.15 &&
                            blockEndTime >= _loopEndSec.Value)
                        {
                            SeekInternalLocked(_loopStartSec.Value);
                            blockEndTime = _currentTimeSec + deltaSec;
                        }

                        ApplyExpressionAutomationLocked(blockEndTime);
                        ApplyModulationAutomationLocked(blockEndTime);

                        // 1. 處理到期應關閉的音符 (NoteOff)
                        for (int i = _activeNotes.Count - 1; i >= 0; i--)
                        {
                            if (_activeNotes[i].OffTimeSec <= blockEndTime)
                            {
                                var an = _activeNotes[i];
                                var targetSynth = an.IsPianoSynth ? _pianoSynth : _synth;
                                targetSynth?.NoteOff(an.Channel, an.Pitch);
                                _activeNotes.RemoveAt(i);
                            }
                        }

                        // 2. 觸發本時間區間內開始的音符 (NoteOn)
                        bool anySolo = false;
                        for (int t = 0; t < _score.Tracks.Count; t++)
                        {
                            if (_score.Tracks[t].IsSolo)
                            {
                                anySolo = true;
                                break;
                            }
                        }

                        while (_nextNoteIndex < _notes.Count && _notes[_nextNoteIndex].StartTimeSeconds <= blockEndTime)
                        {
                            var note = _notes[_nextNoteIndex];
                            _nextNoteIndex++;

                            if (note.TrackIndex >= 0 && note.TrackIndex < _score.Tracks.Count)
                            {
                                var track = _score.Tracks[note.TrackIndex];
                                bool audible = !track.IsMuted && (!anySolo || track.IsSolo) && track.Volume > 0.001;
                                if (audible)
                                {
                                    bool usePianoSynth = ShouldRouteToPianoSynthLocked(track);
                                    var targetSynth = usePianoSynth ? _pianoSynth : _synth;
                                    if (targetSynth != null)
                                    {
                                        int ch = Math.Clamp(track.MidiChannel, 0, 15);
                                        int vel = (int)Math.Clamp(Math.Round(note.Velocity * Math.Min(1.25, track.Volume)), 1, 127);

                                        for (int a = _activeNotes.Count - 1; a >= 0; a--)
                                        {
                                            if (_activeNotes[a].IsPianoSynth == usePianoSynth &&
                                                _activeNotes[a].Channel == ch &&
                                                _activeNotes[a].Pitch == note.MidiPitch)
                                            {
                                                targetSynth.NoteOff(ch, note.MidiPitch);
                                                _activeNotes.RemoveAt(a);
                                            }
                                        }

                                        var targetSoundFont = usePianoSynth ? _pianoSoundFont : _soundFont;
                                        int desiredProgram = note.ProgramOverride ?? track.GmProgram;
                                        if (note.ProgramOverride.HasValue &&
                                            !HasExactBank0ProgramLocked(targetSoundFont, desiredProgram))
                                        {
                                            desiredProgram = track.GmProgram;
                                        }

                                        ApplyNoteProgramLocked(
                                            targetSynth,
                                            targetSoundFont,
                                            ch,
                                            desiredProgram,
                                            usePianoSynth ? _pianoActiveProgram : _generalActiveProgram);

                                        targetSynth.NoteOn(ch, note.MidiPitch, vel);
                                        _activeNotes.Add(new ActiveNote
                                        {
                                            IsPianoSynth = usePianoSynth,
                                            Channel = ch,
                                            Pitch = note.MidiPitch,
                                            OffTimeSec = note.AudioEndTimeSeconds,
                                            TrackIndex = note.TrackIndex
                                        });
                                    }
                                }
                            }
                        }

                        _currentTimeSec = blockEndTime;

                        if (_currentTimeSec >= _score.TotalDurationSeconds + 0.35 && _activeNotes.Count == 0)
                        {
                            _isPlaying = false;
                            reachedEnd = true;
                        }
                    }

                    if (_synth != null)
                    {
                        _synth.Render(_blockLeft.AsSpan(0, blockFrames), _blockRight.AsSpan(0, blockFrames));
                    }
                    else
                    {
                        Array.Clear(_blockLeft, 0, blockFrames);
                        Array.Clear(_blockRight, 0, blockFrames);
                    }

                    if (_pianoSynth != null)
                    {
                        _pianoSynth.Render(_pianoLeft.AsSpan(0, blockFrames), _pianoRight.AsSpan(0, blockFrames));
                        for (int f = 0; f < blockFrames; f++)
                        {
                            _blockLeft[f] += _pianoLeft[f];
                            _blockRight[f] += _pianoRight[f];
                        }
                    }

                    for (int f = 0; f < blockFrames; f++)
                    {
                        buffer[outIndex++] = SoftClip(_blockLeft[f]);
                        buffer[outIndex++] = SoftClip(_blockRight[f]);
                    }

                    framesRemaining -= blockFrames;
                }

                _totalBytesWritten += (long)count * sizeof(float);

                // 更新影音同步時鐘錨點：以裝置實際播放位置反推目前緩衝中的音訊量
                UpdateClockAnchorLocked();
            }

            if (reachedEnd)
            {
                PlaybackCompleted?.Invoke();
            }

            return count;
        }

        /// <summary>
        /// 無分支平滑軟限幅 (Soft Clipper)：線性區間 [-0.92, 0.92] 零失真，超過時平滑漸近至 [-1.0, 1.0] 防止 WASAPI 破音
        /// </summary>
        private static float SoftClip(float x)
        {
            const float threshold = 0.92f;
            float abs = MathF.Abs(x);
            if (abs <= threshold)
                return x;

            float excess = abs - threshold;
            float compressed = threshold + (1.0f - threshold) * (excess / (excess + (1.0f - threshold)));
            return x >= 0f ? compressed : -compressed;
        }

        /// <summary>
        /// 使用獨立合成器實例將目前樂譜以離線極速 (~50x 即時速度) 渲染並匯出為 16-bit 44.1kHz 立體聲 WAV 檔案
        /// </summary>
        public bool ExportToWavFile(string outputWavPath, out string errorMessage)
        {
            errorMessage = string.Empty;
            MusicScore? scoreSnap;
            SoundFont? generalSfSnap;
            SoundFont? pianoSfSnap;
            string generalSfPathSnap;
            string pianoSfPathSnap;
            double speedSnap;
            double masterVolSnap;
            int reverbSnap;
            int chorusSnap;

            lock (_syncLock)
            {
                scoreSnap = _score;
                generalSfSnap = _soundFont;
                pianoSfSnap = _pianoSoundFont;
                generalSfPathSnap = _currentSoundFontPath;
                pianoSfPathSnap = _pianoSoundFontPath;
                speedSnap = _playbackSpeed;
                masterVolSnap = _masterVolume;
                reverbSnap = _reverbAmount;
                chorusSnap = _chorusAmount;
            }

            if (scoreSnap == null || scoreSnap.Notes.Count == 0)
            {
                errorMessage = "目前尚未載入任何樂譜。";
                return false;
            }

            if (generalSfSnap == null && pianoSfSnap == null)
            {
                errorMessage = "尚未載入任何 .sf2 音源檔。";
                return false;
            }

            try
            {
                using var offlineEngine = new AudioEngine
                {
                    PlaybackSpeed = speedSnap,
                    MasterVolume = masterVolSnap,
                    ReverbAmount = reverbSnap,
                    ChorusAmount = chorusSnap
                };

                if (generalSfSnap != null)
                {
                    var generalSettings = CreateSynthesizerSettings(generalSfSnap, generalSfPathSnap);
                    offlineEngine._soundFont = generalSfSnap;
                    offlineEngine._synth = new Synthesizer(generalSfSnap, generalSettings)
                    {
                        MasterVolume = (float)(1.05 * masterVolSnap)
                    };
                    offlineEngine._currentSoundFontPath = generalSfPathSnap;
                }

                if (pianoSfSnap != null)
                {
                    var pianoSettings = CreateSynthesizerSettings(pianoSfSnap, pianoSfPathSnap);
                    offlineEngine._pianoSoundFont = pianoSfSnap;
                    offlineEngine._pianoSynth = new Synthesizer(pianoSfSnap, pianoSettings)
                    {
                        MasterVolume = (float)(1.05 * masterVolSnap)
                    };
                    offlineEngine._pianoSoundFontPath = pianoSfPathSnap;
                }

                offlineEngine.LoadScore(scoreSnap);
                offlineEngine._isPlaying = true;

                using var writer = new WaveFileWriter(outputWavPath, new WaveFormat(_sampleRate, 16, 2));
                float[] renderBuf = new float[4096];
                short[] pcm16Buf = new short[4096];
                byte[] byteBuf = new byte[4096 * 2];

                int maxFrames = (int)Math.Ceiling(((scoreSnap.TotalDurationSeconds / speedSnap) + 2.5) * _sampleRate);
                int framesWritten = 0;

                while (offlineEngine.IsPlaying && framesWritten < maxFrames)
                {
                    int readSamples = offlineEngine.Read(renderBuf, 0, renderBuf.Length);
                    for (int i = 0; i < readSamples; i++)
                    {
                        float clamped = Math.Clamp(renderBuf[i], -1.0f, 1.0f);
                        pcm16Buf[i] = (short)Math.Round(clamped * 32767f);
                    }
                    Buffer.BlockCopy(pcm16Buf, 0, byteBuf, 0, readSamples * 2);
                    writer.Write(byteBuf, 0, readSamples * 2);
                    framesWritten += readSamples / 2;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
