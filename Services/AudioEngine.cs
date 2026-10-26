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

        private SoundFont? _soundFont;
        private Synthesizer? _synth;
        private IWavePlayer? _waveOut;

        private MusicScore? _score;
        private List<NoteEvent> _notes = new();
        private int _nextNoteIndex = 0;

        // 追蹤目前正在發聲的音符以便準時送出 NoteOff
        private struct ActiveNote
        {
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
        private string _currentSoundFontPath = string.Empty;

        // Interleaved Render 緩衝區
        private readonly float[] _blockLeft = new float[64];
        private readonly float[] _blockRight = new float[64];

        public WaveFormat WaveFormat => _waveFormat;

        public bool IsPlaying => _isPlaying;
        public double CurrentTimeSeconds => _currentTimeSec;
        public double TotalDurationSeconds => _score?.TotalDurationSeconds ?? 0.0;
        public string CurrentSoundFontPath => _currentSoundFontPath;
        public string CurrentSoundFontName => string.IsNullOrEmpty(_currentSoundFontPath) ? "未載入音源" : Path.GetFileName(_currentSoundFontPath);

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
                if (_synth != null)
                {
                    lock (_syncLock)
                    {
                        _synth.MasterVolume = (float)(1.05 * _masterVolume);
                    }
                }
            }
        }

        public event Action? PlaybackCompleted;

        public AudioEngine()
        {
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2);
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
                errorMessage = $"載入 SF2 音源失敗：{ex.Message}";
                return false;
            }
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

                if (_score == null || _synth == null) return;
                bool anySolo = _score.Tracks.Any(t => t.IsSolo);

                for (int i = _activeNotes.Count - 1; i >= 0; i--)
                {
                    var an = _activeNotes[i];
                    if (an.TrackIndex >= 0 && an.TrackIndex < _score.Tracks.Count)
                    {
                        var tr = _score.Tracks[an.TrackIndex];
                        bool audible = !tr.IsMuted && (!anySolo || tr.IsSolo) && tr.Volume > 0.001;
                        if (!audible)
                        {
                            _synth.NoteOff(an.Channel, an.Pitch);
                            _activeNotes.RemoveAt(i);
                        }
                    }
                }
            }
        }

        private void ApplyTrackProgramsAndControllersLocked()
        {
            if (_synth == null || _score == null) return;

            foreach (var track in _score.Tracks)
            {
                int ch = Math.Clamp(track.MidiChannel, 0, 15);
                // Bank 0
                _synth.ProcessMidiMessage(ch, 0xB0, 0x00, 0);
                _synth.ProcessMidiMessage(ch, 0xB0, 0x20, 0);
                // Program Change
                _synth.ProcessMidiMessage(ch, 0xC0, Math.Clamp(track.GmProgram, 0, 127), 0);
                // Pan (CC 10)
                _synth.ProcessMidiMessage(ch, 0xB0, 10, Math.Clamp(track.Pan, 0, 127));
                // Expression (CC 11)
                _synth.ProcessMidiMessage(ch, 0xB0, 11, 127);
                // Reverb Send (CC 91) & Chorus Send (CC 93)
                _synth.ProcessMidiMessage(ch, 0xB0, 91, 52);
                _synth.ProcessMidiMessage(ch, 0xB0, 93, 18);
                // Channel Volume (CC 7)
                int ccVol = (int)Math.Clamp(Math.Round(115.0 * track.Volume), 0, 127);
                _synth.ProcessMidiMessage(ch, 0xB0, 7, ccVol);
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
                _activeNotes.Clear();
            }
        }

        public void Stop()
        {
            lock (_syncLock)
            {
                _isPlaying = false;
                _synth?.NoteOffAll(false);
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
            _activeNotes.Clear();

            if (_notes.Count == 0)
            {
                _nextNoteIndex = 0;
                return;
            }

            // 二分搜尋第一個 StartTimeSeconds >= _currentTimeSec 的音符
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
                if (_synth == null)
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
                                _synth.NoteOff(_activeNotes[i].Channel, _activeNotes[i].Pitch);
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
                                    int ch = Math.Clamp(track.MidiChannel, 0, 15);
                                    int vel = (int)Math.Clamp(Math.Round(note.Velocity * Math.Min(1.25, track.Volume)), 1, 127);

                                    // 若同通道同音高仍在響，先釋放再重新觸鍵
                                    for (int a = _activeNotes.Count - 1; a >= 0; a--)
                                    {
                                        if (_activeNotes[a].Channel == ch && _activeNotes[a].Pitch == note.MidiPitch)
                                        {
                                            _synth.NoteOff(ch, note.MidiPitch);
                                            _activeNotes.RemoveAt(a);
                                        }
                                    }

                                    _synth.NoteOn(ch, note.MidiPitch, vel);
                                    _activeNotes.Add(new ActiveNote
                                    {
                                        Channel = ch,
                                        Pitch = note.MidiPitch,
                                        OffTimeSec = note.AudioEndTimeSeconds,
                                        TrackIndex = note.TrackIndex
                                    });
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

                    _synth.Render(_blockLeft.AsSpan(0, blockFrames), _blockRight.AsSpan(0, blockFrames));

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
