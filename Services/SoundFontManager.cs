using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinMusicXmlPlayer.Models;

namespace WinMusicXmlPlayer.Services
{
    public static class SoundFontManager
    {
        public static readonly List<GmInstrumentItem> GmInstruments = BuildGmInstruments();

        private static List<GmInstrumentItem> BuildGmInstruments()
        {
            string[] names = new string[128]
            {
                // 0-7 Piano
                "平台鋼琴 (Acoustic Grand Piano)", "亮音鋼琴 (Bright Acoustic Piano)", "電平台鋼琴 (Electric Grand Piano)", "酒吧鋼琴 (Honky-tonk Piano)",
                "電鋼琴 1 (Electric Piano 1)", "電鋼琴 2 (Electric Piano 2)", "大鍵琴 (Harpsichord)", "克拉維科德琴 (Clavinet)",
                // 8-15 Chromatic Percussion
                "鋼片琴 (Celesta)", "鐘琴 (Glockenspiel)", "音樂盒 (Music Box)", "顫音琴 (Vibraphone)",
                "馬林巴琴 (Marimba)", "木琴 (Xylophone)", "管鐘 (Tubular Bells)", "揚琴 (Dulcimer)",
                // 16-23 Organ
                "音栓風琴 (Drawbar Organ)", "敲擊風琴 (Percussive Organ)", "搖滾風琴 (Rock Organ)", "教堂管風琴 (Church Organ)",
                "簧風琴 (Reed Organ)", "手風琴 (Accordion)", "口琴 (Harmonica)", "探戈手風琴 (Tango Accordion)",
                // 24-31 Guitar
                "尼龍弦吉他 (Acoustic Guitar Nylon)", "鋼弦吉他 (Acoustic Guitar Steel)", "爵士電吉他 (Electric Guitar Jazz)", "清音電吉他 (Electric Guitar Clean)",
                "悶音電吉他 (Electric Guitar Muted)", "破音電吉他 (Overdriven Guitar)", "失真電吉他 (Distortion Guitar)", "吉他泛音 (Guitar Harmonics)",
                // 32-39 Bass
                "原聲貝斯 (Acoustic Bass)", "指彈電貝斯 (Electric Bass Finger)", "撥片電貝斯 (Electric Bass Pick)", "無品貝斯 (Fretless Bass)",
                "擊弦貝斯 1 (Slap Bass 1)", "擊弦貝斯 2 (Slap Bass 2)", "合成貝斯 1 (Synth Bass 1)", "合成貝斯 2 (Synth Bass 2)",
                // 40-47 Strings
                "小提琴 (Violin)", "中提琴 (Viola)", "大提琴 (Cello)", "低音提琴 (Contrabass)",
                "顫音弦樂 (Tremolo Strings)", "撥奏弦樂 (Pizzicato Strings)", "豎琴 (Orchestral Harp)", "定音鼓 (Timpani)",
                // 48-55 Ensemble
                "弦樂合奏 1 (String Ensemble 1)", "弦樂合奏 2 (String Ensemble 2)", "合成弦樂 1 (SynthStrings 1)", "合成弦樂 2 (SynthStrings 2)",
                "人聲合唱 Aahs (Choir Aahs)", "人聲 Oohs (Voice Oohs)", "合成人聲 (Synth Voice)", "管弦樂強音 (Orchestra Hit)",
                // 56-63 Brass
                "小號 (Trumpet)", "長號 (Trombone)", "低音號 (Tuba)", "弱音小號 (Muted Trumpet)",
                "法國號 (French Horn)", "銅管合奏 (Brass Section)", "合成銅管 1 (SynthBrass 1)", "合成銅管 2 (SynthBrass 2)",
                // 64-71 Reed
                "高音薩克斯風 (Soprano Sax)", "中音薩克斯風 (Alto Sax)", "次中音薩克斯風 (Tenor Sax)", "上低音薩克斯風 (Baritone Sax)",
                "雙簧管 (Oboe)", "英國管 (English Horn)", "低音管/巴松管 (Bassoon)", "單簧管/豎笛 (Clarinet)",
                // 72-79 Pipe
                "短笛 (Piccolo)", "長笛 (Flute)", "直笛 (Recorder)", "排笛 (Pan Flute)",
                "吹瓶音 (Blown Bottle)", "尺八 (Shakuhachi)", "哨笛 (Whistle)", "陶笛 (Ocarina)",
                // 80-87 Synth Lead
                "方波主奏 (Lead 1 Square)", "鋸齒波主奏 (Lead 2 Sawtooth)", "汽笛風琴主奏 (Lead 3 Calliope)", "合成吹管 (Lead 4 Chiff)",
                "電吉他主奏 (Lead 5 Charang)", "人聲主奏 (Lead 6 Voice)", "五度音主奏 (Lead 7 Fifths)", "貝斯與主奏 (Lead 8 Bass+Lead)",
                // 88-95 Synth Pad
                "新世紀襯底 (Pad 1 New Age)", "溫暖襯底 (Pad 2 Warm)", "多聲部合成 (Pad 3 Polysynth)", "合唱襯底 (Pad 4 Choir)",
                "玻璃襯底 (Pad 5 Bowed)", "金屬襯底 (Pad 6 Metallic)", "光環襯底 (Pad 7 Halo)", "掃頻襯底 (Pad 8 Sweep)",
                // 96-103 Synth Effects
                "雨聲 (FX 1 Rain)", "電影音效 (FX 2 Soundtrack)", "水晶 (FX 3 Crystal)", "大氣 (FX 4 Atmosphere)",
                "明亮 (FX 5 Brightness)", "小精靈 (FX 6 Goblins)", "回音 (FX 7 Echoes)", "科幻 (FX 8 Sci-fi)",
                // 104-111 Ethnic
                "西塔琴 (Sitar)", "班卓琴 (Banjo)", "三味線 (Shamisen)", "古箏/日本箏 (Koto)",
                "拇指琴 (Kalimba)", "風笛 (Bagpipe)", "民間小提琴 (Fiddle)", "嗩吶/嗩吶笛 (Shanai)",
                // 112-119 Percussive
                "叮噹鈴 (Tinkle Bell)", "阿戈戈鈴 (Agogo)", "鋼鼓 (Steel Drums)", "木魚 (Woodblock)",
                "太鼓 (Taiko Drum)", "通通鼓 (Melodic Tom)", "合成鼓 (Synth Drum)", "反轉鈸 (Reverse Cymbal)",
                // 120-127 Sound Effects
                "吉他滑弦聲 (Guitar Fret Noise)", "呼吸聲 (Breath Noise)", "海浪聲 (Seashore)", "鳥鳴聲 (Bird Tweet)",
                "電話鈴聲 (Telephone Ring)", "直升機 (Helicopter)", "掌聲 (Applause)", "槍聲 (Gunshot)"
            };

            var list = new List<GmInstrumentItem>(128);
            for (int i = 0; i < 128; i++)
            {
                list.Add(new GmInstrumentItem
                {
                    Program = i,
                    DisplayName = names[i]
                });
            }
            return list;
        }

        public static string GetInstrumentDisplayName(int gmProgram)
        {
            int idx = Math.Clamp(gmProgram, 0, 127);
            return GmInstruments[idx].DisplayName;
        }

        /// <summary>
        /// 依據聲部名稱、縮寫與樂器名稱自動推斷最適合的 GM 樂器編號 (0..127)、繁體中文顯示名稱與立體聲 Pan (0..127)
        /// </summary>
        public static (int gmProgram, string zhPartName, int pan) InferInstrumentAndPan(
            string partName, string abbr, string instName, int? xmlMidiProgramOneBased, int partOrder, int totalParts)
        {
            string combined = $"{partName} {abbr} {instName}".ToLowerInvariant();

            // 1. 小提琴家族 (Violino principale / Violino I / Violino II / Violin)
            if (combined.Contains("violino principale") || combined.Contains("violin solo") || combined.Contains("solo violin"))
            {
                return (40, "主奏小提琴 (Violino principale)", 50);
            }
            if (combined.Contains("violino i") && !combined.Contains("violino ii"))
            {
                return (40, "第一小提琴 (Violino I)", 36);
            }
            if (combined.Contains("violin i") && !combined.Contains("violin ii"))
            {
                return (40, "第一小提琴 (Violin I)", 36);
            }
            if (combined.Contains("violino ii") || combined.Contains("violin ii") || combined.Contains("violin 2"))
            {
                return (40, "第二小提琴 (Violino II)", 76);
            }
            if (combined.Contains("violin") || combined.Contains("violino") || combined.Contains("vln") || combined.Contains("小提琴"))
            {
                return (40, "小提琴 (Violin)", 44);
            }

            // 2. 中提琴 (Viola)
            if (combined.Contains("viola") || combined.Contains("vla") || combined.Contains("bratsche") || combined.Contains("中提琴"))
            {
                return (41, "中提琴 (Viola)", 82);
            }

            // 3. 大提琴 (Cello / Violoncello)
            if (combined.Contains("violoncello") || combined.Contains("cello") || combined.Contains("vc.") || combined.Contains("vlc") || combined.Contains("大提琴"))
            {
                return (42, "大提琴 (Cello)", 92);
            }

            // 4. 低音提琴 (Contrabass / Double Bass)
            if (combined.Contains("contrabass") || combined.Contains("double bass") || combined.Contains("contrabbasso") || combined.Contains("basso") || combined.Contains("低音提琴"))
            {
                return (43, "低音提琴 (Contrabass)", 98);
            }

            // 5. 管樂與其他常見協奏樂器
            if (combined.Contains("flute") || combined.Contains("flauto") || combined.Contains("長笛"))
                return (73, "長笛 (Flute)", 48);
            if (combined.Contains("oboe") || combined.Contains("雙簧管"))
                return (68, "雙簧管 (Oboe)", 72);
            if (combined.Contains("clarinet") || combined.Contains("clarinetto") || combined.Contains("單簧管"))
                return (71, "單簧管 (Clarinet)", 54);
            if (combined.Contains("bassoon") || combined.Contains("fagotto") || combined.Contains("低音管"))
                return (70, "低音管 (Bassoon)", 80);
            if (combined.Contains("horn") || combined.Contains("corno") || combined.Contains("法國號"))
                return (60, "法國號 (Horn)", 42);
            if (combined.Contains("trumpet") || combined.Contains("tromba") || combined.Contains("小號"))
                return (56, "小號 (Trumpet)", 85);
            if (combined.Contains("harpsichord") || combined.Contains("cembalo") || combined.Contains("大鍵琴"))
                return (6, "大鍵琴 (Harpsichord)", 64);
            if (combined.Contains("harp") || combined.Contains("arpa") || combined.Contains("豎琴"))
                return (46, "豎琴 (Harp)", 50);
            if (combined.Contains("guitar") || combined.Contains("吉他"))
                return (24, "古典吉他 (Guitar)", 64);

            // 6. 鋼琴 (Piano / Pianoforte / Klavier)
            if (combined.Contains("piano") || combined.Contains("pno") || combined.Contains("klavier") || combined.Contains("鋼琴"))
            {
                return (0, "平台鋼琴 (Piano)", 64);
            }

            // 7. 若 XML 有指定 midi-program
            if (xmlMidiProgramOneBased.HasValue && xmlMidiProgramOneBased.Value >= 1 && xmlMidiProgramOneBased.Value <= 128)
            {
                int prog = xmlMidiProgramOneBased.Value - 1;
                string display = string.IsNullOrWhiteSpace(partName) ? GetInstrumentDisplayName(prog) : partName;
                return (prog, display, 64);
            }

            // 預設平台鋼琴
            string defaultName = string.IsNullOrWhiteSpace(partName) ? $"聲部 {partOrder + 1}" : partName;
            return (0, defaultName, 64);
        }

        /// <summary>
        /// 尋找預設 .sf2 音源路徑
        /// </summary>
        public static string? FindDefaultSoundFontPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string workDir = Directory.GetCurrentDirectory();

            string[] searchDirs = new[]
            {
                Path.Combine(baseDir, "Assets", "SoundFonts"),
                Path.Combine(workDir, "Assets", "SoundFonts"),
                baseDir,
                workDir
            };

            foreach (string dir in searchDirs.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                var sf2Files = Directory.GetFiles(dir, "*.sf2")
                    .OrderByDescending(f => new FileInfo(f).Length) // 優先選用取樣最完整的大容量音源
                    .ToList();
                if (sf2Files.Count > 0)
                    return sf2Files[0];
            }

            return null;
        }
    }
}
