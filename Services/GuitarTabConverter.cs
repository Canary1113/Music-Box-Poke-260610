using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MusicBox.Models;

namespace MusicBox.Services
{
    public sealed class GuitarTabConverter
    {
        private static readonly GuitarString[] Strings =
        {
            new("e", 64),
            new("B", 59),
            new("G", 55),
            new("D", 50),
            new("A", 45),
            new("E", 40)
        };

        private const int MaxFret = 20;
        private const int DefaultMeasuresPerBlock = 4;

        public string BuildAsciiTab(ScoreProject project)
        {
            TabSource source = CreateSource(project);
            if (source.Notes.Count == 0)
            {
                return string.Empty;
            }

            Dictionary<int, ScorePreviewLayoutHelper.PreviewBoundaryDecoration> barlineMap =
                ScorePreviewLayoutHelper.BuildBarlineMap(project.ExpressionMarks, source.TicksPerMeasure, source.MeasureCount);

            var sb = new StringBuilder();
            sb.AppendLine($"Title: {SanitizeTitle(project.Title)}");
            sb.AppendLine($"Tempo: {project.Bpm} BPM    Time: {source.TimeNumerator}/{source.TimeDenominator}    Tuning: E A D G B e");
            sb.AppendLine();

            for (int blockStart = 0; blockStart < source.MeasureCount; blockStart += DefaultMeasuresPerBlock)
            {
                int blockEnd = Math.Min(source.MeasureCount, blockStart + DefaultMeasuresPerBlock);
                StringBuilder[] builders = Strings
                    .Select(guitarString => new StringBuilder($"{guitarString.Label}|"))
                    .ToArray();

                for (int measureIndex = blockStart; measureIndex < blockEnd; measureIndex++)
                {
                    int measureStartTick = measureIndex * source.TicksPerMeasure;
                    int measureEndTick = measureStartTick + source.TicksPerMeasure;
                    List<OnsetGroup> measureOnsets = source.Onsets
                        .Where(onset => onset.StartTick >= measureStartTick && onset.StartTick < measureEndTick)
                        .ToList();

                    int currentTick = measureStartTick;
                    for (int i = 0; i < measureOnsets.Count; i++)
                    {
                        OnsetGroup onset = measureOnsets[i];
                        AppendGap(builders, onset.StartTick - currentTick, source.Ppq, source.TimeDenominator);

                        int nextTick = i + 1 < measureOnsets.Count
                            ? measureOnsets[i + 1].StartTick
                            : measureEndTick;

                        int spanTicks = Math.Max(1, nextTick - onset.StartTick);
                        AppendEvent(builders, ResolvePlacements(onset.Notes), spanTicks, source.Ppq, source.TimeDenominator);
                        currentTick = nextTick;
                    }

                    AppendGap(builders, measureEndTick - currentTick, source.Ppq, source.TimeDenominator);
                    string rightBar = ResolveBarlineText(measureIndex + 1, barlineMap);
                    foreach (StringBuilder builder in builders)
                    {
                        builder.Append(rightBar == "||" ? "||" : "|");
                    }
                }

                sb.AppendLine($"Measures {blockStart + 1}-{blockEnd}");
                foreach (StringBuilder builder in builders)
                {
                    sb.AppendLine(builder.ToString());
                }

                if (blockEnd < source.MeasureCount)
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        public TabPreviewModel? BuildPreviewModel(ScoreProject project, float viewportWidth)
        {
            TabSource source = CreateSource(project);
            if (source.Notes.Count == 0)
            {
                return null;
            }

            Dictionary<int, ScorePreviewLayoutHelper.PreviewBoundaryDecoration> barlineMap =
                ScorePreviewLayoutHelper.BuildBarlineMap(project.ExpressionMarks, source.TicksPerMeasure, source.MeasureCount);

            const float leftMargin = 92f;
            const float rightMargin = 44f;
            const float measureGap = 18f;
            int measuresPerBlock = ResolveMeasuresPerBlock(viewportWidth);
            float contentWidth = Math.Max(900f, viewportWidth);
            float measureWidth = Math.Max(168f, (contentWidth - leftMargin - rightMargin - (measuresPerBlock - 1) * measureGap) / measuresPerBlock);
            float canvasWidth = leftMargin + measuresPerBlock * measureWidth + (measuresPerBlock - 1) * measureGap + rightMargin;

            var systems = new List<TabSystem>();
            for (int blockStart = 0; blockStart < source.MeasureCount; blockStart += measuresPerBlock)
            {
                int blockEnd = Math.Min(source.MeasureCount, blockStart + measuresPerBlock);
                var measures = new List<TabMeasure>();

                for (int measureIndex = blockStart; measureIndex < blockEnd; measureIndex++)
                {
                    int measureStartTick = measureIndex * source.TicksPerMeasure;
                    int measureEndTick = measureStartTick + source.TicksPerMeasure;
                    List<TabPlacement> placements = source.Onsets
                        .Where(onset => onset.StartTick >= measureStartTick && onset.StartTick < measureEndTick)
                        .Select(onset => new TabPlacement(
                            onset.StartTick - measureStartTick,
                            ResolvePlacements(onset.Notes)
                                .Select(position => new TabPosition(position.StringIndex, position.Fret))
                                .ToList()))
                        .Where(placement => placement.Positions.Count > 0)
                        .ToList();

                    measures.Add(new TabMeasure(
                        measureIndex + 1,
                        source.TicksPerMeasure,
                        measureWidth,
                        ResolveBarlineText(measureIndex, barlineMap),
                        ResolveBarlineText(measureIndex + 1, barlineMap),
                        placements));
                }

                systems.Add(new TabSystem(blockStart + 1, measures));
            }

            return new TabPreviewModel(
                SanitizeTitle(project.Title),
                $"{source.TimeNumerator}/{source.TimeDenominator}",
                project.Bpm,
                canvasWidth,
                systems);
        }

        private static int ResolveMeasuresPerBlock(float viewportWidth)
        {
            if (viewportWidth < 980f)
            {
                return 2;
            }

            if (viewportWidth < 1220f)
            {
                return 3;
            }

            return DefaultMeasuresPerBlock;
        }

        private static TabSource CreateSource(ScoreProject project)
        {
            if (project == null)
            {
                return new TabSource(480, 4, 4, 1920, 1, new List<NoteEvent>(), new List<OnsetGroup>());
            }

            List<NoteEvent> notes = project.Notes?
                .Where(note => note != null && !note.IsRest)
                .OrderBy(note => note.StartTick)
                .ThenByDescending(note => note.Midi)
                .ToList()
                ?? new List<NoteEvent>();

            int ppq = Math.Max(1, project.Ppq);
            int numerator = project.TimeSignature?.Numerator ?? 4;
            int denominator = project.TimeSignature?.Denominator ?? 4;
            int ticksPerMeasure = Math.Max(ppq, project.TimeSignature?.TicksPerMeasure(ppq) ?? ppq * 4);
            int totalTicks = notes.Count == 0
                ? ticksPerMeasure
                : Math.Max(ticksPerMeasure, notes.Max(note => note.StartTick + Math.Max(1, note.DurationTicks)));
            int measureCount = Math.Max(
                1,
                Math.Max(
                    (int)Math.Ceiling(totalTicks / (double)ticksPerMeasure),
                    ScorePreviewLayoutHelper.GetContentMeasureCount(project)));
            List<OnsetGroup> onsets = notes
                .GroupBy(note => note.StartTick)
                .OrderBy(group => group.Key)
                .Select(group => new OnsetGroup(group.Key, group.ToList()))
                .ToList();

            return new TabSource(ppq, numerator, denominator, ticksPerMeasure, measureCount, notes, onsets);
        }

        private static List<GuitarPosition> ResolvePlacements(IReadOnlyList<NoteEvent> notes)
        {
            var placements = new List<GuitarPosition>();
            var usedStrings = new HashSet<int>();

            foreach (int midi in notes
                .Where(note => !note.IsRest)
                .Select(note => note.Midi)
                .Distinct()
                .OrderByDescending(value => value)
                .Take(Strings.Length))
            {
                GuitarPosition? chosen = GetCandidates(midi)
                    .Where(candidate => !usedStrings.Contains(candidate.StringIndex))
                    .OrderBy(candidate => candidate.Fret > 12 ? 1 : 0)
                    .ThenBy(candidate => candidate.StringIndex)
                    .ThenBy(candidate => candidate.Fret)
                    .FirstOrDefault();

                if (chosen == null)
                {
                    continue;
                }

                usedStrings.Add(chosen.StringIndex);
                placements.Add(chosen);
            }

            return placements;
        }

        private static IEnumerable<GuitarPosition> GetCandidates(int midi)
        {
            for (int stringIndex = 0; stringIndex < Strings.Length; stringIndex++)
            {
                int fret = midi - Strings[stringIndex].OpenMidi;
                if (fret >= 0 && fret <= MaxFret)
                {
                    yield return new GuitarPosition(stringIndex, fret);
                }
            }
        }

        private static void AppendGap(
            IReadOnlyList<StringBuilder> builders,
            int ticks,
            int ppq,
            int denominator)
        {
            if (ticks <= 0)
            {
                return;
            }

            int width = GetCellWidth(ticks, ppq, denominator);
            string dashes = new string('-', width);
            foreach (StringBuilder builder in builders)
            {
                builder.Append(dashes);
            }
        }

        private static void AppendEvent(
            IReadOnlyList<StringBuilder> builders,
            IReadOnlyList<GuitarPosition> positions,
            int ticks,
            int ppq,
            int denominator)
        {
            int width = Math.Max(
                GetCellWidth(ticks, ppq, denominator),
                positions.Count == 0 ? 2 : positions.Max(position => position.Fret.ToString().Length) + 1);

            for (int stringIndex = 0; stringIndex < Strings.Length; stringIndex++)
            {
                GuitarPosition? position = positions.FirstOrDefault(item => item.StringIndex == stringIndex);
                if (position == null)
                {
                    builders[stringIndex].Append(new string('-', width));
                    continue;
                }

                string fretText = position.Fret.ToString();
                builders[stringIndex].Append(fretText);
                builders[stringIndex].Append(new string('-', Math.Max(0, width - fretText.Length)));
            }
        }

        private static int GetCellWidth(int ticks, int ppq, int denominator)
        {
            int subdivisionTicks = Math.Max(1, (int)Math.Round(ppq * 2.0 / Math.Max(1, denominator)));
            int units = Math.Max(1, (int)Math.Round(ticks / (double)subdivisionTicks));
            return Math.Max(2, units * 2);
        }

        private static string SanitizeTitle(string? title)
        {
            return string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        }

        private static string ResolveBarlineText(
            int boundaryIndex,
            IReadOnlyDictionary<int, ScorePreviewLayoutHelper.PreviewBoundaryDecoration> barlineByBoundary)
        {
            barlineByBoundary.TryGetValue(boundaryIndex, out ScorePreviewLayoutHelper.PreviewBoundaryDecoration mark);
            if (mark.Final)
            {
                return "||";
            }

            if (mark.StartRepeat && mark.EndRepeat)
            {
                return ":|:";
            }

            if (mark.StartRepeat)
            {
                return "|:";
            }

            if (mark.EndRepeat)
            {
                return ":|";
            }

            return "|";
        }

        private sealed record TabSource(
            int Ppq,
            int TimeNumerator,
            int TimeDenominator,
            int TicksPerMeasure,
            int MeasureCount,
            List<NoteEvent> Notes,
            List<OnsetGroup> Onsets);

        private sealed record GuitarString(string Label, int OpenMidi);
        private sealed record GuitarPosition(int StringIndex, int Fret);
        private sealed record OnsetGroup(int StartTick, List<NoteEvent> Notes);
    }

    public sealed record TabPreviewModel(
        string Title,
        string MeterText,
        int Bpm,
        float ContentWidth,
        List<TabSystem> Systems);

    public sealed record TabSystem(int StartMeasureNumber, List<TabMeasure> Measures);

public sealed record TabMeasure(
    int MeasureNumber,
    int MeasureTicks,
    float Width,
    string LeftBarText,
    string RightBarText,
    List<TabPlacement> Placements);

    public sealed record TabPlacement(int TickInMeasure, List<TabPosition> Positions);

    public sealed record TabPosition(int StringIndex, int Fret);
}
