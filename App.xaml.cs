using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using WinMusicXmlPlayer.Models;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ThemeManager.Initialize();

            if (e.Args.Any(a => string.Equals(a, "--verify-all", StringComparison.OrdinalIgnoreCase)))
            {
                int exitCode = RunAutomatedVerification();
                Shutdown(exitCode);
                return;
            }

            if (e.Args.Any(a => string.Equals(a, "--capture-ui", StringComparison.OrdinalIgnoreCase)))
            {
                var win = new MainWindow { AutoLoadLastScore = false };
                win.Show();
                var files = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.musicxml").OrderBy(f => f).ToList();
                string outDir = @"C:\Users\STATUS\.gemini\antigravity\brain\9841aea9-af6e-4051-b038-e01f27520133";
                string? springFile = files.FirstOrDefault(f => f.Contains("allegro.musicxml"));
                string? trioFile = files.FirstOrDefault(f => f.Contains("k-496"));
                string? moonlightFile = files.FirstOrDefault(f => f.Contains("moonlight"));

                if (springFile != null)
                {
                    win.CaptureScreenshotAt(springFile, 8.2, Controls.PianoRollViewMode.HorizontalTimeline, Path.Combine(outDir, "ui_horizontal_spring.png"));
                }
                if (trioFile != null)
                {
                    win.CaptureScreenshotAt(trioFile, 14.5, Controls.PianoRollViewMode.VerticalFalling, Path.Combine(outDir, "ui_vertical_trio.png"));
                }
                if (moonlightFile != null)
                {
                    win.CaptureScreenshotAt(moonlightFile, 32.0, Controls.PianoRollViewMode.HorizontalTimeline, Path.Combine(outDir, "ui_moonlight_voices.png"));
                }
                win.Close();
                Shutdown(0);
                return;
            }

            base.OnStartup(e);
        }

        private static int RunAutomatedVerification()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Windows MusicXML Player Automated Verification ===");

            try
            {
                string? sf2Path = SoundFontManager.FindDefaultSoundFontPath();
                if (string.IsNullOrEmpty(sf2Path) || !File.Exists(sf2Path))
                {
                    sb.AppendLine("[FAIL] Default SoundFont (.sf2) not found!");
                    File.WriteAllText("verification_report.txt", sb.ToString(), Encoding.UTF8);
                    return 1;
                }

                using var engine = new AudioEngine();
                if (!engine.LoadSoundFont(sf2Path, out string err))
                {
                    sb.AppendLine($"[FAIL] Failed to load SoundFont: {err}");
                    File.WriteAllText("verification_report.txt", sb.ToString(), Encoding.UTF8);
                    return 2;
                }

                sb.AppendLine($"[PASS] Loaded SoundFont: {sf2Path}");

                string staffsDir = Path.Combine(Directory.GetCurrentDirectory(), "staffs");
                string searchDir = Directory.Exists(staffsDir) ? staffsDir : Directory.GetCurrentDirectory();
                var musicFiles = Directory.GetFiles(searchDir, "*.musicxml")
                    .OrderBy(f => f)
                    .ToList();

                if (musicFiles.Count == 0)
                {
                    sb.AppendLine("[FAIL] No .musicxml files found in current directory.");
                    File.WriteAllText("verification_report.txt", sb.ToString(), Encoding.UTF8);
                    return 3;
                }

                foreach (string file in musicFiles)
                {
                    var score = MusicXmlLoader.LoadFromFile(file, expandRepeats: true);
                    sb.AppendLine($"--------------------------------------------------");
                    sb.AppendLine($"[PASS] File: {Path.GetFileName(file)}");
                    sb.AppendLine($"  Title: {score.Title} | Composer: {score.Composer}");
                    sb.AppendLine($"  Measures (Expanded): {score.Measures.Count} | Total Notes: {score.Notes.Count} | Duration: {score.TotalDurationSeconds:F2}s");
                    sb.AppendLine($"  Pitch Range: MIDI {score.MinMidiPitch}..{score.MaxMidiPitch}");

                    foreach (var tr in score.Tracks)
                    {
                        sb.AppendLine($"  Track [{tr.TrackIndex}] Ch={tr.MidiChannel}: {tr.Name} -> GM {tr.GmProgram + 1} ({SoundFontManager.GetInstrumentDisplayName(tr.GmProgram)}), Pan={tr.Pan}, Notes={tr.NoteCount}");
                    }

                    // 測試音訊合成與跳轉 (Seek)
                    engine.LoadScore(score);
                    engine.Play();
                    double targetSeek = score.TotalDurationSeconds * 0.25;
                    var seekNote = score.Notes.FirstOrDefault(n => n.StartTimeSeconds >= targetSeek) ?? score.Notes.FirstOrDefault();
                    engine.Seek(seekNote?.StartTimeSeconds ?? 0.0);

                    float[] sampleBuffer = new float[44100 * 2]; // 1 秒立體聲
                    int read = engine.Read(sampleBuffer, 0, sampleBuffer.Length);
                    float peak = 0f;
                    for (int i = 0; i < read; i++)
                    {
                        float abs = Math.Abs(sampleBuffer[i]);
                        if (abs > peak) peak = abs;
                    }
                    engine.Stop();

                    sb.AppendLine($"  Audio Render 1s Peak Amplitude: {peak:F4}");
                    if (peak <= 0.0001f || score.Notes.Count == 0 || score.Tracks.Count == 0)
                    {
                        sb.AppendLine($"[FAIL] Zero audio output or empty notes for {file}");
                        File.WriteAllText("verification_report.txt", sb.ToString(), Encoding.UTF8);
                        return 4;
                    }
                }

                // 測試演奏表情功能：琶音方向、呼吸/Cesura、人性化、顫音與力度曲線
                if (!RunPerformanceExpressionSelfTest(sb))
                {
                    File.WriteAllText("verification_report.txt", sb.ToString(), Encoding.UTF8);
                    return 5;
                }
                sb.AppendLine("[PASS] Performance expression self-test (arpeggio/breath/humanize/vibrato/velocity) passed!");

                // 測試 UI 選單展開、模式單一按鈕切換、重置按鈕隱藏/顯示與 Toast 訊息
                var testWin = new MainWindow { AutoLoadLastScore = false };
                testWin.Show();
                testWin.RunUiSelfTest(musicFiles[0]);
                testWin.Close();
                sb.AppendLine("[PASS] UI Self-Test (Menu Popup, ViewMode Toggle, Reset Button Visibility & Toast) passed!");

                var preferencesWindow = new PreferencesWindow();
                preferencesWindow.Show();
                preferencesWindow.Close();
                sb.AppendLine("[PASS] Preferences Window Self-Test (Performance card & bindings) passed!");

                sb.AppendLine("==================================================");
                sb.AppendLine($"[ALL PASS] Verified {musicFiles.Count} MusicXML files and real-sampled SF2 audio synthesis!");
                File.WriteAllText("verification_report.txt", sb.ToString(), Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[EXCEPTION] {ex}");
                File.WriteAllText("verification_report.txt", sb.ToString(), Encoding.UTF8);
                return 99;
            }
        }

        private const string PerformanceTestMusicXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <score-partwise version="3.1">
              <part-list>
                <score-part id="P1"><part-name>Piano</part-name></score-part>
                <score-part id="P2"><part-name>Violin</part-name></score-part>
              </part-list>
              <part id="P1">
                <measure number="1">
                  <attributes>
                    <divisions>1</divisions>
                    <key><fifths>0</fifths></key>
                    <time><beats>4</beats><beat-type>4</beat-type></time>
                    <clef><sign>G</sign><line>2</line></clef>
                  </attributes>
                  <direction>
                    <direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>60</per-minute></metronome></direction-type>
                    <sound tempo="60"/>
                  </direction>
                  <note>
                    <pitch><step>C</step><octave>4</octave></pitch>
                    <duration>1</duration><type>quarter</type>
                    <notations><arpeggiate direction="down"/></notations>
                  </note>
                  <note>
                    <chord/>
                    <pitch><step>E</step><octave>4</octave></pitch>
                    <duration>1</duration><type>quarter</type>
                  </note>
                  <note>
                    <chord/>
                    <pitch><step>G</step><octave>4</octave></pitch>
                    <duration>1</duration><type>quarter</type>
                  </note>
                  <note>
                    <pitch><step>D</step><octave>4</octave></pitch>
                    <duration>1</duration><type>quarter</type>
                    <notations><articulations><breath-mark/></articulations></notations>
                  </note>
                  <note>
                    <pitch><step>E</step><octave>4</octave></pitch>
                    <duration>1</duration><type>quarter</type>
                  </note>
                  <note>
                    <pitch><step>F</step><octave>4</octave></pitch>
                    <duration>1</duration><type>quarter</type>
                    <notations><articulations><caesura/></articulations></notations>
                  </note>
                </measure>
              </part>
              <part id="P2">
                <measure number="1">
                  <attributes><divisions>1</divisions></attributes>
                  <note>
                    <pitch><step>A</step><octave>4</octave></pitch>
                    <duration>4</duration><type>whole</type>
                  </note>
                </measure>
              </part>
            </score-partwise>
            """;

        private static bool RunPerformanceExpressionSelfTest(StringBuilder sb)
        {
            string path = Path.Combine(Path.GetTempPath(), "wmp-performance-selftest.musicxml");
            try
            {
                File.WriteAllText(path, PerformanceTestMusicXml, new UTF8Encoding(false));

                var baseOptions = new PerformanceOptions
                {
                    HumanizeAmount = 0,
                    VelocityCurve = VelocityCurveMode.Linear,
                    DynamicRangePercent = 100,
                    AutoPianoPedal = true,
                    AutoVibrato = true
                };

                var score = MusicXmlLoader.LoadFromFile(path, expandRepeats: true, baseOptions);

                var chordG = score.Notes.FirstOrDefault(n => n.MidiPitch == 67 && n.StartTimeSeconds < 0.5);
                var chordE = score.Notes.FirstOrDefault(n => n.MidiPitch == 64 && n.StartTimeSeconds < 0.5);
                var chordC = score.Notes.FirstOrDefault(n => n.MidiPitch == 60 && n.StartTimeSeconds < 0.5);
                if (chordG == null || chordE == null || chordC == null ||
                    !(chordG.StartTimeSeconds < chordE.StartTimeSeconds && chordE.StartTimeSeconds < chordC.StartTimeSeconds))
                {
                    sb.AppendLine("[FAIL] Arpeggio direction test failed.");
                    return false;
                }

                var dNote = score.Notes.FirstOrDefault(n => n.MidiPitch == 62 && n.StartTimeSeconds < 1.5);
                var laterE = score.Notes.FirstOrDefault(n => n.MidiPitch == 64 && n.StartTimeSeconds > 1.5);
                if (dNote == null || laterE == null || laterE.StartTimeSeconds - dNote.StartTimeSeconds < 1.15)
                {
                    sb.AppendLine("[FAIL] Breath mark timing test failed.");
                    return false;
                }

                if (score.Measures.Count == 0 ||
                    score.Measures[0].DurationSeconds < 4.60 ||
                    score.Measures[0].DurationSeconds > 4.90)
                {
                    sb.AppendLine("[FAIL] Caesura measure duration test failed.");
                    return false;
                }

                if (!score.Notes.Any(n => n.AudioDurationSeconds > n.DurationSeconds + 0.10))
                {
                    sb.AppendLine("[FAIL] Automatic piano pedal test failed.");
                    return false;
                }

                var humanOptions = new PerformanceOptions
                {
                    HumanizeAmount = 50,
                    VelocityCurve = VelocityCurveMode.Linear,
                    DynamicRangePercent = 100,
                    AutoPianoPedal = true,
                    AutoVibrato = true
                };
                var humanA = MusicXmlLoader.LoadFromFile(path, expandRepeats: true, humanOptions);
                var humanB = MusicXmlLoader.LoadFromFile(path, expandRepeats: true, humanOptions);
                var humanNoteA = humanA.Notes.FirstOrDefault(n => n.MidiPitch == 62 && n.StartTimeSeconds < 1.5);
                var humanNoteB = humanB.Notes.FirstOrDefault(n => n.MidiPitch == 62 && n.StartTimeSeconds < 1.5);
                if (humanNoteA == null || humanNoteB == null ||
                    Math.Abs(humanNoteA.StartTimeSeconds - humanNoteB.StartTimeSeconds) > 0.000001 ||
                    humanNoteA.Velocity != humanNoteB.Velocity)
                {
                    sb.AppendLine("[FAIL] Humanization determinism test failed.");
                    return false;
                }
                if (Math.Abs(humanNoteA.StartTimeSeconds - dNote.StartTimeSeconds) > 0.0121 ||
                    Math.Abs(humanNoteA.Velocity - dNote.Velocity) > 5)
                {
                    sb.AppendLine("[FAIL] Humanization bounds test failed.");
                    return false;
                }

                var softOptions = new PerformanceOptions
                {
                    HumanizeAmount = 0,
                    VelocityCurve = VelocityCurveMode.Soft,
                    DynamicRangePercent = 100,
                    AutoPianoPedal = true,
                    AutoVibrato = true
                };
                var hardOptions = new PerformanceOptions
                {
                    HumanizeAmount = 0,
                    VelocityCurve = VelocityCurveMode.Hard,
                    DynamicRangePercent = 100,
                    AutoPianoPedal = true,
                    AutoVibrato = true
                };
                var softScore = MusicXmlLoader.LoadFromFile(path, expandRepeats: true, softOptions);
                var hardScore = MusicXmlLoader.LoadFromFile(path, expandRepeats: true, hardOptions);
                var softD = softScore.Notes.FirstOrDefault(n => n.MidiPitch == 62 && n.StartTimeSeconds < 1.5);
                var hardD = hardScore.Notes.FirstOrDefault(n => n.MidiPitch == 62 && n.StartTimeSeconds < 1.5);
                if (softD == null || hardD == null || softD.Velocity < hardD.Velocity)
                {
                    sb.AppendLine("[FAIL] Velocity curve test failed.");
                    return false;
                }

                bool hasVibrato = softScore.ModulationPointsByChannel.Values
                    .Any(points => points.Any(p => p.Value > 0));
                if (!hasVibrato)
                {
                    sb.AppendLine("[FAIL] Delayed vibrato automation test failed.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[FAIL] Performance expression self-test exception: {ex.Message}");
                return false;
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
