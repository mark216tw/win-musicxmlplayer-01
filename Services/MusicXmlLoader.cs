using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WinMusicXmlPlayer.Models;

namespace WinMusicXmlPlayer.Services
{
    public static class MusicXmlLoader
    {
        private static readonly string[] TrackPalette = new[]
        {
            "#F43F5E", // 鮮紅玫瑰 (Crimson Rose)
            "#38BDF8", // 天空亮藍 (Sky Blue)
            "#22C55E", // 翡翠亮綠 (Emerald Green)
            "#FBBF24", // 琥珀亮金 (Amber Gold)
            "#C084FC", // 亮紫羅蘭 (Bright Purple)
            "#FB923C", // 鮮橘色 (Vivid Orange)
            "#2DD4BF", // 青碧色 (Teal)
            "#E879F9"  // 洋紅粉 (Fuchsia)
        };

        private static string AssignDistinctTrackColor(string zhName, int gmProgram, int staffNumber, int? voiceFilter, int trackIdx, HashSet<string> usedColors)
        {
            string preferred;
            if (gmProgram == 40) // 小提琴家族
            {
                if (zhName.Contains("第一")) preferred = "#FBBF24";      // 第一小提琴：琥珀金
                else if (zhName.Contains("第二")) preferred = "#38BDF8"; // 第二小提琴：天空藍
                else preferred = "#F43F5E";                              // 小提琴 / 主奏小提琴：鮮紅玫瑰（與鋼琴天藍強烈對比）
            }
            else if (gmProgram == 41) // 中提琴
            {
                preferred = "#C084FC"; // 亮紫羅蘭
            }
            else if (gmProgram == 42) // 大提琴
            {
                preferred = "#22C55E"; // 翡翠亮綠
            }
            else if (gmProgram == 0) // 平台鋼琴
            {
                if (staffNumber == 1 && voiceFilter == 2)
                    preferred = "#FBBF24"; // 鋼琴右手伴奏：琥珀金
                else if (staffNumber == 1)
                    preferred = "#38BDF8"; // 鋼琴右手主旋律/高音譜表：天空亮藍
                else
                    preferred = "#C084FC"; // 鋼琴左手低音譜表：亮紫羅蘭
            }
            else
            {
                preferred = TrackPalette[trackIdx % TrackPalette.Length];
            }

            if (!usedColors.Contains(preferred))
            {
                usedColors.Add(preferred);
                return preferred;
            }

            foreach (string c in TrackPalette)
            {
                if (!usedColors.Contains(c))
                {
                    usedColors.Add(c);
                    return c;
                }
            }

            return TrackPalette[trackIdx % TrackPalette.Length];
        }

        private class RawNoteItem
        {
            public string PartId { get; set; } = string.Empty;
            public int MeasureIndex { get; set; }
            public int OriginalMeasureNumber { get; set; }
            public int Staff { get; set; } = 1;
            public int Voice { get; set; } = 1;
            public int MidiPitch { get; set; }
            public double BeatStart { get; set; }
            public double BeatDuration { get; set; }
            public bool IsGrace { get; set; }
            public bool IsChord { get; set; }
            public bool TieStart { get; set; }
            public bool TieStop { get; set; }
            public bool HasStaccato { get; set; }
            public bool HasStaccatissimo { get; set; }
            public bool HasDetachedLegato { get; set; }
            public bool HasTenuto { get; set; }
            public bool HasAccent { get; set; }
            public bool HasArpeggiate { get; set; }
            public bool HasTrill { get; set; }
            public bool HasMordent { get; set; }
            public bool HasTurn { get; set; }
            public bool HasFermata { get; set; }
            public bool InSlur { get; set; }
            public string? OrnamentAccidental { get; set; }
            public int DynamicVelocity { get; set; } = 78;
            public bool IsSfp { get; set; }
        }

        private class MeasureMeta
        {
            public int MeasureIndex { get; set; }
            public string MeasureNumberText { get; set; } = "1";
            public int NumericMeasureNumber { get; set; } = 1;
            public double BeatDuration { get; set; } = 4.0;
            public int Beats { get; set; } = 4;
            public int BeatType { get; set; } = 4;
            public int Fifths { get; set; } = 0;
            public double? ExplicitTempoBpm { get; set; }
            public bool RepeatForward { get; set; }
            public bool RepeatBackward { get; set; }
            public int? EndingNumber { get; set; }
            public bool HasFermata { get; set; }
            public double FermataBeatPos { get; set; } = -1;
            public string SectionLabel { get; set; } = string.Empty;
        }

        public static MusicScore LoadFromFile(string filePath, bool expandRepeats = true)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"找不到指定的 MusicXML 檔案：{filePath}");

            var (xmlContent, jsonTitle, jsonSubtitle, jsonComposer, jsonTempoBpm) = ReadRawMusicXmlContent(filePath);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };

            XDocument doc;
            using (var sr = new StringReader(xmlContent))
            using (var xr = XmlReader.Create(sr, settings))
            {
                doc = XDocument.Load(xr);
            }

            XElement root = doc.Root ?? throw new InvalidDataException("無效的 MusicXML 檔案：缺少根節點。");

            var score = new MusicScore
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Title = !string.IsNullOrWhiteSpace(jsonTitle)
                    ? jsonTitle
                    : (FirstNonEmpty(
                        root.Element("work")?.Element("work-title")?.Value,
                        root.Element("movement-title")?.Value,
                        Path.GetFileNameWithoutExtension(filePath))),
                Subtitle = jsonSubtitle ?? string.Empty,
                Composer = !string.IsNullOrWhiteSpace(jsonComposer)
                    ? jsonComposer
                    : (root.Descendants("creator")
                        .FirstOrDefault(e => string.Equals((string?)e.Attribute("type"), "composer", StringComparison.OrdinalIgnoreCase))?.Value
                       ?? string.Empty)
            };

            // 1. 解析 score-part 清單
            var scoreParts = new List<(string id, string name, string abbr, string instName, int? midiProg)>();
            var partListEl = root.Element("part-list");
            if (partListEl != null)
            {
                foreach (var sp in partListEl.Elements("score-part"))
                {
                    string id = (string?)sp.Attribute("id") ?? $"P{scoreParts.Count + 1}";
                    string pname = sp.Element("part-name")?.Value?.Trim() ?? id;
                    string pabbr = sp.Element("part-abbreviation")?.Value?.Trim() ?? string.Empty;
                    string instName = sp.Descendants("instrument-name").FirstOrDefault()?.Value?.Trim() ?? string.Empty;
                    int? midiProg = null;
                    if (int.TryParse(sp.Descendants("midi-program").FirstOrDefault()?.Value?.Trim(), out int mp))
                        midiProg = mp;
                    scoreParts.Add((id, pname, pabbr, instName, midiProg));
                }
            }

            var partElements = root.Elements("part").ToList();
            if (scoreParts.Count == 0)
            {
                for (int i = 0; i < partElements.Count; i++)
                {
                    string id = (string?)partElements[i].Attribute("id") ?? $"P{i + 1}";
                    scoreParts.Add((id, $"聲部 {i + 1}", "", "", null));
                }
            }

            int measureCount = partElements.Max(p => p.Elements("measure").Count());
            var measureMetas = new List<MeasureMeta>(measureCount);
            for (int i = 0; i < measureCount; i++)
            {
                measureMetas.Add(new MeasureMeta { MeasureIndex = i, MeasureNumberText = (i + 1).ToString(), NumericMeasureNumber = i + 1 });
            }

            // 預設速度推斷
            double defaultTempo = jsonTempoBpm ?? 120.0;
            bool foundAnyTempo = jsonTempoBpm.HasValue;

            // 2. 掃描每個 part 的小節與原始音符
            var rawNotesByPartAndMeasure = new Dictionary<(string partId, int mIdx), List<RawNoteItem>>();
            var partStavesFound = new Dictionary<string, HashSet<int>>();
            var partStaffVoiceNoteCounts = new Dictionary<(string partId, int staff, int voice), int>();

            int curBeats = 4;
            int curBeatType = 4;
            int curFifths = 0;

            for (int pIdx = 0; pIdx < partElements.Count; pIdx++)
            {
                var partEl = partElements[pIdx];
                string partId = (string?)partEl.Attribute("id") ?? scoreParts[Math.Min(pIdx, scoreParts.Count - 1)].id;
                partStavesFound[partId] = new HashSet<int> { 1 };

                var measures = partEl.Elements("measure").ToList();
                int divisions = 1;
                int currentDynamicVel = 78;
                bool activeSlur = false;
                int activeWedgeType = 0; // 1 = cresc, -1 = dim
                double wedgeStartBeat = 0;

                for (int mIdx = 0; mIdx < measures.Count; mIdx++)
                {
                    var mEl = measures[mIdx];
                    var meta = measureMetas[mIdx];

                    string? mNumAttr = (string?)mEl.Attribute("number");
                    if (!string.IsNullOrWhiteSpace(mNumAttr))
                    {
                        meta.MeasureNumberText = mNumAttr!;
                        if (int.TryParse(new string(mNumAttr!.TakeWhile(char.IsDigit).ToArray()), out int parsedNum))
                            meta.NumericMeasureNumber = parsedNum;
                    }

                    // Barline repeats & endings
                    foreach (var barline in mEl.Elements("barline"))
                    {
                        var repeatEl = barline.Element("repeat");
                        if (repeatEl != null)
                        {
                            string? dir = (string?)repeatEl.Attribute("direction");
                            if (string.Equals(dir, "forward", StringComparison.OrdinalIgnoreCase))
                                meta.RepeatForward = true;
                            else if (string.Equals(dir, "backward", StringComparison.OrdinalIgnoreCase))
                                meta.RepeatBackward = true;
                        }

                        var endingEl = barline.Element("ending");
                        if (endingEl != null)
                        {
                            string? etype = (string?)endingEl.Attribute("type");
                            string? enumStr = (string?)endingEl.Attribute("number");
                            if (string.Equals(etype, "start", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(enumStr?.Split(',')[0].Trim(), out int endNum))
                            {
                                meta.EndingNumber = endNum;
                            }
                        }
                    }

                    double cursorDiv = 0;
                    double maxCursorDiv = 0;
                    double lastChordStartDiv = 0;

                    var noteList = new List<RawNoteItem>();
                    rawNotesByPartAndMeasure[(partId, mIdx)] = noteList;

                    foreach (var child in mEl.Elements())
                    {
                        string tag = child.Name.LocalName;

                        if (tag == "attributes")
                        {
                            var divEl = child.Element("divisions");
                            if (divEl != null && int.TryParse(divEl.Value.Trim(), out int dVal) && dVal > 0)
                                divisions = dVal;

                            var keyEl = child.Element("key")?.Element("fifths");
                            if (keyEl != null && int.TryParse(keyEl.Value.Trim(), out int fVal))
                            {
                                curFifths = fVal;
                            }

                            var timeEl = child.Element("time");
                            if (timeEl != null)
                            {
                                if (int.TryParse(timeEl.Element("beats")?.Value.Trim(), out int bVal) && bVal > 0)
                                    curBeats = bVal;
                                if (int.TryParse(timeEl.Element("beat-type")?.Value.Trim(), out int btVal) && btVal > 0)
                                    curBeatType = btVal;
                            }

                            var stavesEl = child.Element("staves");
                            if (stavesEl != null && int.TryParse(stavesEl.Value.Trim(), out int stCount))
                            {
                                for (int s = 1; s <= stCount; s++)
                                    partStavesFound[partId].Add(s);
                            }
                        }
                        else if (tag == "backup")
                        {
                            if (double.TryParse(child.Element("duration")?.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double bak))
                            {
                                cursorDiv = Math.Max(0, cursorDiv - bak);
                            }
                        }
                        else if (tag == "forward")
                        {
                            if (double.TryParse(child.Element("duration")?.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double fwd))
                            {
                                cursorDiv += fwd;
                                if (cursorDiv > maxCursorDiv) maxCursorDiv = cursorDiv;
                            }
                        }
                        else if (tag == "sound")
                        {
                            ParseSoundElement(child, meta, ref currentDynamicVel, ref foundAnyTempo);
                        }
                        else if (tag == "direction")
                        {
                            foreach (var snd in child.Elements("sound"))
                            {
                                ParseSoundElement(snd, meta, ref currentDynamicVel, ref foundAnyTempo);
                            }

                            foreach (var dt in child.Elements("direction-type"))
                            {
                                var metro = dt.Element("metronome");
                                if (metro != null)
                                {
                                    string? unit = metro.Element("beat-unit")?.Value.Trim();
                                    if (double.TryParse(metro.Element("per-minute")?.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double bpm) && bpm > 10)
                                    {
                                        double qBpm = ConvertBeatUnitToQuarterBpm(unit, bpm);
                                        meta.ExplicitTempoBpm = qBpm;
                                        foundAnyTempo = true;
                                    }
                                }

                                var dynEl = dt.Element("dynamics");
                                if (dynEl != null)
                                {
                                    foreach (var dChild in dynEl.Elements())
                                    {
                                        string dName = dChild.Name.LocalName.ToLowerInvariant();
                                        currentDynamicVel = MapDynamicNameToVelocity(dName, currentDynamicVel);
                                        if (dName == "sfp" || dName == "fz" || dName == "sf" || dName == "sfz")
                                        {
                                            // 標記突強
                                        }
                                    }
                                }

                                var wedgeEl = dt.Element("wedge");
                                if (wedgeEl != null)
                                {
                                    string? wType = (string?)wedgeEl.Attribute("type");
                                    if (wType == "crescendo")
                                    {
                                        activeWedgeType = 1;
                                        wedgeStartBeat = cursorDiv / divisions;
                                    }
                                    else if (wType == "diminuendo")
                                    {
                                        activeWedgeType = -1;
                                        wedgeStartBeat = cursorDiv / divisions;
                                    }
                                    else if (wType == "stop")
                                    {
                                        activeWedgeType = 0;
                                    }
                                }

                                foreach (var wordsEl in dt.Elements("words"))
                                {
                                    string wText = wordsEl.Value?.Trim() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(wText))
                                    {
                                        if (string.IsNullOrEmpty(meta.SectionLabel) && wText.Length <= 28)
                                            meta.SectionLabel = wText;

                                        if (!foundAnyTempo)
                                        {
                                            double? inferred = InferTempoFromWords(wText);
                                            if (inferred.HasValue)
                                            {
                                                defaultTempo = inferred.Value;
                                                foundAnyTempo = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (tag == "note")
                        {
                            bool isRest = child.Element("rest") != null;
                            bool isGrace = child.Element("grace") != null;
                            bool isChord = child.Element("chord") != null;

                            double durDiv = 0;
                            if (!isGrace)
                            {
                                double.TryParse(child.Element("duration")?.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out durDiv);
                            }

                            int staff = 1;
                            if (int.TryParse(child.Element("staff")?.Value.Trim(), out int stVal) && stVal >= 1)
                                staff = stVal;
                            partStavesFound[partId].Add(staff);

                            int voice = 1;
                            if (int.TryParse(child.Element("voice")?.Value.Trim(), out int vVal) && vVal >= 1)
                                voice = vVal;

                            double noteStartDiv = isChord ? lastChordStartDiv : cursorDiv;
                            if (!isGrace && !isChord)
                            {
                                lastChordStartDiv = cursorDiv;
                                cursorDiv += durDiv;
                                if (cursorDiv > maxCursorDiv) maxCursorDiv = cursorDiv;
                            }

                            if (isRest)
                                continue;

                            var pitchEl = child.Element("pitch");
                            if (pitchEl == null)
                                continue;

                            int midiPitch = ParseMidiPitch(pitchEl);

                            bool tieStart = false;
                            bool tieStop = false;
                            foreach (var tieEl in child.Elements("tie"))
                            {
                                string? tType = (string?)tieEl.Attribute("type");
                                if (tType == "start") tieStart = true;
                                else if (tType == "stop") tieStop = true;
                            }

                            var rawNote = new RawNoteItem
                            {
                                PartId = partId,
                                MeasureIndex = mIdx,
                                OriginalMeasureNumber = meta.NumericMeasureNumber,
                                Staff = staff,
                                Voice = voice,
                                MidiPitch = midiPitch,
                                BeatStart = noteStartDiv / divisions,
                                BeatDuration = durDiv / divisions,
                                IsGrace = isGrace,
                                IsChord = isChord,
                                TieStart = tieStart,
                                TieStop = tieStop,
                                DynamicVelocity = currentDynamicVel
                            };

                            if (activeWedgeType != 0)
                            {
                                double deltaBeats = Math.Max(0, rawNote.BeatStart - wedgeStartBeat);
                                int deltaVel = (int)Math.Min(22, deltaBeats * 4.5) * activeWedgeType;
                                rawNote.DynamicVelocity = Math.Clamp(currentDynamicVel + deltaVel, 28, 122);
                            }

                            foreach (var notEl in child.Elements("notations"))
                            {
                                foreach (var tiedEl in notEl.Elements("tied"))
                                {
                                    string? tType = (string?)tiedEl.Attribute("type");
                                    if (tType == "start") rawNote.TieStart = true;
                                    else if (tType == "stop") rawNote.TieStop = true;
                                }

                                if (notEl.Element("arpeggiate") != null)
                                    rawNote.HasArpeggiate = true;

                                if (notEl.Element("fermata") != null)
                                {
                                    rawNote.HasFermata = true;
                                    meta.HasFermata = true;
                                    meta.FermataBeatPos = rawNote.BeatStart;
                                }

                                foreach (var slurEl in notEl.Elements("slur"))
                                {
                                    string? sType = (string?)slurEl.Attribute("type");
                                    if (sType == "start") activeSlur = true;
                                    else if (sType == "stop") activeSlur = false;
                                }

                                var artEl = notEl.Element("articulations");
                                if (artEl != null)
                                {
                                    if (artEl.Element("staccato") != null) rawNote.HasStaccato = true;
                                    if (artEl.Element("staccatissimo") != null) rawNote.HasStaccatissimo = true;
                                    if (artEl.Element("detached-legato") != null) rawNote.HasDetachedLegato = true;
                                    if (artEl.Element("tenuto") != null) rawNote.HasTenuto = true;
                                    if (artEl.Element("accent") != null || artEl.Element("strong-accent") != null) rawNote.HasAccent = true;
                                }

                                var ornEl = notEl.Element("ornaments");
                                if (ornEl != null)
                                {
                                    if (ornEl.Element("trill-mark") != null || ornEl.Element("wavy-line") != null)
                                        rawNote.HasTrill = true;
                                    if (ornEl.Element("mordent") != null || ornEl.Element("inverted-mordent") != null)
                                        rawNote.HasMordent = true;
                                    if (ornEl.Element("turn") != null || ornEl.Element("inverted-turn") != null)
                                        rawNote.HasTurn = true;

                                    var accMark = ornEl.Element("accidental-mark");
                                    if (accMark != null)
                                        rawNote.OrnamentAccidental = accMark.Value?.Trim();
                                }
                            }

                            rawNote.InSlur = activeSlur;
                            noteList.Add(rawNote);

                            if (!rawNote.IsGrace)
                            {
                                var svKey = (partId, staff, voice);
                                partStaffVoiceNoteCounts[svKey] = partStaffVoiceNoteCounts.GetValueOrDefault(svKey, 0) + 1;
                            }
                        }
                    }

                    meta.Beats = curBeats;
                    meta.BeatType = curBeatType;
                    meta.Fifths = curFifths;

                    double actualQuarterBeats = maxCursorDiv / divisions;
                    if (actualQuarterBeats > 0.05)
                    {
                        meta.BeatDuration = Math.Max(pIdx == 0 ? 0 : meta.BeatDuration, actualQuarterBeats);
                    }
                    else if (pIdx == 0)
                    {
                        meta.BeatDuration = curBeats * (4.0 / curBeatType);
                    }
                }
            }

            if (measureMetas.Count > 0 && !measureMetas[0].ExplicitTempoBpm.HasValue)
            {
                measureMetas[0].ExplicitTempoBpm = defaultTempo;
            }

            score.InitialTempoBpm = measureMetas.FirstOrDefault()?.ExplicitTempoBpm ?? defaultTempo;
            score.InitialBeats = measureMetas.FirstOrDefault()?.Beats ?? 4;
            score.InitialBeatType = measureMetas.FirstOrDefault()?.BeatType ?? 4;
            score.InitialFifths = measureMetas.FirstOrDefault()?.Fifths ?? 0;
            score.HasRepeats = measureMetas.Any(m => m.RepeatBackward);

            // 3. 建立可獨立控制的聲部軌道 (ScorePartTrack)
            var trackLookup = BuildScoreTracks(score, scoreParts, partStavesFound, partStaffVoiceNoteCounts);

            // 4. 展開小節順序（處理 Repeat 與一二房 Ending）
            List<(int mIdx, int pass)> expandedMeasures = ExpandMeasureSequence(measureMetas, expandRepeats);

            // 5. 建立全曲時間軸與展開所有音符事件（含裝飾音、琶音、連結線、顫音、波音、迴音、延長記號與多聲部平衡）
            BuildTimelineAndNotes(score, measureMetas, expandedMeasures, rawNotesByPartAndMeasure, trackLookup);

            return score;
        }

        private static Dictionary<(string partId, int staff, int voice), int> BuildScoreTracks(
            MusicScore score,
            List<(string id, string name, string abbr, string instName, int? midiProg)> scoreParts,
            Dictionary<string, HashSet<int>> partStavesFound,
            Dictionary<(string partId, int staff, int voice), int> partStaffVoiceNoteCounts)
        {
            var mapping = new Dictionary<(string partId, int staff, int voice), int>();
            var usedColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int nextChannel = 0;

            int NextMelodicChannel()
            {
                int ch = nextChannel;
                nextChannel++;
                if (nextChannel == 9) nextChannel = 10; // 避開 GM Channel 10 (index 9) 打擊樂通道
                return Math.Min(ch, 15);
            }

            bool isSinglePianoPart = scoreParts.Count == 1;

            for (int pIdx = 0; pIdx < scoreParts.Count; pIdx++)
            {
                var sp = scoreParts[pIdx];
                var (gmProg, zhName, pan) = SoundFontManager.InferInstrumentAndPan(
                    sp.name, sp.abbr, sp.instName, sp.midiProg, pIdx, scoreParts.Count);

                var staves = partStavesFound.TryGetValue(sp.id, out var stSet)
                    ? stSet.OrderBy(s => s).ToList()
                    : new List<int> { 1 };

                if (staves.Count <= 1)
                {
                    int trackIdx = score.Tracks.Count;
                    var track = new ScorePartTrack
                    {
                        TrackIndex = trackIdx,
                        TrackId = $"{sp.id}_S1",
                        PartId = sp.id,
                        Name = zhName,
                        OriginalPartName = sp.name,
                        StaffNumber = 1,
                        MidiChannel = NextMelodicChannel(),
                        GmProgram = gmProg,
                        Pan = pan,
                        ColorHex = AssignDistinctTrackColor(zhName, gmProg, 1, null, trackIdx, usedColors)
                    };
                    score.Tracks.Add(track);

                    foreach (var kv in partStaffVoiceNoteCounts.Where(k => k.Key.partId == sp.id))
                        mapping[kv.Key] = trackIdx;
                    mapping[(sp.id, 1, 1)] = trackIdx;
                }
                else
                {
                    // 雙譜表樂器（如鋼琴）：檢查 Staff 1 是否有兩個高密度獨立聲部（例如《月光奏鳴曲》右手旋律 Voice 1 + 三連音伴奏 Voice 2）
                    var staff1Voices = partStaffVoiceNoteCounts
                        .Where(kv => kv.Key.partId == sp.id && kv.Key.staff == 1 && kv.Value >= 45)
                        .OrderBy(kv => kv.Key.voice)
                        .ToList();

                    if (isSinglePianoPart && staff1Voices.Count >= 2)
                    {
                        // 拆分為：右手主旋律 (Voice 1)、右手伴奏聲部 (Voice 2+)、左手低音 (Staff 2)
                        int primaryVoice = staff1Voices[0].Key.voice;

                        int idxMelody = score.Tracks.Count;
                        score.Tracks.Add(new ScorePartTrack
                        {
                            TrackIndex = idxMelody,
                            TrackId = $"{sp.id}_S1_V{primaryVoice}",
                            PartId = sp.id,
                            Name = "鋼琴 - 右手主旋律 (高音聲部)",
                            OriginalPartName = sp.name,
                            StaffNumber = 1,
                            VoiceFilter = primaryVoice,
                            MidiChannel = NextMelodicChannel(),
                            GmProgram = gmProg,
                            Pan = 70,
                            ColorHex = AssignDistinctTrackColor("鋼琴 - 右手主旋律", gmProg, 1, primaryVoice, idxMelody, usedColors)
                        });

                        int idxAccomp = score.Tracks.Count;
                        score.Tracks.Add(new ScorePartTrack
                        {
                            TrackIndex = idxAccomp,
                            TrackId = $"{sp.id}_S1_Accomp",
                            PartId = sp.id,
                            Name = "鋼琴 - 右手伴奏 (中音三連音/和弦)",
                            OriginalPartName = sp.name,
                            StaffNumber = 1,
                            VoiceFilter = 2,
                            MidiChannel = NextMelodicChannel(),
                            GmProgram = gmProg,
                            Pan = 62,
                            ColorHex = AssignDistinctTrackColor("鋼琴 - 右手伴奏", gmProg, 1, 2, idxAccomp, usedColors)
                        });

                        foreach (var kv in partStaffVoiceNoteCounts.Where(k => k.Key.partId == sp.id && k.Key.staff == 1))
                        {
                            mapping[kv.Key] = kv.Key.voice == primaryVoice ? idxMelody : idxAccomp;
                        }
                        mapping[(sp.id, 1, primaryVoice)] = idxMelody;
                    }
                    else
                    {
                        // 標準右手 (Staff 1)
                        int idxRight = score.Tracks.Count;
                        string prefix = scoreParts.Count > 1 ? zhName.Split('(')[0].Trim() : "鋼琴";
                        score.Tracks.Add(new ScorePartTrack
                        {
                            TrackIndex = idxRight,
                            TrackId = $"{sp.id}_S1",
                            PartId = sp.id,
                            Name = $"{prefix} - 右手 (高音譜表)",
                            OriginalPartName = sp.name,
                            StaffNumber = 1,
                            MidiChannel = NextMelodicChannel(),
                            GmProgram = gmProg,
                            Pan = 72,
                            ColorHex = AssignDistinctTrackColor($"{prefix} - 右手", gmProg, 1, null, idxRight, usedColors)
                        });

                        foreach (var kv in partStaffVoiceNoteCounts.Where(k => k.Key.partId == sp.id && k.Key.staff == 1))
                            mapping[kv.Key] = idxRight;
                        mapping[(sp.id, 1, 1)] = idxRight;
                    }

                    // 左手 (Staff 2+)
                    for (int sIdx = 1; sIdx < staves.Count; sIdx++)
                    {
                        int stNum = staves[sIdx];
                        int idxLeft = score.Tracks.Count;
                        string prefix = scoreParts.Count > 1 ? zhName.Split('(')[0].Trim() : "鋼琴";
                        score.Tracks.Add(new ScorePartTrack
                        {
                            TrackIndex = idxLeft,
                            TrackId = $"{sp.id}_S{stNum}",
                            PartId = sp.id,
                            Name = $"{prefix} - 左手 (低音譜表)",
                            OriginalPartName = sp.name,
                            StaffNumber = stNum,
                            MidiChannel = NextMelodicChannel(),
                            GmProgram = gmProg,
                            Pan = 54,
                            ColorHex = AssignDistinctTrackColor($"{prefix} - 左手", gmProg, stNum, null, idxLeft, usedColors)
                        });

                        foreach (var kv in partStaffVoiceNoteCounts.Where(k => k.Key.partId == sp.id && k.Key.staff == stNum))
                            mapping[kv.Key] = idxLeft;
                        mapping[(sp.id, stNum, 1)] = idxLeft;
                    }
                }
            }

            return mapping;
        }

        private static List<(int mIdx, int pass)> ExpandMeasureSequence(List<MeasureMeta> metas, bool expandRepeats)
        {
            var result = new List<(int mIdx, int pass)>();
            if (metas.Count == 0) return result;

            if (!expandRepeats)
            {
                for (int i = 0; i < metas.Count; i++)
                {
                    if (metas[i].EndingNumber == 1 && i + 1 < metas.Count && metas[i + 1].EndingNumber == 2)
                        continue; // 不反覆時直接進二房
                    result.Add((i, 1));
                }
                return result;
            }

            int repeatStartIdx = 0;
            int pass = 1;
            var backwardTaken = new HashSet<int>();
            int idx = 0;
            int safetyCounter = 0;

            while (idx < metas.Count && safetyCounter < metas.Count * 4)
            {
                safetyCounter++;
                var m = metas[idx];

                if (m.RepeatForward && pass == 1)
                {
                    repeatStartIdx = idx;
                }

                // 若目前為第 2 次通過，且遇到一房 (Ending 1)，跳至二房 (Ending 2)
                if (pass >= 2 && m.EndingNumber == 1)
                {
                    int nextIdx = idx + 1;
                    while (nextIdx < metas.Count && metas[nextIdx].EndingNumber != 2)
                    {
                        nextIdx++;
                    }
                    if (nextIdx < metas.Count)
                    {
                        idx = nextIdx;
                        pass = 1;
                        continue;
                    }
                }

                result.Add((idx, pass));

                if (m.RepeatBackward && !backwardTaken.Contains(idx))
                {
                    backwardTaken.Add(idx);
                    idx = repeatStartIdx;
                    pass = 2;
                    continue;
                }

                if (m.RepeatBackward && pass >= 2)
                {
                    pass = 1;
                }

                idx++;
            }

            return result;
        }

        private static void BuildTimelineAndNotes(
            MusicScore score,
            List<MeasureMeta> measureMetas,
            List<(int mIdx, int pass)> expandedMeasures,
            Dictionary<(string partId, int mIdx), List<RawNoteItem>> rawNotesByPartAndMeasure,
            Dictionary<(string partId, int staff, int voice), int> trackLookup)
        {
            double currentTimeSec = 0.0;
            double currentBpm = score.InitialTempoBpm;
            bool isSlowSustainedPiano = score.Tracks.All(t => t.GmProgram == 0) && score.InitialTempoBpm <= 68.0;

            var allNotes = new List<NoteEvent>(4096);
            // 用於跨小節或同小節連結線 (Tie) 合併：(trackIndex, midiPitch) -> 正在延續中的 NoteEvent
            var activeTiedNotes = new Dictionary<(int trackIdx, int pitch), NoteEvent>();

            for (int seqIdx = 0; seqIdx < expandedMeasures.Count; seqIdx++)
            {
                var (mIdx, pass) = expandedMeasures[seqIdx];
                var meta = measureMetas[mIdx];

                if (meta.ExplicitTempoBpm.HasValue && meta.ExplicitTempoBpm.Value > 10)
                    currentBpm = meta.ExplicitTempoBpm.Value;

                double secPerBeat = 60.0 / currentBpm;
                double fermataStretchFactor = meta.HasFermata ? 1.25 : 1.0;
                double measureDurSec = meta.BeatDuration * secPerBeat * fermataStretchFactor;

                score.Measures.Add(new MeasureMarker
                {
                    TimelineIndex = seqIdx,
                    MeasureNumberText = pass > 1 ? $"{meta.MeasureNumberText} (反覆)" : meta.MeasureNumberText,
                    NumericMeasureNumber = meta.NumericMeasureNumber,
                    RepeatPass = pass,
                    StartTimeSeconds = currentTimeSec,
                    DurationSeconds = measureDurSec,
                    Beats = meta.Beats,
                    BeatType = meta.BeatType,
                    Fifths = meta.Fifths,
                    TempoBpm = currentBpm,
                    SectionText = meta.SectionLabel
                });

                // 依各 Part 處理本小節音符
                foreach (string partId in score.Tracks.Select(t => t.PartId).Distinct())
                {
                    if (!rawNotesByPartAndMeasure.TryGetValue((partId, mIdx), out var rawList) || rawList.Count == 0)
                        continue;

                    // A. 預處理琶音 (<arpeggiate>)：找出同時間點需要展開琶音的和弦音
                    var arpeggioTimestamps = rawList
                        .Where(n => n.HasArpeggiate && !n.IsGrace)
                        .Select(n => (n.Staff, Math.Round(n.BeatStart, 3)))
                        .ToHashSet();

                    var arpeggioOffsetsSec = new Dictionary<RawNoteItem, double>();
                    foreach (var (arpStaff, arpBeatRounded) in arpeggioTimestamps)
                    {
                        var chordNotes = rawList
                            .Where(n => !n.IsGrace && n.Staff == arpStaff && Math.Abs(n.BeatStart - arpBeatRounded) < 0.01)
                            .OrderBy(n => n.MidiPitch)
                            .ToList();

                        for (int c = 0; c < chordNotes.Count; c++)
                        {
                            arpeggioOffsetsSec[chordNotes[c]] = c * 0.026; // 每個和弦音錯開 26ms
                        }
                    }

                    // B. 處理裝飾音群 (<grace>) 與主音的時值關係
                    var pendingGraces = new List<RawNoteItem>();

                    for (int nIdx = 0; nIdx < rawList.Count; nIdx++)
                    {
                        var rNote = rawList[nIdx];
                        if (rNote.IsGrace)
                        {
                            pendingGraces.Add(rNote);
                            continue;
                        }

                        int trackIdx = ResolveTrackIndex(trackLookup, partId, rNote.Staff, rNote.Voice);
                        var track = score.Tracks[trackIdx];

                        double localSecPerBeat = secPerBeat * (meta.HasFermata && rNote.BeatStart >= meta.FermataBeatPos - 0.01 ? 1.55 : 1.0);
                        double noteStartSec = currentTimeSec + (rNote.BeatStart * secPerBeat);
                        if (meta.HasFermata && rNote.BeatStart > meta.FermataBeatPos)
                        {
                            noteStartSec += (rNote.BeatStart - meta.FermataBeatPos) * secPerBeat * 0.55;
                        }

                        double noteDurSec = Math.Max(0.035, rNote.BeatDuration * localSecPerBeat);

                        // 計算觸鍵力度 (Velocity)
                        int velocity = NormalizeVelocity(rNote.DynamicVelocity, isSlowSustainedPiano, track.VoiceFilter == 1);
                        if (rNote.HasAccent) velocity = Math.Min(126, velocity + 16);
                        if (rNote.HasStaccato) velocity = Math.Min(124, velocity + 6);
                        if (rNote.HasStaccatissimo) velocity = Math.Min(125, velocity + 10);

                        // 若前方有累積的裝飾音群 (<grace>)，分配裝飾音時間
                        if (pendingGraces.Count > 0 && !rNote.IsChord)
                        {
                            int gCount = pendingGraces.Count;
                            double singleGraceSec = Math.Clamp(secPerBeat * 0.11, 0.032, 0.052);
                            double totalGraceSec = singleGraceSec * gCount;

                            // 一半取自拍前、一半取自本音開頭，讓土耳其進行曲三連裝飾音既準拍又俐落
                            double preSteal = Math.Min(noteStartSec - currentTimeSec, totalGraceSec * 0.55);
                            double postSteal = Math.Min(noteDurSec * 0.45, totalGraceSec - preSteal);
                            double graceGroupStart = noteStartSec - preSteal;

                            for (int g = 0; g < gCount; g++)
                            {
                                var gItem = pendingGraces[g];
                                int gTrackIdx = ResolveTrackIndex(trackLookup, partId, gItem.Staff, gItem.Voice);
                                var gTrack = score.Tracks[gTrackIdx];
                                double gStart = graceGroupStart + g * singleGraceSec;

                                allNotes.Add(new NoteEvent
                                {
                                    TrackIndex = gTrackIdx,
                                    TrackId = gTrack.TrackId,
                                    MidiChannel = gTrack.MidiChannel,
                                    MidiPitch = gItem.MidiPitch,
                                    Velocity = Math.Clamp(velocity - 6 + g * 2, 32, 120),
                                    StartTimeSeconds = Math.Max(0, gStart),
                                    DurationSeconds = singleGraceSec,
                                    AudioDurationSeconds = singleGraceSec * 1.04,
                                    MeasureNumber = meta.NumericMeasureNumber,
                                    Staff = gItem.Staff,
                                    Voice = gItem.Voice,
                                    IsOrnamentSubNote = true,
                                    ArticulationLabel = "裝飾音"
                                });
                            }

                            noteStartSec += postSteal;
                            noteDurSec = Math.Max(0.035, noteDurSec - postSteal);
                            pendingGraces.Clear();
                        }

                        // 套用琶音錯開偏移
                        if (arpeggioOffsetsSec.TryGetValue(rNote, out double arpOffset))
                        {
                            double shift = Math.Min(arpOffset, noteDurSec * 0.5);
                            noteStartSec += shift;
                            noteDurSec -= shift;
                        }

                        // 處理連結線 (Tie Stop)：若此音是前一個連結音的延續，直接延長前音時長
                        var tieKey = (trackIdx, rNote.MidiPitch);
                        if (rNote.TieStop && activeTiedNotes.TryGetValue(tieKey, out var prevTiedNote))
                        {
                            double newEnd = noteStartSec + noteDurSec;
                            prevTiedNote.DurationSeconds = Math.Max(prevTiedNote.DurationSeconds, newEnd - prevTiedNote.StartTimeSeconds);
                            prevTiedNote.AudioDurationSeconds = prevTiedNote.DurationSeconds * 0.99;

                            if (!rNote.TieStart)
                                activeTiedNotes.Remove(tieKey);
                            continue;
                        }

                        // C. 處理顫音 (Trill)、波音 (Mordent)、迴音 (Turn)
                        if (!rNote.TieStart && (rNote.HasTrill || rNote.HasMordent || rNote.HasTurn) && noteDurSec >= 0.14)
                        {
                            var ornNotes = ExpandOrnamentNote(rNote, track, meta.Fifths, noteStartSec, noteDurSec, velocity, secPerBeat);
                            allNotes.AddRange(ornNotes);
                            continue;
                        }

                        // D. 計算斷奏 / 半連音 / 圓滑線 / 慢板踏板共鳴的實際發聲時長 (AudioDurationSeconds)
                        double audioGateRatio = 0.92;
                        string artLabel = string.Empty;

                        if (rNote.HasStaccatissimo)
                        {
                            audioGateRatio = 0.30;
                            artLabel = "極短斷奏";
                        }
                        else if (rNote.HasStaccato)
                        {
                            audioGateRatio = 0.48;
                            artLabel = "斷奏";
                        }
                        else if (rNote.HasDetachedLegato)
                        {
                            audioGateRatio = 0.72;
                            artLabel = "半連音";
                        }
                        else if (rNote.HasTenuto || rNote.InSlur || rNote.TieStart)
                        {
                            audioGateRatio = 0.99;
                        }
                        else if (isSlowSustainedPiano)
                        {
                            // 月光奏鳴曲慢板三連音與低音：賦予柔和延音共鳴
                            audioGateRatio = track.VoiceFilter == 2 ? 1.45 : 1.12;
                        }

                        var noteEvt = new NoteEvent
                        {
                            TrackIndex = trackIdx,
                            TrackId = track.TrackId,
                            MidiChannel = track.MidiChannel,
                            MidiPitch = rNote.MidiPitch,
                            Velocity = velocity,
                            StartTimeSeconds = noteStartSec,
                            DurationSeconds = noteDurSec,
                            AudioDurationSeconds = Math.Max(0.030, noteDurSec * audioGateRatio),
                            MeasureNumber = meta.NumericMeasureNumber,
                            Staff = rNote.Staff,
                            Voice = rNote.Voice,
                            ArticulationLabel = artLabel
                        };

                        allNotes.Add(noteEvt);

                        if (rNote.TieStart)
                        {
                            activeTiedNotes[tieKey] = noteEvt;
                        }
                    }
                }

                currentTimeSec += measureDurSec;
            }

            // 依時間排序並統計各軌道音符數与音域範圍
            allNotes.Sort((a, b) =>
            {
                int cmp = a.StartTimeSeconds.CompareTo(b.StartTimeSeconds);
                return cmp != 0 ? cmp : a.MidiPitch.CompareTo(b.MidiPitch);
            });

            // 防止同一軌道、同一音高重疊時前音未結束就把後音切斷
            PreventSamePitchOverlap(allNotes);

            score.Notes = allNotes;
            score.TotalDurationSeconds = Math.Max(currentTimeSec, allNotes.Count > 0 ? allNotes.Max(n => n.EndTimeSeconds) : 0.0);

            if (allNotes.Count > 0)
            {
                score.MinMidiPitch = Math.Max(21, allNotes.Min(n => n.MidiPitch) - 2);
                score.MaxMidiPitch = Math.Min(108, allNotes.Max(n => n.MidiPitch) + 2);
            }

            foreach (var track in score.Tracks)
            {
                track.NoteCount = allNotes.Count(n => n.TrackIndex == track.TrackIndex && !n.IsOrnamentSubNote);
            }
        }

        private static List<NoteEvent> ExpandOrnamentNote(
            RawNoteItem rNote,
            ScorePartTrack track,
            int fifths,
            double startSec,
            double totalDurSec,
            int baseVelocity,
            double secPerBeat)
        {
            var list = new List<NoteEvent>();
            int mainPitch = rNote.MidiPitch;
            int upperPitch = GetDiatonicNeighborPitch(mainPitch, +1, fifths, rNote.OrnamentAccidental);
            int lowerPitch = GetDiatonicNeighborPitch(mainPitch, -1, fifths, rNote.OrnamentAccidental);

            if (rNote.HasMordent)
            {
                // 波音 (Mordent)：主音 -> 上方鄰音 -> 主音
                double stepSec = Math.Clamp(secPerBeat * 0.125, 0.042, 0.068);
                if (stepSec * 2.5 > totalDurSec) stepSec = totalDurSec * 0.28;
                double holdSec = Math.Max(0.04, totalDurSec - 2 * stepSec);

                int[] pitches = { mainPitch, upperPitch, mainPitch };
                double[] durs = { stepSec, stepSec, holdSec };
                double t = startSec;
                for (int i = 0; i < 3; i++)
                {
                    list.Add(new NoteEvent
                    {
                        TrackIndex = track.TrackIndex,
                        TrackId = track.TrackId,
                        MidiChannel = track.MidiChannel,
                        MidiPitch = pitches[i],
                        Velocity = i == 2 ? baseVelocity : Math.Max(35, baseVelocity - 4),
                        StartTimeSeconds = t,
                        DurationSeconds = durs[i],
                        AudioDurationSeconds = durs[i] * (i < 2 ? 0.98 : 0.90),
                        MeasureNumber = rNote.OriginalMeasureNumber,
                        Staff = rNote.Staff,
                        Voice = rNote.Voice,
                        IsOrnamentSubNote = i > 0,
                        ArticulationLabel = "波音"
                    });
                    t += durs[i];
                }
                return list;
            }

            if (rNote.HasTurn)
            {
                // 迴音 (Turn)：上方鄰音 -> 主音 -> 下方鄰音 -> 主音
                double stepSec = Math.Clamp(secPerBeat * 0.14, 0.045, 0.075);
                if (stepSec * 3.6 > totalDurSec) stepSec = totalDurSec * 0.24;
                double holdSec = Math.Max(0.04, totalDurSec - 3 * stepSec);

                int[] pitches = { upperPitch, mainPitch, lowerPitch, mainPitch };
                double[] durs = { stepSec, stepSec, stepSec, holdSec };
                double t = startSec;
                for (int i = 0; i < 4; i++)
                {
                    list.Add(new NoteEvent
                    {
                        TrackIndex = track.TrackIndex,
                        TrackId = track.TrackId,
                        MidiChannel = track.MidiChannel,
                        MidiPitch = pitches[i],
                        Velocity = i == 0 ? baseVelocity : Math.Max(35, baseVelocity - 3),
                        StartTimeSeconds = t,
                        DurationSeconds = durs[i],
                        AudioDurationSeconds = durs[i] * 0.96,
                        MeasureNumber = rNote.OriginalMeasureNumber,
                        Staff = rNote.Staff,
                        Voice = rNote.Voice,
                        IsOrnamentSubNote = i > 0,
                        ArticulationLabel = "迴音"
                    });
                    t += durs[i];
                }
                return list;
            }

            // 顫音 (Trill)：快速交替主音與上方調內鄰音
            double trillStep = Math.Clamp(secPerBeat * 0.125, 0.048, 0.072);
            int count = Math.Max(4, (int)Math.Floor(totalDurSec / trillStep));
            if (count % 2 == 0) count--; // 確保最後一音落在主音 (mainPitch)
            if (count < 3) count = 3;

            double actualStep = totalDurSec / count;
            double curT = startSec;
            for (int i = 0; i < count; i++)
            {
                bool isLast = i == count - 1;
                double subDur = isLast ? Math.Max(0.03, (startSec + totalDurSec) - curT) : actualStep;
                int p = (i % 2 == 0) ? mainPitch : upperPitch;

                list.Add(new NoteEvent
                {
                    TrackIndex = track.TrackIndex,
                    TrackId = track.TrackId,
                    MidiChannel = track.MidiChannel,
                    MidiPitch = p,
                    Velocity = i == 0 ? baseVelocity : Math.Max(35, baseVelocity - 5),
                    StartTimeSeconds = curT,
                    DurationSeconds = subDur,
                    AudioDurationSeconds = subDur * (isLast ? 0.90 : 0.98),
                    MeasureNumber = rNote.OriginalMeasureNumber,
                    Staff = rNote.Staff,
                    Voice = rNote.Voice,
                    IsOrnamentSubNote = i > 0,
                    ArticulationLabel = "顫音"
                });
                curT += subDur;
            }

            return list;
        }

        private static void PreventSamePitchOverlap(List<NoteEvent> sortedNotes)
        {
            var lastByTrackAndPitch = new Dictionary<(int trackIdx, int pitch), NoteEvent>();
            foreach (var n in sortedNotes)
            {
                var key = (n.TrackIndex, n.MidiPitch);
                if (lastByTrackAndPitch.TryGetValue(key, out var prev))
                {
                    if (prev.AudioEndTimeSeconds > n.StartTimeSeconds - 0.004)
                    {
                        prev.AudioDurationSeconds = Math.Max(0.020, n.StartTimeSeconds - prev.StartTimeSeconds - 0.004);
                    }
                    if (prev.EndTimeSeconds > n.StartTimeSeconds)
                    {
                        prev.DurationSeconds = Math.Max(0.020, n.StartTimeSeconds - prev.StartTimeSeconds);
                    }
                }
                lastByTrackAndPitch[key] = n;
            }
        }

        private static int GetDiatonicNeighborPitch(int midiPitch, int direction, int fifths, string? accidentalMark)
        {
            // 計算當前調號 (fifths) 下 C, D, E, F, G, A, B 的半音偏移
            int[] baseSteps = { 0, 2, 4, 5, 7, 9, 11 };
            int[] sharpOrder = { 3, 0, 4, 1, 5, 2, 6 }; // F, C, G, D, A, E, B
            int[] flatOrder = { 6, 2, 5, 1, 4, 0, 3 };  // B, E, A, D, G, C, F

            int[] scalePitches = (int[])baseSteps.Clone();
            if (fifths > 0)
            {
                for (int i = 0; i < Math.Min(7, fifths); i++)
                    scalePitches[sharpOrder[i]] = (scalePitches[sharpOrder[i]] + 1) % 12;
            }
            else if (fifths < 0)
            {
                for (int i = 0; i < Math.Min(7, -fifths); i++)
                    scalePitches[flatOrder[i]] = (scalePitches[flatOrder[i]] + 11) % 12;
            }

            int pc = ((midiPitch % 12) + 12) % 12;
            int bestDeg = 0;
            int bestDiff = 99;
            for (int d = 0; d < 7; d++)
            {
                int diff = Math.Abs(scalePitches[d] - pc);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestDeg = d;
                }
            }

            int targetDeg = (bestDeg + direction + 7) % 7;
            int targetPc = scalePitches[targetDeg];

            int neighborPitch = midiPitch + (direction > 0 ? 1 : -1);
            while (((neighborPitch % 12) + 12) % 12 != targetPc)
            {
                neighborPitch += direction > 0 ? 1 : -1;
            }

            int semitoneDist = Math.Abs(neighborPitch - midiPitch);
            if (semitoneDist < 1 || semitoneDist > 2)
            {
                neighborPitch = midiPitch + (direction > 0 ? 2 : -1);
            }

            if (!string.IsNullOrWhiteSpace(accidentalMark))
            {
                string acc = accidentalMark!.ToLowerInvariant();
                if (acc.Contains("sharp") && Math.Abs(neighborPitch - midiPitch) == 2 && direction < 0)
                    neighborPitch = midiPitch - 1; // 如 K.496 m.123 迴音下方鄰音升記號（半音導音）
                else if (acc.Contains("flat") && Math.Abs(neighborPitch - midiPitch) == 2 && direction > 0)
                    neighborPitch = midiPitch + 1;
            }

            return Math.Clamp(neighborPitch, 21, 108);
        }

        private static int ResolveTrackIndex(
            Dictionary<(string partId, int staff, int voice), int> lookup,
            string partId, int staff, int voice)
        {
            if (lookup.TryGetValue((partId, staff, voice), out int idx))
                return idx;
            if (lookup.TryGetValue((partId, staff, 1), out idx))
                return idx;
            if (lookup.TryGetValue((partId, 1, 1), out idx))
                return idx;
            return 0;
        }

        private static int NormalizeVelocity(int rawVelocity, bool isSlowSustainedPiano, bool isPrimaryMelodyVoice)
        {
            int v = Math.Clamp(rawVelocity, 20, 126);
            if (isSlowSustainedPiano)
            {
                // 針對月光奏鳴曲 pp (31) 進行溫潤曲線映射，並突顯 Voice 1 主旋律
                v = (int)Math.Round(48 + (v - 25) * 0.65);
                if (isPrimaryMelodyVoice)
                    v += 16;
                else
                    v -= 5;
            }
            return Math.Clamp(v, 36, 126);
        }

        private static void ParseSoundElement(XElement snd, MeasureMeta meta, ref int currentDynamicVel, ref bool foundAnyTempo)
        {
            string? tempoStr = (string?)snd.Attribute("tempo");
            if (!string.IsNullOrWhiteSpace(tempoStr) &&
                double.TryParse(tempoStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double tVal) && tVal > 10)
            {
                meta.ExplicitTempoBpm = tVal;
                foundAnyTempo = true;
            }

            string? dynStr = (string?)snd.Attribute("dynamics");
            if (!string.IsNullOrWhiteSpace(dynStr) &&
                double.TryParse(dynStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
            {
                // MusicXML <sound dynamics="..."> 常見百分比 (例如 100 = f, 55 = p) 或 MIDI 值
                int mapped = dVal <= 127 ? (int)Math.Round(dVal) : 90;
                currentDynamicVel = Math.Clamp(mapped, 30, 124);
            }
        }

        private static int MapDynamicNameToVelocity(string dName, int fallback)
        {
            return dName switch
            {
                "ppp" => 34,
                "pp" => 46,
                "p" => 58,
                "mp" => 70,
                "mf" => 82,
                "f" => 96,
                "ff" => 110,
                "fff" => 120,
                "sfp" or "fp" => 98,
                "sf" or "sfz" or "fz" => 112,
                _ => fallback
            };
        }

        private static double ConvertBeatUnitToQuarterBpm(string? beatUnit, double perMinute)
        {
            return (beatUnit?.ToLowerInvariant()) switch
            {
                "half" => perMinute * 2.0,
                "eighth" => perMinute * 0.5,
                "whole" => perMinute * 4.0,
                _ => perMinute
            };
        }

        private static double? InferTempoFromWords(string words)
        {
            string w = words.ToLowerInvariant();
            if (w.Contains("presto")) return 156;
            if (w.Contains("allegro")) return 120;
            if (w.Contains("allegretto")) return 112;
            if (w.Contains("moderato")) return 100;
            if (w.Contains("andante")) return 80;
            if (w.Contains("adagio")) return 56;
            if (w.Contains("largo") || w.Contains("lento")) return 48;
            return null;
        }

        private static int ParseMidiPitch(XElement pitchEl)
        {
            string step = pitchEl.Element("step")?.Value.Trim().ToUpperInvariant() ?? "C";
            int octave = 4;
            int.TryParse(pitchEl.Element("octave")?.Value.Trim(), out octave);
            double alter = 0;
            double.TryParse(pitchEl.Element("alter")?.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out alter);

            int stepOffset = step switch
            {
                "C" => 0,
                "D" => 2,
                "E" => 4,
                "F" => 5,
                "G" => 7,
                "A" => 9,
                "B" => 11,
                _ => 0
            };

            int midi = (octave + 1) * 12 + stepOffset + (int)Math.Round(alter);
            return Math.Clamp(midi, 12, 120);
        }

        private static (string xml, string? title, string? subtitle, string? composer, double? tempoBpm) ReadRawMusicXmlContent(string filePath)
        {
            byte[] rawBytes = File.ReadAllBytes(filePath);

            // 1. 檢查是否為 ZIP / .mxl 壓縮檔 (PK\x03\x04)
            if (rawBytes.Length >= 4 && rawBytes[0] == 0x50 && rawBytes[1] == 0x4B && rawBytes[2] == 0x03 && rawBytes[3] == 0x04)
            {
                using var ms = new MemoryStream(rawBytes);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

                string? targetEntryPath = null;
                var containerEntry = archive.GetEntry("META-INF/container.xml");
                if (containerEntry != null)
                {
                    using var csr = new StreamReader(containerEntry.Open(), Encoding.UTF8);
                    var cdoc = XDocument.Load(csr);
                    targetEntryPath = cdoc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "rootfile")?
                        .Attribute("full-path")?.Value;
                }

                ZipArchiveEntry? xmlEntry = null;
                if (!string.IsNullOrWhiteSpace(targetEntryPath))
                    xmlEntry = archive.GetEntry(targetEntryPath!);

                xmlEntry ??= archive.Entries.FirstOrDefault(e =>
                    !e.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase) &&
                    (e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                     e.FullName.EndsWith(".musicxml", StringComparison.OrdinalIgnoreCase)));

                if (xmlEntry == null)
                    throw new InvalidDataException("壓縮的 MXL 檔案中找不到有效的 MusicXML 內容。");

                using var sr = new StreamReader(xmlEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return (sr.ReadToEnd(), null, null, null, null);
            }

            // 2. 轉為字串並檢查是否為 JSON 封裝格式 (如 K.496 與 K.545)
            string text;
            using (var ms = new MemoryStream(rawBytes))
            using (var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = sr.ReadToEnd();
            }

            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                using var jdoc = JsonDocument.Parse(trimmed);
                var root = jdoc.RootElement;

                string xml = root.TryGetProperty("musicxml", out var mxProp)
                    ? (mxProp.GetString() ?? string.Empty)
                    : string.Empty;

                string? title = root.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String
                    ? tProp.GetString()
                    : null;

                double? tempo = null;
                if (root.TryGetProperty("tempo_bpm", out var bpmProp))
                {
                    if (bpmProp.ValueKind == JsonValueKind.Number && bpmProp.TryGetDouble(out double bNum))
                        tempo = bNum;
                    else if (bpmProp.ValueKind == JsonValueKind.String &&
                             double.TryParse(bpmProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double bStr))
                        tempo = bStr;
                }

                string? composer = null;
                string? subtitle = null;
                if (root.TryGetProperty("meta", out var metaProp) && metaProp.ValueKind == JsonValueKind.Object)
                {
                    if (metaProp.TryGetProperty("composer", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                        composer = cProp.GetString();
                    if (metaProp.TryGetProperty("subtitle", out var sProp) && sProp.ValueKind == JsonValueKind.String)
                        subtitle = sProp.GetString();
                }

                if (!string.IsNullOrWhiteSpace(xml))
                    return (xml, title, subtitle, composer, tempo);
            }

            return (text, null, null, null, null);
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v!.Trim();
            }
            return string.Empty;
        }
    }
}
