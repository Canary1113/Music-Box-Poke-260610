using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MusicBox.Models;

namespace MusicBox.Services
{
    public sealed class MusicXmlImporter
    {
        private const float DefaultDirectionCenterStaffOffset = 5.2f;
        private sealed class LayoutMetadataPayload
        {
            public int measures_override { get; set; }
            public int auto_measures { get; set; }
            public List<int>? system_measures { get; set; }
            public Dictionary<int, float>? barline_offsets { get; set; }
        }

        public ScoreProject Import(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("MusicXML file not found.", path);

            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) throw new InvalidDataException("Invalid MusicXML.");

            var project = ProjectFactory.CreateDefault();
            project.Title = ExtractTitle(root, Path.GetFileNameWithoutExtension(path));
            project.Notes.Clear();
            project.ExpressionMarks.Clear();
            ApplyLayoutMetadata(root, project);

            var part = root.Elements("part").FirstOrDefault();
            if (part == null) return project;

            int divisions = project.Ppq;
            int bpm = project.Bpm;
            int fifths = project.KeySignature.Fifths;
            int beats = project.TimeSignature.Numerator;
            int beatType = project.TimeSignature.Denominator;
            int baseFifths = fifths;
            int baseBeats = beats;
            int baseBeatType = beatType;
            bool baseKeyInitialized = false;
            bool baseTimeInitialized = false;
            var keyChanges = new List<KeySignatureChange>();
            var timeChanges = new List<TimeSignatureChange>();
            var activeTies = new Dictionary<(int Voice, int Midi), NoteEvent>();
            var activeSlurs = new Dictionary<(int Voice, int Number), (int StartTick, bool PreferTreble)>();
            var activeBeams = new Dictionary<(int Voice, int Level), int>();
            int nextBeamGroupId = 1;

            long currentMeasureStart = 0;
            foreach (var measure in part.Elements("measure"))
            {
                var attributes = measure.Element("attributes");
                if (attributes != null)
                {
                    divisions = ParseInt(attributes.Element("divisions")?.Value, divisions);

                    var key = attributes.Element("key");
                    if (key != null)
                    {
                        int parsedFifths = Math.Clamp(ParseInt(key.Element("fifths")?.Value, fifths), -7, 7);
                        if (!baseKeyInitialized || currentMeasureStart == 0)
                        {
                            baseFifths = parsedFifths;
                            baseKeyInitialized = true;
                        }
                        else if (parsedFifths != fifths)
                        {
                            keyChanges.Add(new KeySignatureChange
                            {
                                Tick = (int)Math.Max(0, currentMeasureStart),
                                Fifths = parsedFifths,
                                Mode = KeyMode.Major
                            });
                        }

                        fifths = parsedFifths;
                    }

                    var time = attributes.Element("time");
                    if (time != null)
                    {
                        int parsedBeats = Math.Clamp(ParseInt(time.Element("beats")?.Value, beats), 1, 12);
                        int candidateBeatType = ParseInt(time.Element("beat-type")?.Value, beatType);
                        int parsedBeatType = candidateBeatType is 1 or 2 or 4 or 8 or 16 ? candidateBeatType : beatType;
                        if (!baseTimeInitialized || currentMeasureStart == 0)
                        {
                            baseBeats = parsedBeats;
                            baseBeatType = parsedBeatType;
                            baseTimeInitialized = true;
                        }
                        else if (parsedBeats != beats || parsedBeatType != beatType)
                        {
                            timeChanges.Add(new TimeSignatureChange
                            {
                                Tick = (int)Math.Max(0, currentMeasureStart),
                                Numerator = parsedBeats,
                                Denominator = parsedBeatType
                            });
                        }

                        beats = parsedBeats;
                        beatType = parsedBeatType;
                    }
                }
                if (!baseKeyInitialized)
                {
                    baseFifths = fifths;
                    baseKeyInitialized = true;
                }

                if (!baseTimeInitialized)
                {
                    baseBeats = beats;
                    baseBeatType = beatType;
                    baseTimeInitialized = true;
                }

                bpm = ParseTempoFromMeasure(measure, bpm);

                int ticksPerBeat = Math.Max(1, new TimeSignature(beats, beatType).TicksPerBeat(divisions));
                int ticksPerMeasure = Math.Max(1, new TimeSignature(beats, beatType).TicksPerMeasure(divisions));
                long measureCursorLocal = 0;
                long chordAnchorLocal = 0;
                int activeVoice = 1;
                var chordBeamGroupByOnset = new Dictionary<(int Voice, long StartTick), int>();

                foreach (var element in measure.Elements())
                {
                    string name = element.Name.LocalName;
                    if (name == "direction")
                    {
                        long directionCursor = currentMeasureStart + Math.Clamp(measureCursorLocal, 0, ticksPerMeasure);
                        if (TryParseExpressionMark(element, currentMeasureStart, directionCursor, out var mark))
                        {
                            project.ExpressionMarks.Add(mark);
                        }

                        continue;
                    }

                    if (name == "backup")
                    {
                        int backup = Math.Max(0, ParseInt(element.Element("duration")?.Value, 0));
                        measureCursorLocal = Math.Max(0, measureCursorLocal - backup);
                        chordAnchorLocal = measureCursorLocal;
                        continue;
                    }

                    if (name == "forward")
                    {
                        int forward = Math.Max(0, ParseInt(element.Element("duration")?.Value, 0));
                        measureCursorLocal = Math.Clamp(measureCursorLocal + forward, 0, ticksPerMeasure);
                        chordAnchorLocal = measureCursorLocal;
                        continue;
                    }

                    if (name != "note")
                    {
                        continue;
                    }

                    bool isGrace = element.Element("grace") != null;
                    int parsedDuration = ParseInt(element.Element("duration")?.Value, isGrace ? 0 : Math.Max(1, divisions));
                    int duration;
                    int cursorAdvance;
                    if (parsedDuration <= 0)
                    {
                        if (isGrace)
                        {
                            // Grace notes should not shift the measure cursor.
                            duration = Math.Max(1, ticksPerBeat / 4);
                            cursorAdvance = 0;
                        }
                        else
                        {
                            duration = Math.Max(1, divisions);
                            cursorAdvance = duration;
                        }
                    }
                    else
                    {
                        duration = Math.Max(1, parsedDuration);
                        cursorAdvance = isGrace ? 0 : duration;
                    }

                    int voice = Math.Max(1, ParseInt(element.Element("voice")?.Value, activeVoice));
                    activeVoice = voice;

                    bool isChordTone = element.Element("chord") != null;
                    bool isRest = element.Element("rest") != null;

                    long startLocalTick = isChordTone ? chordAnchorLocal : measureCursorLocal;
                    startLocalTick = Math.Clamp(startLocalTick, 0, Math.Max(0, ticksPerMeasure - 1));
                    long startTick = currentMeasureStart + startLocalTick;
                    if (!isChordTone)
                    {
                        chordAnchorLocal = startLocalTick;
                    }

                    var note = ParseNote(element, (int)startTick, duration, voice, isRest, fifths);
                    if (isRest)
                    {
                        project.Notes.Add(note);
                    }
                    else
                    {
                        TryCollectSlurMark(
                            element,
                            note,
                            ticksPerBeat,
                            project.ExpressionMarks,
                            activeSlurs);

                        var chordKey = (Math.Max(1, voice), startTick);
                        int resolvedBeamGroup;
                        if (isChordTone && chordBeamGroupByOnset.TryGetValue(chordKey, out int existingChordBeamGroup))
                        {
                            // Keep all tones and anchor note at same onset in one beam group.
                            resolvedBeamGroup = existingChordBeamGroup;
                        }
                        else
                        {
                            resolvedBeamGroup = ResolveBeamGroupId(element, voice, activeBeams, ref nextBeamGroupId);
                            if (resolvedBeamGroup > 0 && !chordBeamGroupByOnset.ContainsKey(chordKey))
                            {
                                chordBeamGroupByOnset[chordKey] = resolvedBeamGroup;
                            }
                        }

                        note.BeamGroupId = resolvedBeamGroup;
                        MergeOrAppendTiedNote(project, note, activeTies);
                    }

                    if (!isChordTone)
                    {
                        long nextCursor = startLocalTick + cursorAdvance;
                        measureCursorLocal = Math.Clamp(nextCursor, 0, ticksPerMeasure);
                    }
                }

                CollectMeasureBarlineMarks(
                    measure,
                    (int)Math.Max(0, currentMeasureStart),
                    ticksPerBeat,
                    ticksPerMeasure,
                    project.ExpressionMarks);

                // Keep each measure as an isolated timeline segment:
                // next measure always starts from its declared measure length.
                currentMeasureStart += ticksPerMeasure;
            }

            project.Ppq = divisions;
            project.Bpm = Math.Clamp(bpm, 20, 300);
            project.KeySignature = new KeySignature(Math.Clamp(baseFifths, -7, 7), KeyMode.Major);
            project.TimeSignature = new TimeSignature(Math.Clamp(baseBeats, 1, 12), baseBeatType is 1 or 2 or 4 or 8 or 16 ? baseBeatType : 4);
            int baseTicksPerMeasure = Math.Max(1, project.TimeSignature.TicksPerMeasure(Math.Max(1, divisions)));
            List<KeySignatureChange> filteredKeyChanges = SuppressShortKeySignatureFlips(
                keyChanges,
                project.KeySignature.Fifths,
                baseTicksPerMeasure);
            List<TimeSignatureChange> filteredTimeChanges = SuppressShortTimeSignatureFlips(
                timeChanges,
                project.TimeSignature.Numerator,
                project.TimeSignature.Denominator,
                baseTicksPerMeasure);

            project.KeySignatureChanges = filteredKeyChanges
                .Where(c => c != null && c.Tick > 0)
                .GroupBy(c => c.Tick)
                .Select(g => g.Last())
                .OrderBy(c => c.Tick)
                .ToList();
            project.TimeSignatureChanges = filteredTimeChanges
                .Where(c => c != null && c.Tick > 0)
                .GroupBy(c => c.Tick)
                .Select(g => g.Last())
                .OrderBy(c => c.Tick)
                .ToList();
            project.UpdatedAt = DateTimeOffset.Now;
            return project;
        }

        private static void ApplyLayoutMetadata(XElement root, ScoreProject project)
        {
            string? encoded = root.Element("identification")
                ?.Element("miscellaneous")
                ?.Elements("miscellaneous-field")
                .FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), "MBXLayout", StringComparison.Ordinal))
                ?.Value;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return;
            }

            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
                var payload = JsonSerializer.Deserialize<LayoutMetadataPayload>(json);
                if (payload == null) return;

                project.LayoutMeasuresPerSystemOverride = Math.Max(0, payload.measures_override);
                project.LayoutAutoMeasuresPerSystem = Math.Max(0, payload.auto_measures);
                project.LayoutSystemMeasureCounts = payload.system_measures?
                    .Select(v => Math.Max(1, v))
                    .ToList() ?? new List<int>();
                project.LayoutBarlineOffsets = payload.barline_offsets?
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<int, float>();
            }
            catch
            {
                project.LayoutSystemMeasureCounts ??= new List<int>();
                project.LayoutBarlineOffsets ??= new Dictionary<int, float>();
            }
        }

        private static bool TryParseExpressionMark(XElement directionElement, long measureStart, long measureCursor, out ExpressionMark mark)
        {
            mark = new ExpressionMark();
            int offset = ParseInt(directionElement.Element("offset")?.Value, 0);
            int startTick = offset > 0
                ? (int)Math.Max(0, measureStart + offset)
                : (int)Math.Max(0, measureCursor);

            XElement? directionType = directionElement.Element("direction-type");
            string? payload =
                directionType?.Element("other-direction")?.Value
                ?? directionType?.Element("words")?.Value;

            if (string.IsNullOrWhiteSpace(payload))
            {
                // Try standard MusicXML directions: dynamics / pedal.
                string? dynamicCode = directionType?
                    .Element("dynamics")?
                    .Elements()
                    .Select(x => x.Name.LocalName.ToLowerInvariant())
                    .FirstOrDefault();
                if (TryMapDynamicCode(dynamicCode, out string mappedDynamic))
                {
                    float dynamicOffset = ResolveDirectionPlacementStaffOffset(
                        directionElement,
                        centerDefault: DefaultDirectionCenterStaffOffset,
                        aboveDefault: -8f,
                        belowDefault: 18f);
                    dynamicOffset = AdjustGrandStaffDirectionOffset(directionElement, dynamicOffset, DefaultDirectionCenterStaffOffset);

                    mark = new ExpressionMark
                    {
                        Code = mappedDynamic,
                        StartTick = startTick,
                        StaffStepOffset = dynamicOffset,
                        SpanBeats = 1.2f,
                        ShapeHeightSteps = 6f,
                        SlopeSteps = 0f
                    };
                    return true;
                }

                XElement? pedal = directionType?.Element("pedal");
                if (pedal != null)
                {
                    string pedalType = (pedal.Attribute("type")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                    if (pedalType == "start" || pedalType == "resume" || pedalType == "change")
                    {
                        mark = new ExpressionMark
                        {
                            Code = "ped",
                            StartTick = startTick,
                            StaffStepOffset = 24f,
                            SpanBeats = 1.2f,
                            ShapeHeightSteps = 6f,
                            SlopeSteps = 0f
                        };
                        return true;
                    }

                    if (pedalType == "stop")
                    {
                        mark = new ExpressionMark
                        {
                            Code = "ped_release",
                            StartTick = startTick,
                            StaffStepOffset = 24f,
                            SpanBeats = 1.2f,
                            ShapeHeightSteps = 6f,
                            SlopeSteps = 0f
                        };
                        return true;
                    }
                }

                XElement? wedge = directionType?.Element("wedge");
                if (wedge != null)
                {
                    string wedgeType = (wedge.Attribute("type")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                    if (wedgeType is "crescendo" or "diminuendo")
                    {
                        float wedgeOffset = ResolveDirectionPlacementStaffOffset(
                            directionElement,
                            centerDefault: DefaultDirectionCenterStaffOffset,
                            aboveDefault: -8f,
                            belowDefault: 14f);
                        wedgeOffset = AdjustGrandStaffDirectionOffset(directionElement, wedgeOffset, DefaultDirectionCenterStaffOffset + 1.2f);

                        mark = new ExpressionMark
                        {
                            Code = wedgeType == "crescendo" ? "cresc" : "dim",
                            StartTick = startTick,
                            StaffStepOffset = wedgeOffset,
                            SpanBeats = 1.8f,
                            ShapeHeightSteps = 4f,
                            SlopeSteps = 0f
                        };
                        return true;
                    }
                }

                XElement? octaveShift = directionType?.Element("octave-shift");
                if (octaveShift != null)
                {
                    string shiftType = (octaveShift.Attribute("type")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                    int size = Math.Abs(ParseInt(octaveShift.Attribute("size")?.Value, 8));
                    if (shiftType is "up" or "down" && (size == 8 || size == 15))
                    {
                        mark = new ExpressionMark
                        {
                            Code = "ottava",
                            StartTick = startTick,
                            StaffStepOffset = shiftType == "up" ? -10f : 24f,
                            SpanBeats = 2.8f,
                            ShapeHeightSteps = 6f,
                            SlopeSteps = 0f
                        };
                        return true;
                    }
                }

                return false;
            }

            string trimmed = payload.Trim();
            if (!trimmed.StartsWith("MBX|", StringComparison.OrdinalIgnoreCase))
            {
                string lower = trimmed.ToLowerInvariant();
                string? code = null;
                if (lower.Contains("rit") || lower.Contains("rall"))
                {
                    code = "rit";
                }
                else if (lower.Contains("cresc"))
                {
                    code = "cresc_text";
                }
                else if (lower.Contains("dim"))
                {
                    code = "dim_text";
                }
                else if (lower.Contains("ped"))
                {
                    code = lower.Contains("*", StringComparison.Ordinal) || lower.Contains("up", StringComparison.Ordinal)
                        ? "ped_release"
                        : "ped";
                }

                if (code == null)
                {
                    return false;
                }

                float textOffset = code.StartsWith("ped", StringComparison.Ordinal)
                    ? 24f
                    : ResolveDirectionPlacementStaffOffset(
                        directionElement,
                        centerDefault: DefaultDirectionCenterStaffOffset,
                        aboveDefault: -8f,
                        belowDefault: 18f);

                mark = new ExpressionMark
                {
                    Code = code,
                    StartTick = startTick,
                    StaffStepOffset = textOffset,
                    SpanBeats = 1.2f,
                    ShapeHeightSteps = 6f,
                    SlopeSteps = 0f
                };
                return true;
            }

            var parts = trimmed.Split('|');
            if (parts.Length < 6)
            {
                return false;
            }

            string mbxCode = string.IsNullOrWhiteSpace(parts[1]) ? "mf" : parts[1].Trim();
            float defaultOffset = mbxCode.StartsWith("ped", StringComparison.OrdinalIgnoreCase)
                ? 24f
                : DefaultDirectionCenterStaffOffset;

            mark = new ExpressionMark
            {
                Code = mbxCode,
                StartTick = startTick,
                StaffStepOffset = ParseFloat(parts[2], defaultOffset),
                SpanBeats = ParseFloat(parts[3], 1.8f),
                ShapeHeightSteps = ParseFloat(parts[4], 6f),
                SlopeSteps = ParseFloat(parts[5], 0f)
            };

            return true;
        }

        private static bool TryMapDynamicCode(string? code, out string mapped)
        {
            mapped = string.Empty;
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            string normalized = code.Trim().ToLowerInvariant();
            mapped = normalized switch
            {
                "p" => "p",
                "pp" => "pp",
                "ppp" => "ppp",
                "mp" => "mp",
                "mf" => "mf",
                "f" => "f",
                "ff" => "ff",
                "fff" => "fff",
                "fp" => "sf",
                "sf" => "sf",
                "sfz" => "sf",
                "sfp" => "sf",
                _ => string.Empty
            };

            return !string.IsNullOrWhiteSpace(mapped);
        }

        private static float ResolveDirectionPlacementStaffOffset(
            XElement directionElement,
            float centerDefault,
            float aboveDefault,
            float belowDefault)
        {
            string? placement = ResolveDirectionPlacement(directionElement);
            return placement switch
            {
                "above" => aboveDefault,
                "below" => belowDefault,
                _ => centerDefault
            };
        }

        private static float AdjustGrandStaffDirectionOffset(
            XElement directionElement,
            float fallbackOffset,
            float middleGapOffset)
        {
            string? placement = ResolveDirectionPlacement(directionElement);
            int staffNumber = ParseInt(directionElement.Element("staff")?.Value, 0);
            return (staffNumber, placement) switch
            {
                (1, "below") => middleGapOffset,
                (2, "above") => middleGapOffset,
                _ => fallbackOffset
            };
        }

        private static string? ResolveDirectionPlacement(XElement directionElement)
        {
            string? placement = NormalizePlacement(directionElement.Attribute("placement")?.Value);
            if (!string.IsNullOrWhiteSpace(placement))
            {
                return placement;
            }

            XElement? directionType = directionElement.Element("direction-type");
            if (directionType == null)
            {
                return null;
            }

            foreach (XElement element in directionType.Elements())
            {
                placement = NormalizePlacement(element.Attribute("placement")?.Value);
                if (!string.IsNullOrWhiteSpace(placement))
                {
                    return placement;
                }

                foreach (XElement child in element.Elements())
                {
                    placement = NormalizePlacement(child.Attribute("placement")?.Value);
                    if (!string.IsNullOrWhiteSpace(placement))
                    {
                        return placement;
                    }
                }
            }

            return null;
        }

        private static string? NormalizePlacement(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized = value.Trim().ToLowerInvariant();
            return normalized is "above" or "below" ? normalized : null;
        }

        private static void TryCollectSlurMark(
            XElement noteElement,
            NoteEvent note,
            int ticksPerBeat,
            List<ExpressionMark> expressionMarks,
            Dictionary<(int Voice, int Number), (int StartTick, bool PreferTreble)> activeSlurs)
        {
            XElement? notations = noteElement.Element("notations");
            if (notations == null)
            {
                return;
            }

            foreach (XElement slur in notations.Elements("slur"))
            {
                string type = (slur.Attribute("type")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                int number = Math.Max(1, ParseInt(slur.Attribute("number")?.Value, 1));
                var key = (Math.Max(1, note.Voice), number);

                if (type == "start")
                {
                    activeSlurs[key] = (Math.Max(0, note.StartTick), note.PreferTrebleStaff != false);
                    continue;
                }

                if (type != "stop")
                {
                    continue;
                }

                if (!activeSlurs.TryGetValue(key, out var start))
                {
                    continue;
                }

                int endTick = Math.Max(start.StartTick + 1, note.StartTick + Math.Max(1, note.DurationTicks / 2));
                int spanTicks = Math.Max(1, endTick - start.StartTick);
                float spanBeats = Math.Clamp(spanTicks / (float)Math.Max(1, ticksPerBeat), 0.3f, 64f);

                expressionMarks.Add(new ExpressionMark
                {
                    Code = "slur",
                    StartTick = start.StartTick,
                    StaffStepOffset = start.PreferTreble ? -8f : 24f,
                    SpanBeats = spanBeats,
                    ShapeHeightSteps = 6f,
                    SlopeSteps = 0f
                });

                activeSlurs.Remove(key);
            }
        }

        private static void CollectMeasureBarlineMarks(
            XElement measureElement,
            int measureStartTick,
            int ticksPerBeat,
            int ticksPerMeasure,
            List<ExpressionMark> expressionMarks)
        {
            foreach (XElement barline in measureElement.Elements("barline"))
            {
                string location = (barline.Attribute("location")?.Value ?? "right").Trim().ToLowerInvariant();
                int tick = location == "left"
                    ? Math.Max(0, measureStartTick)
                    : Math.Max(0, measureStartTick + Math.Max(1, ticksPerMeasure));

                XElement? repeat = barline.Element("repeat");
                if (repeat != null)
                {
                    string direction = (repeat.Attribute("direction")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                    var repeatMark = new ExpressionMark
                    {
                        Code = "score_repeat_barline",
                        StartTick = tick,
                        StaffStepOffset = 0f,
                        SpanBeats = 1.2f,
                        ShapeHeightSteps = direction == "forward" ? -1f : 1f,
                        SlopeSteps = 0f
                    };
                    UpsertExpressionMark(expressionMarks, repeatMark);
                }

                string barStyle = (barline.Element("bar-style")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                if (barStyle is "light-heavy" or "heavy-light")
                {
                    var finalMark = new ExpressionMark
                    {
                        Code = "score_final_barline",
                        StartTick = tick,
                        StaffStepOffset = 0f,
                        SpanBeats = 1.2f,
                        ShapeHeightSteps = 0f,
                        SlopeSteps = 0f
                    };
                    UpsertExpressionMark(expressionMarks, finalMark);
                }

                foreach (XElement ending in barline.Elements("ending"))
                {
                    string endingType = (ending.Attribute("type")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                    if (endingType != "start")
                    {
                        continue;
                    }

                    string number = (ending.Attribute("number")?.Value ?? string.Empty).Trim();
                    if (number.Contains('1'))
                    {
                        var mark1 = new ExpressionMark
                        {
                            Code = "score_ending_1",
                            StartTick = tick,
                            StaffStepOffset = -12f,
                            SpanBeats = 4f,
                            ShapeHeightSteps = 6f,
                            SlopeSteps = 0f
                        };
                        UpsertExpressionMark(expressionMarks, mark1);
                    }

                    if (number.Contains('2'))
                    {
                        var mark2 = new ExpressionMark
                        {
                            Code = "score_ending_2",
                            StartTick = tick,
                            StaffStepOffset = -12f,
                            SpanBeats = 4f,
                            ShapeHeightSteps = 6f,
                            SlopeSteps = 0f
                        };
                        UpsertExpressionMark(expressionMarks, mark2);
                    }
                }
            }
        }

        private static void UpsertExpressionMark(List<ExpressionMark> marks, ExpressionMark candidate)
        {
            int existingIndex = marks.FindIndex(m =>
                m.StartTick == candidate.StartTick &&
                string.Equals(m.Code, candidate.Code, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                marks[existingIndex] = candidate;
            }
            else
            {
                marks.Add(candidate);
            }
        }

        private static List<KeySignatureChange> SuppressShortKeySignatureFlips(
            List<KeySignatureChange> source,
            int baseFifths,
            int ticksPerMeasure)
        {
            var ordered = source
                .Where(c => c != null && c.Tick > 0)
                .OrderBy(c => c.Tick)
                .Select(c => new KeySignatureChange
                {
                    Tick = c.Tick,
                    Fifths = Math.Clamp(c.Fifths, -7, 7),
                    Mode = c.Mode
                })
                .ToList();

            if (ordered.Count < 2)
            {
                return ordered;
            }

            int minSpan = Math.Max(1, ticksPerMeasure / 6);
            int active = baseFifths;
            var result = new List<KeySignatureChange>();
            int i = 0;
            while (i < ordered.Count)
            {
                KeySignatureChange current = ordered[i];
                int newKey = current.Fifths;
                int nextTick = i + 1 < ordered.Count ? ordered[i + 1].Tick : int.MaxValue;
                int span = nextTick == int.MaxValue ? int.MaxValue : Math.Max(0, nextTick - current.Tick);

                bool isShortFlip = i + 1 < ordered.Count
                                   && ordered[i + 1].Fifths == active
                                   && span < minSpan;
                if (isShortFlip)
                {
                    i += 2;
                    continue;
                }

                if (newKey != active)
                {
                    result.Add(current);
                    active = newKey;
                }

                i++;
            }

            return result;
        }

        private static List<TimeSignatureChange> SuppressShortTimeSignatureFlips(
            List<TimeSignatureChange> source,
            int baseNumerator,
            int baseDenominator,
            int ticksPerMeasure)
        {
            var ordered = source
                .Where(c => c != null && c.Tick > 0)
                .OrderBy(c => c.Tick)
                .Select(c => new TimeSignatureChange
                {
                    Tick = c.Tick,
                    Numerator = Math.Clamp(c.Numerator, 1, 12),
                    Denominator = c.Denominator is 1 or 2 or 4 or 8 or 16 ? c.Denominator : 4
                })
                .ToList();

            if (ordered.Count < 2)
            {
                return ordered;
            }

            int minSpan = Math.Max(1, ticksPerMeasure / 6);
            int activeNum = baseNumerator;
            int activeDen = baseDenominator;
            var result = new List<TimeSignatureChange>();

            int i = 0;
            while (i < ordered.Count)
            {
                TimeSignatureChange current = ordered[i];
                int nextTick = i + 1 < ordered.Count ? ordered[i + 1].Tick : int.MaxValue;
                int span = nextTick == int.MaxValue ? int.MaxValue : Math.Max(0, nextTick - current.Tick);

                bool revertsImmediately = i + 1 < ordered.Count
                                          && ordered[i + 1].Numerator == activeNum
                                          && ordered[i + 1].Denominator == activeDen
                                          && span < minSpan;
                if (revertsImmediately)
                {
                    i += 2;
                    continue;
                }

                if (current.Numerator != activeNum || current.Denominator != activeDen)
                {
                    result.Add(current);
                    activeNum = current.Numerator;
                    activeDen = current.Denominator;
                }

                i++;
            }

            return result;
        }

        private static void MergeOrAppendTiedNote(
            ScoreProject project,
            NoteEvent parsedNote,
            Dictionary<(int Voice, int Midi), NoteEvent> activeTies)
        {
            var key = (Math.Max(1, parsedNote.Voice), Math.Clamp(parsedNote.Midi, 0, 127));
            bool hasTieStop = parsedNote.TieEnd;
            bool hasTieStart = parsedNote.TieStart;

            if (hasTieStop && TryResolveActiveTie(activeTies, parsedNote, key, out var matchedKey, out var ongoing))
            {
                ongoing.DurationTicks = Math.Max(1, ongoing.DurationTicks) + Math.Max(1, parsedNote.DurationTicks);
                ongoing.TieStart = false;
                ongoing.TieEnd = false;

                if (!hasTieStart)
                {
                    activeTies.Remove(matchedKey);
                }
                else
                {
                    activeTies.Remove(matchedKey);
                    activeTies[key] = ongoing;
                }

                return;
            }

            parsedNote.TieStart = false;
            parsedNote.TieEnd = false;
            project.Notes.Add(parsedNote);

            if (hasTieStart)
            {
                activeTies[key] = parsedNote;
            }
        }

        private static bool TryResolveActiveTie(
            Dictionary<(int Voice, int Midi), NoteEvent> activeTies,
            NoteEvent parsedNote,
            (int Voice, int Midi) preferredKey,
            out (int Voice, int Midi) matchedKey,
            out NoteEvent matchedNote)
        {
            if (activeTies.TryGetValue(preferredKey, out matchedNote!))
            {
                matchedKey = preferredKey;
                return true;
            }

            int targetMidi = Math.Clamp(parsedNote.Midi, 0, 127);
            bool targetTreble = parsedNote.PreferTrebleStaff != false;
            int targetStart = Math.Max(0, parsedNote.StartTick);

            var candidates = activeTies
                .Where(kvp => kvp.Key.Midi == targetMidi)
                .Select(kvp =>
                {
                    int endTick = Math.Max(0, kvp.Value.StartTick) + Math.Max(1, kvp.Value.DurationTicks);
                    int distance = Math.Abs(endTick - targetStart);
                    bool sameStaff = (kvp.Value.PreferTrebleStaff != false) == targetTreble;
                    return new
                    {
                        kvp.Key,
                        kvp.Value,
                        distance,
                        sameStaff
                    };
                })
                .OrderByDescending(c => c.sameStaff)
                .ThenBy(c => c.distance)
                .ThenByDescending(c => c.Value.StartTick)
                .ToList();

            if (candidates.Count > 0)
            {
                matchedKey = candidates[0].Key;
                matchedNote = candidates[0].Value;
                return true;
            }

            matchedKey = default;
            matchedNote = null!;
            return false;
        }

        private static int ResolveBeamGroupId(
            XElement noteElement,
            int voice,
            Dictionary<(int Voice, int Level), int> activeBeams,
            ref int nextBeamGroupId)
        {
            var beamStates = ParseBeamStates(noteElement);
            if (beamStates.Count == 0)
            {
                return 0;
            }

            int noteGroupId = 0;
            foreach (var (level, state) in beamStates.OrderBy(b => b.Level))
            {
                var key = (Math.Max(1, voice), Math.Max(1, level));
                string normalized = state.Trim().ToLowerInvariant();

                if (normalized == "begin")
                {
                    if (noteGroupId == 0)
                    {
                        noteGroupId = nextBeamGroupId++;
                    }

                    activeBeams[key] = noteGroupId;
                    continue;
                }

                if (normalized == "continue")
                {
                    if (activeBeams.TryGetValue(key, out int activeId))
                    {
                        if (noteGroupId == 0)
                        {
                            noteGroupId = activeId;
                        }
                    }
                    else
                    {
                        if (noteGroupId == 0)
                        {
                            noteGroupId = nextBeamGroupId++;
                        }

                        activeBeams[key] = noteGroupId;
                    }

                    if (noteGroupId != 0)
                    {
                        activeBeams[key] = noteGroupId;
                    }

                    continue;
                }

                if (normalized == "end")
                {
                    if (activeBeams.TryGetValue(key, out int activeId))
                    {
                        if (noteGroupId == 0)
                        {
                            noteGroupId = activeId;
                        }

                        activeBeams.Remove(key);
                    }
                    else if (noteGroupId == 0)
                    {
                        noteGroupId = nextBeamGroupId++;
                    }

                    continue;
                }

                if (normalized is "forward hook" or "backward hook")
                {
                    if (activeBeams.TryGetValue(key, out int activeId))
                    {
                        if (noteGroupId == 0)
                        {
                            noteGroupId = activeId;
                        }
                    }
                    else if (noteGroupId == 0)
                    {
                        noteGroupId = nextBeamGroupId++;
                    }
                }
            }

            return noteGroupId;
        }

        private static List<(int Level, string State)> ParseBeamStates(XElement noteElement)
        {
            var states = new List<(int Level, string State)>();
            foreach (var beam in noteElement.Elements("beam"))
            {
                int level = ParseInt(beam.Attribute("number")?.Value, 1);
                string state = (beam.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(state))
                {
                    continue;
                }

                states.Add((Math.Max(1, level), state));
            }

            return states;
        }

        private static string ExtractTitle(XElement root, string fallback)
        {
            string? title =
                root.Element("work")?.Element("work-title")?.Value ??
                root.Element("movement-title")?.Value ??
                root.Element("part-list")?.Element("score-part")?.Element("part-name")?.Value;

            if (string.IsNullOrWhiteSpace(title)) return string.IsNullOrWhiteSpace(fallback) ? "Untitled" : fallback;
            return title.Trim();
        }

        private static int ParseTempoFromMeasure(XElement measure, int fallback)
        {
            foreach (var direction in measure.Elements("direction"))
            {
                var metronome = direction.Element("direction-type")?.Element("metronome");
                if (metronome != null)
                {
                    int perMinute = ParseInt(metronome.Element("per-minute")?.Value, fallback);
                    if (perMinute > 0) return perMinute;
                }

                var sound = direction.Element("sound");
                if (sound != null)
                {
                    string? tempoText = sound.Attribute("tempo")?.Value;
                    if (double.TryParse(tempoText, NumberStyles.Float, CultureInfo.InvariantCulture, out double tempo))
                    {
                        int rounded = (int)Math.Round(tempo);
                        if (rounded > 0) return rounded;
                    }
                }
            }

            return fallback;
        }

        private static NoteEvent ParseNote(XElement noteElement, int startTick, int duration, int voice, bool isRest, int keyFifths)
        {
            int midi = 60;
            NoteAccidental accidental = NoteAccidental.None;
            bool? stemUpOverride = null;
            bool preferTrebleStaff = ParseInt(noteElement.Element("staff")?.Value, 1) != 2;

            if (!isRest)
            {
                var pitch = noteElement.Element("pitch");
                if (pitch != null)
                {
                    string step = (pitch.Element("step")?.Value ?? "C").Trim().ToUpperInvariant();
                    int octave = ParseInt(pitch.Element("octave")?.Value, 4);
                    int alter = ParseInt(pitch.Element("alter")?.Value, 0);
                    string accidentalText = (noteElement.Element("accidental")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                    int naturalMidi = PitchToMidi(step, octave, 0);
                    accidental = ResolveImportedAccidental(step, alter, keyFifths, accidentalText);
                    midi = naturalMidi + GetStoredMidiOffset(accidental);
                }

                string stemText = (noteElement.Element("stem")?.Value ?? string.Empty).Trim().ToLowerInvariant();
                if (stemText == "up")
                {
                    stemUpOverride = true;
                }
                else if (stemText == "down")
                {
                    stemUpOverride = false;
                }
            }

            int dotCount = noteElement.Elements("dot").Count();
            int baseDuration = InferBaseDuration(duration, dotCount);
            bool tieStart = !isRest && HasTieType(noteElement, "start");
            bool tieEnd = !isRest && HasTieType(noteElement, "stop");

            var note = new NoteEvent
            {
                Midi = Math.Clamp(midi, 24, 108),
                StartTick = Math.Max(0, startTick),
                DurationTicks = duration,
                BaseDurationTicks = baseDuration,
                AugmentationDots = Math.Clamp(dotCount, 0, 2),
                IsRest = isRest,
                Voice = Math.Max(1, voice),
                Accidental = accidental,
                TieStart = tieStart,
                TieEnd = tieEnd,
                StemUpOverride = stemUpOverride,
                PreferTrebleStaff = preferTrebleStaff
            };

            var notations = !isRest ? noteElement.Element("notations") : null;
            var articulations = notations?.Element("articulations");
            if (articulations != null)
            {
                note.IsStaccatissimo = articulations.Element("staccatissimo") != null;
                note.IsStaccato = !note.IsStaccatissimo && articulations.Element("staccato") != null;
                note.IsAccent = articulations.Element("accent") != null;
            }

            var ornaments = notations?.Element("ornaments");
            if (ornaments != null)
            {
                if (TryParseStandardOrnament(ornaments, out var standardOrnament))
                {
                    note.Ornament = standardOrnament;
                }

                if (TryParseOrnamentMetadata(ornaments, out var customOrnament, out float ox, out float oy, out float gox, out float goy))
                {
                    if (customOrnament != NoteOrnament.None)
                    {
                        note.Ornament = customOrnament;
                    }

                    note.OrnamentOffsetX = ox;
                    note.OrnamentOffsetY = oy;
                    note.GraceOrnamentOffsetX = gox;
                    note.GraceOrnamentOffsetY = goy;
                }
            }

            return note;
        }

        private static bool HasTieType(XElement noteElement, string type)
        {
            bool tieElementHit = noteElement.Elements("tie")
                .Any(t => string.Equals(t.Attribute("type")?.Value, type, StringComparison.OrdinalIgnoreCase));
            if (tieElementHit) return true;

            return noteElement.Element("notations")?.Elements("tied")
                .Any(t => string.Equals(t.Attribute("type")?.Value, type, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static bool TryParseStandardOrnament(XElement ornaments, out NoteOrnament ornament)
        {
            ornament = NoteOrnament.None;
            if (ornaments.Element("trill-mark") != null)
            {
                ornament = NoteOrnament.Trill;
                return true;
            }

            if (ornaments.Element("inverted-mordent") != null)
            {
                ornament = NoteOrnament.UpperMordent;
                return true;
            }

            if (ornaments.Element("mordent") != null)
            {
                ornament = NoteOrnament.LowerMordent;
                return true;
            }

            if (ornaments.Element("turn") != null)
            {
                ornament = NoteOrnament.Turn;
                return true;
            }

            if (ornaments.Element("inverted-turn") != null)
            {
                ornament = NoteOrnament.InvertedTurn;
                return true;
            }

            var tremolo = ornaments.Element("tremolo");
            if (tremolo != null)
            {
                int level = ParseInt(tremolo.Value, 3);
                ornament = level >= 4 ? NoteOrnament.TremoloDouble : NoteOrnament.TremoloSingle;
                return true;
            }

            return false;
        }

        private static bool TryParseOrnamentMetadata(
            XElement ornaments,
            out NoteOrnament ornament,
            out float ornamentOffsetX,
            out float ornamentOffsetY,
            out float graceOrnamentOffsetX,
            out float graceOrnamentOffsetY)
        {
            ornament = NoteOrnament.None;
            ornamentOffsetX = 0f;
            ornamentOffsetY = 0f;
            graceOrnamentOffsetX = 0f;
            graceOrnamentOffsetY = 0f;

            string? payload = ornaments.Elements("other-ornament")
                .Select(x => (x.Value ?? string.Empty).Trim())
                .FirstOrDefault(x => x.StartsWith("MBXORN|", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            var parts = payload.Split('|');
            if (parts.Length < 6)
            {
                return false;
            }

            if (!Enum.TryParse(parts[1], ignoreCase: true, out ornament))
            {
                ornament = NoteOrnament.None;
            }

            ornamentOffsetX = ParseFloat(parts[2], 0f);
            ornamentOffsetY = ParseFloat(parts[3], 0f);
            graceOrnamentOffsetX = ParseFloat(parts[4], 0f);
            graceOrnamentOffsetY = ParseFloat(parts[5], 0f);
            return true;
        }

        private static int InferBaseDuration(int durationTicks, int dotCount)
        {
            if (dotCount <= 0) return Math.Max(1, durationTicks);

            double factor = dotCount switch
            {
                1 => 1.5,
                2 => 1.75,
                _ => 1.5
            };

            return Math.Max(1, (int)Math.Round(durationTicks / factor));
        }

        private static NoteAccidental ResolveImportedAccidental(string step, int alter, int keyFifths, string accidentalText)
        {
            if (accidentalText == "natural")
            {
                return NoteAccidental.Natural;
            }

            if (accidentalText == "double-sharp" || accidentalText == "sharp-sharp")
            {
                return NoteAccidental.DoubleSharp;
            }

            if (accidentalText == "flat-flat" || accidentalText == "double-flat")
            {
                return NoteAccidental.DoubleFlat;
            }

            int keyOffset = GetKeySignatureAlterForStep(step, keyFifths);

            if (accidentalText == "sharp")
            {
                return alter == keyOffset ? NoteAccidental.None : NoteAccidental.Sharp;
            }

            if (accidentalText == "flat")
            {
                return alter == keyOffset ? NoteAccidental.None : NoteAccidental.Flat;
            }

            if (alter == keyOffset)
            {
                return NoteAccidental.None;
            }

            if (alter == 0 && keyOffset != 0)
            {
                return NoteAccidental.Natural;
            }

            int alterDelta = alter - keyOffset;
            if (alterDelta >= 2)
            {
                return NoteAccidental.DoubleSharp;
            }

            if (alterDelta <= -2)
            {
                return NoteAccidental.DoubleFlat;
            }

            return alterDelta > 0 ? NoteAccidental.Sharp : NoteAccidental.Flat;
        }

        private static int GetStoredMidiOffset(NoteAccidental accidental)
        {
            return accidental switch
            {
                NoteAccidental.DoubleSharp => 2,
                NoteAccidental.Sharp => 1,
                NoteAccidental.Flat => -1,
                NoteAccidental.DoubleFlat => -2,
                _ => 0
            };
        }

        private static int GetKeySignatureAlterForStep(string step, int fifths)
        {
            string normalized = (step ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(normalized) || fifths == 0)
            {
                return 0;
            }

            if (fifths > 0)
            {
                string[] sharpOrder = { "F", "C", "G", "D", "A", "E", "B" };
                return sharpOrder.Take(Math.Min(fifths, sharpOrder.Length)).Contains(normalized) ? 1 : 0;
            }

            string[] flatOrder = { "B", "E", "A", "D", "G", "C", "F" };
            return flatOrder.Take(Math.Min(Math.Abs(fifths), flatOrder.Length)).Contains(normalized) ? -1 : 0;
        }

        private static int PitchToMidi(string step, int octave, int alter)
        {
            int stepSemitone = step switch
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

            return (octave + 1) * 12 + stepSemitone + alter;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }

        private static float ParseFloat(string? value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : fallback;
        }
    }
}
