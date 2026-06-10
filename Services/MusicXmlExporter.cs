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
    public sealed class MusicXmlExporter
    {
        public void Export(ScoreProject project, string path)
        {
            var divisions = project.Ppq;
            var measures = BuildMeasures(project);
            var timeChanges = GetNormalizedTimeSignatureChanges(project);
            var keyChanges = GetNormalizedKeySignatureChanges(project);

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", "no"),
                new XElement("score-partwise",
                    new XAttribute("version", "3.1"),
                    new XElement("part-list",
                        new XElement("score-part",
                            new XAttribute("id", "P1"),
                            new XElement("part-name", project.Title)
                        )
                    ),
                    new XElement("part",
                        new XAttribute("id", "P1"),
                        measures.Select((measure, index) =>
                        {
                            var measureElement = new XElement("measure", new XAttribute("number", measure.Number));
                            bool isFirstMeasure = index == 0;
                            bool hasTimeChangeAtStart = measure.StartTick > 0 && timeChanges.Any(c => c.Tick == measure.StartTick);
                            bool hasKeyChangeAtStart = measure.StartTick > 0 && keyChanges.Any(c => c.Tick == measure.StartTick);
                            if (isFirstMeasure || hasTimeChangeAtStart || hasKeyChangeAtStart)
                            {
                                var attributes = new XElement("attributes",
                                    new XElement("divisions", divisions));
                                if (isFirstMeasure || hasKeyChangeAtStart)
                                {
                                    int fifths = GetEffectiveKeySignatureFifthsAtTick(keyChanges, measure.StartTick);
                                    attributes.Add(new XElement("key", new XElement("fifths", fifths)));
                                }

                                if (isFirstMeasure || hasTimeChangeAtStart)
                                {
                                    attributes.Add(new XElement("time",
                                        new XElement("beats", measure.TimeSignature.Numerator),
                                        new XElement("beat-type", measure.TimeSignature.Denominator)));
                                }

                                if (isFirstMeasure)
                                {
                                    attributes.Add(new XElement("staves", 2));
                                    attributes.Add(new XElement("clef",
                                        new XAttribute("number", "1"),
                                        new XElement("sign", "G"),
                                        new XElement("line", 2)
                                    ));
                                    attributes.Add(new XElement("clef",
                                        new XAttribute("number", "2"),
                                        new XElement("sign", "F"),
                                        new XElement("line", 4)
                                    ));
                                }

                                measureElement.Add(attributes);
                            }

                            AppendMeasureDirections(
                                measureElement,
                                project,
                                measure.StartTick,
                                measure.EndTick,
                                measure.TimeSignature,
                                includeTempo: isFirstMeasure);

                            AppendMeasureNotesByVoice(
                                measureElement,
                                measure.Notes,
                                measure.StartTick,
                                measure.DurationTicks,
                                divisions);

                            return measureElement;
                        })
                    )
                )
            );

            AppendLayoutMetadata(doc.Root, project);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            doc.Save(path);
        }

        private sealed class LayoutMetadataPayload
        {
            public int measures_override { get; set; }
            public int auto_measures { get; set; }
            public List<int> system_measures { get; set; } = new();
            public Dictionary<int, float> barline_offsets { get; set; } = new();
        }

        private sealed class MeasureData
        {
            public int Number { get; set; }
            public int StartTick { get; set; }
            public int EndTick { get; set; }
            public TimeSignature TimeSignature { get; set; } = new(4, 4);
            public List<NoteEvent> Notes { get; } = new();
            public int DurationTicks => Math.Max(1, EndTick - StartTick);
        }

        private sealed class VoiceTrackState
        {
            public int Voice { get; set; }
            public int EndTick { get; set; }
            public int LastStartTick { get; set; } = int.MinValue;
            public int LastDurationTicks { get; set; }
        }

        private static void AppendLayoutMetadata(XElement? root, ScoreProject project)
        {
            if (root == null) return;

            bool hasLayout = (project.LayoutSystemMeasureCounts?.Count ?? 0) > 0
                || (project.LayoutBarlineOffsets?.Count ?? 0) > 0
                || project.LayoutMeasuresPerSystemOverride > 0
                || project.LayoutAutoMeasuresPerSystem > 0;
            if (!hasLayout)
            {
                return;
            }

            var payload = new LayoutMetadataPayload
            {
                measures_override = Math.Max(0, project.LayoutMeasuresPerSystemOverride),
                auto_measures = Math.Max(0, project.LayoutAutoMeasuresPerSystem),
                system_measures = project.LayoutSystemMeasureCounts?
                    .Select(v => Math.Max(1, v))
                    .ToList() ?? new List<int>(),
                barline_offsets = project.LayoutBarlineOffsets?
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<int, float>()
            };

            string json = JsonSerializer.Serialize(payload);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var identification = root.Element("identification");
            if (identification == null)
            {
                identification = new XElement("identification");
                root.Add(identification);
            }

            var miscellaneous = identification.Element("miscellaneous");
            if (miscellaneous == null)
            {
                miscellaneous = new XElement("miscellaneous");
                identification.Add(miscellaneous);
            }

            miscellaneous.Elements("miscellaneous-field")
                .Where(x => string.Equals((string?)x.Attribute("name"), "MBXLayout", StringComparison.Ordinal))
                .Remove();

            miscellaneous.Add(new XElement("miscellaneous-field",
                new XAttribute("name", "MBXLayout"),
                encoded));
        }

        private sealed class TimeSignatureChangePoint
        {
            public int Tick { get; init; }
            public TimeSignature Signature { get; init; } = new(4, 4);
        }

        private sealed class KeySignatureChangePoint
        {
            public int Tick { get; init; }
            public int Fifths { get; init; }
        }

        private static List<MeasureData> BuildMeasures(ScoreProject project)
        {
            var measures = BuildMeasureTimeline(project);
            var orderedSourceNotes = project.Notes.OrderBy(n => n.StartTick).ThenBy(n => n.Midi).ToList();
            var orderedNotes = ResolveVoicesForExport(orderedSourceNotes)
                .OrderBy(n => n.StartTick)
                .ThenBy(n => n.Midi)
                .ToList();
            if (measures.Count == 0)
            {
                int fallbackLength = Math.Max(1, GetSanitizedTimeSignature(project.TimeSignature).TicksPerMeasure(Math.Max(1, project.Ppq)));
                measures.Add(new MeasureData
                {
                    Number = 1,
                    StartTick = 0,
                    EndTick = fallbackLength,
                    TimeSignature = GetSanitizedTimeSignature(project.TimeSignature)
                });
            }

            foreach (var note in orderedNotes)
            {
                foreach (var slice in SplitNote(note, measures))
                {
                    int measureIndex = FindMeasureIndexForTick(measures, slice.StartTick);
                    measures[Math.Clamp(measureIndex, 0, measures.Count - 1)].Notes.Add(slice);
                }
            }

            return measures;
        }

        private static List<MeasureData> BuildMeasureTimeline(ScoreProject project)
        {
            int ppq = Math.Max(1, project.Ppq);
            TimeSignature baseSignature = GetSanitizedTimeSignature(project.TimeSignature);
            var timeChanges = GetNormalizedTimeSignatureChanges(project);
            int maxNoteEnd = project.Notes.Count == 0 ? 0 : project.Notes.Max(n => Math.Max(0, n.StartTick) + Math.Max(1, n.DurationTicks));
            int maxExpressionTick = project.ExpressionMarks.Count == 0 ? 0 : project.ExpressionMarks.Max(m => Math.Max(0, m.StartTick));
            int maxTimeChangeTick = timeChanges.Count == 0 ? 0 : timeChanges.Max(c => Math.Max(0, c.Tick));
            int maxKeyChangeTick = project.KeySignatureChanges.Count == 0 ? 0 : project.KeySignatureChanges.Max(c => Math.Max(0, c.Tick));
            int minimumLength = Math.Max(1, baseSignature.TicksPerMeasure(ppq));
            int targetEnd = Math.Max(minimumLength, Math.Max(Math.Max(maxNoteEnd, maxExpressionTick + 1), Math.Max(maxTimeChangeTick + 1, maxKeyChangeTick + 1)));

            var measures = new List<MeasureData>();
            int cursor = 0;
            int number = 1;
            int timeIndex = 0;
            TimeSignature active = timeChanges[0].Signature;

            while (cursor < targetEnd)
            {
                int nextChangeTick = timeIndex + 1 < timeChanges.Count
                    ? Math.Max(cursor + 1, timeChanges[timeIndex + 1].Tick)
                    : int.MaxValue;
                int ticksPerMeasure = Math.Max(1, active.TicksPerMeasure(ppq));
                int measureEnd = cursor + ticksPerMeasure;
                if (nextChangeTick > cursor && nextChangeTick < measureEnd)
                {
                    measureEnd = nextChangeTick;
                }

                if (measureEnd <= cursor)
                {
                    measureEnd = cursor + 1;
                }

                measures.Add(new MeasureData
                {
                    Number = number++,
                    StartTick = cursor,
                    EndTick = measureEnd,
                    TimeSignature = new TimeSignature(active.Numerator, active.Denominator)
                });

                cursor = measureEnd;
                while (timeIndex + 1 < timeChanges.Count && timeChanges[timeIndex + 1].Tick <= cursor)
                {
                    timeIndex++;
                    active = timeChanges[timeIndex].Signature;
                }
            }

            return measures;
        }

        private static int FindMeasureIndexForTick(IReadOnlyList<MeasureData> measures, int tick)
        {
            if (measures.Count == 0)
            {
                return 0;
            }

            int safeTick = Math.Max(0, tick);
            int low = 0;
            int high = measures.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                MeasureData measure = measures[mid];
                if (safeTick < measure.StartTick)
                {
                    high = mid - 1;
                }
                else if (safeTick >= measure.EndTick)
                {
                    low = mid + 1;
                }
                else
                {
                    return mid;
                }
            }

            return Math.Clamp(low, 0, measures.Count - 1);
        }

        private static List<TimeSignatureChangePoint> GetNormalizedTimeSignatureChanges(ScoreProject project)
        {
            var list = new List<TimeSignatureChangePoint>
            {
                new TimeSignatureChangePoint
                {
                    Tick = 0,
                    Signature = GetSanitizedTimeSignature(project.TimeSignature)
                }
            };

            foreach (var change in project.TimeSignatureChanges
                .Where(c => c != null)
                .OrderBy(c => c.Tick))
            {
                int tick = Math.Max(0, change.Tick);
                if (tick <= 0)
                {
                    continue;
                }

                var signature = GetSanitizedTimeSignature(new TimeSignature(change.Numerator, change.Denominator));
                var previous = list[list.Count - 1].Signature;
                if (previous.Numerator == signature.Numerator && previous.Denominator == signature.Denominator)
                {
                    continue;
                }

                list.Add(new TimeSignatureChangePoint
                {
                    Tick = tick,
                    Signature = signature
                });
            }

            return list;
        }

        private static List<KeySignatureChangePoint> GetNormalizedKeySignatureChanges(ScoreProject project)
        {
            var list = new List<KeySignatureChangePoint>
            {
                new KeySignatureChangePoint
                {
                    Tick = 0,
                    Fifths = Math.Clamp(project.KeySignature.Fifths, -7, 7)
                }
            };

            foreach (var change in project.KeySignatureChanges
                .Where(c => c != null)
                .OrderBy(c => c.Tick))
            {
                int tick = Math.Max(0, change.Tick);
                if (tick <= 0)
                {
                    continue;
                }

                int fifths = Math.Clamp(change.Fifths, -7, 7);
                if (list[list.Count - 1].Fifths == fifths)
                {
                    continue;
                }

                list.Add(new KeySignatureChangePoint
                {
                    Tick = tick,
                    Fifths = fifths
                });
            }

            return list;
        }

        private static int GetEffectiveKeySignatureFifthsAtTick(IReadOnlyList<KeySignatureChangePoint> changes, int tick)
        {
            int safeTick = Math.Max(0, tick);
            int active = changes.Count > 0 ? changes[0].Fifths : 0;
            foreach (var change in changes)
            {
                if (change.Tick > safeTick)
                {
                    break;
                }

                active = change.Fifths;
            }

            return Math.Clamp(active, -7, 7);
        }

        private static TimeSignature GetSanitizedTimeSignature(TimeSignature signature)
        {
            int numerator = Math.Clamp(signature.Numerator, 1, 12);
            int denominator = signature.Denominator is 1 or 2 or 4 or 8 or 16 ? signature.Denominator : 4;
            return new TimeSignature(numerator, denominator);
        }

        private static void AppendMeasureDirections(
            XElement measureElement,
            ScoreProject project,
            int measureStartTick,
            int measureEndTick,
            TimeSignature timeSig,
            bool includeTempo)
        {
            if (includeTempo)
            {
                measureElement.Add(BuildTempoDirection(project.Bpm, timeSig));
            }

            foreach (var mark in project.ExpressionMarks
                .Where(m => m.StartTick >= measureStartTick && m.StartTick < measureEndTick)
                .OrderBy(m => m.StartTick))
            {
                measureElement.Add(BuildExpressionDirection(mark, measureStartTick));
            }
        }

        private static XElement BuildTempoDirection(int bpm, TimeSignature timeSig)
        {
            int clampedBpm = Math.Clamp(bpm, 20, 300);
            string beatUnit = timeSig.Denominator switch
            {
                1 => "whole",
                2 => "half",
                8 => "eighth",
                16 => "16th",
                _ => "quarter"
            };

            return new XElement("direction",
                new XElement("direction-type",
                    new XElement("metronome",
                        new XElement("beat-unit", beatUnit),
                        new XElement("per-minute", clampedBpm.ToString(CultureInfo.InvariantCulture)))),
                new XElement("sound",
                    new XAttribute("tempo", clampedBpm.ToString(CultureInfo.InvariantCulture))));
        }

        private static XElement BuildExpressionDirection(ExpressionMark mark, int measureStartTick)
        {
            int offset = Math.Max(0, mark.StartTick - measureStartTick);
            string payload = SerializeExpressionMark(mark);
            return new XElement("direction",
                new XElement("direction-type",
                    new XElement("other-direction", payload)),
                new XElement("offset", offset.ToString(CultureInfo.InvariantCulture)),
                new XElement("voice", "1"),
                new XElement("staff", "1"));
        }

        private static string SerializeExpressionMark(ExpressionMark mark)
        {
            string code = (mark.Code ?? "mf").Trim();
            return string.Join("|",
                "MBX",
                code,
                mark.StaffStepOffset.ToString("0.###", CultureInfo.InvariantCulture),
                mark.SpanBeats.ToString("0.###", CultureInfo.InvariantCulture),
                mark.ShapeHeightSteps.ToString("0.###", CultureInfo.InvariantCulture),
                mark.SlopeSteps.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void AppendMeasureNotesByVoice(
            XElement measureElement,
            List<NoteEvent> measureNotes,
            int measureStartTick,
            int ticksPerMeasure,
            int divisions)
        {
            var normalizedNotes = measureNotes
                .GroupBy(n => Math.Max(1, n.Voice))
                .OrderBy(g => g.Key)
                .ToList();

            if (normalizedNotes.Count == 0)
            {
                return;
            }

            int measureEnd = measureStartTick + Math.Max(1, ticksPerMeasure);

            for (int voiceIndex = 0; voiceIndex < normalizedNotes.Count; voiceIndex++)
            {
                int voice = Math.Max(1, normalizedNotes[voiceIndex].Key);
                if (voiceIndex > 0)
                {
                    measureElement.Add(BuildBackupElement(Math.Max(1, ticksPerMeasure)));
                }

                int cursor = measureStartTick;
                var voiceNotes = normalizedNotes[voiceIndex]
                    .OrderBy(n => n.StartTick)
                    .ThenBy(n => n.Midi)
                    .ToList();
                var beamStatesByNote = BuildBeamStatesForVoice(voiceNotes, divisions);

                foreach (var group in voiceNotes.GroupBy(n => n.StartTick).OrderBy(g => g.Key))
                {
                    int groupStart = Math.Max(measureStartTick, group.Key);
                    if (groupStart > cursor)
                    {
                        measureElement.Add(BuildForwardElement(groupStart - cursor));
                    }

                    var groupNotes = group
                        .OrderBy(n => n.IsRest ? 1 : 0)
                        .ThenBy(n => n.Midi)
                        .ToList();
                    int groupDuration = Math.Max(1, groupNotes.Max(n => Math.Max(1, n.DurationTicks)));
                    var soundingNotes = groupNotes.Where(n => !n.IsRest).ToList();

                    if (soundingNotes.Count > 0)
                    {
                        int onsetBeamGroupId = soundingNotes
                            .Select(n => n.BeamGroupId)
                            .FirstOrDefault(id => id > 0);
                        List<(int Level, string State)>? onsetBeamStates = null;
                        foreach (var candidate in soundingNotes)
                        {
                            if (beamStatesByNote.TryGetValue(candidate, out var candidateStates)
                                && candidateStates != null
                                && candidateStates.Count > 0)
                            {
                                onsetBeamStates = candidateStates;
                                break;
                            }
                        }

                        bool isFirstChordTone = true;
                        foreach (var source in soundingNotes)
                        {
                            var chordNote = new NoteEvent
                            {
                                Midi = source.Midi,
                                StartTick = source.StartTick,
                                DurationTicks = Math.Max(1, source.DurationTicks),
                                BaseDurationTicks = source.BaseDurationTicks,
                                AugmentationDots = source.AugmentationDots,
                                IsRest = false,
                                Voice = Math.Max(1, source.Voice),
                                Accidental = source.Accidental,
                                TieStart = source.TieStart,
                                TieEnd = source.TieEnd,
                                IsStaccato = source.IsStaccato,
                                IsStaccatissimo = source.IsStaccatissimo,
                                IsAccent = source.IsAccent,
                                Ornament = source.Ornament,
                                OrnamentOffsetX = source.OrnamentOffsetX,
                                OrnamentOffsetY = source.OrnamentOffsetY,
                                GraceOrnamentOffsetX = source.GraceOrnamentOffsetX,
                                GraceOrnamentOffsetY = source.GraceOrnamentOffsetY,
                                StemUpOverride = source.StemUpOverride,
                                PreferTrebleStaff = source.PreferTrebleStaff,
                                BeamGroupId = onsetBeamGroupId > 0 ? onsetBeamGroupId : source.BeamGroupId
                            };

                            List<(int Level, string State)>? beamStates = null;
                            if (isFirstChordTone)
                            {
                                if (!beamStatesByNote.TryGetValue(source, out beamStates) || beamStates == null || beamStates.Count == 0)
                                {
                                    beamStates = onsetBeamStates;
                                }
                            }

                            measureElement.Add(BuildNoteElement(chordNote, divisions, isChord: !isFirstChordTone, beamStates));
                            isFirstChordTone = false;
                        }
                    }
                    else
                    {
                        var restSource = groupNotes
                            .OrderByDescending(n => n.DurationTicks)
                            .First();
                        var rest = new NoteEvent
                        {
                            Midi = 60,
                            StartTick = restSource.StartTick,
                            DurationTicks = Math.Max(1, restSource.DurationTicks),
                            BaseDurationTicks = restSource.BaseDurationTicks,
                            AugmentationDots = restSource.AugmentationDots,
                            IsRest = true,
                            Voice = Math.Max(1, restSource.Voice),
                            PreferTrebleStaff = restSource.PreferTrebleStaff
                        };
                        measureElement.Add(BuildNoteElement(rest, divisions, isChord: false, beamStates: null));
                    }

                    cursor = Math.Max(cursor, groupStart + groupDuration);
                }

                if (cursor < measureEnd)
                {
                    measureElement.Add(BuildForwardElement(measureEnd - cursor));
                }
            }
        }

        private static List<NoteEvent> ResolveVoicesForExport(List<NoteEvent> measureNotes)
        {
            var notes = measureNotes
                .OrderBy(n => n.StartTick)
                .ThenBy(n => n.IsRest ? 1 : 0)
                .ThenByDescending(n => n.Midi)
                .ThenBy(n => n.DurationTicks)
                .ToList();

            if (notes.Count == 0)
            {
                return new List<NoteEvent>();
            }

            if (notes.Any(n => n.Voice > 1))
            {
                return notes.Select(CloneNote).ToList();
            }

            var tracks = new List<VoiceTrackState>();
            int nextVoice = 1;
            var resolved = new List<NoteEvent>(notes.Count);

            foreach (var note in notes)
            {
                int start = Math.Max(0, note.StartTick);
                int duration = Math.Max(1, note.DurationTicks);
                VoiceTrackState? chosen = null;

                foreach (var track in tracks.OrderBy(t => t.Voice))
                {
                    bool sameChordSlot = !note.IsRest && track.LastStartTick == start && track.LastDurationTicks == duration;
                    bool trackAvailable = start >= track.EndTick;
                    if (sameChordSlot || trackAvailable)
                    {
                        chosen = track;
                        break;
                    }
                }

                if (chosen == null)
                {
                    chosen = new VoiceTrackState
                    {
                        Voice = nextVoice++
                    };
                    tracks.Add(chosen);
                }

                chosen.LastStartTick = start;
                chosen.LastDurationTicks = duration;
                chosen.EndTick = Math.Max(chosen.EndTick, start + duration);

                var cloned = CloneNote(note);
                cloned.Voice = chosen.Voice;
                resolved.Add(cloned);
            }

            return resolved;
        }

        private static Dictionary<NoteEvent, List<(int Level, string State)>> BuildBeamStatesForVoice(List<NoteEvent> voiceNotes, int divisions)
        {
            var result = new Dictionary<NoteEvent, List<(int Level, string State)>>();
            if (voiceNotes.Count == 0) return result;

            var groupedByBeam = voiceNotes
                .Where(n => !n.IsRest && n.BeamGroupId > 0)
                .GroupBy(n => n.BeamGroupId);

            foreach (var beamGroup in groupedByBeam)
            {
                var onsetGroups = beamGroup
                    .GroupBy(n => n.StartTick)
                    .OrderBy(g => g.Key)
                    .ToList();

                for (int onsetIndex = 0; onsetIndex < onsetGroups.Count; onsetIndex++)
                {
                    var onset = onsetGroups[onsetIndex].ToList();
                    var prevOnset = onsetIndex > 0 ? onsetGroups[onsetIndex - 1].ToList() : null;
                    var nextOnset = onsetIndex + 1 < onsetGroups.Count ? onsetGroups[onsetIndex + 1].ToList() : null;

                    foreach (var note in onset)
                    {
                        int beamCount = GetBeamCount(note, divisions);
                        if (beamCount <= 0) continue;

                        var states = new List<(int Level, string State)>();
                        for (int level = 1; level <= beamCount; level++)
                        {
                            bool hasPrev = prevOnset != null && prevOnset.Any(n => GetBeamCount(n, divisions) >= level);
                            bool hasNext = nextOnset != null && nextOnset.Any(n => GetBeamCount(n, divisions) >= level);

                            string? state = null;
                            if (hasPrev && hasNext) state = "continue";
                            else if (!hasPrev && hasNext) state = "begin";
                            else if (hasPrev && !hasNext) state = "end";

                            if (!string.IsNullOrWhiteSpace(state))
                            {
                                states.Add((level, state));
                            }
                        }

                        if (states.Count > 0)
                        {
                            result[note] = states;
                        }
                    }
                }
            }

            return result;
        }

        private static int GetBeamCount(NoteEvent note, int divisions)
        {
            if (note.IsRest) return 0;

            int safeDivisions = Math.Max(1, divisions);
            int baseDuration = note.BaseDurationTicks > 0 ? note.BaseDurationTicks : note.DurationTicks;
            if (baseDuration <= safeDivisions / 8) return 3;
            if (baseDuration <= safeDivisions / 4) return 2;
            if (baseDuration <= safeDivisions / 2) return 1;
            return 0;
        }

        private static NoteEvent CloneNote(NoteEvent source)
        {
            return new NoteEvent
            {
                Midi = source.Midi,
                StartTick = source.StartTick,
                DurationTicks = source.DurationTicks,
                BaseDurationTicks = source.BaseDurationTicks,
                AugmentationDots = source.AugmentationDots,
                IsRest = source.IsRest,
                Voice = Math.Max(1, source.Voice),
                Accidental = source.Accidental,
                IsStaccato = source.IsStaccato,
                IsStaccatissimo = source.IsStaccatissimo,
                IsAccent = source.IsAccent,
                Ornament = source.Ornament,
                OrnamentOffsetX = source.OrnamentOffsetX,
                OrnamentOffsetY = source.OrnamentOffsetY,
                GraceOrnamentOffsetX = source.GraceOrnamentOffsetX,
                GraceOrnamentOffsetY = source.GraceOrnamentOffsetY,
                TieStart = source.TieStart,
                TieEnd = source.TieEnd,
                StemUpOverride = source.StemUpOverride,
                PreferTrebleStaff = source.PreferTrebleStaff,
                BeamGroupId = source.BeamGroupId
            };
        }

        private static XElement BuildBackupElement(int durationTicks)
        {
            return new XElement("backup", new XElement("duration", Math.Max(1, durationTicks)));
        }

        private static XElement BuildForwardElement(int durationTicks)
        {
            return new XElement("forward", new XElement("duration", Math.Max(1, durationTicks)));
        }

        private static IEnumerable<NoteEvent> SplitNote(NoteEvent note, IReadOnlyList<MeasureData> measures)
        {
            if (measures.Count == 0)
            {
                yield break;
            }

            int remaining = Math.Max(0, note.DurationTicks);
            int start = Math.Max(0, note.StartTick);
            bool isFirst = true;

            while (remaining > 0)
            {
                int measureIndex = FindMeasureIndexForTick(measures, start);
                MeasureData measure = measures[Math.Clamp(measureIndex, 0, measures.Count - 1)];
                int measureRemaining = Math.Max(1, measure.EndTick - start);
                int duration = Math.Min(remaining, measureRemaining);

                var slice = new NoteEvent
                {
                    Midi = note.Midi,
                    StartTick = start,
                    DurationTicks = duration,
                    BaseDurationTicks = note.BaseDurationTicks,
                    AugmentationDots = note.AugmentationDots,
                    IsRest = note.IsRest,
                    Voice = note.Voice,
                    Accidental = note.Accidental,
                    IsStaccato = note.IsStaccato,
                    IsStaccatissimo = note.IsStaccatissimo,
                    IsAccent = note.IsAccent,
                    Ornament = note.Ornament,
                    OrnamentOffsetX = note.OrnamentOffsetX,
                    OrnamentOffsetY = note.OrnamentOffsetY,
                    GraceOrnamentOffsetX = note.GraceOrnamentOffsetX,
                    GraceOrnamentOffsetY = note.GraceOrnamentOffsetY,
                    TieStart = note.TieStart || remaining > duration,
                    TieEnd = note.TieEnd || !isFirst,
                    StemUpOverride = note.StemUpOverride,
                    PreferTrebleStaff = note.PreferTrebleStaff,
                    BeamGroupId = note.BeamGroupId
                };

                yield return slice;

                remaining -= duration;
                start += duration;
                isFirst = false;
            }
        }

        private static XElement BuildRestElement(int durationTicks, int divisions, int voice = 1)
        {
            var rest = new NoteEvent
            {
                DurationTicks = Math.Max(1, durationTicks),
                IsRest = true,
                Voice = Math.Max(1, voice)
            };
            return BuildNoteElement(rest, divisions, isChord: false, beamStates: null);
        }

        private static XElement BuildNoteElement(
            NoteEvent note,
            int divisions,
            bool isChord,
            List<(int Level, string State)>? beamStates)
        {
            var noteElement = new XElement("note");
            if (isChord)
            {
                noteElement.Add(new XElement("chord"));
            }

            if (note.IsRest)
            {
                noteElement.Add(new XElement("rest"));
            }
            else
            {
                var pitch = NoteToPitch(note);
                var pitchElement = new XElement("pitch",
                    new XElement("step", pitch.Step),
                    pitch.Alter != 0 ? new XElement("alter", pitch.Alter) : null,
                    new XElement("octave", pitch.Octave)
                );

                noteElement.Add(pitchElement);
            }

            noteElement.Add(new XElement("duration", note.DurationTicks));
            noteElement.Add(new XElement("voice", note.Voice.ToString(CultureInfo.InvariantCulture)));
            noteElement.Add(new XElement("staff", note.PreferTrebleStaff == false ? "2" : "1"));

            int baseDuration = note.BaseDurationTicks > 0 ? note.BaseDurationTicks : note.DurationTicks;
            int explicitDots = Math.Clamp(note.AugmentationDots, 0, 2);
            var (baseType, _) = DurationToTypeAndDots(baseDuration, divisions);
            var (type, dots) = !string.IsNullOrWhiteSpace(baseType)
                ? (baseType, explicitDots)
                : DurationToTypeAndDots(note.DurationTicks, divisions);
            if (!string.IsNullOrWhiteSpace(type))
            {
                noteElement.Add(new XElement("type", type));
                for (int i = 0; i < dots; i++)
                {
                    noteElement.Add(new XElement("dot"));
                }
            }

            string accidental = note.Accidental switch
            {
                NoteAccidental.DoubleSharp => "double-sharp",
                NoteAccidental.Sharp => "sharp",
                NoteAccidental.Flat => "flat",
                NoteAccidental.DoubleFlat => "flat-flat",
                NoteAccidental.Natural => "natural",
                _ => ""
            };
            if (!string.IsNullOrEmpty(accidental))
            {
                noteElement.Add(new XElement("accidental", accidental));
            }

            if (!note.IsRest && note.StemUpOverride.HasValue)
            {
                noteElement.Add(new XElement("stem", note.StemUpOverride.Value ? "up" : "down"));
            }

            if (!note.IsRest && beamStates != null && beamStates.Count > 0)
            {
                foreach (var (level, state) in beamStates.OrderBy(b => b.Level))
                {
                    noteElement.Add(new XElement(
                        "beam",
                        new XAttribute("number", level.ToString(CultureInfo.InvariantCulture)),
                        state));
                }
            }

            if (note.TieStart)
            {
                noteElement.Add(new XElement("tie", new XAttribute("type", "start")));
            }

            if (note.TieEnd)
            {
                noteElement.Add(new XElement("tie", new XAttribute("type", "stop")));
            }

            XElement? notations = null;
            XElement? articulations = null;
            XElement? ornaments = BuildOrnamentsElement(note);
            if (note.IsStaccato || note.IsStaccatissimo || note.IsAccent)
            {
                articulations = new XElement("articulations");
                if (note.IsStaccatissimo)
                {
                    articulations.Add(new XElement("staccatissimo"));
                }
                else if (note.IsStaccato)
                {
                    articulations.Add(new XElement("staccato"));
                }

                if (note.IsAccent)
                {
                    articulations.Add(new XElement("accent"));
                }
            }

            if (articulations != null)
            {
                notations ??= new XElement("notations");
                notations.Add(articulations);
            }

            if (!note.IsRest && note.TieStart)
            {
                notations ??= new XElement("notations");
                notations.Add(new XElement("tied", new XAttribute("type", "start")));
            }

            if (!note.IsRest && note.TieEnd)
            {
                notations ??= new XElement("notations");
                notations.Add(new XElement("tied", new XAttribute("type", "stop")));
            }

            if (ornaments != null)
            {
                notations ??= new XElement("notations");
                notations.Add(ornaments);
            }

            if (notations != null)
            {
                noteElement.Add(notations);
            }

            return noteElement;
        }

        private static (string Type, int Dots) DurationToTypeAndDots(int durationTicks, int divisions)
        {
            if (divisions <= 0) return ("", 0);

            double quarters = durationTicks / (double)divisions;
            return quarters switch
            {
                >= 3.99 and <= 4.01 => ("whole", 0),
                >= 2.99 and <= 3.01 => ("half", 1),
                >= 1.99 and <= 2.01 => ("half", 0),
                >= 1.49 and <= 1.51 => ("quarter", 1),
                >= 0.99 and <= 1.01 => ("quarter", 0),
                >= 0.74 and <= 0.76 => ("eighth", 1),
                >= 0.49 and <= 0.51 => ("eighth", 0),
                >= 0.37 and <= 0.38 => ("16th", 1),
                >= 0.24 and <= 0.26 => ("16th", 0),
                >= 0.12 and <= 0.13 => ("32nd", 0),
                _ => ("", 0)
            };
        }

        private static XElement? BuildOrnamentsElement(NoteEvent note)
        {
            if (note.IsRest)
            {
                return null;
            }

            var ornaments = new XElement("ornaments");
            switch (note.Ornament)
            {
                case NoteOrnament.Trill:
                    ornaments.Add(new XElement("trill-mark"));
                    break;
                case NoteOrnament.UpperMordent:
                    ornaments.Add(new XElement("inverted-mordent"));
                    break;
                case NoteOrnament.LowerMordent:
                    ornaments.Add(new XElement("mordent"));
                    break;
                case NoteOrnament.Turn:
                    ornaments.Add(new XElement("turn"));
                    break;
                case NoteOrnament.InvertedTurn:
                    ornaments.Add(new XElement("inverted-turn"));
                    break;
                case NoteOrnament.TremoloSingle:
                    ornaments.Add(new XElement("tremolo", new XAttribute("type", "single"), "3"));
                    break;
                case NoteOrnament.TremoloDouble:
                    ornaments.Add(new XElement("tremolo", new XAttribute("type", "single"), "4"));
                    break;
            }

            string? metadata = SerializeOrnamentMetadata(note);
            if (!string.IsNullOrWhiteSpace(metadata))
            {
                ornaments.Add(new XElement("other-ornament", metadata));
            }

            return ornaments.HasElements ? ornaments : null;
        }

        private static string? SerializeOrnamentMetadata(NoteEvent note)
        {
            bool hasOffsets =
                Math.Abs(note.OrnamentOffsetX) > 0.0001f
                || Math.Abs(note.OrnamentOffsetY) > 0.0001f
                || Math.Abs(note.GraceOrnamentOffsetX) > 0.0001f
                || Math.Abs(note.GraceOrnamentOffsetY) > 0.0001f;

            bool needsCustomOrnament = note.Ornament is NoteOrnament.Appoggiatura or NoteOrnament.Acciaccatura;
            if (!hasOffsets && !needsCustomOrnament)
            {
                return null;
            }

            string ornament = note.Ornament.ToString();
            return string.Join("|",
                "MBXORN",
                ornament,
                note.OrnamentOffsetX.ToString("0.###", CultureInfo.InvariantCulture),
                note.OrnamentOffsetY.ToString("0.###", CultureInfo.InvariantCulture),
                note.GraceOrnamentOffsetX.ToString("0.###", CultureInfo.InvariantCulture),
                note.GraceOrnamentOffsetY.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static (string Step, int Alter, int Octave) NoteToPitch(NoteEvent noteEvent)
        {
            int midi = Math.Clamp(noteEvent.Midi, 0, 127);
            int note = ((midi % 12) + 12) % 12;
            int octave = midi / 12 - 1;

            if (noteEvent.Accidental == NoteAccidental.DoubleSharp)
            {
                return NaturalPitchWithAlter(midi - 2, 2);
            }

            if (noteEvent.Accidental == NoteAccidental.DoubleFlat)
            {
                return NaturalPitchWithAlter(midi + 2, -2);
            }

            if (noteEvent.Accidental == NoteAccidental.Flat)
            {
                return note switch
                {
                    1 => ("D", -1, octave),
                    3 => ("E", -1, octave),
                    6 => ("G", -1, octave),
                    8 => ("A", -1, octave),
                    10 => ("B", -1, octave),
                    _ => MidiToDefaultPitch(midi)
                };
            }

            if (noteEvent.Accidental == NoteAccidental.Sharp)
            {
                return note switch
                {
                    1 => ("C", 1, octave),
                    3 => ("D", 1, octave),
                    6 => ("F", 1, octave),
                    8 => ("G", 1, octave),
                    10 => ("A", 1, octave),
                    _ => MidiToDefaultPitch(midi + 1)
                };
            }

            if (noteEvent.Accidental == NoteAccidental.Natural)
            {
                return note switch
                {
                    0 => ("C", 0, octave),
                    2 => ("D", 0, octave),
                    4 => ("E", 0, octave),
                    5 => ("F", 0, octave),
                    7 => ("G", 0, octave),
                    9 => ("A", 0, octave),
                    11 => ("B", 0, octave),
                    _ => MidiToDefaultPitch(midi)
                };
            }

            return MidiToDefaultPitch(midi);
        }

        private static (string Step, int Alter, int Octave) NaturalPitchWithAlter(int naturalMidi, int alter)
        {
            int clamped = Math.Clamp(naturalMidi, 0, 127);
            int note = ((clamped % 12) + 12) % 12;
            int octave = clamped / 12 - 1;

            return note switch
            {
                0 => ("C", alter, octave),
                2 => ("D", alter, octave),
                4 => ("E", alter, octave),
                5 => ("F", alter, octave),
                7 => ("G", alter, octave),
                9 => ("A", alter, octave),
                11 => ("B", alter, octave),
                _ => MidiToDefaultPitch(clamped)
            };
        }

        private static (string Step, int Alter, int Octave) MidiToDefaultPitch(int midi)
        {
            int note = ((midi % 12) + 12) % 12;
            int octave = midi / 12 - 1;

            return note switch
            {
                0 => ("C", 0, octave),
                1 => ("C", 1, octave),
                2 => ("D", 0, octave),
                3 => ("D", 1, octave),
                4 => ("E", 0, octave),
                5 => ("F", 0, octave),
                6 => ("F", 1, octave),
                7 => ("G", 0, octave),
                8 => ("G", 1, octave),
                9 => ("A", 0, octave),
                10 => ("A", 1, octave),
                11 => ("B", 0, octave),
                _ => ("C", 0, octave)
            };
        }
    }
}
