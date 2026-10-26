using System;
using System.Collections.Generic;
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

        // 通用樂器音源 (General MIDI SoundFont)
        private SoundFont? _soundFont;
        private Synthesizer? _synth;
        private string _currentSoundFontPath = string.Empty;

        // 鋼琴專用音源 (Dedicated Piano SoundFont - 方案 A)
        private SoundFont? _pianoSoundFont;
        private Synthesizer? _pianoSynth;
        private string _pianoSoundFontPath = string.Empty;

        private IWavePlayer? _waveOut;

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
        private double _masterVolume = 1.25;

        // Interleaved Render 緩衝區 (通用合成器 + 鋼琴專用合成器混音緩衝)
        private readonly float[] _blockLeft = new float[64];
        private readonly float[] _blockRight = new float[64];
        private readonly float[] _pianoLeft = new float[64];
        private readonly float[] _pianoRight = new float[64];

        public WaveFormat WaveFormat => _waveFormat;

        public bool IsPlaying => _isPlaying;
        public double CurrentTimeSeconds => _currentTimeSec;
        public double TotalDurationSeconds => _score?.TotalDurationSeconds ?? 0.0;

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

        public event Action? PlaybackCompleted;

        public AudioEngine()
        {
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2);
        }

        public string GetStatusSummaryText()
        {
            if (HasDedicatedPianoSoundFont)
            {
                return $"鋼琴：{PianoSoundFontName} ｜ 其他樂器：{CurrentSoundFontName}";
            }
            return $"目前音源：{CurrentSoundFontName} (真實樂器取樣)";
        }

        public bool LoadSoundFont(string sf2Path, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                if (!File.Exists(sf2Path))
                {
                    errorMessage = $"找不到音源檔案：{sf2Path}";
                    return false;
                }

                var newSf = new SoundFont(sf2Path);
                var settings = new SynthesizerSettings(_sampleRate)
                {
                    EnableReverbAndChorus = true,
                    MaximumPolyphony = 128
                };
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
                if (!File.Exists(sf2Path))
                {
                    errorMessage = $"找不到鋼琴音源檔案：{sf2Path}";
                    return false;
                }

                var newSf = new SoundFont(sf2Path);
                var settings = new SynthesizerSettings(_sampleRate)
                {
                    EnableReverbAndChorus = true,
                    MaximumPolyphony = 128
                };
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
                var wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 35);
                wasapi.Init(this);
                wasapi.Play();
                _waveOut = wasapi;
            }
            catch
            {
                try
                {
                    var waveOut = new WaveOutEvent { DesiredLatency = 55 };
                    waveOut.Init(this);
                    waveOut.Play();
                    _waveOut = waveOut;
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
                _nextNoteIndex = 0;

                ApplyTrackProgramsAndControllersLocked();
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

        private void ApplyTrackProgramsAndControllersLocked()
        {
            if (_score == null) return;

            foreach (var track in _score.Tracks)
            {
                int ch = Math.Clamp(track.MidiChannel, 0, 15);
                int ccVol = (int)Math.Clamp(Math.Round(115.0 * track.Volume), 0, 127);

                if (_synth != null)
                {
                    var (bank, patch) = ResolvePresetForSoundFont(_soundFont, track.GmProgram);
                    _synth.ProcessMidiMessage(ch, 0xB0, 0x00, bank);
                    _synth.ProcessMidiMessage(ch, 0xB0, 0x20, 0);
                    _synth.ProcessMidiMessage(ch, 0xC0, patch, 0);
                    _synth.ProcessMidiMessage(ch, 0xB0, 10, Math.Clamp(track.Pan, 0, 127));
                    _synth.ProcessMidiMessage(ch, 0xB0, 11, 127);
                    _synth.ProcessMidiMessage(ch, 0xB0, 91, 52);
                    _synth.ProcessMidiMessage(ch, 0xB0, 93, 18);
                    _synth.ProcessMidiMessage(ch, 0xB0, 7, ccVol);
                }

                if (_pianoSynth != null)
                {
                    var (pBank, pPatch) = ResolvePresetForSoundFont(_pianoSoundFont, track.GmProgram);
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 0x00, pBank);
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 0x20, 0);
                    _pianoSynth.ProcessMidiMessage(ch, 0xC0, pPatch, 0);
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 10, Math.Clamp(track.Pan, 0, 127));
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 11, 127);
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 91, 52);
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 93, 18);
                    _pianoSynth.ProcessMidiMessage(ch, 0xB0, 7, ccVol);
                }
            }
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
                _isPlaying = true;
            }
        }

        public void Pause()
        {
            lock (_syncLock)
            {
                _isPlaying = false;
                _synth?.NoteOffAll(false);
                _pianoSynth?.NoteOffAll(false);
                _activeNotes.Clear();
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

            _synth?.NoteOffAll(false);
            _pianoSynth?.NoteOffAll(false);
            _activeNotes.Clear();

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
                    return count;
                }

                while (framesRemaining > 0)
                {
                    int blockFrames = Math.Min(64, framesRemaining);

                    if (_isPlaying && _score != null)
                    {
                        double deltaSec = (blockFrames / (double)_sampleRate) * _playbackSpeed;
                        double blockEndTime = _currentTimeSec + deltaSec;

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
                        buffer[outIndex++] = _blockLeft[f];
                        buffer[outIndex++] = _blockRight[f];
                    }

                    framesRemaining -= blockFrames;
                }
            }

            if (reachedEnd)
            {
                PlaybackCompleted?.Invoke();
            }

            return count;
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
