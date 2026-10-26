using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
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
                var win = new MainWindow();
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

                var musicFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.musicxml")
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
                    engine.Seek(Math.Min(5.0, score.TotalDurationSeconds * 0.25));

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

                // 測試 UI 選單展開、模式單一按鈕切換、重置按鈕隱藏/顯示與 Toast 訊息
                var testWin = new MainWindow();
                testWin.Show();
                testWin.RunUiSelfTest(musicFiles[0]);
                testWin.Close();
                sb.AppendLine("[PASS] UI Self-Test (Menu Popup, ViewMode Toggle, Reset Button Visibility & Toast) passed!");

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
    }
}
