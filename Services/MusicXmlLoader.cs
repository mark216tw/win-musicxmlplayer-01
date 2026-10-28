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
            public bool ArpeggioDown { get; set; }
            public bool HasBreathMark { get; set; }
            public bool HasCaesura { get; set; }
            public bool HasTrill { get; set; }
            public bool HasMordent { get; set; }
            public bool HasTurn { get; set; }
            public bool HasFermata { get; set; }
            public bool InSlur { get; set; }
            public bool IsSlurStart { get; set; }
            public bool HasPizzicato { get; set; }
            public bool HasTremolo { get; set; }
            public int TremoloMarks { get; set; }
            public bool HasMute { get; set; }
            public bool HasHarmonic { get; set; }
            public string? OrnamentAccidental { get; set; }
            public int DynamicVelocity { get; set; } = 78;
            public bool IsSfp { get; set; }
            public bool HasSforzando { get; set; }
            public bool IsSoloSection { get; set; }
        }

        private enum PedalActionType
        {
            Start,
            Stop,
            Change
        }

        private sealed class PedalAction
        {
            public string PartId { get; set; } = string.Empty;
            public int MeasureIndex { get; set; }
            public double BeatStart { get; set; }
            public PedalActionType Type { get; set; }
        }

        private sealed class PedalInterval
        {
            public string PartId { get; set; } = string.Empty;
            public double StartSeconds { get; set; }
            public double EndSeconds { get; set; }
        }

        private sealed class ExpressionKeyframe
        {
            public string PartId { get; set; } = string.Empty;
            public int MeasureIndex { get; set; }
            public double BeatStart { get; set; }
            public int Velocity { get; set; }
            public bool RampToNext { get; set; }
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
            public List<(double Beat, double Beats)> Pauses { get; } = new();
            public string SectionLabel { get; set; } = string.Empty;
        }

        public static MusicScore LoadFromFile(string filePath, bool expandRepeats = true, PerformanceOptions? performance = null)
        {
            performance ??= PerformanceOptions.Default;

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"找不到指定的 MusicXML 檔案：{filePath}");

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };

            var (doc, jsonTitle, jsonSubtitle, jsonComposer, jsonTempoBpm) = LoadXDocumentAndMeta(filePath, settings);

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
            var pedalActions = new List<PedalAction>();
            var expressionKeyframes = new List<ExpressionKeyframe>();

            for (int pIdx = 0; pIdx < partElements.Count; pIdx++)
            {
                var partEl = partElements[pIdx];
                string partId = (string?)partEl.Attribute("id") ?? scoreParts[Math.Min(pIdx, scoreParts.Count - 1)].id;
                partStavesFound[partId] = new HashSet<int> { 1 };

                var measures = partEl.Elements("measure").ToList();
                int divisions = 1;
                int curBeats = 4;
                int curBeatType = 4;
                int curFifths = 0;
                int currentDynamicVel = 78;
                bool pendingSforzando = false;
                bool pendingSfp = false;
                bool activeSlur = false;
                int activeWedgeType = 0; // 1 = cresc, -1 = dim
                bool isWordsWedge = false;
                double wedgeStartBeat = 0;
                int wedgeStartVelocity = currentDynamicVel;
                double wedgeStartAbsoluteBeat = 0;
                double partAbsoluteBeat = 0;
                bool activePizzicato = false;
                bool activeMute = false;
                bool activeHarmonic = false;
                bool activeTremolo = false;
                int activeTremoloMarks = 3;
                bool activeSoloSection = false;

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
                            int beforeVel = currentDynamicVel;
                            ParseSoundElement(child, meta, ref currentDynamicVel, ref foundAnyTempo);
                            if (currentDynamicVel != beforeVel)
                                AddExpressionKeyframe(expressionKeyframes, partId, mIdx, cursorDiv / divisions, currentDynamicVel, false);
                        }
                        else if (tag == "direction")
                        {
                            double dirOffsetDiv = 0;
                            var offsetEl = child.Element("offset");
                            if (offsetEl != null)
                            {
                                double.TryParse(offsetEl.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out dirOffsetDiv);
                            }
                            double dirBeatPos = Math.Max(0, (cursorDiv + dirOffsetDiv) / divisions);

                            foreach (var snd in child.Elements("sound"))
                            {
                                int beforeVel = currentDynamicVel;
                                ParseSoundElement(snd, meta, ref currentDynamicVel, ref foundAnyTempo);
                                if (currentDynamicVel != beforeVel)
                                    AddExpressionKeyframe(expressionKeyframes, partId, mIdx, dirBeatPos, currentDynamicVel, false);
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
                                    bool updatedBaseDynamic = false;
                                    foreach (var dChild in dynEl.Elements())
                                    {
                                        string dName = dChild.Name.LocalName.ToLowerInvariant();
                                        if (dName is "sfp" or "fp")
                                        {
                                            pendingSfp = true;
                                            pendingSforzando = true;
                                        }
                                        else if (dName is "sf" or "sfz" or "fz" or "rf" or "rfz")
                                        {
                                            pendingSforzando = true;
                                        }
                                        else if (dName != "other-dynamics")
                                        {
                                            int mappedVel = MapDynamicNameToVelocity(dName, currentDynamicVel);
                                            if (mappedVel != currentDynamicVel)
                                            {
                                                if (activeWedgeType != 0 && isWordsWedge)
                                                {
                                                    activeWedgeType = 0;
                                                    isWordsWedge = false;
                                                }
                                                currentDynamicVel = mappedVel;
                                                updatedBaseDynamic = true;
                                            }
                                        }
                                    }

                                    if (updatedBaseDynamic)
                                    {
                                        AddExpressionKeyframe(expressionKeyframes, partId, mIdx, dirBeatPos, currentDynamicVel, false);
                                    }
                                }

                                var wedgeEl = dt.Element("wedge");
                                if (wedgeEl != null)
                                {
                                    string? wType = (string?)wedgeEl.Attribute("type");
                                    if (wType == "crescendo" || wType == "diminuendo")
                                    {
                                        activeWedgeType = wType == "crescendo" ? 1 : -1;
                                        wedgeStartBeat = cursorDiv / divisions;
                                        wedgeStartVelocity = currentDynamicVel;
                                        wedgeStartAbsoluteBeat = partAbsoluteBeat + wedgeStartBeat;
                                        AddExpressionKeyframe(expressionKeyframes, partId, mIdx, wedgeStartBeat, currentDynamicVel, true);
                                    }
                                    else if (wType == "stop")
                                    {
                                        if (activeWedgeType != 0)
                                        {
                                            double stopBeat = cursorDiv / divisions;
                                            double stopAbsoluteBeat = partAbsoluteBeat + stopBeat;
                                            double deltaBeats = Math.Max(0, stopAbsoluteBeat - wedgeStartAbsoluteBeat);
                                            int endVelocity = Math.Clamp(
                                                wedgeStartVelocity + (int)Math.Min(22, deltaBeats * 4.5) * activeWedgeType,
                                                28, 122);
                                            AddExpressionKeyframe(expressionKeyframes, partId, mIdx, stopBeat, endVelocity, false);
                                        }
                                        activeWedgeType = 0;
                                    }
                                }

                                var pedalEl = dt.Element("pedal");
                                if (pedalEl != null)
                                {
                                    string? pedalType = (string?)pedalEl.Attribute("type");
                                    PedalActionType? actionType = pedalType switch
                                    {
                                        "start" => PedalActionType.Start,
                                        "stop" => PedalActionType.Stop,
                                        "change" => PedalActionType.Change,
                                        _ => null
                                    };

                                    if (actionType.HasValue)
                                    {
                                        pedalActions.Add(new PedalAction
                                        {
                                            PartId = partId,
                                            MeasureIndex = mIdx,
                                            BeatStart = dirBeatPos,
                                            Type = actionType.Value
                                        });
                                    }
                                }

                                foreach (var wordsEl in dt.Elements("words"))
                                {
                                    string wText = wordsEl.Value?.Trim() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(wText))
                                    {
                                        if (string.IsNullOrEmpty(meta.SectionLabel) && wText.Length <= 28)
                                            meta.SectionLabel = wText;

                                        string wLower = wText.ToLowerInvariant();
                                        if (wLower.Contains("pizz"))
                                            activePizzicato = true;
                                        if (wLower.Contains("arco"))
                                            activePizzicato = false;
                                        if (wLower.Contains("con sord") || wLower.Contains("mute"))
                                            activeMute = true;
                                        if (wLower.Contains("senza sord"))
                                            activeMute = false;
                                        if (wLower.Contains("harmon"))
                                            activeHarmonic = true;
                                        if (wLower.Contains("ordinario") || wLower.Contains("ord."))
                                            activeHarmonic = false;
                                        if (wLower.Contains("solo"))
                                            activeSoloSection = true;
                                        if (wLower.Contains("tutti"))
                                            activeSoloSection = false;

                                        if (wLower.StartsWith("cresc") || wLower.StartsWith("dim") || wLower.StartsWith("decresc"))
                                        {
                                            activeWedgeType = wLower.StartsWith("cresc") ? 1 : -1;
                                            isWordsWedge = true;
                                            wedgeStartBeat = dirBeatPos;
                                            wedgeStartVelocity = currentDynamicVel;
                                            wedgeStartAbsoluteBeat = partAbsoluteBeat + wedgeStartBeat;
                                            AddExpressionKeyframe(expressionKeyframes, partId, mIdx, wedgeStartBeat, currentDynamicVel, true);
                                        }
                                        else if (wLower is "(f)" or "f")
                                        {
                                            activeWedgeType = 0;
                                            isWordsWedge = false;
                                            currentDynamicVel = 96;
                                            AddExpressionKeyframe(expressionKeyframes, partId, mIdx, dirBeatPos, currentDynamicVel, false);
                                        }
                                        else if (wLower is "(p)" or "p")
                                        {
                                            activeWedgeType = 0;
                                            isWordsWedge = false;
                                            currentDynamicVel = 58;
                                            AddExpressionKeyframe(expressionKeyframes, partId, mIdx, dirBeatPos, currentDynamicVel, false);
                                        }

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
                            {
                                foreach (var notEl in child.Elements("notations"))
                                {
                                    if (notEl.Element("fermata") != null)
                                    {
                                        meta.HasFermata = true;
                                        double rBeat = noteStartDiv / divisions;
                                        if (meta.FermataBeatPos < 0 || rBeat < meta.FermataBeatPos)
                                            meta.FermataBeatPos = rBeat;
                                    }
                                }
                                continue;
                            }

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
                                DynamicVelocity = currentDynamicVel,
                                IsSoloSection = activeSoloSection
                            };

                            if (!isGrace)
                            {
                                if (pendingSforzando || (isChord && noteList.Count > 0 && noteList[^1].HasSforzando))
                                {
                                    rawNote.HasSforzando = true;
                                    rawNote.IsSfp = pendingSfp || (isChord && noteList.Count > 0 && noteList[^1].IsSfp);
                                    if (!isChord)
                                    {
                                        pendingSforzando = false;
                                        pendingSfp = false;
                                    }
                                }
                            }

                            if (activeWedgeType != 0)
                            {
                                double noteAbsoluteBeat = partAbsoluteBeat + rawNote.BeatStart;
                                double deltaBeats = Math.Max(0, noteAbsoluteBeat - wedgeStartAbsoluteBeat);
                                int deltaVel = (int)Math.Min(22, deltaBeats * 4.5) * activeWedgeType;
                                rawNote.DynamicVelocity = Math.Clamp(currentDynamicVel + deltaVel, 28, 122);
                            }

                            bool inSlurBefore = activeSlur;
                            bool slurStartHere = false;
                            bool tremoloHere = false;

                            foreach (var notEl in child.Elements("notations"))
                            {
                                foreach (var tiedEl in notEl.Elements("tied"))
                                {
                                    string? tType = (string?)tiedEl.Attribute("type");
                                    if (tType == "start") rawNote.TieStart = true;
                                    else if (tType == "stop") rawNote.TieStop = true;
                                }

                                var arpeggiateEl = notEl.Element("arpeggiate");
                                if (arpeggiateEl != null)
                                {
                                    rawNote.HasArpeggiate = true;
                                    rawNote.ArpeggioDown = string.Equals(
                                        (string?)arpeggiateEl.Attribute("direction"),
                                        "down",
                                        StringComparison.OrdinalIgnoreCase);
                                }
                                if (notEl.Element("breath-mark") != null) rawNote.HasBreathMark = true;
                                if (notEl.Element("caesura") != null) rawNote.HasCaesura = true;

                                if (notEl.Element("fermata") != null)
                                {
                                    rawNote.HasFermata = true;
                                    meta.HasFermata = true;
                                    if (meta.FermataBeatPos < 0 || rawNote.BeatStart < meta.FermataBeatPos)
                                        meta.FermataBeatPos = rawNote.BeatStart;
                                }

                                var pedalNotation = notEl.Element("pedal");
                                if (pedalNotation != null)
                                {
                                    string? pedalType = (string?)pedalNotation.Attribute("type");
                                    PedalActionType? actionType = pedalType switch
                                    {
                                        "start" => PedalActionType.Start,
                                        "stop" => PedalActionType.Stop,
                                        "change" => PedalActionType.Change,
                                        _ => null
                                    };

                                    if (actionType.HasValue)
                                    {
                                        pedalActions.Add(new PedalAction
                                        {
                                            PartId = partId,
                                            MeasureIndex = mIdx,
                                            BeatStart = rawNote.BeatStart,
                                            Type = actionType.Value
                                        });
                                    }
                                }

                                foreach (var slurEl in notEl.Elements("slur"))
                                {
                                    string? sType = (string?)slurEl.Attribute("type");
                                    if (sType == "start")
                                    {
                                        activeSlur = true;
                                        slurStartHere = true;
                                    }
                                    else if (sType == "stop")
                                    {
                                        activeSlur = false;
                                    }
                                }

                                var artEl = notEl.Element("articulations");
                                if (artEl != null)
                                {
                                    if (artEl.Element("staccato") != null) rawNote.HasStaccato = true;
                                    if (artEl.Element("staccatissimo") != null) rawNote.HasStaccatissimo = true;
                                    if (artEl.Element("detached-legato") != null) rawNote.HasDetachedLegato = true;
                                    if (artEl.Element("tenuto") != null) rawNote.HasTenuto = true;
                                    if (artEl.Element("accent") != null || artEl.Element("strong-accent") != null) rawNote.HasAccent = true;
                                    if (artEl.Element("breath-mark") != null) rawNote.HasBreathMark = true;
                                    if (artEl.Element("caesura") != null) rawNote.HasCaesura = true;
                                }

                                var techEl = notEl.Element("technical");
                                if (techEl != null)
                                {
                                    if (techEl.Element("snap-pizzicato") != null || techEl.Element("pluck") != null)
                                        rawNote.HasPizzicato = true;
                                    if (techEl.Element("harmonic") != null)
                                        rawNote.HasHarmonic = true;
                                    if (techEl.Element("stopped") != null)
                                        rawNote.HasMute = true;
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

                                    var tremoloEl = ornEl.Element("tremolo");
                                    if (tremoloEl != null)
                                    {
                                        string? tremoloType = (string?)tremoloEl.Attribute("type");
                                        tremoloHere = true;
                                        int marks = 3;
                                        if (int.TryParse(tremoloEl.Value?.Trim(), out int parsedMarks) && parsedMarks >= 1)
                                            marks = parsedMarks;
                                        rawNote.TremoloMarks = marks;
                                        if (tremoloType == "start")
                                        {
                                            activeTremolo = true;
                                            activeTremoloMarks = marks;
                                        }
                                        else if (tremoloType == "stop")
                                        {
                                            activeTremolo = false;
                                        }
                                    }

                                    var accMark = ornEl.Element("accidental-mark");
                                    if (accMark != null)
                                        rawNote.OrnamentAccidental = accMark.Value?.Trim();
                                }
                            }

                            rawNote.InSlur = activeSlur || inSlurBefore;
                            rawNote.IsSlurStart = slurStartHere;
                            rawNote.HasPizzicato = rawNote.HasPizzicato || activePizzicato;
                            rawNote.HasTremolo = rawNote.HasTremolo || activeTremolo || tremoloHere;
                            if (rawNote.HasTremolo && rawNote.TremoloMarks <= 0)
                                rawNote.TremoloMarks = activeTremoloMarks;
                            rawNote.HasMute = rawNote.HasMute || activeMute;
                            rawNote.HasHarmonic = rawNote.HasHarmonic || activeHarmonic;
                            if (!rawNote.IsGrace && !rawNote.TieStart && (rawNote.HasBreathMark || rawNote.HasCaesura))
                            {
                                double pauseBeat = Math.Round(rawNote.BeatStart + rawNote.BeatDuration, 3);
                                double pauseBeats = rawNote.HasCaesura ? 0.55 : 0.20;
                                if (!meta.Pauses.Any(p => Math.Abs(p.Beat - pauseBeat) < 0.01))
                                    meta.Pauses.Add((pauseBeat, pauseBeats));
                            }
                            noteList.Add(rawNote);

                            if (!rawNote.IsGrace)
                            {
                                var svKey = (partId, staff, voice);
                                partStaffVoiceNoteCounts[svKey] = partStaffVoiceNoteCounts.GetValueOrDefault(svKey, 0) + 1;
                            }
                        }
                    }

                    if (pIdx == 0)
                    {
                        meta.Beats = curBeats;
                        meta.BeatType = curBeatType;
                        meta.Fifths = curFifths;
                    }

                    double actualQuarterBeats = maxCursorDiv / divisions;
                    if (actualQuarterBeats > 0.05)
                    {
                        meta.BeatDuration = Math.Max(pIdx == 0 ? 0 : meta.BeatDuration, actualQuarterBeats);
                    }
                    else if (pIdx == 0)
                    {
                        meta.BeatDuration = curBeats * (4.0 / curBeatType);
                    }

                    partAbsoluteBeat += meta.BeatDuration;
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
            BuildTimelineAndNotes(score, measureMetas, expandedMeasures, rawNotesByPartAndMeasure, trackLookup, pedalActions, expressionKeyframes, performance);

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

            int NextMelodicChannel(int gmProg)
            {
                if (nextChannel == 9) nextChannel = 10; // 避開 GM Channel 10 (index 9) 打擊樂通道
                if (nextChannel <= 15)
                {
                    return nextChannel++;
                }

                // 當總聲部超過 15 軌時，優先共用相同樂器 (GmProgram) 的既有 MIDI Channel，避免全部擠在 Channel 15 互相覆蓋音色
                var existingSameProg = score.Tracks.FirstOrDefault(t => t.GmProgram == gmProg);
                if (existingSameProg != null)
                {
                    return existingSameProg.MidiChannel;
                }

                return 15;
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
                        MidiChannel = NextMelodicChannel(gmProg),
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
                            MidiChannel = NextMelodicChannel(gmProg),
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
                            MidiChannel = NextMelodicChannel(gmProg),
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
                            MidiChannel = NextMelodicChannel(gmProg),
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
                            MidiChannel = NextMelodicChannel(gmProg),
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
            Dictionary<(string partId, int staff, int voice), int> trackLookup,
            List<PedalAction> pedalActions,
            List<ExpressionKeyframe> expressionKeyframes,
            PerformanceOptions performance)
        {
            double currentTimeSec = 0.0;
            double currentBpm = score.InitialTempoBpm;
            bool isSlowSustainedPiano = score.Tracks.All(t => t.GmProgram == 0) && score.InitialTempoBpm <= 68.0;
            bool hasDedicatedSoloistPart = score.Tracks.Any(t => t.Name.Contains("主奏") || t.OriginalPartName.Contains("principale", StringComparison.OrdinalIgnoreCase) || t.OriginalPartName.Contains("solo", StringComparison.OrdinalIgnoreCase));
            bool isPianoStringsChamber = score.Tracks.Any(t => t.GmProgram is >= 0 and <= 5) && score.Tracks.Any(t => IsStringProgram(t.GmProgram));

            var allNotes = new List<NoteEvent>(4096);
            // �Ω��p�`�ΦP�p�`�s���u (Tie) �X�֡G(trackIndex, midiPitch) -> ���b���򤤪� NoteEvent
            var activeTiedNotes = new Dictionary<(int trackIdx, int pitch), NoteEvent>();

            var pedalByMeasure = pedalActions
                .GroupBy(a => a.MeasureIndex)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a.BeatStart).ToList());
            var expressionByMeasure = expressionKeyframes
                .GroupBy(k => k.MeasureIndex)
                .ToDictionary(g => g.Key, g => g.OrderBy(k => k.BeatStart).ToList());
            var pedalIntervals = new List<PedalInterval>();
            var activePedalStarts = new Dictionary<string, double>();
            var absoluteExpressionByPart = new Dictionary<string, List<(double time, int velocity, bool ramp)>>();

            for (int seqIdx = 0; seqIdx < expandedMeasures.Count; seqIdx++)
            {
                var (mIdx, pass) = expandedMeasures[seqIdx];
                var meta = measureMetas[mIdx];

                if (meta.ExplicitTempoBpm.HasValue && meta.ExplicitTempoBpm.Value > 10)
                    currentBpm = meta.ExplicitTempoBpm.Value;

                double secPerBeat = 60.0 / currentBpm;
                bool hasFermataInMeasure = meta.HasFermata;
                double fermataBeatPos = meta.FermataBeatPos >= 0
                    ? Math.Clamp(meta.FermataBeatPos, 0.0, Math.Max(0.0, meta.BeatDuration - 0.25))
                    : Math.Max(0.0, meta.BeatDuration - 1.0);
                const double fermataHoldMultiplier = 1.60;

                var measurePauses = new List<(double Beat, double Sec)>();
                double pauseBudgetSec = 0.60;
                foreach (var pause in meta.Pauses.OrderBy(p => p.Beat))
                {
                    if (pauseBudgetSec <= 0.0)
                        break;
                    bool isCaesura = pause.Beats >= 0.50;
                    double rawSec = pause.Beats * secPerBeat;
                    double pauseSec = isCaesura
                        ? Math.Clamp(rawSec, 0.20, 0.45)
                        : Math.Clamp(rawSec, 0.06, 0.18);
                    pauseSec = Math.Min(pauseSec, pauseBudgetSec);
                    if (pauseSec <= 0.001)
                        continue;
                    pauseBudgetSec -= pauseSec;
                    measurePauses.Add((pause.Beat, pauseSec));
                }

                double MapBeatBase(double beatPos)
                {
                    double clampedBeat = Math.Max(0.0, beatPos);
                    if (!hasFermataInMeasure || clampedBeat <= fermataBeatPos)
                        return clampedBeat * secPerBeat;
                    return (fermataBeatPos + (clampedBeat - fermataBeatPos) * fermataHoldMultiplier) * secPerBeat;
                }

                double MapBeatToOffsetSec(double beatPos)
                {
                    double result = MapBeatBase(beatPos);
                    foreach (var pause in measurePauses)
                    {
                        if (beatPos >= pause.Beat - 0.000001)
                            result += pause.Sec;
                    }
                    return result;
                }

                double MapBeatToEndSec(double beatPos)
                {
                    double result = MapBeatBase(beatPos);
                    foreach (var pause in measurePauses)
                    {
                        if (beatPos > pause.Beat + 0.000001)
                            result += pause.Sec;
                    }
                    return result;
                }

                double measureDurSec = MapBeatToOffsetSec(meta.BeatDuration);
                double beatUnit = meta.BeatDuration / Math.Max(1, meta.Beats);
                var beatLineTimes = new double[Math.Max(1, meta.Beats)];
                for (int beatIdx = 0; beatIdx < beatLineTimes.Length; beatIdx++)
                    beatLineTimes[beatIdx] = MapBeatToOffsetSec(beatIdx * beatUnit);

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
                    SectionText = meta.SectionLabel,
                    BeatLineTimes = beatLineTimes
                });

                // 依各 Part 處理本小節音符
                if (pedalByMeasure.TryGetValue(mIdx, out var measurePedals))
                {
                    foreach (var action in measurePedals)
                    {
                        double actionTime = currentTimeSec + MapBeatToOffsetSec(action.BeatStart);
                        if (action.Type == PedalActionType.Start)
                        {
                            if (activePedalStarts.TryGetValue(action.PartId, out double previousStart))
                            {
                                AddPedalInterval(pedalIntervals, action.PartId, previousStart, actionTime);
                            }
                            activePedalStarts[action.PartId] = actionTime;
                        }
                        else if (action.Type == PedalActionType.Change)
                        {
                            if (activePedalStarts.TryGetValue(action.PartId, out double previousStart))
                            {
                                AddPedalInterval(pedalIntervals, action.PartId, previousStart, actionTime);
                            }
                            activePedalStarts[action.PartId] = actionTime;
                        }
                        else if (action.Type == PedalActionType.Stop)
                        {
                            if (activePedalStarts.TryGetValue(action.PartId, out double startTime))
                            {
                                AddPedalInterval(pedalIntervals, action.PartId, startTime, actionTime);
                                activePedalStarts.Remove(action.PartId);
                            }
                        }
                    }
                }

                if (expressionByMeasure.TryGetValue(mIdx, out var measureExpression))
                {
                    foreach (var keyframe in measureExpression)
                    {
                        if (!absoluteExpressionByPart.TryGetValue(keyframe.PartId, out var partKeyframes))
                        {
                            partKeyframes = new List<(double time, int velocity, bool ramp)>();
                            absoluteExpressionByPart[keyframe.PartId] = partKeyframes;
                        }

                        partKeyframes.Add((
                            currentTimeSec + MapBeatToOffsetSec(keyframe.BeatStart),
                            keyframe.Velocity,
                            keyframe.RampToNext));
                    }
                }

                bool anyPartSoloInMeasure = false;
                foreach (string pid in score.Tracks.Select(t => t.PartId).Distinct())
                {
                    if (rawNotesByPartAndMeasure.TryGetValue((pid, mIdx), out var pList) && pList.Any(n => n.IsSoloSection))
                    {
                        anyPartSoloInMeasure = true;
                        break;
                    }
                }

                foreach (string partId in score.Tracks.Select(t => t.PartId).Distinct())
                {
                    if (!rawNotesByPartAndMeasure.TryGetValue((partId, mIdx), out var rawList) || rawList.Count == 0)
                        continue;

                    // A. 預處理琶音 (<arpeggiate>)：支援上下行方向與依速度調整滾奏速度
                    var arpeggioOffsetsSec = new Dictionary<RawNoteItem, double>();
                    var arpeggioVelocityDelta = new Dictionary<RawNoteItem, int>();
                    var arpeggioKeys = rawList
                        .Where(n => n.HasArpeggiate && !n.IsGrace)
                        .GroupBy(n => (n.Staff, Math.Round(n.BeatStart, 3)))
                        .Select(g => (Staff: g.Key.Staff, Beat: g.Key.Item2, Down: g.Any(n => n.ArpeggioDown)))
                        .ToList();

                    foreach (var arpeggioKey in arpeggioKeys)
                    {
                        var chordNotes = rawList
                            .Where(n => !n.IsGrace && n.Staff == arpeggioKey.Staff && Math.Abs(n.BeatStart - arpeggioKey.Beat) < 0.01)
                            .OrderBy(n => arpeggioKey.Down ? -n.MidiPitch : n.MidiPitch)
                            .ToList();
                        if (chordNotes.Count == 0)
                            continue;

                        double chordDur = chordNotes
                            .Select(n => Math.Max(0.035, MapBeatToEndSec(n.BeatStart + n.BeatDuration) - MapBeatToOffsetSec(n.BeatStart)))
                            .DefaultIfEmpty(0.2)
                            .Min();
                        double stepSec = Math.Clamp(secPerBeat * 0.05, 0.012, 0.040);
                        if (chordNotes.Count > 1)
                            stepSec = Math.Min(stepSec, chordDur * 0.35 / (chordNotes.Count - 1));

                        for (int c = 0; c < chordNotes.Count; c++)
                        {
                            arpeggioOffsetsSec[chordNotes[c]] = c * stepSec;
                            arpeggioVelocityDelta[chordNotes[c]] = c == 0 ? -1 : (c == chordNotes.Count - 1 ? 2 : 0);
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

                        double noteStartSec = currentTimeSec + MapBeatToOffsetSec(rNote.BeatStart);
                        double noteEndSec = currentTimeSec + MapBeatToEndSec(rNote.BeatStart + rNote.BeatDuration);
                        double noteDurSec = Math.Max(0.035, noteEndSec - noteStartSec);

                        // 計算觸鍵力度 (Velocity) 與古典拍點微律動、聲部平衡
                        int velocity = NormalizeVelocity(rNote.DynamicVelocity, isSlowSustainedPiano, track.VoiceFilter == 1, performance);
                        velocity += ComputeMusicalVoicingDelta(rNote, track, meta, hasDedicatedSoloistPart, isPianoStringsChamber, anyPartSoloInMeasure, isSlowSustainedPiano);

                        if (rNote.HasSforzando)
                        {
                            velocity = Math.Max(velocity + 20, rNote.IsSfp ? 98 : 110);
                        }
                        if (rNote.HasAccent) velocity = Math.Min(126, velocity + 16);
                        if (rNote.HasStaccato) velocity = Math.Min(124, velocity + 6);
                        if (rNote.HasStaccatissimo) velocity = Math.Min(125, velocity + 10);
                        if (IsStringProgram(track.GmProgram) && rNote.InSlur && !rNote.IsSlurStart && !rNote.HasSforzando)
                            velocity = Math.Max(1, velocity - 4);
                        if (arpeggioVelocityDelta.TryGetValue(rNote, out int arpVelDelta))
                            velocity += arpVelDelta;
                        velocity = Math.Clamp(velocity, 28, 126);

                        // 若為 sfp / fp 突強後立刻轉弱，為弦樂/管樂寫入快速衰減的 CC11 表情自動化點
                        if (rNote.IsSfp && !rNote.IsChord && track.GmProgram is < 0 or > 5)
                        {
                            if (!absoluteExpressionByPart.TryGetValue(partId, out var sfpKeyframes))
                            {
                                sfpKeyframes = new List<(double time, int velocity, bool ramp)>();
                                absoluteExpressionByPart[partId] = sfpKeyframes;
                            }
                            int peakExpVel = Math.Max(102, velocity);
                            int softExpVel = Math.Clamp(rNote.DynamicVelocity, 42, 64);
                            double decayStart = noteStartSec + Math.Min(0.06, noteDurSec * 0.20);
                            double decayEnd = noteStartSec + Math.Min(0.24, noteDurSec * 0.65);
                            sfpKeyframes.Add((noteStartSec, peakExpVel, false));
                            if (decayEnd > decayStart + 0.02)
                            {
                                sfpKeyframes.Add((decayStart, peakExpVel, true));
                                sfpKeyframes.Add((decayEnd, softExpVel, false));
                            }
                        }

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

                        // C1. 處理定速斜線反覆弓音 (Measured Bowed Tremolo，如 K.525 小夜曲 <tremolo type="single">2</tremolo> 十六分音符連弓)
                        if (!rNote.TieStart && rNote.HasTremolo && rNote.TremoloMarks is 1 or 2 && rNote.BeatDuration >= 0.30)
                        {
                            var bowNotes = ExpandMeasuredTremoloNote(rNote, track, noteStartSec, noteDurSec, velocity);
                            if (bowNotes.Count > 1)
                            {
                                allNotes.AddRange(bowNotes);
                                continue;
                            }
                        }

                        // C2. 處理顫音 (Trill)、波音 (Mordent)、迴音 (Turn)
                        if (!rNote.TieStart && (rNote.HasTrill || rNote.HasMordent || rNote.HasTurn) && noteDurSec >= 0.14)
                        {
                            var ornNotes = ExpandOrnamentNote(rNote, track, meta.Fifths, noteStartSec, noteDurSec, velocity, secPerBeat);
                            allNotes.AddRange(ornNotes);
                            continue;
                        }

                        // D. 計算斷奏 / 半連音 / 圓滑線 / 慢板踏板共鳴的實際發聲時長 (AudioDurationSeconds)
                        double audioGateRatio = 0.92;
                        double legatoOverlapSec = 0.0;
                        string artLabel = string.Empty;

                        if (rNote.HasStaccatissimo)
                        {
                            audioGateRatio = 0.30;
                            artLabel = "極斷奏 (Staccatissimo)";
                        }
                        else if (rNote.HasStaccato)
                        {
                            audioGateRatio = 0.48;
                            artLabel = "斷奏 (Staccato)";
                        }
                        else if (rNote.HasDetachedLegato)
                        {
                            audioGateRatio = 0.72;
                            artLabel = "斷連奏 (Detached Legato)";
                        }
                        else if (rNote.HasTenuto || rNote.InSlur)
                        {
                            audioGateRatio = 1.0;
                            legatoOverlapSec = IsStringProgram(track.GmProgram) ? 0.035 : 0.015;
                            if (rNote.HasTenuto)
                                legatoOverlapSec += 0.010;
                        }
                        else if (rNote.TieStart)
                        {
                            audioGateRatio = 0.99;
                        }
                        else if (isSlowSustainedPiano)
                        {
                            // ������鈴�C�O�T�s���P�C���G�ᤩ�X�M�����@��
                            audioGateRatio = track.VoiceFilter == 2 ? 1.45 : 1.12;
                        }

                        int? programOverride = null;
                        if (IsStringProgram(track.GmProgram))
                        {
                            if (rNote.HasPizzicato)
                                programOverride = 45;
                            else if (rNote.HasTremolo && rNote.TremoloMarks >= 3)
                                programOverride = 44;
                        }

                        if (rNote.HasPizzicato)
                            artLabel = "撥奏 (Pizzicato)";
                        else if (rNote.HasTremolo)
                            artLabel = "震音 (Tremolo)";
                        else if (rNote.HasMute)
                            artLabel = "弱音 (Mute)";
                        else if (rNote.HasHarmonic)
                            artLabel = "泛音 (Harmonic)";
                        else if (rNote.IsSfp)
                            artLabel = "突強轉弱 (sfp)";
                        else if (rNote.HasSforzando)
                            artLabel = "突強 (sf)";

                        var noteEvt = new NoteEvent
                        {
                            TrackIndex = trackIdx,
                            TrackId = track.TrackId,
                            MidiChannel = track.MidiChannel,
                            MidiPitch = rNote.MidiPitch,
                            Velocity = velocity,
                            ProgramOverride = programOverride,
                            StartTimeSeconds = noteStartSec,
                            DurationSeconds = noteDurSec,
                            AudioDurationSeconds = Math.Max(0.030, noteDurSec * audioGateRatio + legatoOverlapSec),
                            MeasureNumber = meta.NumericMeasureNumber,
                            Staff = rNote.Staff,
                            Voice = rNote.Voice,
                            IsSfpDecay = rNote.IsSfp,
                            IsArpeggiated = arpeggioOffsetsSec.ContainsKey(rNote),
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

            foreach (var activePedal in activePedalStarts)
            {
                AddPedalInterval(pedalIntervals, activePedal.Key, activePedal.Value, currentTimeSec);
            }

            if (performance.HumanizeAmount > 0)
            {
                ApplyPerformanceHumanization(score, allNotes, performance.HumanizeAmount);
            }

            if (performance.AutoPianoPedal)
            {
                BuildImplicitPianoPedalIntervals(score, allNotes, pedalIntervals, currentTimeSec);
            }

            // 依時間排序並建立卷軸與鍵盤所需的索引資料
            allNotes.Sort((a, b) =>
            {
                int cmp = a.StartTimeSeconds.CompareTo(b.StartTimeSeconds);
                return cmp != 0 ? cmp : a.MidiPitch.CompareTo(b.MidiPitch);
            });

            ApplyPedalSustain(score, allNotes, pedalIntervals);
            BuildExpressionPoints(score, absoluteExpressionByPart);

            // 避免同一聲部、同一音高前後音重疊造成懸音
            PreventSamePitchOverlap(allNotes);

            if (performance.AutoVibrato)
            {
                BuildModulationPoints(score, allNotes);
            }


            // 建立單調遞增的前綴最大結束時間陣列，供視窗裁剪與鍵盤狀態 O(log N) 精準二分定位
            var prefixMax = new double[allNotes.Count];
            double runningMaxEnd = 0.0;
            double maxAudioEnd = 0.0;
            for (int i = 0; i < allNotes.Count; i++)
            {
                double endSec = allNotes[i].EndTimeSeconds;
                if (endSec > runningMaxEnd)
                    runningMaxEnd = endSec;
                if (allNotes[i].AudioEndTimeSeconds > maxAudioEnd)
                    maxAudioEnd = allNotes[i].AudioEndTimeSeconds;
                prefixMax[i] = runningMaxEnd;
            }

            score.Notes = allNotes;
            score.MaxEndTimePrefix = prefixMax;
            score.TotalDurationSeconds = Math.Max(Math.Max(currentTimeSec, runningMaxEnd), maxAudioEnd);

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

        private static int ComputeMusicalVoicingDelta(
            RawNoteItem rNote,
            ScorePartTrack track,
            MeasureMeta meta,
            bool hasDedicatedSoloistPart,
            bool isPianoStringsChamber,
            bool anyPartSoloInMeasure,
            bool isSlowSustainedPiano)
        {
            int delta = 0;

            // 1. 小節拍點自然微律動 (Metric Accent / Micro-Dynamics)
            double beatPos = rNote.BeatStart;
            double nearestIntBeat = Math.Round(beatPos);
            bool onQuarterBeat = Math.Abs(beatPos - nearestIntBeat) < 0.03;
            int beatIdxZeroBased = (int)nearestIntBeat;

            if (onQuarterBeat)
            {
                if (beatIdxZeroBased == 0)
                {
                    delta += 4; // 小節第一拍強拍 (Downbeat)
                }
                else if (meta.Beats == 4 && beatIdxZeroBased == 2)
                {
                    delta += 2; // 4/4 拍第三拍次強拍
                }
                else
                {
                    delta -= 1; // 弱拍
                }
            }
            else
            {
                // 半拍或十六分音符內拍：微幅收斂，消除機械齊平感
                double subPos = beatPos - Math.Floor(beatPos);
                if (Math.Abs(subPos - 0.5) < 0.04)
                    delta -= 2;
                else
                    delta -= 4;
            }

            // 2. 鋼琴左右手聲部層次與分解和弦 (Alberti Bass) 柔化
            if (track.GmProgram is >= 0 and <= 5 && !isSlowSustainedPiano)
            {
                if (rNote.Staff == 1 && (track.VoiceFilter == null || track.VoiceFilter == 1))
                {
                    // 右手主旋律微幅突顯，讓旋律線條清晰浮現於伴奏之上
                    delta += 4;
                }
                else if (rNote.Staff >= 2)
                {
                    // 左手低音譜表：若為快速十六分/八分分解和弦伴奏，托出拍頭低音並柔化內聲部反覆音
                    if (rNote.BeatDuration <= 0.52)
                    {
                        delta += onQuarterBeat ? -3 : -8;
                    }
                    else
                    {
                        delta -= 2;
                    }
                }
            }

            // 3. 小提琴協奏曲（如《四季：春》）與鋼琴室內樂（如《K.496 鋼琴三重奏》）聲部平衡
            if (hasDedicatedSoloistPart)
            {
                bool isPrincipalSoloTrack = track.Name.Contains("主奏") ||
                                           track.OriginalPartName.Contains("principale", StringComparison.OrdinalIgnoreCase) ||
                                           track.OriginalPartName.Contains("solo", StringComparison.OrdinalIgnoreCase);
                if (isPrincipalSoloTrack)
                {
                    delta += 7; // 獨奏小提琴聚焦於樂團前方
                }
                else if (anyPartSoloInMeasure)
                {
                    delta -= 6; // Solo 樂段時，協奏弦樂群自動退讓烘托獨奏
                }
                else
                {
                    delta -= 2;
                }
            }
            else if (isPianoStringsChamber && IsStringProgram(track.GmProgram))
            {
                // 鋼琴與提琴重奏時，微幅增益提琴與大提琴中低音，避免被鋼琴雙手密集音群掩蓋
                delta += track.GmProgram == 42 ? 5 : 4;
            }

            return delta;
        }

        private static List<NoteEvent> ExpandMeasuredTremoloNote(
            RawNoteItem rNote,
            ScorePartTrack track,
            double startSec,
            double totalDurSec,
            int baseVelocity)
        {
            var list = new List<NoteEvent>();
            // 1 條斜線 = 八分音符 (0.5 拍)，2 條斜線 = 十六分音符 (0.25 拍)
            double subBeat = rNote.TremoloMarks == 1 ? 0.5 : 0.25;
            int count = Math.Max(2, (int)Math.Round(rNote.BeatDuration / subBeat));
            double stepSec = totalDurSec / count;
            if (stepSec < 0.035)
                return list;

            for (int i = 0; i < count; i++)
            {
                double subStart = startSec + i * stepSec;
                double subDur = (i == count - 1) ? Math.Max(0.03, (startSec + totalDurSec) - subStart) : stepSec;
                int subVel = i == 0
                    ? baseVelocity
                    : (i % 2 == 0 ? Math.Max(30, baseVelocity - 3) : Math.Max(28, baseVelocity - 6));

                list.Add(new NoteEvent
                {
                    TrackIndex = track.TrackIndex,
                    TrackId = track.TrackId,
                    MidiChannel = track.MidiChannel,
                    MidiPitch = rNote.MidiPitch,
                    Velocity = subVel,
                    StartTimeSeconds = subStart,
                    DurationSeconds = subDur,
                    AudioDurationSeconds = Math.Max(0.028, subDur * 0.88),
                    MeasureNumber = rNote.OriginalMeasureNumber,
                    Staff = rNote.Staff,
                    Voice = rNote.Voice,
                    IsOrnamentSubNote = i > 0,
                    ArticulationLabel = "連弓反覆 (Tremolo)"
                });
            }

            return list;
        }

        private static void BuildImplicitPianoPedalIntervals(
            MusicScore score,
            List<NoteEvent> allNotes,
            List<PedalInterval> pedalIntervals,
            double totalEndSec)
        {
            var explicitParts = pedalIntervals
                .Select(i => i.PartId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string partId in score.Tracks
                         .Where(t => t.GmProgram is >= 0 and <= 5)
                         .Select(t => t.PartId)
                         .Distinct())
            {
                if (explicitParts.Contains(partId))
                    continue;

                var partTrackIndices = score.Tracks
                    .Where(t => t.PartId == partId)
                    .Select(t => t.TrackIndex)
                    .ToHashSet();

                var partNotes = allNotes
                    .Where(n => partTrackIndices.Contains(n.TrackIndex) && !n.IsOrnamentSubNote)
                    .ToList();
                if (partNotes.Count < 2)
                    continue;

                // 優先以左手低音譜表的低音變化作為換踏點；單譜表則以音高集合變化判斷。
                var bassNotes = partNotes.Where(n => n.Staff >= 2).ToList();
                List<double> pedalChangeTimes;
                if (bassNotes.Count >= 2)
                {
                    pedalChangeTimes = bassNotes
                        .Select(n => Math.Round(n.StartTimeSeconds, 2))
                        .Distinct()
                        .OrderBy(t => t)
                        .ToList();
                }
                else
                {
                    pedalChangeTimes = BuildSingleStaffPedalChangeTimes(partNotes);
                }

                if (pedalChangeTimes.Count < 2)
                    continue;

                // 合併過於密集的換踏點，避免分解和弦每個音都重新踩踏板
                var filteredChangeTimes = new List<double>(pedalChangeTimes.Count);
                foreach (double changeTime in pedalChangeTimes)
                {
                    if (filteredChangeTimes.Count == 0 || changeTime - filteredChangeTimes[^1] >= 0.30)
                        filteredChangeTimes.Add(changeTime);
                }
                pedalChangeTimes = filteredChangeTimes;

                if (pedalChangeTimes.Count < 2)
                    continue;

                for (int i = 0; i < pedalChangeTimes.Count; i++)
                {
                    double segStart = pedalChangeTimes[i];
                    double segEnd = (i + 1 < pedalChangeTimes.Count) ? pedalChangeTimes[i + 1] : totalEndSec;
                    // 單次踏板最長 2.5 秒，換踏前保留 30ms 清音間隙
                    double clampedEnd = Math.Min(segEnd - 0.030, segStart + 2.5);
                    if (clampedEnd > segStart + 0.15)
                    {
                        AddPedalInterval(pedalIntervals, partId, segStart, clampedEnd);
                    }
                }
            }
        }

        private static List<double> BuildSingleStaffPedalChangeTimes(List<NoteEvent> notes)
        {
            var times = new List<double>();
            HashSet<int>? previousPitchClasses = null;

            foreach (var group in notes
                         .GroupBy(n => Math.Round(n.StartTimeSeconds, 2))
                         .OrderBy(g => g.Key))
            {
                var pitchClasses = group
                    .Select(n => ((n.MidiPitch % 12) + 12) % 12)
                    .ToHashSet();

                if (previousPitchClasses == null || !pitchClasses.SetEquals(previousPitchClasses))
                {
                    times.Add(group.Key);
                    previousPitchClasses = pitchClasses;
                }
            }

            return times;
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

        private static void AddExpressionKeyframe(
            List<ExpressionKeyframe> keyframes,
            string partId,
            int measureIndex,
            double beatStart,
            int velocity,
            bool rampToNext)
        {
            keyframes.Add(new ExpressionKeyframe
            {
                PartId = partId,
                MeasureIndex = measureIndex,
                BeatStart = beatStart,
                Velocity = velocity,
                RampToNext = rampToNext
            });
        }

        private static void AddPedalInterval(
            List<PedalInterval> intervals,
            string partId,
            double startSeconds,
            double endSeconds)
        {
            if (endSeconds <= startSeconds + 0.001)
                return;

            intervals.Add(new PedalInterval
            {
                PartId = partId,
                StartSeconds = startSeconds,
                EndSeconds = endSeconds
            });
        }

        private static void ApplyPedalSustain(
            MusicScore score,
            List<NoteEvent> notes,
            List<PedalInterval> intervals)
        {
            if (intervals.Count == 0)
                return;

            var intervalsByPart = new Dictionary<string, List<PedalInterval>>();
            foreach (var interval in intervals)
            {
                if (!intervalsByPart.TryGetValue(interval.PartId, out var partIntervals))
                {
                    partIntervals = new List<PedalInterval>();
                    intervalsByPart[interval.PartId] = partIntervals;
                }
                partIntervals.Add(interval);
            }

            foreach (var note in notes)
            {
                if (note.TrackIndex < 0 || note.TrackIndex >= score.Tracks.Count)
                    continue;

                var track = score.Tracks[note.TrackIndex];
                if (track.GmProgram is < 0 or > 5)
                    continue;
                if (!intervalsByPart.TryGetValue(track.PartId, out var partIntervals))
                    continue;

                double noteEnd = note.StartTimeSeconds + note.AudioDurationSeconds;
                double newEnd = noteEnd;

                foreach (var interval in partIntervals)
                {
                    if (interval.StartSeconds <= note.StartTimeSeconds && note.StartTimeSeconds < interval.EndSeconds)
                    {
                        newEnd = Math.Max(newEnd, interval.EndSeconds);
                    }
                    else if (interval.StartSeconds > note.StartTimeSeconds && interval.StartSeconds < noteEnd)
                    {
                        newEnd = Math.Max(newEnd, interval.EndSeconds);
                    }
                }

                if (newEnd > noteEnd)
                    note.AudioDurationSeconds = newEnd - note.StartTimeSeconds;
            }
        }

        private static void BuildExpressionPoints(
            MusicScore score,
            Dictionary<string, List<(double time, int velocity, bool ramp)>> expressionByPart)
        {
            if (expressionByPart.Count == 0)
                return;

            foreach (var track in score.Tracks)
            {
                if (track.GmProgram is >= 0 and <= 5 || track.MidiChannel == 9)
                    continue;
                if (!expressionByPart.TryGetValue(track.PartId, out var rawKeyframes) || rawKeyframes.Count == 0)
                    continue;

                var keyframes = rawKeyframes.OrderBy(k => k.time).ToList();
                var deduped = new List<(double time, int velocity, bool ramp)>();
                foreach (var keyframe in keyframes)
                {
                    if (deduped.Count > 0 && Math.Abs(deduped[^1].time - keyframe.time) < 0.0005)
                        deduped[^1] = keyframe;
                    else
                        deduped.Add(keyframe);
                }

                var points = new List<ExpressionPoint>();
                for (int i = 0; i < deduped.Count; i++)
                {
                    var keyframe = deduped[i];
                    int startValue = MapVelocityToExpression(keyframe.velocity, track.GmProgram);
                    AddExpressionPoint(points, keyframe.time, startValue);

                    if (!keyframe.ramp || i + 1 >= deduped.Count)
                        continue;

                    var next = deduped[i + 1];
                    double duration = next.time - keyframe.time;
                    if (duration <= 0.02)
                        continue;

                    int endValue = MapVelocityToExpression(next.velocity, track.GmProgram);
                    int steps = Math.Max(1, (int)Math.Ceiling(duration / 0.02));
                    for (int step = 1; step < steps; step++)
                    {
                        double t = keyframe.time + duration * step / steps;
                        int value = (int)Math.Round(startValue + (endValue - startValue) * (double)step / steps);
                        AddExpressionPoint(points, t, value);
                    }
                }

                if (points.Count > 0)
                    track.ExpressionPoints.AddRange(points);
            }
        }

        private static void AddExpressionPoint(List<ExpressionPoint> points, double timeSeconds, int value)
        {
            if (points.Count > 0)
            {
                var last = points[^1];
                if (Math.Abs(last.TimeSeconds - timeSeconds) < 0.0005)
                {
                    last.Value = value;
                    return;
                }
                if (last.Value == value && timeSeconds - last.TimeSeconds < 0.010)
                    return;
            }

            points.Add(new ExpressionPoint
            {
                TimeSeconds = timeSeconds,
                Value = value
            });
        }

        private static int MapVelocityToExpression(int velocity, int gmProgram)
        {
            int v = Math.Clamp(velocity, 20, 126);
            double strength = gmProgram is >= 40 and <= 79 ? 0.30 : 0.15;
            double expression = 108.0 + (v - 78) * strength;
            return Math.Clamp((int)Math.Round(expression), 60, 127);
        }

        private static bool IsStringProgram(int gmProgram)
        {
            return gmProgram is >= 40 and <= 45 || gmProgram is >= 48 and <= 51;
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

        private static void ApplyPerformanceHumanization(MusicScore score, List<NoteEvent> notes, int amount)
        {
            double strength = Math.Clamp(amount, 0, 100) / 100.0;
            const double maxTimingMs = 12.0;
            const double maxVelocity = 5.0;
            const double maxRollMs = 6.0;

            foreach (var group in notes.GroupBy(n => (n.TrackIndex, (int)Math.Round(n.StartTimeSeconds * 1000.0), n.Staff)))
            {
                var items = group.ToList();
                bool isPiano = group.Key.TrackIndex >= 0 &&
                               group.Key.TrackIndex < score.Tracks.Count &&
                               score.Tracks[group.Key.TrackIndex].GmProgram is >= 0 and <= 5;

                uint baseHash = StableHash32(group.Key.TrackIndex, group.Key.Item2, items[0].MidiPitch, items[0].MeasureNumber);
                double baseJitterMs = ((baseHash & 0xFFFF) / 32767.5 - 1.0) * maxTimingMs * strength;
                bool canRoll = isPiano && items.Count > 1 && items.All(n => !n.IsOrnamentSubNote && !n.IsArpeggiated);
                var ordered = canRoll ? items.OrderBy(n => n.MidiPitch).ToList() : items;

                for (int i = 0; i < ordered.Count; i++)
                {
                    var note = ordered[i];
                    double jitterMs = note.IsOrnamentSubNote ? 0.0 : baseJitterMs;
                    if (canRoll && i > 0)
                        jitterMs += i * maxRollMs * strength;

                    if (Math.Abs(jitterMs) > 0.0001)
                        note.StartTimeSeconds = Math.Max(0.0, note.StartTimeSeconds + jitterMs / 1000.0);

                    uint velocityHash = StableHash32(note.TrackIndex, note.MidiPitch, (int)Math.Round(note.StartTimeSeconds * 1000.0), note.MeasureNumber);
                    double velocityJitter = ((velocityHash & 0xFFFF) / 32767.5 - 1.0) * maxVelocity * strength;
                    if (note.IsOrnamentSubNote)
                        velocityJitter *= 0.5;

                    note.Velocity = Math.Clamp((int)Math.Round(note.Velocity + velocityJitter), 20, 126);
                }
            }
        }

        private static uint StableHash32(int a, int b, int c, int d)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)a) * 16777619u;
                hash = (hash ^ (uint)b) * 16777619u;
                hash = (hash ^ (uint)c) * 16777619u;
                hash = (hash ^ (uint)d) * 16777619u;
                hash ^= hash >> 13;
                hash *= 0x5bd1e995u;
                hash ^= hash >> 15;
                return hash;
            }
        }

        private static void BuildModulationPoints(MusicScore score, List<NoteEvent> notes)
        {
            var windowsByChannel = new Dictionary<int, List<(double Start, double End, int Value)>>();

            foreach (var track in score.Tracks)
            {
                if (!IsVibratoProgram(track.GmProgram) || track.MidiChannel == 9)
                    continue;

                foreach (var note in notes)
                {
                    if (note.TrackIndex != track.TrackIndex || note.IsOrnamentSubNote || note.AudioDurationSeconds < 0.50)
                        continue;

                    double start = note.StartTimeSeconds;
                    double end = note.StartTimeSeconds + note.AudioDurationSeconds;
                    double delay = Math.Clamp(0.30 - note.AudioDurationSeconds * 0.08, 0.18, 0.30);
                    double vibratoStart = start + delay;
                    double vibratoEnd = end - 0.12;
                    if (vibratoEnd <= vibratoStart + 0.10)
                        continue;

                    int target = IsStringProgram(track.GmProgram)
                        ? Math.Clamp(48 + (note.Velocity - 78) / 6, 30, 64)
                        : Math.Clamp(38 + (note.Velocity - 78) / 7, 26, 58);

                    if (!windowsByChannel.TryGetValue(track.MidiChannel, out var windows))
                    {
                        windows = new List<(double Start, double End, int Value)>();
                        windowsByChannel[track.MidiChannel] = windows;
                    }
                    windows.Add((vibratoStart, vibratoEnd, target));
                }
            }

            foreach (var pair in windowsByChannel)
            {
                var merged = MergeVibratoWindows(pair.Value);
                var points = new List<ExpressionPoint>();
                foreach (var window in merged)
                {
                    double attackEnd = Math.Min(window.Start + 0.20, window.End - 0.06);
                    double releaseStart = Math.Max(attackEnd + 0.01, window.End - 0.12);

                    AddModulationPoint(points, window.Start, 0);
                    AddModulationRamp(points, window.Start, 0, attackEnd, window.Value);
                    AddModulationPoint(points, releaseStart, window.Value);
                    AddModulationRamp(points, releaseStart, window.Value, window.End, 0);
                }

                if (points.Count > 0)
                    score.ModulationPointsByChannel[pair.Key] = points;
            }
        }

        private static bool IsVibratoProgram(int gmProgram)
        {
            return gmProgram is (>= 40 and <= 45) or (>= 48 and <= 51) or (>= 56 and <= 79);
        }

        private static List<(double Start, double End, int Value)> MergeVibratoWindows(
            List<(double Start, double End, int Value)> windows)
        {
            var result = new List<(double Start, double End, int Value)>();
            foreach (var window in windows.OrderBy(w => w.Start))
            {
                if (result.Count > 0 && window.Start <= result[^1].End + 0.05)
                {
                    var last = result[^1];
                    result[^1] = (last.Start, Math.Max(last.End, window.End), Math.Max(last.Value, window.Value));
                }
                else
                {
                    result.Add(window);
                }
            }
            return result;
        }

        private static void AddModulationRamp(List<ExpressionPoint> points, double startTime, int startValue, double endTime, int endValue)
        {
            if (endTime <= startTime + 0.0001)
            {
                AddModulationPoint(points, endTime, endValue);
                return;
            }

            int steps = Math.Max(1, (int)Math.Ceiling((endTime - startTime) / 0.02));
            for (int step = 1; step <= steps; step++)
            {
                double time = startTime + (endTime - startTime) * step / steps;
                int value = (int)Math.Round(startValue + (endValue - startValue) * (double)step / steps);
                AddModulationPoint(points, time, value);
            }
        }

        private static void AddModulationPoint(List<ExpressionPoint> points, double timeSeconds, int value)
        {
            if (points.Count > 0)
            {
                var last = points[^1];
                if (timeSeconds <= last.TimeSeconds + 0.0005)
                {
                    last.Value = value;
                    return;
                }
                if (last.Value == value && timeSeconds - last.TimeSeconds < 0.010)
                    return;
            }

            points.Add(new ExpressionPoint
            {
                TimeSeconds = timeSeconds,
                Value = value
            });
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

        private static int NormalizeVelocity(int rawVelocity, bool isSlowSustainedPiano, bool isPrimaryMelodyVoice, PerformanceOptions performance)
        {
            double normalized = Math.Clamp(rawVelocity, 20, 126) / 126.0;
            normalized = performance.VelocityCurve switch
            {
                VelocityCurveMode.Soft => Math.Pow(normalized, 0.75),
                VelocityCurveMode.Hard => Math.Pow(normalized, 1.35),
                _ => normalized
            };

            double rangeFactor = Math.Clamp(performance.DynamicRangePercent, 50, 150) / 100.0;
            const double center = 0.62;
            normalized = Math.Clamp(center + (normalized - center) * rangeFactor, 0.0, 1.0);

            int v = (int)Math.Round(normalized * 126.0);
            if (isSlowSustainedPiano)
            {
                // 針對月光奏鳴曲 pp (31) 進行溫潤曲線映射，並突顯 Voice 1 主旋律
                v = (int)Math.Round(48 + (v - 25) * 0.65);
                if (isPrimaryMelodyVoice)
                    v += 16;
                else
                    v -= 5;
            }
            return Math.Clamp(v, 28, 126);
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

        private static (XDocument doc, string? title, string? subtitle, string? composer, double? tempoBpm) LoadXDocumentAndMeta(
            string filePath,
            XmlReaderSettings settings)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

            // 讀取前 4 bytes 判斷是否為 ZIP (.mxl) 或 JSON 封裝格式，避免整檔讀入 byte[] 與 string 造成 3 倍記憶體峰值
            Span<byte> header = stackalloc byte[4];
            int headerRead = fs.Read(header);
            fs.Position = 0;

            // 1. 檢查是否為 ZIP / .mxl 壓縮檔 (PK\x03\x04)
            if (headerRead >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
            {
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

                string? targetEntryPath = null;
                var containerEntry = archive.GetEntry("META-INF/container.xml");
                if (containerEntry != null)
                {
                    using var cStream = containerEntry.Open();
                    using var cxr = XmlReader.Create(cStream, settings);
                    var cdoc = XDocument.Load(cxr);
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

                using var entryStream = xmlEntry.Open();
                using var xr = XmlReader.Create(entryStream, settings);
                return (XDocument.Load(xr), null, null, null, null);
            }

            // 2. 略過 UTF-8 BOM 與前導空白，檢查第一個有效字元是否為 '{' (JSON 封裝格式，如 K.496 與 K.545)
            int firstNonWs = -1;
            int b;
            while ((b = fs.ReadByte()) != -1)
            {
                if (b is 0xEF or 0xBB or 0xBF or ' ' or '\t' or '\r' or '\n')
                    continue;
                firstNonWs = b;
                break;
            }
            fs.Position = 0;

            if (firstNonWs == '{')
            {
                using var jdoc = JsonDocument.Parse(fs);
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
                {
                    using var sr = new StringReader(xml);
                    using var xr = XmlReader.Create(sr, settings);
                    return (XDocument.Load(xr), title, subtitle, composer, tempo);
                }
            }

            // 3. 標準未壓縮 .musicxml / .xml：直接將 FileStream 串流交由 XmlReader 解析，零中間大字串配置
            using (var xr = XmlReader.Create(fs, settings))
            {
                return (XDocument.Load(xr), null, null, null, null);
            }
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
