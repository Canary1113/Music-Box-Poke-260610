using System;
using System.Collections.Generic;
using System.Linq;
using MusicBox.Models;

namespace MusicBox.Services
{
    public sealed class OmrPostProcessReport
    {
        public int SnappedTimeChanges { get; set; }
        public int SnappedKeyChanges { get; set; }
        public int AddedMeasureRests { get; set; }
        public int AddedFinalBarline { get; set; }
        public int AddedSlurs { get; set; }
        public bool HasChanges =>
            SnappedTimeChanges > 0
            || SnappedKeyChanges > 0
            || AddedMeasureRests > 0
            || AddedFinalBarline > 0
            || AddedSlurs > 0;
    }

    public sealed class OmrImportPostProcessor
    {
        private const string ScoreMarkFinalBarline = "score_final_barline";
        private const string ScoreMarkSlur = "slur";

        public OmrPostProcessReport Apply(ScoreProject project)
        {
            var report = new OmrPostProcessReport();
            if (project == null)
            {
                return report;
            }

            project.Notes ??= new List<NoteEvent>();
            project.ExpressionMarks ??= new List<ExpressionMark>();
            project.TimeSignatureChanges ??= new List<TimeSignatureChange>();
            project.KeySignatureChanges ??= new List<KeySignatureChange>();

            NormalizeNotes(project.Notes);
            int maxTick = GetMaxTick(project);
            int ppq = Math.Max(1, project.Ppq);

            List<TimeSignatureChange> normalizedTime = GetNormalizedTimeChanges(project);
            List<KeySignatureChange> normalizedKey = GetNormalizedKeyChanges(project);
            List<int> measureBoundaries = BuildMeasureBoundaries(ppq, normalizedTime, maxTick);

            report.SnappedTimeChanges = SnapTimeChangesToBoundaries(project, normalizedTime, measureBoundaries);
            report.SnappedKeyChanges = SnapKeyChangesToBoundaries(project, normalizedKey, measureBoundaries);
            PruneNoisySignatureChanges(project, measureBoundaries);
            report.AddedMeasureRests = AddWholeMeasureRests(project, measureBoundaries);
            report.AddedFinalBarline = EnsureFinalBarline(project, measureBoundaries, maxTick) ? 1 : 0;
            report.AddedSlurs = AddBeamGroupSlurs(project, ppq);

            project.ExpressionMarks = project.ExpressionMarks
                .GroupBy(m => (Code: NormalizeCode(m.Code), Tick: Math.Max(0, m.StartTick)))
                .Select(g => g.First())
                .OrderBy(m => Math.Max(0, m.StartTick))
                .ToList();
            project.Notes = project.Notes
                .OrderBy(n => Math.Max(0, n.StartTick))
                .ThenByDescending(n => n.Midi)
                .ToList();
            project.UpdatedAt = DateTimeOffset.Now;
            return report;
        }

        private static void NormalizeNotes(List<NoteEvent> notes)
        {
            foreach (NoteEvent note in notes)
            {
                note.StartTick = Math.Max(0, note.StartTick);
                note.DurationTicks = Math.Max(1, note.DurationTicks);
                note.BaseDurationTicks = Math.Max(1, note.BaseDurationTicks > 0 ? note.BaseDurationTicks : note.DurationTicks);
                note.Voice = Math.Max(1, note.Voice);
            }

            var deduped = notes
                .GroupBy(n => (Tick: n.StartTick, n.Midi, n.IsRest, Voice: Math.Max(1, n.Voice)))
                .Select(g => g
                    .OrderByDescending(n => Math.Max(1, n.DurationTicks))
                    .ThenByDescending(n => n.BeamGroupId)
                    .First())
                .OrderBy(n => n.StartTick)
                .ThenByDescending(n => n.Midi)
                .ToList();

            notes.Clear();
            notes.AddRange(deduped);
        }

        private static int GetMaxTick(ScoreProject project)
        {
            int noteMax = project.Notes.Count == 0
                ? 0
                : project.Notes.Max(n => Math.Max(0, n.StartTick) + Math.Max(1, n.DurationTicks));
            int markMax = project.ExpressionMarks.Count == 0
                ? 0
                : project.ExpressionMarks.Max(m => Math.Max(0, m.StartTick));
            int timeMax = project.TimeSignatureChanges.Count == 0
                ? 0
                : project.TimeSignatureChanges.Max(c => Math.Max(0, c.Tick));
            int keyMax = project.KeySignatureChanges.Count == 0
                ? 0
                : project.KeySignatureChanges.Max(c => Math.Max(0, c.Tick));
            return Math.Max(Math.Max(noteMax, markMax), Math.Max(timeMax, keyMax));
        }

        private static List<TimeSignatureChange> GetNormalizedTimeChanges(ScoreProject project)
        {
            var list = new List<TimeSignatureChange>
            {
                new TimeSignatureChange
                {
                    Tick = 0,
                    Numerator = Math.Clamp(project.TimeSignature.Numerator, 1, 12),
                    Denominator = project.TimeSignature.Denominator is 1 or 2 or 4 or 8 or 16
                        ? project.TimeSignature.Denominator
                        : 4
                }
            };

            foreach (TimeSignatureChange change in project.TimeSignatureChanges)
            {
                if (change == null)
                {
                    continue;
                }

                int tick = Math.Max(0, change.Tick);
                int numerator = Math.Clamp(change.Numerator, 1, 12);
                int denominator = change.Denominator is 1 or 2 or 4 or 8 or 16 ? change.Denominator : 4;
                if (tick == 0)
                {
                    list[0].Numerator = numerator;
                    list[0].Denominator = denominator;
                }
                else
                {
                    list.Add(new TimeSignatureChange
                    {
                        Tick = tick,
                        Numerator = numerator,
                        Denominator = denominator
                    });
                }
            }

            return list
                .OrderBy(c => c.Tick)
                .ThenBy(c => c.Numerator)
                .GroupBy(c => c.Tick)
                .Select(g => g.Last())
                .ToList();
        }

        private static List<KeySignatureChange> GetNormalizedKeyChanges(ScoreProject project)
        {
            var list = new List<KeySignatureChange>
            {
                new KeySignatureChange
                {
                    Tick = 0,
                    Fifths = Math.Clamp(project.KeySignature.Fifths, -7, 7),
                    Mode = project.KeySignature.Mode
                }
            };

            foreach (KeySignatureChange change in project.KeySignatureChanges)
            {
                if (change == null)
                {
                    continue;
                }

                int tick = Math.Max(0, change.Tick);
                int fifths = Math.Clamp(change.Fifths, -7, 7);
                if (tick == 0)
                {
                    list[0].Fifths = fifths;
                    list[0].Mode = change.Mode;
                }
                else
                {
                    list.Add(new KeySignatureChange
                    {
                        Tick = tick,
                        Fifths = fifths,
                        Mode = change.Mode
                    });
                }
            }

            return list
                .OrderBy(c => c.Tick)
                .ThenBy(c => c.Fifths)
                .GroupBy(c => c.Tick)
                .Select(g => g.Last())
                .ToList();
        }

        private static List<int> BuildMeasureBoundaries(
            int ppq,
            List<TimeSignatureChange> normalizedTime,
            int maxTick)
        {
            var boundaries = new List<int> { 0 };
            if (normalizedTime.Count == 0)
            {
                normalizedTime.Add(new TimeSignatureChange { Tick = 0, Numerator = 4, Denominator = 4 });
            }

            int safePpq = Math.Max(1, ppq);
            int changeIndex = 0;
            int cursorTick = 0;
            int baseMeasure = Math.Max(1, new TimeSignature(
                normalizedTime[0].Numerator,
                normalizedTime[0].Denominator).TicksPerMeasure(safePpq));
            int targetEnd = Math.Max(maxTick + baseMeasure * 2, baseMeasure * 2);
            const int maxMeasures = 8192;

            for (int i = 0; i < maxMeasures && cursorTick < targetEnd; i++)
            {
                while (changeIndex + 1 < normalizedTime.Count && normalizedTime[changeIndex + 1].Tick <= cursorTick)
                {
                    changeIndex++;
                }

                TimeSignatureChange active = normalizedTime[changeIndex];
                int measureTicks = Math.Max(1, new TimeSignature(active.Numerator, active.Denominator).TicksPerMeasure(safePpq));
                int next = cursorTick + measureTicks;
                if (next <= cursorTick)
                {
                    next = cursorTick + 1;
                }

                boundaries.Add(next);
                cursorTick = next;
            }

            if (boundaries.Count < 2)
            {
                boundaries.Add(Math.Max(1, baseMeasure));
            }

            return boundaries;
        }

        private static int SnapTimeChangesToBoundaries(
            ScoreProject project,
            List<TimeSignatureChange> normalizedTime,
            List<int> boundaries)
        {
            int changed = 0;
            var snapped = new List<TimeSignatureChange>();

            int baseNumerator = normalizedTime[0].Numerator;
            int baseDenominator = normalizedTime[0].Denominator;
            foreach (TimeSignatureChange change in normalizedTime.Skip(1))
            {
                int snappedTick = SnapToNearestBoundary(boundaries, change.Tick);
                if (snappedTick != change.Tick)
                {
                    changed++;
                }

                if (snappedTick <= 0)
                {
                    baseNumerator = change.Numerator;
                    baseDenominator = change.Denominator;
                    continue;
                }

                int existing = snapped.FindIndex(c => c.Tick == snappedTick);
                if (existing >= 0)
                {
                    snapped[existing] = new TimeSignatureChange
                    {
                        Tick = snappedTick,
                        Numerator = change.Numerator,
                        Denominator = change.Denominator
                    };
                    changed++;
                }
                else
                {
                    snapped.Add(new TimeSignatureChange
                    {
                        Tick = snappedTick,
                        Numerator = change.Numerator,
                        Denominator = change.Denominator
                    });
                }
            }

            if (project.TimeSignature.Numerator != baseNumerator || project.TimeSignature.Denominator != baseDenominator)
            {
                project.TimeSignature.Numerator = baseNumerator;
                project.TimeSignature.Denominator = baseDenominator;
                changed++;
            }

            var deduped = snapped
                .OrderBy(c => c.Tick)
                .GroupBy(c => c.Tick)
                .Select(g => g.Last())
                .ToList();
            if (project.TimeSignatureChanges.Count != deduped.Count)
            {
                changed++;
            }

            project.TimeSignatureChanges = deduped;
            return changed;
        }

        private static int SnapKeyChangesToBoundaries(
            ScoreProject project,
            List<KeySignatureChange> normalizedKey,
            List<int> boundaries)
        {
            int changed = 0;
            var snapped = new List<KeySignatureChange>();
            int baseFifths = normalizedKey[0].Fifths;
            KeyMode baseMode = normalizedKey[0].Mode;

            foreach (KeySignatureChange change in normalizedKey.Skip(1))
            {
                int snappedTick = SnapToNearestBoundary(boundaries, change.Tick);
                if (snappedTick != change.Tick)
                {
                    changed++;
                }

                if (snappedTick <= 0)
                {
                    baseFifths = change.Fifths;
                    baseMode = change.Mode;
                    continue;
                }

                int existing = snapped.FindIndex(c => c.Tick == snappedTick);
                if (existing >= 0)
                {
                    snapped[existing] = new KeySignatureChange
                    {
                        Tick = snappedTick,
                        Fifths = change.Fifths,
                        Mode = change.Mode
                    };
                    changed++;
                }
                else
                {
                    snapped.Add(new KeySignatureChange
                    {
                        Tick = snappedTick,
                        Fifths = change.Fifths,
                        Mode = change.Mode
                    });
                }
            }

            if (project.KeySignature.Fifths != baseFifths || project.KeySignature.Mode != baseMode)
            {
                project.KeySignature.Fifths = baseFifths;
                project.KeySignature.Mode = baseMode;
                changed++;
            }

            var deduped = snapped
                .OrderBy(c => c.Tick)
                .GroupBy(c => c.Tick)
                .Select(g => g.Last())
                .ToList();
            if (project.KeySignatureChanges.Count != deduped.Count)
            {
                changed++;
            }

            project.KeySignatureChanges = deduped;
            return changed;
        }

        private static int AddWholeMeasureRests(ScoreProject project, List<int> boundaries)
        {
            if (boundaries.Count < 2)
            {
                return 0;
            }

            int maxTick = GetMaxTick(project);
            int lastBoundaryIndex = FindBoundaryAtOrAfter(boundaries, Math.Max(0, maxTick));
            int added = 0;

            for (int i = 0; i < Math.Max(1, lastBoundaryIndex); i++)
            {
                int start = boundaries[i];
                int end = boundaries[Math.Min(i + 1, boundaries.Count - 1)];
                if (end <= start)
                {
                    continue;
                }

                bool hasPlayable = project.Notes.Any(n =>
                    !n.IsRest
                    && Math.Max(0, n.StartTick) < end
                    && (Math.Max(0, n.StartTick) + Math.Max(1, n.DurationTicks)) > start);
                if (hasPlayable)
                {
                    continue;
                }

                bool hasRest = project.Notes.Any(n =>
                    n.IsRest
                    && Math.Max(0, n.StartTick) < end
                    && (Math.Max(0, n.StartTick) + Math.Max(1, n.DurationTicks)) > start);
                if (hasRest)
                {
                    continue;
                }

                int duration = Math.Max(1, end - start);
                project.Notes.Add(new NoteEvent
                {
                    Midi = 60,
                    StartTick = start,
                    DurationTicks = duration,
                    BaseDurationTicks = duration,
                    AugmentationDots = 0,
                    IsRest = true,
                    Voice = 1,
                    Accidental = NoteAccidental.None,
                    BeamGroupId = 0,
                    PreferTrebleStaff = true
                });
                added++;
            }

            return added;
        }

        private static void PruneNoisySignatureChanges(ScoreProject project, List<int> boundaries)
        {
            if (boundaries.Count < 2)
            {
                return;
            }

            int maxMeasureIndex = boundaries.Count - 2;
            int maxKeyChanges = Math.Max(2, maxMeasureIndex / 4);
            int maxTimeChanges = Math.Max(1, maxMeasureIndex / 5);

            project.KeySignatureChanges = FilterSignatureChanges(
                project.KeySignatureChanges,
                maxKeyChanges,
                minMeasureDistance: 1,
                boundaries);
            project.TimeSignatureChanges = FilterSignatureChanges(
                project.TimeSignatureChanges,
                maxTimeChanges,
                minMeasureDistance: 2,
                boundaries);
        }

        private static List<TChange> FilterSignatureChanges<TChange>(
            List<TChange> source,
            int maxCount,
            int minMeasureDistance,
            List<int> boundaries)
            where TChange : class
        {
            if (source == null || source.Count == 0)
            {
                return new List<TChange>();
            }

            var ordered = source
                .Where(c => GetChangeTick(c) > 0)
                .OrderBy(GetChangeTick)
                .ToList();
            if (ordered.Count == 0)
            {
                return new List<TChange>();
            }

            var kept = new List<TChange>();
            int lastMeasure = -9999;
            foreach (TChange change in ordered)
            {
                int tick = Math.Max(0, GetChangeTick(change));
                int measure = FindBoundaryAtOrAfter(boundaries, tick);
                if (measure - lastMeasure < Math.Max(0, minMeasureDistance))
                {
                    continue;
                }

                kept.Add(change);
                lastMeasure = measure;
                if (kept.Count >= Math.Max(0, maxCount))
                {
                    break;
                }
            }

            return kept
                .OrderBy(GetChangeTick)
                .GroupBy(GetChangeTick)
                .Select(g => g.Last())
                .ToList();
        }

        private static bool EnsureFinalBarline(ScoreProject project, List<int> boundaries, int maxTick)
        {
            ExpressionMark? existing = project.ExpressionMarks
                .FirstOrDefault(m => string.Equals(NormalizeCode(m.Code), ScoreMarkFinalBarline, StringComparison.Ordinal));
            int targetTick = boundaries[Math.Min(boundaries.Count - 1, Math.Max(1, FindBoundaryAtOrAfter(boundaries, Math.Max(0, maxTick))))];
            if (existing != null)
            {
                int snapped = SnapToNearestBoundary(boundaries, Math.Max(0, existing.StartTick));
                bool changed = snapped != existing.StartTick;
                existing.StartTick = snapped;
                return changed;
            }

            project.ExpressionMarks.Add(new ExpressionMark
            {
                Code = ScoreMarkFinalBarline,
                StartTick = targetTick,
                StaffStepOffset = 8f,
                SpanBeats = 0f,
                ShapeHeightSteps = 0f,
                SlopeSteps = 0f
            });
            return true;
        }

        private static int AddBeamGroupSlurs(ScoreProject project, int ppq)
        {
            var existingSlurTicks = new HashSet<int>(
                project.ExpressionMarks
                    .Where(m => string.Equals(NormalizeCode(m.Code), ScoreMarkSlur, StringComparison.Ordinal))
                    .Select(m => Math.Max(0, m.StartTick)));

            var groups = project.Notes
                .Where(n => !n.IsRest && n.BeamGroupId > 0)
                .GroupBy(n => n.BeamGroupId)
                .Select(g => g.OrderBy(n => n.StartTick).ToList())
                .Where(g => g.Count >= 2)
                .ToList();

            int added = 0;
            int ticksPerBeat = Math.Max(1, new TimeSignature(
                project.TimeSignature.Numerator,
                project.TimeSignature.Denominator).TicksPerBeat(Math.Max(1, ppq)));
            int tolerance = Math.Max(1, ticksPerBeat / 8);

            foreach (List<NoteEvent> group in groups)
            {
                NoteEvent first = group[0];
                NoteEvent last = group[^1];
                int startTick = Math.Max(0, first.StartTick);
                bool alreadyExists = existingSlurTicks.Any(t => Math.Abs(t - startTick) <= tolerance);
                if (alreadyExists)
                {
                    continue;
                }

                int endTick = Math.Max(startTick + 1, last.StartTick + Math.Max(1, last.DurationTicks));
                float spanBeats = (endTick - startTick) / (float)ticksPerBeat;
                if (spanBeats < 0.45f)
                {
                    continue;
                }

                float meanMidi = (float)group.Average(n => n.Midi);
                float staffOffset = meanMidi >= 60f ? 18f : 7f;
                project.ExpressionMarks.Add(new ExpressionMark
                {
                    Code = ScoreMarkSlur,
                    StartTick = startTick,
                    StaffStepOffset = staffOffset,
                    SpanBeats = Math.Clamp(spanBeats, 0.6f, 10f),
                    ShapeHeightSteps = 6f,
                    SlopeSteps = 0f
                });
                existingSlurTicks.Add(startTick);
                added++;
                if (added >= 48)
                {
                    break;
                }
            }

            return added;
        }

        private static int SnapToNearestBoundary(List<int> boundaries, int tick)
        {
            if (boundaries.Count == 0)
            {
                return Math.Max(0, tick);
            }

            int safeTick = Math.Max(0, tick);
            int index = boundaries.BinarySearch(safeTick);
            if (index >= 0)
            {
                return boundaries[index];
            }

            int next = ~index;
            if (next <= 0)
            {
                return boundaries[0];
            }

            if (next >= boundaries.Count)
            {
                return boundaries[^1];
            }

            int left = boundaries[next - 1];
            int right = boundaries[next];
            return Math.Abs(safeTick - left) <= Math.Abs(right - safeTick) ? left : right;
        }

        private static int FindBoundaryAtOrAfter(List<int> boundaries, int tick)
        {
            if (boundaries.Count == 0)
            {
                return 0;
            }

            int safeTick = Math.Max(0, tick);
            int index = boundaries.BinarySearch(safeTick);
            if (index >= 0)
            {
                return index;
            }

            int next = ~index;
            if (next < 0)
            {
                return 0;
            }

            return Math.Min(boundaries.Count - 1, next);
        }

        private static int GetChangeTick<TChange>(TChange change)
        {
            if (change is TimeSignatureChange ts)
            {
                return Math.Max(0, ts.Tick);
            }

            if (change is KeySignatureChange ks)
            {
                return Math.Max(0, ks.Tick);
            }

            return 0;
        }

        private static string NormalizeCode(string? code)
            => (code ?? string.Empty).Trim().ToLowerInvariant();
    }
}
