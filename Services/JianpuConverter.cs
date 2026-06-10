using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using MusicBox.Models;

namespace MusicBox.Services
{
    /// <summary>
    /// JianpuConverter converts a ScoreProject into a Jianpu (numbered musical notation) preview.
    /// This implementation uses a Sweep-line algorithm to efficiently process notes and generate a linear timeline.
    /// 
    /// 简谱转换器：将 ScoreProject 转换为简谱预览。
    /// 本实现采用 扫描线 (Sweep-line) 算法，以高效处理音符并生成线性时间轴。
    /// </summary>
    public sealed class JianpuConverter
    {
        private const int DefaultMeasuresPerLine = 3;
        private const int JianpuPitchCorrectionSemitones = 0;
        private const int NativeBarlineRenderWidthPx = 22;
        private const int NativeKeyLabelRenderWidthPx = 56;
        private const int NativeLineRightPaddingPx = 18;

        private readonly struct JianpuEvent
        {
            public JianpuEvent(int startTick, int endTick, bool isRest, IReadOnlyList<PitchToken>? pitches = null)
            {
                StartTick = Math.Max(0, startTick);
                EndTick = Math.Max(StartTick + 1, endTick);
                IsRest = isRest;
                Pitches = isRest ? Array.Empty<PitchToken>() : pitches ?? Array.Empty<PitchToken>();
            }

            public int StartTick { get; }
            public int EndTick { get; }
            public int DurationTicks => Math.Max(1, EndTick - StartTick);
            public bool IsRest { get; }
            public IReadOnlyList<PitchToken> Pitches { get; }
        }

        private readonly struct PitchToken
        {
            public PitchToken(string prefix, string accidental, string degree, string suffix)
            {
                Prefix = prefix;
                Accidental = accidental;
                Degree = degree;
                Suffix = suffix;
            }

            public string Prefix { get; }
            public string Accidental { get; }
            public string Degree { get; }
            public string Suffix { get; }

            public string ToDisplayString()
            {
                return $"{Prefix}{Accidental}{Degree}{Suffix}";
            }
        }

        private readonly struct DurationVisual
        {
            public DurationVisual(int extendCount, int underlineCount)
            {
                ExtendCount = Math.Max(0, extendCount);
                UnderlineCount = Math.Max(0, underlineCount);
            }

            public int ExtendCount { get; }
            public int UnderlineCount { get; }
        }

        private readonly struct RenderToken
        {
            public RenderToken(
                string accidental,
                string degreeText,
                int topDots,
                int downDots,
                DurationVisual duration,
                bool isChord,
                IReadOnlyList<PitchToken>? chordPitches,
                int localTickInBeat,
                int durationTicks,
                double beatWeight)
            {
                Accidental = accidental ?? string.Empty;
                DegreeText = degreeText ?? "0";
                TopDots = Math.Max(0, topDots);
                DownDots = Math.Max(0, downDots);
                Duration = duration;
                IsChord = isChord;
                ChordPitches = isChord && chordPitches != null
                    ? chordPitches.Where(p => !string.IsNullOrWhiteSpace(p.Degree)).ToArray()
                    : Array.Empty<PitchToken>();
                LocalTickInBeat = Math.Max(0, localTickInBeat);
                DurationTicks = Math.Max(1, durationTicks);
                BeatWeight = Math.Max(0.2d, beatWeight);
            }

            public string Accidental { get; }
            public string DegreeText { get; }
            public int TopDots { get; }
            public int DownDots { get; }
            public DurationVisual Duration { get; }
            public bool IsChord { get; }
            public IReadOnlyList<PitchToken> ChordPitches { get; }
            public int LocalTickInBeat { get; }
            public int DurationTicks { get; }
            public double BeatWeight { get; }
        }

        /// <summary>
        /// Logic: The Sweep-line algorithm is implemented in BuildTimelineWithRests.
        /// 1. We create "Event Points" from all note starts and ends.
        /// 2. Sort these points by time.
        /// 3. Iterate through sorted time segments. For each segment, determine the active notes (sounding) or if it's a rest.
        /// 4. This ensures that overlapping notes are handled correctly as chords, and gaps are filled with rests.
        /// 
        /// 逻辑：在 BuildTimelineWithRests 中实现了 扫描线算法。
        /// 1. 从所有音符的起始和结束位置创建“事件点”。
        /// 2. 按时间对这些点进行排序。
        /// 3. 遍历排序后的时间段。对于每个时间段，确定活动的音符（发声）或是否为休止符。
        /// 4. 这确保了重叠的音符被正确处理为和弦，而间隙被休止符填充。
        /// </summary>
        private static List<JianpuEvent> BuildTimelineWithRests(
            IReadOnlyList<NoteEvent> notes,
            int tonicPitchClass,
            int tonicMidiRef,
            bool preferFlat)
        {
            if (notes == null || !notes.Any()) return new List<JianpuEvent>();

            // Sweep-line: Identify all unique time boundaries (start and end ticks).
            // 扫描线：识别所有唯一的时间边界（起始和结束 tick）。
            var timePoints = new SortedSet<int>();
            foreach (var n in notes)
            {
                timePoints.Add(n.StartTick);
                timePoints.Add(n.StartTick + GetVisualDurationTicks(n));
            }

            var timeline = new List<JianpuEvent>();
            var points = timePoints.ToList();

            // Process segments between consecutive time points.
            // 处理相邻时间点之间的线段。
            for (int i = 0; i < points.Count - 1; i++)
            {
                int start = points[i];
                int end = points[i + 1];

                // Find all notes that cover this specific time segment [start, end).
                // 查找覆盖此特定时间段 [start, end) 的所有音符。
                var activeNotes = notes.Where(n => n.StartTick <= start && (n.StartTick + GetVisualDurationTicks(n)) >= end && !n.IsRest).ToList();

                if (!activeNotes.Any())
                {
                    timeline.Add(new JianpuEvent(start, end, isRest: true));
                }
                else
                {
                    // Convert active notes to PitchTokens, handling MIDI correction and key transposition.
                    // 将活动音符转换为 PitchToken，处理 MIDI 修正和调性转调。
                    var pitches = activeNotes
                        .GroupBy(n => n.Midi)
                        .Select(g => g.First())
                        .OrderBy(n => n.Midi)
                        .Select(n => {
                            int correctedMidi = Math.Clamp(n.Midi + JianpuPitchCorrectionSemitones, 0, 127);
                            return ToPitchToken(correctedMidi, tonicPitchClass, tonicMidiRef, preferFlat);
                        })
                        .ToList();

                    timeline.Add(new JianpuEvent(start, end, isRest: false, pitches));
                }
            }

            return timeline;
        }

        public string BuildPreviewHtml(ScoreProject project, bool darkTheme = false)
        {
            if (project == null)
            {
                return BuildEmptyHtml("工程为空，无法转换。", darkTheme);
            }

            int ppq = Math.Max(1, project.Ppq);
            int measureTicks = Math.Max(1, project.TimeSignature.TicksPerMeasure(ppq));
            var allNotes = project.Notes
                .Where(n => n != null)
                .OrderBy(n => n.StartTick)
                .ThenBy(n => n.IsRest ? 1 : 0)
                .ThenBy(n => n.Midi)
                .ToList();

            if (allNotes.Count == 0)
            {
                return BuildEmptyHtml("当前工程没有音符。", darkTheme);
            }

            int fifths = Math.Clamp(project.KeySignature?.Fifths ?? 0, -7, 7);
            bool preferFlat = fifths < 0;
            int tonicPc = GetTonicPitchClass(project.KeySignature);
            int tonicMidiRef = GetReferenceTonicMidi(tonicPc);
            string keyText = GetKeyDescription(project.KeySignature);
            int bpm = Math.Clamp(project.Bpm, 20, 300);

            var (upperNotes, lowerNotes) = SplitNotesByStaff(allNotes);
            bool dualStaff = upperNotes.Count > 0 && lowerNotes.Count > 0;

            List<JianpuEvent> upperTimeline = BuildTimelineWithRests(
                dualStaff ? upperNotes : allNotes,
                tonicPc,
                tonicMidiRef,
                preferFlat);
            if (upperTimeline.Count == 0)
            {
                return BuildEmptyHtml("无法从当前音符生成简谱。", darkTheme);
            }

            var upperMeasures = BuildMeasures(upperTimeline, measureTicks);
            List<List<JianpuEvent>>? lowerMeasures = null;
            int totalMeasureCount = Math.Max(ScorePreviewLayoutHelper.GetContentMeasureCount(project), upperMeasures.Count);
            if (dualStaff)
            {
                List<JianpuEvent> lowerTimeline = BuildTimelineWithRests(lowerNotes, tonicPc, tonicMidiRef, preferFlat);
                lowerMeasures = BuildMeasures(lowerTimeline, measureTicks);
                totalMeasureCount = Math.Max(upperMeasures.Count, lowerMeasures.Count);
            }

            PadMeasureList(upperMeasures, totalMeasureCount, measureTicks);
            if (lowerMeasures != null)
            {
                PadMeasureList(lowerMeasures, totalMeasureCount, measureTicks);
            }

            var barlineMap = ScorePreviewLayoutHelper.BuildBarlineMap(project.ExpressionMarks, measureTicks, totalMeasureCount);
            var keyChangeMap = BuildKeyChangeMap(project.KeySignatureChanges, measureTicks, totalMeasureCount);

            return BuildDocumentHtml(
                project.Title,
                keyText,
                bpm,
                project.TimeSignature,
                upperMeasures,
                lowerMeasures,
                dualStaff,
                barlineMap,
                keyChangeMap,
                ppq,
                darkTheme);
        }

        public sealed class NativePreviewModel
        {
            public string Title { get; init; } = "Untitled";
            public string KeyText { get; init; } = "C";
            public string MeterText { get; init; } = "4/4";
            public int Bpm { get; init; } = 120;
            public bool DualStaff { get; init; } = true;
            public float ContentWidth { get; init; }
            public float ContentHeight { get; init; }
            public List<NativeSystem> Systems { get; init; } = new();
        }

        public sealed class NativeSystem
        {
            public int StartMeasureNumber { get; init; }
            public string LeftBarText { get; init; } = "|";
            public string? LeftKeyText { get; init; }
            public List<NativeMeasure> Measures { get; init; } = new();
        }

        public sealed class NativeMeasure
        {
            public float Width { get; init; }
            public int MeasureTicks { get; init; }
            public int BeatTicks { get; init; }
            public List<NativeToken> UpperTokens { get; init; } = new();
            public List<NativeToken> LowerTokens { get; init; } = new();
            public string RightBarText { get; init; } = "|";
            public string? RightKeyText { get; init; }
        }

        public sealed class NativeToken
        {
            public string Text { get; init; } = "0";
            public int TopDots { get; init; }
            public int BottomDots { get; init; }
            public int ExtendCount { get; init; }
            public int UnderlineCount { get; init; }
            public int TickInMeasure { get; init; }
            public int DurationTicks { get; init; } = 1;
            public float BeatWeight { get; init; } = 1f;
            public float WidthScale { get; init; } = 1f;
            public List<NativeChordPitch> ChordPitches { get; init; } = new();
            public bool IsChord => ChordPitches.Count > 1;
        }

        public sealed class NativeChordPitch
        {
            public string Accidental { get; init; } = string.Empty;
            public string Degree { get; init; } = "0";
            public int TopDots { get; init; }
            public int BottomDots { get; init; }
        }

        public NativePreviewModel BuildNativePreviewModel(ScoreProject project, float maxLineWidth = 1280f)
        {
            if (project == null)
            {
                return new NativePreviewModel
                {
                    Title = "Untitled",
                    KeyText = "C",
                    MeterText = "4/4",
                    Bpm = 120,
                    DualStaff = true,
                    ContentWidth = 900f,
                    ContentHeight = 520f
                };
            }

            int ppq = Math.Max(1, project.Ppq);
            int measureTicks = Math.Max(1, project.TimeSignature.TicksPerMeasure(ppq));
            int beatTicks = Math.Max(1, (int)Math.Round(ppq * (4d / Math.Max(1, project.TimeSignature.Denominator))));
            var allNotes = project.Notes
                .Where(n => n != null)
                .OrderBy(n => n.StartTick)
                .ThenBy(n => n.Midi)
                .ToList();

            bool preferFlat = project.KeySignature.Fifths < 0;
            int tonicPc = GetTonicPitchClass(project.KeySignature);
            int tonicMidiRef = GetReferenceTonicMidi(tonicPc);
            string keyText = GetKeyDescription(project.KeySignature);
            int bpm = Math.Clamp(project.Bpm <= 0 ? 120 : project.Bpm, 24, 260);

            var upperNotes = new List<NoteEvent>();
            var lowerNotes = new List<NoteEvent>();
            foreach (var note in allNotes)
            {
                if (note == null) continue;
                bool upper = note.PreferTrebleStaff ?? note.Midi >= 60;
                (upper ? upperNotes : lowerNotes).Add(note);
            }

            bool dualStaff = true;
            List<JianpuEvent> upperTimeline = BuildTimelineWithRests(
                dualStaff ? upperNotes : allNotes,
                tonicPc,
                tonicMidiRef,
                preferFlat);
            if (upperTimeline.Count == 0)
            {
                upperTimeline.Add(new JianpuEvent(0, measureTicks, isRest: true));
            }

            var upperMeasures = BuildMeasures(upperTimeline, measureTicks);
            List<List<JianpuEvent>>? lowerMeasures = null;
            int totalMeasureCount = Math.Max(ScorePreviewLayoutHelper.GetContentMeasureCount(project), upperMeasures.Count);
            if (dualStaff)
            {
                List<JianpuEvent> lowerTimeline = BuildTimelineWithRests(lowerNotes, tonicPc, tonicMidiRef, preferFlat);
                lowerMeasures = BuildMeasures(lowerTimeline, measureTicks);
                totalMeasureCount = Math.Max(upperMeasures.Count, lowerMeasures.Count);
            }

            PadMeasureList(upperMeasures, totalMeasureCount, measureTicks);
            if (lowerMeasures != null)
            {
                PadMeasureList(lowerMeasures, totalMeasureCount, measureTicks);
            }

            var barlineMap = ScorePreviewLayoutHelper.BuildBarlineMap(project.ExpressionMarks, measureTicks, totalMeasureCount);
            var keyChangeMap = BuildKeyChangeMap(project.KeySignatureChanges, measureTicks, totalMeasureCount);

            var upperTokensByMeasure = new List<List<NativeToken>>(totalMeasureCount);
            var lowerTokensByMeasure = new List<List<NativeToken>>(totalMeasureCount);
            var measureWidths = new List<int>(totalMeasureCount);
            for (int i = 0; i < totalMeasureCount; i++)
            {
                int measureStartTick = i * measureTicks;
                var upperRenderTokens = BuildRenderTokens(upperMeasures[i], ppq, measureStartTick, beatTicks);
                var upperTokens = ConvertToNativeTokens(upperRenderTokens);
                upperTokensByMeasure.Add(upperTokens);

                List<NativeToken> lowerTokens;
                if (dualStaff && lowerMeasures != null && i < lowerMeasures.Count)
                {
                    var lowerRenderTokens = BuildRenderTokens(lowerMeasures[i], ppq, measureStartTick, beatTicks);
                    lowerTokens = ConvertToNativeTokens(lowerRenderTokens);
                }
                else
                {
                    lowerTokens = new List<NativeToken> { new NativeToken { Text = "0", WidthScale = 1f } };
                }

                lowerTokensByMeasure.Add(lowerTokens);

                int estimateUpper = EstimateCompactMeasureRenderWidthPx(upperMeasures[i], ppq);
                int estimateLower = dualStaff && lowerMeasures != null && i < lowerMeasures.Count
                    ? EstimateCompactMeasureRenderWidthPx(lowerMeasures[i], ppq)
                    : 82;
                int visualBoost = Math.Max(
                    upperTokens.Count == 0 ? 0 : upperTokens.Max(t => t.UnderlineCount),
                    lowerTokens.Count == 0 ? 0 : lowerTokens.Max(t => t.UnderlineCount));
                int baseEstimate = Math.Max(estimateUpper, estimateLower);
                int width = Math.Max(100, (int)Math.Round(baseEstimate * 1.12 + visualBoost * 7));
                measureWidths.Add(width);
            }

            var lines = BuildMeasureLines(
                measureWidths,
                (int)Math.Max(680f, maxLineWidth),
                keyChangeMap);
            var systems = new List<NativeSystem>(lines.Count);
            float maxSystemRenderWidth = 900f;

            foreach (var line in lines)
            {
                if (line.Count == 0)
                {
                    continue;
                }

                int firstMeasure = line[0];
                string leftBar = ResolveBarlineText(firstMeasure, barlineMap);
                keyChangeMap.TryGetValue(firstMeasure, out string? leftKey);

                var measures = new List<NativeMeasure>(line.Count);
                float systemWidth = 0f;
                systemWidth += string.IsNullOrWhiteSpace(leftKey) ? 0f : 56f;
                systemWidth += 22f;

                foreach (int measureIndex in line)
                {
                    float width = Math.Max(88f, measureWidths[measureIndex]);
                    keyChangeMap.TryGetValue(measureIndex + 1, out string? rightKey);
                    string rightBar = ResolveBarlineText(measureIndex + 1, barlineMap);
                    measures.Add(new NativeMeasure
                    {
                        Width = width,
                        MeasureTicks = measureTicks,
                        BeatTicks = beatTicks,
                        UpperTokens = upperTokensByMeasure[measureIndex],
                        LowerTokens = lowerTokensByMeasure[measureIndex],
                        RightBarText = rightBar,
                        RightKeyText = rightKey
                    });

                    systemWidth += width;
                    systemWidth += string.IsNullOrWhiteSpace(rightKey) ? 0f : 56f;
                    systemWidth += 22f;
                }

                maxSystemRenderWidth = Math.Max(maxSystemRenderWidth, systemWidth);
                systems.Add(new NativeSystem
                {
                    StartMeasureNumber = firstMeasure + 1,
                    LeftBarText = leftBar,
                    LeftKeyText = leftKey,
                    Measures = measures
                });
            }

            return new NativePreviewModel
            {
                Title = string.IsNullOrWhiteSpace(project.Title) ? "Untitled" : project.Title.Trim(),
                KeyText = keyText,
                MeterText = $"{project.TimeSignature.Numerator}/{project.TimeSignature.Denominator}",
                Bpm = bpm,
                DualStaff = dualStaff,
                Systems = systems,
                ContentWidth = maxSystemRenderWidth + 120f,
                ContentHeight = 130f + systems.Count * 190f + 40f
            };
        }

        private static (List<NoteEvent> upper, List<NoteEvent> lower) SplitNotesByStaff(IReadOnlyList<NoteEvent> notes)
        {
            var upper = new List<NoteEvent>();
            var lower = new List<NoteEvent>();
            foreach (var note in notes)
            {
                if (note == null)
                {
                    continue;
                }

                bool goesUpper = note.PreferTrebleStaff
                    ?? (!note.IsRest
                        ? note.Midi >= 60
                        : note.Midi >= 60);
                if (goesUpper)
                {
                    upper.Add(note);
                }
                else
                {
                    lower.Add(note);
                }
            }

            return (upper, lower);
        }

        private static void PadMeasureList(List<List<JianpuEvent>> measures, int targetCount, int measureTicks)
        {
            if (measures == null)
            {
                return;
            }

            int safeMeasureTicks = Math.Max(1, measureTicks);
            while (measures.Count < targetCount)
            {
                int start = measures.Count * safeMeasureTicks;
                int end = start + safeMeasureTicks;
                measures.Add(new List<JianpuEvent> { new JianpuEvent(start, end, isRest: true) });
            }
        }

        private static List<List<JianpuEvent>> BuildMeasures(IReadOnlyList<JianpuEvent> events, int measureTicks)
        {
            int maxTick = events.Max(e => e.EndTick);
            int measureCount = Math.Max(1, (int)Math.Ceiling(maxTick / (double)Math.Max(1, measureTicks)));
            var byMeasure = new Dictionary<int, List<JianpuEvent>>();

            foreach (var item in events)
            {
                int segStart = item.StartTick;
                while (segStart < item.EndTick)
                {
                    int measureIndex = segStart / measureTicks;
                    int measureEnd = (measureIndex + 1) * measureTicks;
                    int segEnd = Math.Min(item.EndTick, measureEnd);
                    if (!byMeasure.TryGetValue(measureIndex, out var list))
                    {
                        list = new List<JianpuEvent>();
                        byMeasure[measureIndex] = list;
                    }

                    list.Add(new JianpuEvent(segStart, segEnd, item.IsRest, item.Pitches));
                    segStart = segEnd;
                }
            }

            var measures = new List<List<JianpuEvent>>(measureCount);
            for (int measureIndex = 0; measureIndex < measureCount; measureIndex++)
            {
                int localStart = measureIndex * measureTicks;
                int localEnd = localStart + measureTicks;
                int cursor = localStart;
                var merged = new List<JianpuEvent>();

                if (byMeasure.TryGetValue(measureIndex, out var measureItems))
                {
                    foreach (var item in measureItems.OrderBy(e => e.StartTick).ThenBy(e => e.EndTick))
                    {
                        if (item.StartTick > cursor)
                        {
                            merged.Add(new JianpuEvent(cursor, item.StartTick, isRest: true));
                        }

                        merged.Add(item);
                        cursor = Math.Max(cursor, item.EndTick);
                    }
                }

                if (cursor < localEnd)
                {
                    merged.Add(new JianpuEvent(cursor, localEnd, isRest: true));
                }

                measures.Add(merged);
            }

            return measures;
        }

        private static Dictionary<int, string> BuildKeyChangeMap(
            IReadOnlyList<KeySignatureChange>? changes,
            int measureTicks,
            int measureCount)
        {
            var map = new Dictionary<int, string>();
            if (changes == null || changes.Count == 0)
            {
                return map;
            }

            foreach (var change in changes.Where(c => c != null).OrderBy(c => c.Tick))
            {
                int boundaryIndex = ScorePreviewLayoutHelper.ResolveBoundaryIndex(change.Tick, measureTicks, measureCount);
                if (boundaryIndex <= 0)
                {
                    continue;
                }

                var key = new KeySignature(Math.Clamp(change.Fifths, -7, 7), change.Mode);
                map[boundaryIndex] = $"1={GetKeyDescription(key)}";
            }

            return map;
        }

        private static int ResolveMeasuresPerLine(IReadOnlyList<List<JianpuEvent>> measures)
        {
            if (measures == null || measures.Count == 0)
            {
                return DefaultMeasuresPerLine;
            }

            int peakDensity = measures.Max(m => Math.Max(0, m?.Count ?? 0));
            double peakComplexity = measures.Max(EstimateMeasureComplexity);
            double avgComplexity = measures.Average(EstimateMeasureComplexity);

            if (peakDensity >= 20 || peakComplexity >= 18d || avgComplexity >= 11d)
            {
                return 2;
            }

            if (peakDensity >= 13 || peakComplexity >= 12d || avgComplexity >= 8d)
            {
                return 3;
            }

            return 4;
        }

        private static double EstimateMeasureComplexity(IReadOnlyList<JianpuEvent>? measure)
        {
            if (measure == null || measure.Count == 0)
            {
                return 0d;
            }

            double score = 0d;
            foreach (var item in measure)
            {
                if (item.IsRest || item.Pitches.Count == 0)
                {
                    score += 0.65d;
                    continue;
                }

                int pitchCount = Math.Max(1, item.Pitches.Count);
                int accidentalCount = item.Pitches.Count(p => !string.IsNullOrWhiteSpace(p.Accidental));
                score += 0.9d + pitchCount * 0.8d + accidentalCount * 0.35d;
            }

            return score;
        }

        private static void PadMeasuresToFullLines(List<List<JianpuEvent>> measures, int measureTicks, int measuresPerLine)
        {
            if (measures.Count == 0)
            {
                measures.Add(new List<JianpuEvent> { new JianpuEvent(0, Math.Max(1, measureTicks), isRest: true) });
            }

            int safePerLine = Math.Max(1, measuresPerLine);
            int remainder = measures.Count % safePerLine;
            if (remainder == 0)
            {
                return;
            }

            int padCount = safePerLine - remainder;
            for (int i = 0; i < padCount; i++)
            {
                int measureIndex = measures.Count;
                int start = measureIndex * Math.Max(1, measureTicks);
                int end = start + Math.Max(1, measureTicks);
                measures.Add(new List<JianpuEvent> { new JianpuEvent(start, end, isRest: true) });
            }
        }

        private static void MoveFinalBarlineToPaddedTailIfNeeded(
            Dictionary<int, ScorePreviewLayoutHelper.PreviewBoundaryDecoration> barlineMap,
            int originalMeasureCount,
            int paddedMeasureCount)
        {
            if (paddedMeasureCount <= originalMeasureCount)
            {
                return;
            }

            if (!barlineMap.TryGetValue(originalMeasureCount, out var decoration) || !decoration.Final)
            {
                return;
            }

            barlineMap.Remove(originalMeasureCount);
            barlineMap.TryGetValue(paddedMeasureCount, out var tailDecoration);
            barlineMap[paddedMeasureCount] = tailDecoration.WithFinal();
        }

        private static string BuildDocumentHtml(
            string title,
            string keyText,
            int bpm,
            TimeSignature timeSignature,
            IReadOnlyList<List<JianpuEvent>> upperMeasures,
            IReadOnlyList<List<JianpuEvent>>? lowerMeasures,
            bool dualStaff,
            IReadOnlyDictionary<int, ScorePreviewLayoutHelper.PreviewBoundaryDecoration> barlineByBoundary,
            IReadOnlyDictionary<int, string> keyChangeByBoundary,
            int ppq,
            bool darkTheme)
        {
            string safeTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim());
            string safeKey = WebUtility.HtmlEncode(keyText);
            string safeMeter = $"{Math.Max(1, timeSignature.Numerator)}/{timeSignature.Denominator}";
            string themeAttr = darkTheme ? "dark" : "light";
            int noteMinWidth = 18;
            int tokenGap = 5;
            int safePpq = Math.Max(1, ppq);
            int measureTicks = Math.Max(1, timeSignature.TicksPerMeasure(safePpq));
            int measureCount = Math.Max(upperMeasures.Count, lowerMeasures?.Count ?? 0);
            var renderedUpperMeasures = new List<(string Html, int Width)>(measureCount);
            var renderedLowerMeasures = new List<(string Html, int Width)>(measureCount);
            for (int i = 0; i < measureCount; i++)
            {
                var upperMeasure = i < upperMeasures.Count
                    ? upperMeasures[i]
                    : new List<JianpuEvent> { new JianpuEvent(i * measureTicks, (i + 1) * measureTicks, isRest: true) };
                string upperHtml = RenderMeasureHtml(upperMeasure, safePpq, i * measureTicks);
                int upperWidth = EstimateCompactMeasureRenderWidthPx(upperMeasure, safePpq);
                renderedUpperMeasures.Add((upperHtml, upperWidth));

                if (dualStaff)
                {
                    var lowerMeasure = i < (lowerMeasures?.Count ?? 0)
                        ? lowerMeasures![i]
                        : new List<JianpuEvent> { new JianpuEvent(i * measureTicks, (i + 1) * measureTicks, isRest: true) };
                    string lowerHtml = RenderMeasureHtml(lowerMeasure, safePpq, i * measureTicks);
                    int lowerWidth = EstimateCompactMeasureRenderWidthPx(lowerMeasure, safePpq);
                    renderedLowerMeasures.Add((lowerHtml, lowerWidth));
                }
            }

            var baseLineWidths = dualStaff
                ? renderedUpperMeasures.Select((m, i) => Math.Max(m.Width, renderedLowerMeasures[i].Width)).ToArray()
                : renderedUpperMeasures.Select(m => m.Width).ToArray();
            const double printHorizontalStretch = 1.10d;
            var displayLineWidths = baseLineWidths
                .Select(w => (int)Math.Round(Math.Max(82, w) * printHorizontalStretch))
                .ToArray();
            int maxLineWidth = (int)Math.Round(1240d * printHorizontalStretch);
            var lines = BuildMeasureLines(
                displayLineWidths,
                maxLineWidth: maxLineWidth,
                keyChangeByBoundary);

            var sb = new StringBuilder(24576);
            sb.AppendLine("<!doctype html>");
            sb.AppendLine($"<html lang=\"zh-CN\" data-theme=\"{themeAttr}\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\" />");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\" />");
            sb.AppendLine("<meta name=\"color-scheme\" content=\"light dark\" />");
            sb.AppendLine("<title>简谱预览</title>");
            sb.AppendLine("<style>");
            sb.AppendLine($":root{{--note-min-width:{noteMinWidth}px;--token-gap:{tokenGap}px;--bg:transparent;--paper:transparent;--ink:#121212;--sub:#4d4d4d;--bar:#000;--line:#000;--scroll-track:rgba(20,20,20,.08);--scroll-thumb:rgba(20,20,20,.42);}}");
            sb.AppendLine("html[data-theme='dark']{--bg:transparent;--paper:transparent;--ink:#f3f4f6;--sub:#b4b8c0;--bar:#fff;--line:#fff;--scroll-track:rgba(255,255,255,.10);--scroll-thumb:rgba(255,255,255,.42);}");
            sb.AppendLine("@media (prefers-color-scheme:dark){html:not([data-theme='light']){--bg:transparent;--paper:transparent;--ink:#f3f4f6;--sub:#b4b8c0;--bar:#fff;--line:#fff;--scroll-track:rgba(255,255,255,.10);--scroll-thumb:rgba(255,255,255,.42);}}");
            sb.AppendLine("@page{size:A4 portrait;margin:7mm 4.5mm 6.5mm;}");
            sb.AppendLine("html,body{margin:0;padding:0;height:100%;background:transparent !important;}");
            sb.AppendLine("body{overflow-y:auto;overflow-x:hidden;background:transparent !important;color:var(--ink);font-family:'Source Han Serif SC','Noto Serif CJK SC','Songti SC','SimSun',serif;}");
            sb.AppendLine("html::-webkit-scrollbar{height:12px;width:12px;}");
            sb.AppendLine("html::-webkit-scrollbar-track{background:var(--scroll-track);}");
            sb.AppendLine("html::-webkit-scrollbar-thumb{background:var(--scroll-thumb);border-radius:8px;}");
            sb.AppendLine("body::-webkit-scrollbar{height:12px;width:12px;}");
            sb.AppendLine("body::-webkit-scrollbar-track{background:var(--scroll-track);}");
            sb.AppendLine("body::-webkit-scrollbar-thumb{background:var(--scroll-thumb);border-radius:8px;}");
            sb.AppendLine($":root{{--measure-start-offset:{(dualStaff ? 72 : 18)}px;}}");
            sb.AppendLine(".paper{width:min(2140px,calc(100vw - 1px));margin:0 auto;background:var(--paper);padding:16px 1px 6px;box-sizing:border-box;}");
            sb.AppendLine(".title{text-align:center;font-size:26px;line-height:1.13;font-weight:700;letter-spacing:.2px;margin:0 0 18px;font-family:'Microsoft YaHei UI','Segoe UI','Source Han Sans SC',sans-serif;}");
            sb.AppendLine(".content{width:max-content;max-width:100%;margin:0 auto;}");
            sb.AppendLine(".meta{display:flex;justify-content:flex-start;align-items:flex-end;gap:11px;width:100%;text-align:left;margin:0 0 13px 0;padding-left:var(--measure-start-offset);font-size:16px;font-weight:600;font-family:'Times New Roman','Source Han Serif SC',serif;}");
            sb.AppendLine(".meta .small{font-size:15px;}");
            sb.AppendLine(".score{display:flex;flex-direction:column;align-items:stretch;gap:9px;overflow:visible;padding-bottom:2px;max-width:100%;width:max-content;margin:0;}");
            sb.AppendLine(".jp-line{display:flex;align-items:flex-start;flex-wrap:nowrap;gap:0;page-break-inside:avoid;max-width:100%;width:max-content;margin:0;}");
            sb.AppendLine(".jp-system{display:flex;align-items:flex-start;gap:3px;}");
            sb.AppendLine(".brace-col{display:flex;align-items:center;justify-content:center;min-width:24px;margin-top:15px;transform:translateX(8px);}");
            sb.AppendLine(".brace{display:inline-block;transform:scaleX(.72);transform-origin:center top;font-family:'Times New Roman','Noto Serif SC',serif;font-size:70px;line-height:.76;color:var(--bar);}");
            sb.AppendLine(".staff-col{display:flex;flex-direction:column;gap:4px;}");
            sb.AppendLine(".key-change{display:inline-flex;align-items:flex-end;justify-content:center;flex:0 0 auto;min-width:52px;font-family:'Times New Roman','Noto Serif SC',serif;font-size:16px;line-height:1;color:var(--sub);margin-top:4px;margin-right:2px;}");
            sb.AppendLine(".key-change.placeholder{visibility:hidden;}");
            sb.AppendLine(".bar{display:inline-flex;align-items:center;justify-content:center;flex:0 0 auto;min-width:17px;font-family:'Times New Roman',serif;font-size:28px;line-height:1;margin-top:10px;color:var(--bar);}");
            sb.AppendLine(".measure-wrap{width:auto;flex:0 0 auto;display:flex;flex-direction:column;align-items:stretch;}");
            sb.AppendLine(".measure{display:inline-flex;align-items:flex-start;gap:0;min-height:58px;width:max-content;padding:2px 6px 0 6px;box-sizing:border-box;}");
            sb.AppendLine(".measure-grid{display:inline-flex;flex-direction:column;align-items:flex-start;gap:0;width:max-content;}");
            sb.AppendLine(".measure-notes{display:inline-flex;align-items:flex-start;gap:var(--token-gap);width:max-content;}");
            sb.AppendLine(".token-slot{display:inline-flex;flex-direction:column;align-items:center;justify-content:flex-start;min-width:var(--note-min-width);}");
            sb.AppendLine(".jp-note{display:grid;grid-template-rows:7px auto auto;justify-items:center;align-items:start;min-width:100%;}");
            sb.AppendLine(".jp-note .oct{height:5px;min-height:5px;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;font-size:8px;line-height:1;white-space:normal;overflow:visible;}");
            sb.AppendLine(".jp-note .oct.up{grid-row:1;}");
            sb.AppendLine(".jp-note .oct .dot{display:block;height:2px;line-height:2px;}");
            sb.AppendLine(".jp-note .oct .dot + .dot{margin-top:1px;}");
            sb.AppendLine(".jp-note .mid{grid-row:2;display:inline-flex;align-items:center;line-height:1.02;font-size:24px;font-weight:600;font-family:'Times New Roman','Noto Serif SC',serif;white-space:pre;}");
            sb.AppendLine(".jp-note .acc{font-size:10.5px;line-height:1;position:relative;top:-7px;margin-right:.5px;}");
            sb.AppendLine(".jp-note .acc-inline{display:inline-block;font-size:.56em;line-height:1;position:relative;top:-.50em;margin-right:.04em;}");
            sb.AppendLine(".jp-note .deg{letter-spacing:.1px;}");
            sb.AppendLine(".jp-note .ext{display:inline-flex;align-items:center;font-size:22px;font-weight:500;margin-left:1px;letter-spacing:.4px;line-height:1;}");
            sb.AppendLine(".jp-note .under{display:none;}");
            sb.AppendLine(".jp-note .under .u{display:block;width:100%;border-top:1px solid var(--line);height:0;}");
            sb.AppendLine(".beam-stack{display:flex;flex-direction:column;align-items:flex-start;gap:1px;margin-top:-1px;}");
            sb.AppendLine(".beam-row{display:inline-flex;align-items:flex-start;gap:0;}");
            sb.AppendLine(".beam-seg{display:block;width:calc(var(--note-min-width) + var(--token-gap));height:.5px;background:transparent;}");
            sb.AppendLine(".beam-seg.on{background:var(--line);}");
            sb.AppendLine(".oct-down-row{display:inline-flex;align-items:flex-start;gap:var(--token-gap);margin-top:0;}");
            sb.AppendLine(".oct-down-slot{display:inline-flex;flex-direction:column;align-items:center;justify-content:flex-start;min-width:var(--note-min-width);height:7px;font-size:8px;line-height:1;}");
            sb.AppendLine(".oct-down-slot .dot{display:block;height:2px;line-height:2px;}");
            sb.AppendLine(".oct-down-slot .dot + .dot{margin-top:1px;}");
            sb.AppendLine(".jp-note.chord .mid{display:inline-flex;align-items:flex-end;line-height:1;}");
            sb.AppendLine(".jp-note.chord .chord-stack{display:inline-flex;flex-direction:column;align-items:center;gap:4px;}");
            sb.AppendLine(".jp-note.chord .chord-row{display:grid;grid-template-columns:auto auto auto;align-items:center;min-height:22px;}");
            sb.AppendLine(".jp-note.chord .chord-oct{display:flex;flex-direction:column;align-items:center;justify-content:center;min-width:8px;height:14px;font-size:8px;line-height:1;}");
            sb.AppendLine(".jp-note.chord .chord-oct .dot{display:block;height:2px;line-height:2px;}");
            sb.AppendLine(".jp-note.chord .chord-oct .dot + .dot{margin-top:3px;}");
            sb.AppendLine(".jp-note.chord .chord-core{display:inline-flex;align-items:center;justify-content:center;font-size:21px;line-height:1;font-weight:600;font-family:'Times New Roman','Noto Serif SC',serif;}");
            sb.AppendLine(".jp-note.chord .chord-core .acc{font-size:10px;line-height:1;position:relative;top:-5px;margin-right:.4px;}");
            sb.AppendLine(".jp-note.chord .chord-core .deg{display:inline-block;white-space:nowrap;text-align:center;line-height:1;}");
            sb.AppendLine(".jp-note.chord .chord-ext{margin-left:2px;font-size:20px;line-height:1;}");
            sb.AppendLine("@media print{html[data-theme='dark'],html[data-theme='light']{--bg:#fff;--paper:#fff;--ink:#121212;--sub:#4d4d4d;--bar:#1a1a1a;--line:#222;}body{background:#fff;color:#121212;}.paper{width:auto;margin:0;padding:0 .8mm .4mm;}.score{gap:8px;transform:scaleY(0.9);transform-origin:left top;}.staff-col{gap:4px;}.jp-line{margin:0;}}");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"paper\">");
            sb.AppendLine($"<div class=\"title\">{safeTitle}</div>");
            sb.AppendLine("<div class=\"content\">");
            sb.AppendLine($"<div class=\"meta\"><span>1={safeKey}</span><span>{safeMeter}</span><span class=\"small\">{bpm} BPM</span></div>");
            sb.AppendLine("<div class=\"score\">");

            foreach (var line in lines)
            {
                if (line.Count == 0)
                {
                    continue;
                }

                int firstMeasure = line[0];
                if (dualStaff)
                {
                    sb.AppendLine("<div class=\"jp-system\">");
                    sb.AppendLine("<div class=\"brace-col\"><span class=\"brace\">{</span></div>");
                    sb.AppendLine("<div class=\"staff-col\">");
                    sb.AppendLine("<div class=\"jp-line\">");
                    sb.AppendLine(RenderBoundaryPrefixHtml(firstMeasure, keyChangeByBoundary, placeholderOnly: false));
                    sb.AppendLine(RenderBarlineHtml(firstMeasure, barlineByBoundary));
                    foreach (int measureIndex in line)
                    {
                        int alignedWidth = Math.Max(82, displayLineWidths[measureIndex]);
                        sb.AppendLine("<div class=\"measure-wrap\">");
                        sb.AppendLine($"<div class=\"measure\" style=\"width:{alignedWidth}px;\">{renderedUpperMeasures[measureIndex].Html}</div>");
                        sb.AppendLine("</div>");
                        int nextBoundary = measureIndex + 1;
                        sb.AppendLine(RenderBoundaryPrefixHtml(nextBoundary, keyChangeByBoundary, placeholderOnly: false));
                        sb.AppendLine(RenderBarlineHtml(nextBoundary, barlineByBoundary));
                    }

                    sb.AppendLine("</div>");
                    sb.AppendLine("<div class=\"jp-line\">");
                    sb.AppendLine(RenderBoundaryPrefixHtml(firstMeasure, keyChangeByBoundary, placeholderOnly: true));
                    sb.AppendLine(RenderBarlineHtml(firstMeasure, barlineByBoundary));
                    foreach (int measureIndex in line)
                    {
                        int alignedWidth = Math.Max(82, displayLineWidths[measureIndex]);
                        sb.AppendLine("<div class=\"measure-wrap\">");
                        sb.AppendLine($"<div class=\"measure\" style=\"width:{alignedWidth}px;\">{renderedLowerMeasures[measureIndex].Html}</div>");
                        sb.AppendLine("</div>");
                        int nextBoundary = measureIndex + 1;
                        sb.AppendLine(RenderBoundaryPrefixHtml(nextBoundary, keyChangeByBoundary, placeholderOnly: true));
                        sb.AppendLine(RenderBarlineHtml(nextBoundary, barlineByBoundary));
                    }

                    sb.AppendLine("</div>");
                    sb.AppendLine("</div>");
                    sb.AppendLine("</div>");
                }
                else
                {
                    sb.AppendLine("<div class=\"jp-line\">");
                    sb.AppendLine(RenderBoundaryPrefixHtml(firstMeasure, keyChangeByBoundary, placeholderOnly: false));
                    sb.AppendLine(RenderBarlineHtml(firstMeasure, barlineByBoundary));
                    foreach (int measureIndex in line)
                    {
                        int alignedWidth = Math.Max(82, displayLineWidths[measureIndex]);
                        sb.AppendLine("<div class=\"measure-wrap\">");
                        sb.AppendLine($"<div class=\"measure\" style=\"width:{alignedWidth}px;\">{renderedUpperMeasures[measureIndex].Html}</div>");
                        sb.AppendLine("</div>");
                        int nextBoundary = measureIndex + 1;
                        sb.AppendLine(RenderBoundaryPrefixHtml(nextBoundary, keyChangeByBoundary, placeholderOnly: false));
                        sb.AppendLine(RenderBarlineHtml(nextBoundary, barlineByBoundary));
                    }

                    sb.AppendLine("</div>");
                }
            }

            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private static string RenderBarlineHtml(int boundaryIndex, IReadOnlyDictionary<int, ScorePreviewLayoutHelper.PreviewBoundaryDecoration> barlineByBoundary)
        {
            string text = ResolveBarlineText(boundaryIndex, barlineByBoundary);
            return $"<span class=\"bar\">{WebUtility.HtmlEncode(text)}</span>";
        }

        private static string ResolveBarlineText(int boundaryIndex, IReadOnlyDictionary<int, ScorePreviewLayoutHelper.PreviewBoundaryDecoration> barlineByBoundary)
        {
            barlineByBoundary.TryGetValue(boundaryIndex, out var mark);
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

        private static string RenderBoundaryPrefixHtml(
            int boundaryIndex,
            IReadOnlyDictionary<int, string> keyChangeByBoundary,
            bool placeholderOnly)
        {
            if (keyChangeByBoundary == null
                || !keyChangeByBoundary.TryGetValue(boundaryIndex, out string? label)
                || string.IsNullOrWhiteSpace(label))
            {
                return string.Empty;
            }

            if (placeholderOnly)
            {
                return "<span class=\"key-change placeholder\">&nbsp;</span>";
            }

            return $"<span class=\"key-change\">{WebUtility.HtmlEncode(label)}</span>";
        }

        private static List<RenderToken> BuildRenderTokens(
            IReadOnlyList<JianpuEvent> events,
            int ppq,
            int measureStartTick,
            int beatTicks)
        {
            var tokens = new List<RenderToken>();
            int safeBeatTicks = Math.Max(1, beatTicks);
            int safePpq = Math.Max(1, ppq);

            if (events != null)
            {
                foreach (var item in events)
                {
                    DurationVisual duration = GetDurationVisual(item.DurationTicks, ppq);
                    int localTick = Math.Max(0, item.StartTick - measureStartTick);
                    int localTickInMeasure = localTick;
                    bool onBeatBoundary = localTickInMeasure % safeBeatTicks == 0;
                    double durationWeight = duration.UnderlineCount switch
                    {
                        0 => 1.28d,
                        1 => 0.96d,
                        2 => 0.78d,
                        _ => 0.64d
                    };
                    durationWeight += Math.Min(0.32d, duration.ExtendCount * 0.09d);
                    if (onBeatBoundary)
                    {
                        durationWeight += 0.10d;
                    }

                    // Slightly up-weight clearly long events so time-axis spacing leaves them more room.
                    durationWeight += Math.Clamp(item.DurationTicks / (double)safePpq - 1d, 0d, 1.3d) * 0.14d;

                    RenderToken token;
                    if (item.IsRest || item.Pitches.Count == 0)
                    {
                        token = new RenderToken(string.Empty, "0", 0, 0, duration, false, null, localTickInMeasure, item.DurationTicks, durationWeight);
                    }
                    else if (item.Pitches.Count == 1)
                    {
                        var pitch = item.Pitches[0];
                        int topDots = pitch.Suffix.Count(c => c == '\'');
                        int downDots = pitch.Prefix.Count(c => c == ',');
                        token = new RenderToken(
                            pitch.Accidental,
                            pitch.Degree,
                            topDots,
                            downDots,
                            duration,
                            false,
                            null,
                            localTickInMeasure,
                            item.DurationTicks,
                            durationWeight);
                    }
                    else
                    {
                        var chordPitches = item.Pitches
                            .Reverse()
                            .ToArray();

                        token = new RenderToken(string.Empty, "0", 0, 0, duration, true, chordPitches, localTickInMeasure, item.DurationTicks, durationWeight + 0.08d);
                    }

                    tokens.Add(token);
                }
            }

            tokens.Sort((a, b) => a.LocalTickInBeat.CompareTo(b.LocalTickInBeat));
            if (tokens.Count == 0)
            {
                tokens.Add(new RenderToken(string.Empty, "0", 0, 0, new DurationVisual(0, 0), false, null, 0, safePpq, 1d));
            }

            return tokens;
        }

        private static List<NativeToken> ConvertToNativeTokens(IReadOnlyList<RenderToken> tokens)
        {
            var native = new List<NativeToken>(tokens?.Count ?? 0);
            if (tokens == null || tokens.Count == 0)
            {
                native.Add(new NativeToken
                {
                    Text = "0",
                    WidthScale = 1f
                });
                return native;
            }

            foreach (var token in tokens)
            {
                int extendCount = Math.Max(0, token.Duration.ExtendCount);
                bool isChord = token.IsChord && token.ChordPitches.Count > 1;
                string extend = isChord ? string.Empty : (extendCount > 0 ? new string('-', Math.Min(6, extendCount)) : string.Empty);
                string text = isChord ? "0" : $"{token.Accidental}{token.DegreeText}{extend}";
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = "0";
                }

                var nativeChord = isChord
                    ? token.ChordPitches
                        .Select(p => new NativeChordPitch
                        {
                            Accidental = p.Accidental ?? string.Empty,
                            Degree = string.IsNullOrWhiteSpace(p.Degree) ? "0" : p.Degree,
                            TopDots = Math.Max(0, p.Suffix.Count(c => c == '\'')),
                            BottomDots = Math.Max(0, p.Prefix.Count(c => c == ','))
                        })
                        .ToList()
                    : new List<NativeChordPitch>();

                native.Add(new NativeToken
                {
                    Text = text,
                    TopDots = Math.Max(0, token.TopDots),
                    BottomDots = Math.Max(0, token.DownDots),
                    ExtendCount = extendCount,
                    UnderlineCount = Math.Max(0, token.Duration.UnderlineCount),
                    TickInMeasure = Math.Max(0, token.LocalTickInBeat),
                    DurationTicks = Math.Max(1, token.DurationTicks),
                    BeatWeight = (float)Math.Max(0.2d, token.BeatWeight),
                    WidthScale = ResolveNativeTokenWidthScale(token.Duration.UnderlineCount),
                    ChordPitches = nativeChord
                });
            }

            return native;
        }

        private static float ResolveNativeTokenWidthScale(int underlineCount)
        {
            return underlineCount switch
            {
                >= 3 => 0.74f,
                2 => 0.84f,
                1 => 0.93f,
                _ => 1f
            };
        }

        private static string RenderMeasureHtml(
            IReadOnlyList<JianpuEvent> events,
            int ppq,
            int measureStartTick)
        {
            var tokens = BuildRenderTokens(events, ppq, measureStartTick, Math.Max(1, ppq));

            var sb = new StringBuilder(1024);
            sb.Append("<span class=\"measure-grid\">");
            sb.Append("<span class=\"measure-notes\">");
            foreach (var token in tokens)
            {
                string slotWidth = BuildSlotWidthCss(token.Duration.UnderlineCount);
                sb.Append($"<span class=\"token-slot\" style=\"min-width:{slotWidth};\">");
                sb.Append(RenderNoteTokenHtml(
                    token.Accidental,
                    token.DegreeText,
                    token.TopDots,
                    token.Duration,
                    token.IsChord,
                    token.ChordPitches,
                    underlineOverride: 0));
                sb.Append("</span>");
            }

            sb.Append("</span>");

            int maxUnderline = tokens.Max(t => Math.Max(0, t.Duration.UnderlineCount));
            if (maxUnderline > 0)
            {
                sb.Append("<span class=\"beam-stack\">");
                for (int level = 1; level <= maxUnderline; level++)
                {
                    sb.Append("<span class=\"beam-row\">");
                    foreach (var token in tokens)
                    {
                        bool on = token.Duration.UnderlineCount >= level;
                        string segWidth = BuildBeamSegmentWidthCss(token.Duration.UnderlineCount);
                        sb.Append($"<span class=\"beam-seg {(on ? "on" : "off")}\" style=\"width:{segWidth};\"></span>");
                    }

                    sb.Append("</span>");
                }

                sb.Append("</span>");
            }

            int maxDownDots = tokens.Max(t => Math.Max(0, t.DownDots));
            if (maxDownDots > 0)
            {
                sb.Append("<span class=\"oct-down-row\">");
                foreach (var token in tokens)
                {
                    sb.Append("<span class=\"oct-down-slot\">");
                    sb.Append(BuildOctaveDotsHtml(token.DownDots));
                    sb.Append("</span>");
                }

                sb.Append("</span>");
            }

            sb.Append("</span>");
            return sb.ToString();
        }

        private static string BuildSlotWidthCss(int underlineCount)
        {
            string factor = underlineCount switch
            {
                >= 3 => "0.62",
                2 => "0.74",
                1 => "0.86",
                _ => "1"
            };

            return factor == "1"
                ? "var(--note-min-width)"
                : $"calc(var(--note-min-width) * {factor})";
        }

        private static string BuildBeamSegmentWidthCss(int underlineCount)
        {
            string factor = underlineCount switch
            {
                >= 3 => "0.62",
                2 => "0.74",
                1 => "0.86",
                _ => "1"
            };

            return factor == "1"
                ? "calc(var(--note-min-width) + var(--token-gap))"
                : $"calc(var(--note-min-width) * {factor} + var(--token-gap))";
        }

        private static List<List<int>> BuildMeasureLines(
            IReadOnlyList<int> measureWidths,
            int maxLineWidth,
            IReadOnlyDictionary<int, string>? keyChangeByBoundary = null,
            int barlineWidth = NativeBarlineRenderWidthPx,
            int keyLabelWidth = NativeKeyLabelRenderWidthPx,
            int lineRightPadding = NativeLineRightPaddingPx)
        {
            int safeMaxWidth = Math.Max(680, maxLineWidth);
            var lines = new List<List<int>>();
            var current = new List<int>();
            int currentWidth = 0;
            int safeBarWidth = Math.Max(12, barlineWidth);
            int safeKeyWidth = Math.Max(36, keyLabelWidth);
            int safeRightPadding = Math.Max(6, lineRightPadding);

            int ResolveBoundaryPrefixWidth(int boundaryIndex)
            {
                int width = safeBarWidth;
                if (keyChangeByBoundary != null
                    && keyChangeByBoundary.TryGetValue(boundaryIndex, out string? label)
                    && !string.IsNullOrWhiteSpace(label))
                {
                    width += safeKeyWidth;
                }

                return width;
            }

            int ResolveMeasureSegmentWidth(int measureIndex)
            {
                int nextBoundary = measureIndex + 1;
                int width = Math.Max(88, measureWidths[measureIndex]);
                width += ResolveBoundaryPrefixWidth(nextBoundary);
                return width;
            }

            for (int i = 0; i < measureWidths.Count;)
            {
                if (current.Count == 0)
                {
                    currentWidth = ResolveBoundaryPrefixWidth(i);
                }

                int segmentWidth = ResolveMeasureSegmentWidth(i);
                bool wouldOverflow = current.Count > 0 && currentWidth + segmentWidth + safeRightPadding > safeMaxWidth;
                if (wouldOverflow)
                {
                    lines.Add(current);
                    current = new List<int>();
                    currentWidth = 0;
                    continue;
                }

                current.Add(i);
                currentWidth += segmentWidth;
                i++;
            }

            if (current.Count > 0)
            {
                lines.Add(current);
            }

            if (lines.Count == 0)
            {
                lines.Add(new List<int>());
            }

            return lines;
        }

        private static int EstimateCompactMeasureRenderWidthPx(IReadOnlyList<JianpuEvent>? events, int ppq)
        {
            if (events == null || events.Count == 0)
            {
                return 96;
            }

            int width = 32;
            foreach (var item in events)
            {
                DurationVisual duration = GetDurationVisual(item.DurationTicks, ppq);
                int accidentalCount = item.IsRest ? 0 : item.Pitches.Count(p => !string.IsNullOrWhiteSpace(p.Accidental));
                int pitchSpan = Math.Max(1, item.Pitches.Count);
                width += 14
                    + duration.UnderlineCount * 3
                    + duration.ExtendCount * 5
                    + accidentalCount * 4
                    + Math.Max(0, pitchSpan - 1) * 5;
            }

            return Math.Clamp(width, 96, 360);
        }

        private static string RenderNoteTokenHtml(
            string accidental,
            string degreeText,
            int topDots,
            DurationVisual duration,
            bool isChord,
            IReadOnlyList<PitchToken>? chordPitches,
            int underlineOverride = -1)
        {
            string safeAcc = WebUtility.HtmlEncode(accidental ?? string.Empty);
            string safeDegree = FormatDegreeTextHtml(degreeText);
            string top = BuildOctaveDotsHtml(topDots);
            string extend = duration.ExtendCount > 0 ? new string('-', Math.Min(6, duration.ExtendCount)) : string.Empty;

            bool renderAsChord = isChord && chordPitches != null && chordPitches.Count > 1;
            var sb = new StringBuilder(192);
            sb.Append(renderAsChord ? "<span class=\"jp-note chord\">" : "<span class=\"jp-note\">");
            if (renderAsChord)
            {
                sb.Append("<span class=\"oct up\"></span>");
                sb.Append("<span class=\"mid chord-mid\">");
                sb.Append("<span class=\"chord-stack\">");
                foreach (var pitch in chordPitches!)
                {
                    string pitchAcc = WebUtility.HtmlEncode(pitch.Accidental ?? string.Empty);
                    string pitchDegree = FormatDegreeTextHtml(pitch.Degree);
                    string pitchTop = BuildOctaveDotsHtml(pitch.Suffix.Count(c => c == '\''));
                    string pitchBottom = BuildOctaveDotsHtml(pitch.Prefix.Count(c => c == ','));
                    sb.Append("<span class=\"chord-row\">");
                    sb.Append($"<span class=\"chord-oct up\">{pitchTop}</span>");
                    sb.Append("<span class=\"chord-core\">");
                    if (!string.IsNullOrEmpty(pitchAcc))
                    {
                        sb.Append($"<span class=\"acc\">{pitchAcc}</span>");
                    }

                    sb.Append($"<span class=\"deg\">{pitchDegree}</span>");
                    sb.Append("</span>");
                    sb.Append($"<span class=\"chord-oct down\">{pitchBottom}</span>");
                    sb.Append("</span>");
                }

                sb.Append("</span>");
                if (!string.IsNullOrEmpty(extend))
                {
                    sb.Append($"<span class=\"ext chord-ext\">{extend}</span>");
                }

                sb.Append("</span>");
            }
            else
            {
                sb.Append($"<span class=\"oct up\">{top}</span>");
                sb.Append("<span class=\"mid\">");
                if (!string.IsNullOrEmpty(safeAcc))
                {
                    sb.Append($"<span class=\"acc\">{safeAcc}</span>");
                }

                sb.Append($"<span class=\"deg\">{safeDegree}</span>");
                if (!string.IsNullOrEmpty(extend))
                {
                    sb.Append($"<span class=\"ext\">{extend}</span>");
                }

                sb.Append("</span>");
            }

            int underlineCount = underlineOverride >= 0 ? underlineOverride : duration.UnderlineCount;
            sb.Append("<span class=\"under\">");
            for (int i = 0; i < underlineCount; i++)
            {
                sb.Append("<span class=\"u\"></span>");
            }

            sb.Append("</span>");
            sb.Append("</span>");
            return sb.ToString();
        }

        private static string FormatDegreeTextHtml(string? degreeText)
        {
            string source = string.IsNullOrWhiteSpace(degreeText) ? "0" : degreeText;
            var sb = new StringBuilder(source.Length * 6);
            foreach (char ch in source)
            {
                if (ch == '#' || ch == '♯' || ch == 'b' || ch == '♭' || ch == '♮')
                {
                    sb.Append("<span class=\"acc-inline\">");
                    sb.Append(WebUtility.HtmlEncode(ch.ToString()));
                    sb.Append("</span>");
                }
                else
                {
                    sb.Append(WebUtility.HtmlEncode(ch.ToString()));
                }
            }

            return sb.ToString();
        }

        private static string BuildOctaveDotsHtml(int dotCount)
        {
            int count = Math.Clamp(dotCount, 0, 3);
            if (count <= 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(48);
            for (int i = 0; i < count; i++)
            {
                sb.Append("<span class=\"dot\">&middot;</span>");
            }

            return sb.ToString();
        }

        private static DurationVisual GetDurationVisual(int durationTicks, int ppq)
        {
            double ratio = durationTicks / (double)Math.Max(1, ppq);
            if (ratio >= 1d)
            {
                int beats = Math.Max(1, (int)Math.Round(ratio));
                return new DurationVisual(Math.Max(0, beats - 1), 0);
            }

            if (ratio >= 0.95d)
            {
                return new DurationVisual(0, 0);
            }

            if (ratio >= 0.47d)
            {
                return new DurationVisual(0, 1);
            }

            if (ratio >= 0.235d)
            {
                return new DurationVisual(0, 2);
            }

            return new DurationVisual(0, 3);
        }

        private static string BuildEmptyHtml(string message, bool darkTheme)
        {
            string safeMessage = WebUtility.HtmlEncode(message ?? "无内容。");
            string themeAttr = darkTheme ? "dark" : "light";
            return "<!doctype html>\n"
                + $"<html lang=\"zh-CN\" data-theme=\"{themeAttr}\">\n"
                + "<head>\n"
                + "<meta charset=\"utf-8\" />\n"
                + "<meta name=\"color-scheme\" content=\"light dark\" />\n"
                + "<style>\n"
                + ":root{--bg:transparent;--ink:#222;}\n"
                + "html[data-theme='dark']{--bg:transparent;--ink:#f3f4f6;}\n"
                + "@media (prefers-color-scheme:dark){html:not([data-theme='light']){--bg:transparent;--ink:#f3f4f6;}}\n"
                + "html,body{margin:0;padding:0;background:transparent !important;}\n"
                + "body{padding:16px;color:var(--ink);font-family:'Microsoft YaHei UI',sans-serif;}\n"
                + ".empty{font-size:14px;opacity:.92;}\n"
                + "</style>\n"
                + "</head>\n"
                + $"<body><div class=\"empty\">{safeMessage}</div></body>\n"
                + "</html>\n";
        }

        private static int GetVisualDurationTicks(NoteEvent note)
        {
            if (note == null) return 1;

            int baseDuration = note.BaseDurationTicks > 0 ? note.BaseDurationTicks : note.DurationTicks;
            return Math.Max(1, baseDuration);
        }

        private static PitchToken ToPitchToken(int midi, int tonicPitchClass, int tonicMidiRef, bool preferFlat)
        {
            int safeMidi = Math.Clamp(midi, 0, 127);
            int offset = Mod12(safeMidi - tonicPitchClass);
            (string accidental, string degree) core = ToDegreeCore(offset, preferFlat);

            int octaveShift = (int)Math.Floor((safeMidi - tonicMidiRef) / 12.0);
            string prefix = octaveShift < 0 ? new string(',', Math.Min(3, -octaveShift)) : string.Empty;
            string suffix = octaveShift > 0 ? new string('\'', Math.Min(3, octaveShift)) : string.Empty;

            return new PitchToken(prefix, core.accidental, core.degree, suffix);
        }

        private static (string accidental, string degree) ToDegreeCore(int offset, bool preferFlat)
        {
            return offset switch
            {
                0 => ("", "1"),
                1 => preferFlat ? ("♭", "2") : ("♯", "1"),
                2 => ("", "2"),
                3 => preferFlat ? ("♭", "3") : ("♯", "2"),
                4 => ("", "3"),
                5 => ("", "4"),
                6 => preferFlat ? ("♭", "5") : ("♯", "4"),
                7 => ("", "5"),
                8 => preferFlat ? ("♭", "6") : ("♯", "5"),
                9 => ("", "6"),
                10 => preferFlat ? ("♭", "7") : ("♯", "6"),
                11 => ("", "7"),
                _ => ("", "1")
            };
        }

        private static int GetTonicPitchClass(KeySignature? key)
        {
            int[] majorTonicByFifths =
            {
                11, 6, 1, 8, 3, 10, 5, 0, 7, 2, 9, 4, 11, 6, 1
            };
            int fifths = Math.Clamp(key?.Fifths ?? 0, -7, 7);
            int majorTonic = majorTonicByFifths[fifths + 7];
            if (key?.Mode == KeyMode.Minor)
            {
                return Mod12(majorTonic + 9);
            }

            return majorTonic;
        }

        private static int GetReferenceTonicMidi(int tonicPitchClass)
        {
            int refMidi = 60 + Mod12(tonicPitchClass);
            while (refMidi > 65)
            {
                refMidi -= 12;
            }

            while (refMidi < 53)
            {
                refMidi += 12;
            }

            return refMidi;
        }

        private static string GetKeyDescription(KeySignature? key)
        {
            string[] majorByFifths = { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
            string[] minorByFifths = { "Abm", "Ebm", "Bbm", "Fm", "Cm", "Gm", "Dm", "Am", "Em", "Bm", "F#m", "C#m", "G#m", "D#m", "A#m" };
            int fifths = Math.Clamp(key?.Fifths ?? 0, -7, 7);
            return key?.Mode == KeyMode.Minor ? minorByFifths[fifths + 7] : majorByFifths[fifths + 7];
        }

        private static int Mod12(int value)
        {
            int mod = value % 12;
            return mod < 0 ? mod + 12 : mod;
        }
    }
}
