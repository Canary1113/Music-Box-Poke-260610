using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using MusicBox.Models;

namespace MusicBox.Services
{
    /// <summary>
    /// SmartComposeService provides automated music generation capabilities.
    /// This implementation uses ArrayPool to optimize memory allocation and reduce GC overhead during intensive composition tasks.
    /// 
    /// 智能创作服务：提供自动化的音乐生成功能。
    /// 本实现采用 数组池 (ArrayPool) 优化内存分配，在密集型创作任务中显著降低垃圾回收 (GC) 的开销。
    /// </summary>
    public sealed class SmartComposeService
    {
        private const int ComposeMelodyMinMidi = 60;
        private const int ComposeMelodyMaxMidi = 81;
        private const int ComposeBassMinMidi = 40;
        private const int ComposeBassMaxMidi = 60;
        private const int ComposeBassRootMaxMidi = 55;
        private const int ComposeHarmonyMinMidi = 52;
        private const int ComposeHarmonyMaxMidi = 81;

        private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly int[] MinorScale = { 0, 2, 3, 5, 7, 8, 10 };
        private static readonly string[] SharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        private static readonly string[] FlatNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };
        private static readonly DurationSpec[] DurationCandidates =
        {
            new(NoteLengthUtils.ToTicks(NoteLength.Whole, 480), NoteLengthUtils.ToTicks(NoteLength.Whole, 480), 0),
            new(NoteLengthUtils.ToTicks(NoteLength.Half, 480), NoteLengthUtils.ToTicks(NoteLength.Half, 480), 0),
            new(NoteLengthUtils.ToTicks(NoteLength.Quarter, 480), NoteLengthUtils.ToTicks(NoteLength.Quarter, 480), 0),
            new(NoteLengthUtils.ToTicks(NoteLength.Eighth, 480), NoteLengthUtils.ToTicks(NoteLength.Eighth, 480), 0),
            new(NoteLengthUtils.ToTicks(NoteLength.Sixteenth, 480), NoteLengthUtils.ToTicks(NoteLength.Sixteenth, 480), 0),
            new(NoteLengthUtils.ToTicks(NoteLength.Half, 480), NoteLengthUtils.ToTicks(NoteLength.Half, 480) + NoteLengthUtils.ToTicks(NoteLength.Quarter, 480), 1),
            new(NoteLengthUtils.ToTicks(NoteLength.Quarter, 480), NoteLengthUtils.ToTicks(NoteLength.Quarter, 480) + NoteLengthUtils.ToTicks(NoteLength.Eighth, 480), 1),
            new(NoteLengthUtils.ToTicks(NoteLength.Eighth, 480), NoteLengthUtils.ToTicks(NoteLength.Eighth, 480) + NoteLengthUtils.ToTicks(NoteLength.Sixteenth, 480), 1)
        };

        public SmartComposeResult Generate(SmartComposeRequest request)
        {
            return GenerateCandidates(request, 1).First();
        }

        public IReadOnlyList<SmartComposeResult> GenerateCandidates(SmartComposeRequest request, int count = 3)
        {
            SmartComposeRequest normalized = request ?? new SmartComposeRequest();
            int baseSeed = normalized.Seed == 0 ? Environment.TickCount : normalized.Seed;
            int safeCount = Math.Clamp(count, 1, 18);
            var results = new List<SmartComposeResult>(safeCount);

            for (int index = 0; index < safeCount; index++)
            {
                int candidateSeed = baseSeed + index * 7919;
                SmartComposeRequest candidateRequest = CloneRequest(normalized);
                if (candidateRequest.AutoTonality)
                {
                    (candidateRequest.KeyFifths, candidateRequest.Mode) = ResolveAutoTonality(candidateRequest.MoodId, candidateSeed + 421);
                }

                results.Add(GenerateVariant(candidateRequest, candidateSeed, index));
            }

            return results;
        }

        private static SmartComposeRequest CloneRequest(SmartComposeRequest source)
        {
            return new SmartComposeRequest
            {
                Title = source.Title,
                Bpm = source.Bpm,
                Measures = source.Measures,
                KeyFifths = source.KeyFifths,
                Mode = source.Mode,
                TimeSignature = source.TimeSignature == null
                    ? new TimeSignature(4, 4)
                    : new TimeSignature(source.TimeSignature.Numerator, source.TimeSignature.Denominator),
                StyleId = source.StyleId,
                MoodId = source.MoodId,
                LengthId = source.LengthId,
                IncludeBass = source.IncludeBass,
                Seed = source.Seed,
                AutoTonality = source.AutoTonality,
                UseSustainPedal = source.UseSustainPedal
            };
        }

        private static SmartComposeResult GenerateVariant(SmartComposeRequest request, int seed, int variantIndex)
        {
            var random = new Random(seed);
            TimeSignature timeSignature = request.TimeSignature ?? new TimeSignature(4, 4);
            MoodSpec mood = ResolveMood(request.MoodId);
            StyleSpec style = ResolveStyle(request.StyleId, timeSignature);
            VariantSpec variant = ResolveVariant(variantIndex);
            int ppq = 480;
            int measures = Math.Clamp(request.Measures, 4, 64);
            int ticksPerMeasure = timeSignature.TicksPerMeasure(ppq);
            int measureUnits = ResolveMeasureUnits(timeSignature);
            int unitTicks = Math.Max(1, ticksPerMeasure / measureUnits);
            int[] scale = request.Mode == KeyMode.Minor ? MinorScale : MajorScale;
            int tonicPitchClass = Mod(request.KeyFifths * 7 + (request.Mode == KeyMode.Minor ? 9 : 0), 12);
            int melodyAnchor = ClampMelodyMidi(ClosestMidiToTarget(tonicPitchClass, style.CenterMidi + mood.RangeOffset + variant.MelodyOffset));
            int bassAnchor = ClampBassRootMidi(ClosestMidiToTarget(tonicPitchClass, 43 + variant.BassOffset));
            
            // Optimization using ArrayPool: Buffer for progression array.
            // 使用 ArrayPool 的优化：用于进程数组的缓冲区。
            int[] progressionPool = ArrayPool<int>.Shared.Rent(measures);
            try {
                StructurePlan structure = BuildStructure(style, measures, variant, random, progressionPool);
                int[] progression = structure.Progression;

                string resolvedTitle = string.IsNullOrWhiteSpace(request.Title)
                    ? LocalizationService.Translate("compose.default_title")
                    : request.Title.Trim();

                var project = new ScoreProject
                {
                    Title = string.IsNullOrWhiteSpace(request.Title) ? "智能创作" : request.Title.Trim(),
                    Bpm = Math.Clamp((request.Bpm <= 0 ? style.DefaultTempo : request.Bpm) + mood.TempoOffset + variant.TempoOffset, mood.MinTempo, mood.MaxTempo),
                    TimeSignature = new TimeSignature(timeSignature.Numerator, timeSignature.Denominator),
                    KeySignature = new KeySignature(Math.Clamp(request.KeyFifths, -7, 7), request.Mode),
                    Ppq = ppq,
                    UpdatedAt = DateTimeOffset.Now
                };

                project.Title = resolvedTitle;

                string keyLabel = BuildKeyLabel(request.KeyFifths, request.Mode);
                var chordNames = new List<string>(measures);
                var melodyState = new MelodyState(mood.StartDegree);
                int? previousBassMidi = null;
                int[]? previousHarmony = null;
                int? previousMelodyMidi = null;
                int[]? previousRhythm = null;
                int repeatedRhythmCount = 0;
                var themeMotifs = new Dictionary<ThemeFamily, ThemeMotif>();

                for (int measure = 0; measure < measures; measure++)
                {
                    int startTick = measure * ticksPerMeasure;
                    int chordDegree = progression[measure];
                    SectionPlan section = structure.SectionMap[measure];
                    int measureInSection = measure - section.StartMeasure;
                    bool hasThemeMotif = themeMotifs.TryGetValue(section.Theme, out ThemeMotif? referenceMotif);
                    MeasureRole role = ResolveRole(measureInSection, section.Length, section.IsFinalSection, section.Energy, hasThemeMotif);
                    ChordPlan chordPlan = BuildChordPlan(chordDegree, role, request.Mode, mood, variant, random);
                    chordNames.Add(chordPlan.Name);

                    int[] rhythm = PickRhythm(style, mood, variant, role, measureUnits, previousRhythm, repeatedRhythmCount, measure, measures, random);
                    if (hasThemeMotif
                        && measureInSection == 0
                        && referenceMotif?.Rhythm != null
                        && PatternsEqual(rhythm, referenceMotif.Rhythm))
                    {
                        rhythm = CreateRhythmVariation(rhythm, measureUnits, role);
                    }

                    int bassMidi = bassAnchor;
                    int[] harmonyVoicing = Array.Empty<int>();
                    if (request.IncludeBass)
                    {
                        bassMidi = AddBass(project, startTick, ticksPerMeasure, unitTicks, bassAnchor, tonicPitchClass, scale, chordPlan, previousBassMidi, mood, variant, role, rhythm);
                        harmonyVoicing = AddHarmony(
                            project,
                            startTick,
                            ticksPerMeasure,
                            unitTicks,
                            tonicPitchClass,
                            scale,
                            chordPlan,
                            style,
                            mood,
                            variant,
                            bassMidi,
                            previousBassMidi,
                            previousHarmony,
                            role,
                            rhythm);
                    }

                    List<int> melodyDegrees = BuildMeasureDegrees(
                        rhythm,
                        chordPlan,
                        role,
                        mood,
                        style,
                        variant,
                        melodyState,
                        referenceMotif?.Degrees,
                        referenceMotif?.Rhythm,
                        random);
                    int measureMelodyAnchor = AdjustMelodyAnchorForRole(melodyAnchor, role, variant);

                    int cursor = startTick;
                    for (int index = 0; index < rhythm.Length; index++)
                    {
                        int durationTicks = rhythm[index] * unitTicks;
                        int degree = melodyDegrees[Math.Min(index, melodyDegrees.Count - 1)];
                        int midi = ClampMelodyMidi(ClampToRange(
                            GetScaleMidi(tonicPitchClass, measureMelodyAnchor, scale, degree),
                            style.MinMidi + variant.RangeFloorOffset,
                            style.MaxMidi + variant.RangeCeilingOffset));

                        midi = ResolveMelodyCollisionWithBass(previousMelodyMidi, previousBassMidi, midi, bassMidi, tonicPitchClass, scale, chordPlan, style, variant);
                        midi = ClampMelodyMidi(midi);

                        NoteEvent note = CreateNote(midi, cursor, durationTicks, ppq, 1, true);
                        ApplyMelodyExpression(note, index, rhythm.Length, role, mood, variant);
                        project.Notes.Add(note);
                        previousMelodyMidi = midi;
                        cursor += durationTicks;
                    }

                    if (measureInSection == 0 && !hasThemeMotif)
                    {
                        themeMotifs[section.Theme] = new ThemeMotif(melodyDegrees.ToList(), rhythm.ToArray());
                    }

                    if (PatternsEqual(previousRhythm, rhythm))
                    {
                        repeatedRhythmCount++;
                    }
                    else
                    {
                        repeatedRhythmCount = 0;
                    }

                    previousRhythm = rhythm.ToArray();
                    previousBassMidi = bassMidi;
                    previousHarmony = harmonyVoicing;
                }

                AddExpressionMarks(project, mood, variant, measures, ticksPerMeasure, structure.SectionMap, request.UseSustainPedal);
                project.Notes = project.Notes
                    .OrderBy(note => note.StartTick)
                    .ThenBy(note => note.Voice)
                    .ThenByDescending(note => note.Midi)
                    .ToList();

                string summary = $"{variant.DisplayName} | {style.DisplayName} | {mood.DisplayName} | {keyLabel}";
                string chordProgression = $"[{keyLabel}] " + string.Join("  |  ", chordNames);
                return new SmartComposeResult(project, chordProgression, summary, seed);
            } finally {
                ArrayPool<int>.Shared.Return(progressionPool);
            }
        }

        private static StructurePlan BuildStructure(StyleSpec style, int measures, VariantSpec variant, Random random, int[] progressionBuffer)
        {
            int sectionLength = ResolveSectionLength(measures);
            IReadOnlyList<SectionBlueprint> blueprints = BuildSectionBlueprints(measures, sectionLength);
            
            // progressionBuffer is rented from ArrayPool by the caller.
            // progressionBuffer 由调用方从 ArrayPool 租用。
            var progression = progressionBuffer;
            var sectionMap = new SectionPlan[measures];
            var themePhrases = new Dictionary<ThemeFamily, int[]>();

            foreach (SectionBlueprint blueprint in blueprints)
            {
                int actualLength = Math.Min(blueprint.Length, measures - blueprint.StartMeasure);
                if (actualLength <= 0)
                {
                    continue;
                }

                if (!themePhrases.TryGetValue(blueprint.Theme, out int[]? basePhrase))
                {
                    basePhrase = SelectThemePhrase(style, blueprint.Theme, variant, random);
                    themePhrases[blueprint.Theme] = basePhrase;
                }

                int[] sectionProgression = BuildSectionProgression(basePhrase, actualLength, blueprint.Energy, blueprint.IsFinalSection, variant, random);
                var section = new SectionPlan(blueprint.StartMeasure, actualLength, blueprint.Theme, blueprint.Energy, blueprint.IsFinalSection);

                for (int localIndex = 0; localIndex < actualLength; localIndex++)
                {
                    int absoluteMeasure = blueprint.StartMeasure + localIndex;
                    progression[absoluteMeasure] = sectionProgression[localIndex];
                    sectionMap[absoluteMeasure] = section;
                }
            }

            // Create a copy for the result to safely return the rented buffer later.
            // 为结果创建副本，以便稍后安全返回租用的缓冲区。
            int[] resultProgression = new int[measures];
            Array.Copy(progression, resultProgression, measures);

            return new StructurePlan(resultProgression, sectionMap);
        }

        private static int ResolveSectionLength(int measures)
        {
            return measures >= 24 ? 8 : 4;
        }

        private static IReadOnlyList<SectionBlueprint> BuildSectionBlueprints(int measures, int sectionLength)
        {
            int sectionCount = Math.Max(1, (int)Math.Ceiling(measures / (double)sectionLength));
            ThemeFamily[] themes = sectionCount switch
            {
                1 => new[] { ThemeFamily.A },
                2 => new[] { ThemeFamily.A, ThemeFamily.B },
                3 => new[] { ThemeFamily.A, ThemeFamily.B, ThemeFamily.A },
                4 => new[] { ThemeFamily.A, ThemeFamily.B, ThemeFamily.C, ThemeFamily.A },
                5 => new[] { ThemeFamily.A, ThemeFamily.B, ThemeFamily.A, ThemeFamily.C, ThemeFamily.A },
                6 => new[] { ThemeFamily.A, ThemeFamily.B, ThemeFamily.C, ThemeFamily.A, ThemeFamily.B, ThemeFamily.A },
                7 => new[] { ThemeFamily.A, ThemeFamily.B, ThemeFamily.A, ThemeFamily.C, ThemeFamily.B, ThemeFamily.C, ThemeFamily.A },
                _ => new[] { ThemeFamily.A, ThemeFamily.B, ThemeFamily.C, ThemeFamily.A, ThemeFamily.B, ThemeFamily.C, ThemeFamily.A, ThemeFamily.B }
            };

            SectionEnergy[] energies = sectionCount switch
            {
                1 => new[] { SectionEnergy.Resolution },
                2 => new[] { SectionEnergy.Statement, SectionEnergy.Resolution },
                3 => new[] { SectionEnergy.Statement, SectionEnergy.Contrast, SectionEnergy.Resolution },
                4 => new[] { SectionEnergy.Statement, SectionEnergy.Development, SectionEnergy.Contrast, SectionEnergy.Resolution },
                5 => new[] { SectionEnergy.Statement, SectionEnergy.Development, SectionEnergy.Contrast, SectionEnergy.Development, SectionEnergy.Resolution },
                6 => new[] { SectionEnergy.Statement, SectionEnergy.Development, SectionEnergy.Contrast, SectionEnergy.Development, SectionEnergy.Climax, SectionEnergy.Resolution },
                7 => new[] { SectionEnergy.Statement, SectionEnergy.Development, SectionEnergy.Contrast, SectionEnergy.Development, SectionEnergy.Contrast, SectionEnergy.Climax, SectionEnergy.Resolution },
                _ => new[] { SectionEnergy.Statement, SectionEnergy.Development, SectionEnergy.Contrast, SectionEnergy.Development, SectionEnergy.Contrast, SectionEnergy.Climax, SectionEnergy.Development, SectionEnergy.Resolution }
            };

            var sections = new List<SectionBlueprint>(sectionCount);
            for (int index = 0; index < sectionCount; index++)
            {
                sections.Add(new SectionBlueprint(
                    index * sectionLength,
                    sectionLength,
                    themes[Math.Min(index, themes.Length - 1)],
                    energies[Math.Min(index, energies.Length - 1)],
                    index == sectionCount - 1));
            }

            return sections;
        }

        private static int[] SelectThemePhrase(StyleSpec style, ThemeFamily theme, VariantSpec variant, Random random)
        {
            int[][] bank = style.Progressions;
            int offset = theme switch
            {
                ThemeFamily.B => 1,
                ThemeFamily.C => 2,
                _ => 0
            };

            int[] selected = bank[(variant.ProgressionBank + offset + random.Next(bank.Length)) % bank.Length].ToArray();
            if (selected.Length >= 4 && random.NextDouble() < 0.38)
            {
                int rotation = random.Next(1, selected.Length);
                selected = selected.Skip(rotation).Concat(selected.Take(rotation)).ToArray();
            }

            return selected;
        }

        private static int[] BuildSectionProgression(
            IReadOnlyList<int> basePhrase,
            int sectionLength,
            SectionEnergy energy,
            bool isFinalSection,
            VariantSpec variant,
            Random random)
        {
            var output = new List<int>(sectionLength);

            while (output.Count < sectionLength)
            {
                bool openingHalf = output.Count < Math.Min(4, sectionLength);
                int[] phrase = ShapePhrase(basePhrase, energy, openingHalf, variant, random);
                int remaining = sectionLength - output.Count;
                output.AddRange(phrase.Take(remaining));
            }

            ApplySectionCadence(output, energy, isFinalSection, variant);
            return output.ToArray();
        }

        private static int[] ShapePhrase(
            IReadOnlyList<int> phrase,
            SectionEnergy energy,
            bool openingHalf,
            VariantSpec variant,
            Random random)
        {
            int[] result = phrase.ToArray();
            if (result.Length == 0)
            {
                return result;
            }

            switch (energy)
            {
                case SectionEnergy.Development:
                    if (!openingHalf && result.Length >= 3)
                    {
                        result[1] = result[0] == 1 ? 6 : 4;
                        result[2] = result[1] == 6 ? 4 : 2;
                    }
                    break;

                case SectionEnergy.Contrast:
                    result[0] = openingHalf ? 6 : 4;
                    if (result.Length >= 2)
                    {
                        result[1] = openingHalf ? 4 : 2;
                    }

                    if (result.Length >= 3)
                    {
                        result[2] = variant.Texture == VariantTexture.Atmosphere ? 2 : 5;
                    }
                    break;

                case SectionEnergy.Climax:
                    result[0] = openingHalf ? 4 : 6;
                    if (result.Length >= 2)
                    {
                        result[1] = 5;
                    }

                    if (result.Length >= 3)
                    {
                        result[2] = openingHalf ? 6 : 5;
                    }
                    break;

                case SectionEnergy.Resolution:
                    result[0] = openingHalf ? 1 : result[0];
                    if (!openingHalf && result.Length >= 3)
                    {
                        result[1] = 4;
                        result[2] = 5;
                    }
                    break;
            }

            if (!openingHalf && result.Length >= 3 && random.NextDouble() < 0.35)
            {
                (result[1], result[2]) = (result[2], result[1]);
            }

            for (int index = 0; index < result.Length; index++)
            {
                result[index] = Math.Clamp(result[index], 1, 7);
            }

            return result;
        }

        private static void ApplySectionCadence(List<int> progression, SectionEnergy energy, bool isFinalSection, VariantSpec variant)
        {
            if (progression.Count == 0)
            {
                return;
            }

            progression[^1] = 1;
            if (progression.Count >= 2)
            {
                progression[^2] = isFinalSection || energy == SectionEnergy.Climax || variant.Texture == VariantTexture.Tension
                    ? 5
                    : 2;
            }

            if (progression.Count >= 4)
            {
                progression[^4] = energy switch
                {
                    SectionEnergy.Contrast => 6,
                    SectionEnergy.Climax => 4,
                    SectionEnergy.Resolution => 1,
                    _ => progression[^4]
                };
            }
        }

        private static int[] PickRhythm(
            StyleSpec style,
            MoodSpec mood,
            VariantSpec variant,
            MeasureRole role,
            int measureUnits,
            int[]? previousPattern,
            int repeatedRhythmCount,
            int measureIndex,
            int totalMeasures,
            Random random)
        {
            int[][] bank = role switch
            {
                MeasureRole.Cadence or MeasureRole.FinalCadence => mood.CadencePatterns,
                MeasureRole.Contrast or MeasureRole.Climax => style.LiftPatterns,
                _ => style.CorePatterns
            };

            List<int[]> normalized = bank
                .Select(pattern => NormalizePattern(pattern, measureUnits))
                .OrderBy(pattern => pattern.Length)
                .ThenBy(pattern => pattern.Max())
                .ToList();

            List<int[]> candidates = repeatedRhythmCount >= 1 && previousPattern != null
                ? normalized.Where(pattern => !PatternsEqual(pattern, previousPattern)).ToList()
                : normalized;

            if (candidates.Count == 0)
            {
                candidates = normalized;
            }

            List<int[]> shortlist = variant.Texture switch
            {
                VariantTexture.Anthem => candidates.Skip(Math.Max(0, candidates.Count - 2)).ToList(),
                VariantTexture.Atmosphere => candidates.Take(Math.Min(2, candidates.Count)).ToList(),
                VariantTexture.Tension => candidates.Skip(Math.Max(0, candidates.Count / 2 - 1)).Take(Math.Min(2, candidates.Count)).ToList(),
                _ when role is MeasureRole.Cadence or MeasureRole.FinalCadence => candidates.Take(Math.Min(2, candidates.Count)).ToList(),
                _ when mood.Texture is MoodTexture.Calm or MoodTexture.Airy or MoodTexture.Gentle => candidates.Take(Math.Min(2, candidates.Count)).ToList(),
                _ when role is MeasureRole.Contrast or MeasureRole.Climax => candidates.Skip(Math.Max(0, candidates.Count - 2)).ToList(),
                _ => candidates.Skip(Math.Max(0, (candidates.Count - 2) / 2)).Take(Math.Min(2, candidates.Count)).ToList()
            };

            if (shortlist.Count == 0)
            {
                shortlist = candidates;
            }

            int[] selected = shortlist[random.Next(shortlist.Count)].ToArray();
            return ShapeRhythmForRole(selected, role, measureUnits, measureIndex, totalMeasures);
        }

        private static int[] NormalizePattern(IReadOnlyList<int> pattern, int measureUnits)
        {
            int[] copy = pattern.ToArray();
            copy[^1] += measureUnits - copy.Sum();
            return copy;
        }

        private static int[] CreateRhythmVariation(IReadOnlyList<int> source, int measureUnits, MeasureRole role)
        {
            int[] varied = source.ToArray();
            if (varied.Length <= 1)
            {
                return varied;
            }

            int pivot = Math.Clamp(varied.Length / 2, 1, varied.Length - 1);
            if (varied[pivot - 1] > 1)
            {
                varied[pivot - 1]--;
                varied[pivot]++;
            }
            else if (varied[pivot] > 1)
            {
                varied[pivot]--;
                varied[pivot - 1]++;
            }

            if (role is MeasureRole.Cadence or MeasureRole.FinalCadence)
            {
                varied[^1] = Math.Min(measureUnits - 1, varied[^1] + 1);
                varied[0] = Math.Max(1, measureUnits - varied.Skip(1).Sum());
            }

            return NormalizePattern(varied, measureUnits);
        }

        private static bool PatternsEqual(IReadOnlyList<int>? left, IReadOnlyList<int>? right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static int[] ShapeRhythmForRole(int[] pattern, MeasureRole role, int measureUnits, int measureIndex, int totalMeasures)
        {
            int[] shaped = pattern.ToArray();
            if (shaped.Length == 0)
            {
                return shaped;
            }

            if (role is MeasureRole.Cadence or MeasureRole.FinalCadence)
            {
                int desiredTail = role == MeasureRole.FinalCadence
                    ? Math.Max(3, measureUnits / 2)
                    : Math.Max(2, measureUnits / 3);

                int deficit = Math.Max(0, desiredTail - shaped[^1]);
                for (int index = shaped.Length - 2; index >= 0 && deficit > 0; index--)
                {
                    int available = Math.Max(0, shaped[index] - 1);
                    int transfer = Math.Min(deficit, available);
                    shaped[index] -= transfer;
                    shaped[^1] += transfer;
                    deficit -= transfer;
                }
            }
            else if ((role is MeasureRole.Opening or MeasureRole.Return) && shaped.Length > 2 && shaped[0] == 1)
            {
                shaped[0] = 2;
                shaped[1] = Math.Max(1, shaped[1] - 1);
            }

            return shaped;
        }

        private static ChordPlan BuildChordPlan(int chordDegree, MeasureRole role, KeyMode mode, MoodSpec mood, VariantSpec variant, Random random)
        {
            int root = chordDegree - 1;
            var harmonyDegrees = new List<int> { root, root + 2, root + 4 };
            string extension = string.Empty;

            double colorChance = variant.Texture switch
            {
                VariantTexture.Atmosphere => 0.42,
                VariantTexture.Anthem => 0.24,
                VariantTexture.Tension => 0.18,
                _ => 0.16
            };

            bool useAdd9 = mood.UseAdd9
                && role is MeasureRole.Opening or MeasureRole.Return or MeasureRole.Climax
                && random.NextDouble() < (IsCalmFamily(mood) ? colorChance * 1.25 : colorChance);
            bool useMaj7 = mood.UseMaj7
                && mode == KeyMode.Major
                && chordDegree is 1 or 4
                && random.NextDouble() < colorChance * 0.75;
            bool useSus2 = mood.UseSus2
                && role is MeasureRole.Opening or MeasureRole.Return
                && random.NextDouble() < colorChance * 0.55;
            bool useSus4 = mood.UseSus4
                && role is MeasureRole.Contrast or MeasureRole.Climax
                && random.NextDouble() < Math.Max(0.12, colorChance * 0.5);
            bool useSeventh = mood.UseSeventh
                && (chordDegree == 5 || role is MeasureRole.Cadence or MeasureRole.FinalCadence)
                && random.NextDouble() < Math.Max(0.18, colorChance * 0.7);
            if (IsSadFamily(mood)
                && !useMaj7
                && random.NextDouble() < 0.42
                && chordDegree is 1 or 4 or 6)
            {
                useSeventh = true;
            }

            if (useSus2)
            {
                harmonyDegrees[1] = root + 1;
                extension = "sus2";
            }
            else if (useSus4)
            {
                harmonyDegrees[1] = root + 3;
                extension = "sus4";
            }

            if (useMaj7)
            {
                harmonyDegrees.Add(root + 6);
                extension = CombineExtension(extension, "maj7");
            }
            else if (useSeventh)
            {
                harmonyDegrees.Add(root + 6);
                extension = CombineExtension(extension, "7");
            }

            if (useAdd9)
            {
                harmonyDegrees.Add(root + 8);
                extension = CombineExtension(extension, "add9");
            }

            int[] strongDegrees = harmonyDegrees.Take(3).Distinct().OrderBy(value => value).ToArray();
            int[] weakDegrees = new[] { root - 1, root, root + 1, root + 2, root + 3, root + 4, root + 5 };
            int[] cadenceDegrees = role == MeasureRole.FinalCadence
                ? new[] { 0, 2, 4 }
                : role == MeasureRole.Cadence
                    ? new[] { root, root + 2, root + 4, 0, 2, 4 }
                    : new[] { root, root + 2, root + 4 };

            string chordName = BuildChordName(chordDegree, mode, extension);
            return new ChordPlan(chordDegree, chordName, strongDegrees, weakDegrees, cadenceDegrees, harmonyDegrees.Distinct().OrderBy(value => value).ToArray());
        }

        private static List<int> BuildMeasureDegrees(
            IReadOnlyList<int> rhythm,
            ChordPlan chordPlan,
            MeasureRole role,
            MoodSpec mood,
            StyleSpec style,
            VariantSpec variant,
            MelodyState state,
            IReadOnlyList<int>? referenceMotif,
            IReadOnlyList<int>? referenceRhythm,
            Random random)
        {
            int noteCount = rhythm.Count;
            if ((role == MeasureRole.Return || role == MeasureRole.Opening)
                && referenceMotif != null
                && referenceRhythm != null
                && referenceRhythm.Count == noteCount)
            {
                List<int> reused = RecastMotif(referenceMotif, chordPlan, style, variant, role, random);
                ApplyCadenceContour(reused, chordPlan, role);
                state.PreviousDegree = reused[^1];
                return reused;
            }

            var result = new List<int>(noteCount);
            int current = state.PreviousDegree;

            for (int index = 0; index < noteCount; index++)
            {
                bool strongBeat = IsStrongBeat(index, noteCount);
                int candidate;

                if (index == noteCount - 1)
                {
                    candidate = ResolveCadenceDegree(current, chordPlan, role);
                }
                else if (index == 0)
                {
                    candidate = ChooseNearest(current, chordPlan.StrongDegrees);
                }
                else
                {
                    candidate = ChooseInnerDegree(current, chordPlan, strongBeat, mood, variant, state, random);
                }

                candidate = ApplyLeapPolicy(candidate, current, state, mood, strongBeat);
                candidate = Math.Clamp(candidate, style.MinDegree, style.MaxDegree);
                result.Add(candidate);
                current = candidate;
            }

            ApplyCadenceContour(result, chordPlan, role);
            state.PreviousDegree = result[^1];
            return result;
        }

        private static List<int> RecastMotif(IReadOnlyList<int> motif, ChordPlan chordPlan, StyleSpec style, VariantSpec variant, MeasureRole role, Random random)
        {
            var result = new List<int>(motif.Count);
            int first = Math.Clamp(ChooseNearest(motif[0], chordPlan.StrongDegrees), style.MinDegree, style.MaxDegree);
            result.Add(first);

            for (int index = 1; index < motif.Count; index++)
            {
                int interval = Math.Clamp(motif[index] - motif[index - 1], -2, 2);
                int target = result[^1] + interval;
                IReadOnlyList<int> pool = IsStrongBeat(index, motif.Count) ? chordPlan.StrongDegrees : chordPlan.WeakDegrees;
                int candidate = Math.Clamp(ChooseNearest(target, pool), style.MinDegree, style.MaxDegree);
                result.Add(candidate);
            }

            ApplyMotifVariation(result, chordPlan, style, role, variant, random);
            return result;
        }

        private static void ApplyMotifVariation(List<int> motif, ChordPlan chordPlan, StyleSpec style, MeasureRole role, VariantSpec variant, Random random)
        {
            if (motif.Count <= 2)
            {
                return;
            }

            int pivot = Math.Clamp(motif.Count / 2, 1, motif.Count - 2);
            IReadOnlyList<int> pivotPool = IsStrongBeat(pivot, motif.Count) ? chordPlan.StrongDegrees : chordPlan.WeakDegrees;
            int pivotTarget = motif[pivot] + (random.Next(0, 2) == 0 ? -1 : 1);
            motif[pivot] = Math.Clamp(ChooseNearest(pivotTarget, pivotPool), style.MinDegree, style.MaxDegree);

            if (role == MeasureRole.Return && motif.Count >= 4)
            {
                int tailIndex = motif.Count - 2;
                int tailTarget = motif[tailIndex] + (variant.Texture == VariantTexture.Anthem ? 1 : -1);
                motif[tailIndex] = Math.Clamp(ChooseNearest(tailTarget, chordPlan.WeakDegrees), style.MinDegree, style.MaxDegree);
            }

            if (motif.Count >= 5 && random.NextDouble() < 0.45)
            {
                int altIndex = variant.Texture == VariantTexture.Anthem ? 1 : motif.Count - 3;
                altIndex = Math.Clamp(altIndex, 1, motif.Count - 2);
                IReadOnlyList<int> altPool = IsStrongBeat(altIndex, motif.Count) ? chordPlan.StrongDegrees : chordPlan.WeakDegrees;
                int bias = variant.Texture switch
                {
                    VariantTexture.Atmosphere => -1,
                    VariantTexture.Tension => 2,
                    VariantTexture.Anthem => 1,
                    _ => random.Next(0, 2) == 0 ? -1 : 1
                };
                motif[altIndex] = Math.Clamp(ChooseNearest(motif[altIndex] + bias, altPool), style.MinDegree, style.MaxDegree);
            }

            for (int index = 1; index < motif.Count; index++)
            {
                int delta = motif[index] - motif[index - 1];
                if (Math.Abs(delta) > 3)
                {
                    motif[index] = motif[index - 1] + Math.Sign(delta) * 3;
                }
            }
        }

        private static void ApplyCadenceContour(List<int> degrees, ChordPlan chordPlan, MeasureRole role)
        {
            if (degrees.Count == 0 || role is not (MeasureRole.Cadence or MeasureRole.FinalCadence))
            {
                return;
            }

            degrees[^1] = ResolveCadenceDegree(degrees.Count > 1 ? degrees[^2] : degrees[^1], chordPlan, role);

            if (degrees.Count >= 2)
            {
                int direction = degrees[^2] <= degrees[^1] ? -1 : 1;
                degrees[^2] = ChooseNearest(degrees[^1] + direction, chordPlan.WeakDegrees);
            }

            if (degrees.Count >= 3)
            {
                int delta = degrees[^2] - degrees[^3];
                if (Math.Abs(delta) > 2)
                {
                    degrees[^3] = degrees[^2] - Math.Sign(delta) * 2;
                }
            }
        }

        private static int ChooseInnerDegree(int current, ChordPlan chordPlan, bool strongBeat, MoodSpec mood, VariantSpec variant, MelodyState state, Random random)
        {
            if (state.ForceContraryStep && state.PreviousDirection != 0)
            {
                state.ForceContraryStep = false;
                return current - state.PreviousDirection;
            }

            if (strongBeat)
            {
                int contourTarget = current + Pick(mood.StrongMotion, random) + variant.ContourBias;
                if (IsSadFamily(mood))
                {
                    contourTarget -= 1;
                }
                else if (IsCalmFamily(mood))
                {
                    contourTarget += Math.Sign(contourTarget - current);
                    contourTarget = current + Math.Clamp(contourTarget - current, -1, 1);
                }
                return ChooseNearest(contourTarget, chordPlan.StrongDegrees);
            }

            int weakTarget = current + Pick(mood.WeakMotion, random);
            if (mood.FavorStepwise)
            {
                weakTarget = current + Math.Clamp(weakTarget - current, -1, 1);
            }

            if (variant.Texture == VariantTexture.Tension && random.NextDouble() < 0.35)
            {
                weakTarget += random.Next(0, 2) == 0 ? -1 : 1;
            }
            else if (IsSadFamily(mood))
            {
                weakTarget -= 1;
            }
            else if (IsCalmFamily(mood))
            {
                weakTarget = current + Math.Clamp(weakTarget - current, -1, 1);
            }

            return ChooseNearest(weakTarget, chordPlan.WeakDegrees);
        }

        private static int ApplyLeapPolicy(int candidate, int current, MelodyState state, MoodSpec mood, bool strongBeat)
        {
            int delta = candidate - current;
            int absDelta = Math.Abs(delta);

            if (absDelta > mood.MaxLeapDegrees)
            {
                candidate = current + Math.Sign(delta) * mood.MaxLeapDegrees;
                delta = candidate - current;
                absDelta = Math.Abs(delta);
            }

            if (IsSadFamily(mood) && absDelta > 1)
            {
                candidate = current + Math.Sign(delta) * 1;
                delta = candidate - current;
                absDelta = Math.Abs(delta);
            }
            else if (IsCalmFamily(mood) && absDelta > 2)
            {
                candidate = current + Math.Sign(delta) * 2;
                delta = candidate - current;
                absDelta = Math.Abs(delta);
            }

            if (absDelta > 2)
            {
                if (state.ConsecutiveLargeLeaps >= 1)
                {
                    candidate = current + Math.Sign(delta) * 2;
                    delta = candidate - current;
                    absDelta = Math.Abs(delta);
                }

                if (absDelta > 2)
                {
                    state.ConsecutiveLargeLeaps++;
                    state.ForceContraryStep = true;
                }
            }
            else
            {
                state.ConsecutiveLargeLeaps = 0;
                if (!strongBeat)
                {
                    candidate = current + Math.Clamp(candidate - current, -1, 1);
                }
            }

            state.PreviousDirection = Math.Sign(candidate - current);
            return candidate;
        }

        private static int ResolveCadenceDegree(int current, ChordPlan chordPlan, MeasureRole role)
        {
            if (role == MeasureRole.FinalCadence)
            {
                int[] finalTargets = { 0, 2, 4 };
                return finalTargets.OrderBy(value => Math.Abs(value - current)).ThenBy(value => value == 0 ? 0 : 1).First();
            }

            return chordPlan.CadenceDegrees.OrderBy(value => Math.Abs(value - current)).First();
        }

        private static int AddBass(
            ScoreProject project,
            int startTick,
            int ticksPerMeasure,
            int unitTicks,
            int bassAnchor,
            int tonicPitchClass,
            IReadOnlyList<int> scale,
            ChordPlan chordPlan,
            int? previousBassMidi,
            MoodSpec mood,
            VariantSpec variant,
            MeasureRole role,
            IReadOnlyList<int> rhythm)
        {
            int root = ClosestMidiToTarget(GetScaleMidi(tonicPitchClass, bassAnchor, scale, chordPlan.Degree - 1), previousBassMidi ?? bassAnchor);
            int fifth = ClosestMidiToTarget(GetScaleMidi(tonicPitchClass, bassAnchor + 5, scale, chordPlan.Degree + 3), root + 7);
            root = ClampBassRootMidi(ShiftNear(root, previousBassMidi ?? root));
            fifth = ClampBassMidi(ShiftNear(fifth, root + 7));

            if (rhythm == null || rhythm.Count == 0)
            {
                project.Notes.Add(CreateNote(root, startTick, ticksPerMeasure, 480, 2, false));
                return root;
            }

            int cursor = startTick;
            for (int index = 0; index < rhythm.Count; index++)
            {
                int duration = Math.Max(1, rhythm[index] * unitTicks);
                int bassNote = index switch
                {
                    0 => root,
                    _ when role is MeasureRole.Cadence or MeasureRole.FinalCadence && index == rhythm.Count - 1 => root,
                    _ when variant.Texture == VariantTexture.Atmosphere => index % 3 == 1 ? fifth : root,
                    _ when variant.Texture is VariantTexture.Anthem or VariantTexture.Tension => index % 2 == 0 ? root : fifth,
                    _ => index == rhythm.Count / 2 ? fifth : root
                };

                project.Notes.Add(CreateNote(bassNote, cursor, duration, 480, 2, false));
                cursor += duration;
            }

            return root;
        }

        private static void AddBassHit(ScoreProject project, int midi, int startTick, int durationTicks, bool accent)
        {
            if (durationTicks <= 0)
            {
                return;
            }

            NoteEvent note = CreateNote(midi, startTick, durationTicks, 480, 2, false);
            project.Notes.Add(note);
        }

        private static int[] AddHarmony(
            ScoreProject project,
            int startTick,
            int ticksPerMeasure,
            int unitTicks,
            int tonicPitchClass,
            IReadOnlyList<int> scale,
            ChordPlan chordPlan,
            StyleSpec style,
            MoodSpec mood,
            VariantSpec variant,
            int bassMidi,
            int? previousBassMidi,
            int[]? previousHarmony,
            MeasureRole role,
            IReadOnlyList<int> rhythm)
        {
            int measureIndex = Math.Max(0, startTick / Math.Max(1, ticksPerMeasure));
            int center = style.CenterMidi - 4 + variant.MelodyOffset;
            int[] voicing = BuildChordVoicing(tonicPitchClass, center, scale, chordPlan.HarmonyDegrees, mood, bassMidi, previousBassMidi, previousHarmony);
            int activeToneCount = Math.Max(1, Math.Min(voicing.Length, DetermineHarmonyToneCount(mood, variant, role)));
            int[] activeVoicing = voicing.Take(activeToneCount).ToArray();

            if (ShouldSkipHarmonyMeasure(measureIndex, mood, variant, role))
            {
                return voicing;
            }

            if (rhythm == null || rhythm.Count == 0)
            {
                foreach (int midi in activeVoicing)
                {
                    project.Notes.Add(CreateNote(midi, startTick, ticksPerMeasure, 480, 3, true));
                }

                return voicing;
            }

            int cursor = startTick;
            int harmonyToneCount = variant.Texture == VariantTexture.Atmosphere
                ? Math.Min(2, activeVoicing.Length)
                : Math.Min(3, activeVoicing.Length);

            for (int index = 0; index < rhythm.Count; index++)
            {
                int duration = Math.Max(1, rhythm[index] * unitTicks);
                if (ShouldPlaceHarmonyOnSegment(index, rhythm.Count, duration, unitTicks, role, variant))
                {
                    foreach (int midi in activeVoicing.Take(harmonyToneCount))
                    {
                        project.Notes.Add(CreateNote(midi, cursor, duration, 480, 3, true));
                    }
                }

                cursor += duration;
            }

            return voicing;
        }

        private static bool ShouldPlaceHarmonyOnSegment(
            int index,
            int segmentCount,
            int durationTicks,
            int unitTicks,
            MeasureRole role,
            VariantSpec variant)
        {
            if (segmentCount <= 2)
            {
                return true;
            }

            if (index == 0)
            {
                return true;
            }

            if (role is MeasureRole.Cadence or MeasureRole.FinalCadence && index == segmentCount - 1)
            {
                return true;
            }

            if (variant.Texture == VariantTexture.Atmosphere)
            {
                return durationTicks >= unitTicks * 2 && (index == segmentCount - 1 || index == segmentCount / 2);
            }

            if (variant.Texture == VariantTexture.Anthem)
            {
                return index % 2 == 1;
            }

            if (variant.Texture == VariantTexture.Tension)
            {
                return index % 2 == 0;
            }

            return index == segmentCount / 2;
        }

        private static int DetermineHarmonyToneCount(MoodSpec mood, VariantSpec variant, MeasureRole role)
        {
            if (variant.Texture == VariantTexture.Tension)
            {
                return 3;
            }

            if (variant.Texture == VariantTexture.Atmosphere)
            {
                return 2;
            }

            if (role is MeasureRole.Cadence or MeasureRole.FinalCadence or MeasureRole.Climax)
            {
                return 3;
            }

            return mood.Texture switch
            {
                MoodTexture.Calm or MoodTexture.Airy or MoodTexture.Gentle => 2,
                _ => variant.Texture == VariantTexture.Anthem ? 3 : 2
            };
        }

        private static bool ShouldSkipHarmonyMeasure(int measureIndex, MoodSpec mood, VariantSpec variant, MeasureRole role)
        {
            if (role is MeasureRole.Cadence or MeasureRole.FinalCadence)
            {
                return false;
            }

            if (variant.Texture == VariantTexture.Tension)
            {
                return false;
            }

            return mood.Texture switch
            {
                MoodTexture.Calm or MoodTexture.Airy => role == MeasureRole.Answer || measureIndex % 2 == 1,
                MoodTexture.Gentle => role == MeasureRole.Answer || (variant.Texture != VariantTexture.Anthem && measureIndex % 3 == 2),
                _ => variant.Texture == VariantTexture.Narrative && role == MeasureRole.Contrast
            };
        }

        private static int[] BuildChordVoicing(
            int tonicPitchClass,
            int center,
            IReadOnlyList<int> scale,
            IReadOnlyList<int> harmonyDegrees,
            MoodSpec mood,
            int bassMidi,
            int? previousBassMidi,
            int[]? previousHarmony)
        {
            var resolved = new List<int>(harmonyDegrees.Count);
            int minGap = mood.Texture == MoodTexture.Tense ? 2 : 4;

            for (int index = 0; index < harmonyDegrees.Count; index++)
            {
                int target = center + (index - 1) * 5;
                int midi = GetScaleMidi(tonicPitchClass, target, scale, harmonyDegrees[index]);
                if (previousHarmony != null && index < previousHarmony.Length)
                {
                    midi = ShiftNear(midi, previousHarmony[index]);
                }

                midi = ShiftAbove(midi, bassMidi + 5 + index * 2);
                resolved.Add(midi);
            }

            resolved.Sort();
            for (int index = 1; index < resolved.Count; index++)
            {
                while (resolved[index] - resolved[index - 1] < minGap)
                {
                    resolved[index] += 12;
                }
            }

            SpreadLowRegisterIntervals(resolved);

            int[] voicing = resolved
                .Select(ClampHarmonyMidi)
                .Distinct()
                .OrderBy(midi => midi)
                .ToArray();

            if (previousHarmony != null && previousBassMidi.HasValue && voicing.Length > 0)
            {
                voicing = AvoidOuterParallelPerfects(voicing, previousHarmony, bassMidi, previousBassMidi.Value, tonicPitchClass, scale, harmonyDegrees);
            }

            return voicing;
        }

        private static void SpreadLowRegisterIntervals(List<int> resolved)
        {
            for (int index = 1; index < resolved.Count; index++)
            {
                while (resolved[index] < 60 && resolved[index] - resolved[index - 1] < 3)
                {
                    resolved[index] += 12;
                }
            }
        }

        private static int[] AvoidOuterParallelPerfects(
            int[] current,
            int[] previous,
            int currentBass,
            int previousBass,
            int tonicPitchClass,
            IReadOnlyList<int> scale,
            IReadOnlyList<int> harmonyDegrees)
        {
            if (current.Length == 0 || previous.Length == 0)
            {
                return current;
            }

            int previousTop = previous[^1];
            int currentTop = current[^1];
            int topDirection = Math.Sign(currentTop - previousTop);
            int bassDirection = Math.Sign(currentBass - previousBass);
            int oldInterval = Math.Abs(previousTop - previousBass) % 12;
            int newInterval = Math.Abs(currentTop - currentBass) % 12;

            if (topDirection != 0
                && topDirection == bassDirection
                && (oldInterval == 0 || oldInterval == 7)
                && (newInterval == 0 || newInterval == 7))
            {
                for (int index = harmonyDegrees.Count - 1; index >= 0; index--)
                {
                    int alternative = ClampToRange(
                        ShiftAbove(GetScaleMidi(tonicPitchClass, currentTop, scale, harmonyDegrees[index]), currentBass + 7),
                        52,
                        92);

                    int alternativeInterval = Math.Abs(alternative - currentBass) % 12;
                    if (alternativeInterval != 0 && alternativeInterval != 7)
                    {
                        int[] adjusted = current.ToArray();
                        adjusted[^1] = alternative;
                        Array.Sort(adjusted);
                        return adjusted;
                    }
                }
            }

            return current;
        }

        private static int ResolveMelodyCollisionWithBass(
            int? previousMelodyMidi,
            int? previousBassMidi,
            int melodyMidi,
            int bassMidi,
            int tonicPitchClass,
            IReadOnlyList<int> scale,
            ChordPlan chordPlan,
            StyleSpec style,
            VariantSpec variant)
        {
            if (!previousMelodyMidi.HasValue || !previousBassMidi.HasValue)
            {
                return melodyMidi;
            }

            int oldInterval = Math.Abs(previousMelodyMidi.Value - previousBassMidi.Value) % 12;
            int newInterval = Math.Abs(melodyMidi - bassMidi) % 12;
            int melodyDirection = Math.Sign(melodyMidi - previousMelodyMidi.Value);
            int bassDirection = Math.Sign(bassMidi - previousBassMidi.Value);

            if (melodyDirection != 0
                && melodyDirection == bassDirection
                && (oldInterval == 0 || oldInterval == 7)
                && (newInterval == 0 || newInterval == 7))
            {
                foreach (int degree in chordPlan.StrongDegrees.Reverse())
                {
                    int alternative = ClampToRange(
                        GetScaleMidi(tonicPitchClass, melodyMidi, scale, degree),
                        style.MinMidi + variant.RangeFloorOffset,
                        style.MaxMidi + variant.RangeCeilingOffset);

                    int alternativeInterval = Math.Abs(alternative - bassMidi) % 12;
                    if (alternativeInterval != 0 && alternativeInterval != 7)
                    {
                        return alternative;
                    }
                }
            }

            return melodyMidi;
        }

        private static void ApplyMelodyExpression(NoteEvent note, int index, int count, MeasureRole role, MoodSpec mood, VariantSpec variant)
        {
            int safeCount = Math.Max(1, count);
            bool first = index == 0;
            bool last = index >= safeCount - 1;
            bool interior = !first && !last;
            int quarterTicks = NoteLengthUtils.ToTicks(NoteLength.Quarter, 480);
            int eighthTicks = NoteLengthUtils.ToTicks(NoteLength.Eighth, 480);
            int baseTicks = Math.Max(1, note.BaseDurationTicks > 0 ? note.BaseDurationTicks : note.DurationTicks);
            bool shortNote = baseTicks <= eighthTicks;
            bool mediumOrShort = baseTicks <= quarterTicks;

            if (variant.Texture == VariantTexture.Atmosphere
                && role == MeasureRole.Climax
                && index == Math.Max(0, safeCount - 2))
            {
                note.Ornament = NoteOrnament.Appoggiatura;
            }

            if (IsDreamyFamily(mood)
                && role is MeasureRole.Opening or MeasureRole.Return
                && first)
            {
                note.Ornament = NoteOrnament.Appoggiatura;
            }

            if (IsSadFamily(mood))
            {
                note.IsAccent = role == MeasureRole.Climax && first;
                if (role is MeasureRole.Cadence or MeasureRole.FinalCadence
                    && index == Math.Max(0, safeCount - 2))
                {
                    note.Ornament = NoteOrnament.Appoggiatura;
                }

                return;
            }

            if (mood.Texture == MoodTexture.Tense)
            {
                note.IsAccent = first || role is MeasureRole.Climax or MeasureRole.Contrast;
                note.IsStaccatissimo = shortNote && interior;
                note.IsStaccato = !note.IsStaccatissimo && mediumOrShort && interior;
                return;
            }

            if (IsEnergeticFamily(mood))
            {
                note.IsAccent = first || (role == MeasureRole.Climax && mediumOrShort);
                note.IsStaccato = shortNote && interior;
                return;
            }

            if (variant.Texture == VariantTexture.Anthem && mediumOrShort && interior)
            {
                note.IsAccent = role is MeasureRole.Contrast or MeasureRole.Climax;
                note.IsStaccato = shortNote;
            }
        }

        private static int AdjustMelodyAnchorForRole(int melodyAnchor, MeasureRole role, VariantSpec variant)
        {
            int offset = role switch
            {
                MeasureRole.Answer => -2,
                MeasureRole.Contrast => 1,
                MeasureRole.Climax => 3,
                MeasureRole.Cadence or MeasureRole.FinalCadence => -1,
                _ => 0
            };

            return melodyAnchor + offset + (variant.Texture == VariantTexture.Atmosphere && role == MeasureRole.Climax ? 2 : 0);
        }

        private static void AddExpressionMarks(
            ScoreProject project,
            MoodSpec mood,
            VariantSpec variant,
            int measures,
            int ticksPerMeasure,
            IReadOnlyList<SectionPlan> sectionMap,
            bool useSustainPedal)
        {
            int totalTicks = measures * ticksPerMeasure;
            string openingDynamic = ResolveOpeningDynamic(mood, variant);
            string closingDynamic = ResolveClosingDynamic(mood, variant);
            const float centerGapStaffOffset = 5.2f;
            const float pedalStaffOffset = 24f;

            project.ExpressionMarks.Add(new ExpressionMark { Code = openingDynamic, StartTick = 0, StaffStepOffset = centerGapStaffOffset });
            project.ExpressionMarks.Add(new ExpressionMark { Code = mood.Texture == MoodTexture.Tense ? "cresc" : "cresc_text", StartTick = totalTicks / 3, StaffStepOffset = centerGapStaffOffset, SpanBeats = 3.5f });
            project.ExpressionMarks.Add(new ExpressionMark { Code = "rit", StartTick = Math.Max(0, totalTicks - ticksPerMeasure * 2), StaffStepOffset = centerGapStaffOffset, SpanBeats = 2.4f });
            project.ExpressionMarks.Add(new ExpressionMark { Code = closingDynamic, StartTick = Math.Max(0, totalTicks - ticksPerMeasure), StaffStepOffset = centerGapStaffOffset });

            int previousSectionStart = -1;
            for (int measure = 0; measure < measures; measure++)
            {
                if (measure >= sectionMap.Count)
                {
                    break;
                }

                SectionPlan section = sectionMap[measure];
                if (section.StartMeasure != measure || section.StartMeasure == previousSectionStart)
                {
                    continue;
                }

                previousSectionStart = section.StartMeasure;
                int sectionStartTick = measure * ticksPerMeasure;
                if (sectionStartTick <= 0)
                {
                    continue;
                }

                string sectionDynamic = ResolveSectionDynamic(section.Energy, mood, variant, section.IsFinalSection);
                if (!string.Equals(sectionDynamic, openingDynamic, StringComparison.Ordinal))
                {
                    project.ExpressionMarks.Add(new ExpressionMark
                    {
                        Code = sectionDynamic,
                        StartTick = sectionStartTick,
                        StaffStepOffset = centerGapStaffOffset
                    });
                }

                if (section.Energy is SectionEnergy.Climax or SectionEnergy.Contrast)
                {
                    int leadInTick = Math.Max(0, sectionStartTick - ticksPerMeasure);
                    project.ExpressionMarks.Add(new ExpressionMark
                    {
                        Code = "cresc",
                        StartTick = leadInTick,
                        StaffStepOffset = centerGapStaffOffset,
                        SpanBeats = Math.Max(2f, ticksPerMeasure / 480f)
                    });
                }
                else if (section.Energy == SectionEnergy.Resolution)
                {
                    project.ExpressionMarks.Add(new ExpressionMark
                    {
                        Code = IsSadFamily(mood) ? "dim" : "dim_text",
                        StartTick = sectionStartTick,
                        StaffStepOffset = centerGapStaffOffset,
                        SpanBeats = Math.Max(1.8f, ticksPerMeasure / 600f)
                    });
                }
            }

            if (!useSustainPedal && !mood.PedalFriendly && !variant.ForcePedal)
            {
                return;
            }

            int pedalSpanMeasures = variant.Texture == VariantTexture.Atmosphere || mood.Texture is MoodTexture.Calm or MoodTexture.Airy
                ? 2
                : 1;

            for (int measure = 0; measure < measures; measure += pedalSpanMeasures)
            {
                SectionEnergy energy = sectionMap.Count > measure ? sectionMap[measure].Energy : SectionEnergy.Statement;
                int start = measure * ticksPerMeasure;
                int spanMeasures = energy is SectionEnergy.Climax or SectionEnergy.Contrast ? 1 : pedalSpanMeasures;
                int end = Math.Min(totalTicks, start + ticksPerMeasure * spanMeasures);
                project.ExpressionMarks.Add(new ExpressionMark { Code = "ped", StartTick = start, StaffStepOffset = pedalStaffOffset });
                project.ExpressionMarks.Add(new ExpressionMark { Code = "ped_release", StartTick = end, StaffStepOffset = pedalStaffOffset });
            }
        }

        private static NoteEvent CreateNote(int midi, int startTick, int durationTicks, int ppq, int voice, bool preferTrebleStaff)
        {
            DurationSpec notation = ResolveNotationDuration(durationTicks, ppq);
            bool resolvedStaff = voice switch
            {
                1 => true,
                2 => false,
                _ => preferTrebleStaff && midi >= 60
            };
            return new NoteEvent
            {
                Midi = midi,
                StartTick = startTick,
                DurationTicks = durationTicks,
                BaseDurationTicks = notation.BaseTicks,
                AugmentationDots = notation.Dots,
                Voice = voice,
                PreferTrebleStaff = resolvedStaff
            };
        }

        private static DurationSpec ResolveNotationDuration(int durationTicks, int ppq)
        {
            int scale = Math.Max(1, ppq / 480);
            foreach (DurationSpec candidate in DurationCandidates)
            {
                if (candidate.TotalTicks * scale == durationTicks)
                {
                    return new DurationSpec(candidate.BaseTicks * scale, durationTicks, candidate.Dots);
                }
            }

            return new DurationSpec(durationTicks, durationTicks, 0);
        }

        private static int GetScaleMidi(int tonicPitchClass, int tonicMidiNearTarget, IReadOnlyList<int> scale, int degreeIndex)
        {
            int octaveOffset = FloorDiv(degreeIndex, scale.Count);
            int scaleIndex = Mod(degreeIndex, scale.Count);
            int pitchClass = Mod(tonicPitchClass + scale[scaleIndex], 12);
            int candidate = tonicMidiNearTarget + scale[scaleIndex] + octaveOffset * 12;
            while (Mod(candidate, 12) != pitchClass)
            {
                candidate++;
            }

            return candidate;
        }

        private static string BuildChordName(int degree, KeyMode mode, string extension)
        {
            string roman = mode == KeyMode.Major
                ? degree switch
                {
                    1 => "I",
                    2 => "ii",
                    3 => "iii",
                    4 => "IV",
                    5 => "V",
                    6 => "vi",
                    7 => "vii°",
                    _ => "I"
                }
                : degree switch
                {
                    1 => "i",
                    2 => "ii°",
                    3 => "III",
                    4 => "iv",
                    5 => "v",
                    6 => "VI",
                    7 => "VII",
                    _ => "i"
                };

            return string.IsNullOrWhiteSpace(extension) ? roman : $"{roman}({extension})";
        }

        private static string BuildKeyLabel(int fifths, KeyMode mode)
        {
            int pitchClass = Mod(fifths * 7 + (mode == KeyMode.Minor ? 9 : 0), 12);
            string tonic = fifths >= 0 ? SharpNames[pitchClass] : FlatNames[pitchClass];
            return $"{tonic} {(mode == KeyMode.Minor ? "minor" : "major")}";
        }

        private static bool IsStrongBeat(int index, int count)
        {
            return index == 0 || index == count - 1 || index == count / 2;
        }

        private static int ChooseNearest(int target, IReadOnlyList<int> options)
        {
            return options
                .OrderBy(value => Math.Abs(value - target))
                .ThenBy(value => value)
                .First();
        }

        private static int Pick(IReadOnlyList<int> values, Random random)
        {
            return values[random.Next(values.Count)];
        }

        private static int ClosestMidiToTarget(int pitchClass, int targetMidi)
        {
            int baseMidi = targetMidi - Mod(targetMidi - pitchClass, 12);
            int below = baseMidi;
            int above = baseMidi + 12;
            return Math.Abs(below - targetMidi) <= Math.Abs(above - targetMidi) ? below : above;
        }

        private static int ShiftNear(int midi, int target)
        {
            int best = midi;
            int bestDistance = Math.Abs(midi - target);
            for (int offset = -24; offset <= 24; offset += 12)
            {
                int candidate = midi + offset;
                int distance = Math.Abs(candidate - target);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static int ShiftAbove(int midi, int minimum)
        {
            while (midi <= minimum)
            {
                midi += 12;
            }

            return midi;
        }

        private static int ClampToRange(int midi, int minMidi, int maxMidi)
        {
            while (midi < minMidi)
            {
                midi += 12;
            }

            while (midi > maxMidi)
            {
                midi -= 12;
            }

            return Math.Clamp(midi, minMidi, maxMidi);
        }

        private static int ClampMelodyMidi(int midi)
        {
            return ClampToRange(midi, ComposeMelodyMinMidi, ComposeMelodyMaxMidi);
        }

        private static int ClampBassMidi(int midi)
        {
            return ClampToRange(midi, ComposeBassMinMidi, ComposeBassMaxMidi);
        }

        private static int ClampBassRootMidi(int midi)
        {
            return ClampToRange(midi, ComposeBassMinMidi, ComposeBassRootMaxMidi);
        }

        private static int ClampHarmonyMidi(int midi)
        {
            return ClampToRange(midi, ComposeHarmonyMinMidi, ComposeHarmonyMaxMidi);
        }

        private static string CombineExtension(string current, string next)
        {
            if (string.IsNullOrWhiteSpace(current))
            {
                return next;
            }

            return current.Contains(next, StringComparison.OrdinalIgnoreCase) ? current : $"{current},{next}";
        }

        private static int ResolveMeasureUnits(TimeSignature timeSignature)
        {
            return (timeSignature.Numerator, timeSignature.Denominator) switch
            {
                (3, 4) => 6,
                (6, 8) => 6,
                _ => 8
            };
        }

        private static MeasureRole ResolveRole(int measureInSection, int sectionLength, bool isFinalSection, SectionEnergy energy, bool hasThemeMotif)
        {
            int lastMeasure = Math.Max(0, sectionLength - 1);
            if (measureInSection <= 0)
            {
                return hasThemeMotif && energy != SectionEnergy.Contrast ? MeasureRole.Return : MeasureRole.Opening;
            }

            if (measureInSection >= lastMeasure)
            {
                return isFinalSection ? MeasureRole.FinalCadence : MeasureRole.Cadence;
            }

            if (measureInSection == 1)
            {
                return MeasureRole.Answer;
            }

            if (measureInSection == Math.Max(1, lastMeasure - 1))
            {
                return energy is SectionEnergy.Climax or SectionEnergy.Resolution
                    ? MeasureRole.Climax
                    : MeasureRole.Contrast;
            }

            if (measureInSection >= Math.Max(2, sectionLength / 2))
            {
                return hasThemeMotif && energy == SectionEnergy.Development
                    ? MeasureRole.Return
                    : MeasureRole.Contrast;
            }

            return energy == SectionEnergy.Contrast ? MeasureRole.Contrast : MeasureRole.Answer;
        }

        private static (int KeyFifths, KeyMode Mode) ResolveAutoTonality(string? moodId, int seed)
        {
            var random = new Random(seed == 0 ? Environment.TickCount : seed);
            TonalityOption[] options = (moodId ?? "calm").Trim().ToLowerInvariant() switch
            {
                "sleep" => new[]
                {
                    new TonalityOption(-4, KeyMode.Minor),
                    new TonalityOption(-3, KeyMode.Minor),
                    new TonalityOption(-2, KeyMode.Minor),
                    new TonalityOption(-1, KeyMode.Minor),
                    new TonalityOption(0, KeyMode.Minor)
                },
                "sad" => new[]
                {
                    new TonalityOption(-4, KeyMode.Minor),
                    new TonalityOption(-3, KeyMode.Minor),
                    new TonalityOption(-2, KeyMode.Minor),
                    new TonalityOption(-1, KeyMode.Minor),
                    new TonalityOption(0, KeyMode.Minor)
                },
                "nostalgic" => new[]
                {
                    new TonalityOption(-3, KeyMode.Minor),
                    new TonalityOption(-2, KeyMode.Minor),
                    new TonalityOption(-1, KeyMode.Minor),
                    new TonalityOption(0, KeyMode.Minor),
                    new TonalityOption(-2, KeyMode.Major),
                    new TonalityOption(-1, KeyMode.Major)
                },
                "positive" => new[]
                {
                    new TonalityOption(0, KeyMode.Major),
                    new TonalityOption(1, KeyMode.Major),
                    new TonalityOption(2, KeyMode.Major),
                    new TonalityOption(3, KeyMode.Major),
                    new TonalityOption(4, KeyMode.Major)
                },
                "hopeful" => new[]
                {
                    new TonalityOption(-1, KeyMode.Major),
                    new TonalityOption(0, KeyMode.Major),
                    new TonalityOption(1, KeyMode.Major),
                    new TonalityOption(2, KeyMode.Major),
                    new TonalityOption(3, KeyMode.Major),
                    new TonalityOption(0, KeyMode.Minor)
                },
                "dreamy" => new[]
                {
                    new TonalityOption(-2, KeyMode.Major),
                    new TonalityOption(-1, KeyMode.Major),
                    new TonalityOption(-2, KeyMode.Minor),
                    new TonalityOption(-1, KeyMode.Minor),
                    new TonalityOption(0, KeyMode.Minor)
                },
                "tense" => new[]
                {
                    new TonalityOption(-1, KeyMode.Minor),
                    new TonalityOption(0, KeyMode.Minor),
                    new TonalityOption(1, KeyMode.Minor),
                    new TonalityOption(2, KeyMode.Minor),
                    new TonalityOption(3, KeyMode.Minor)
                },
                _ => new[]
                {
                    new TonalityOption(-2, KeyMode.Major),
                    new TonalityOption(-1, KeyMode.Major),
                    new TonalityOption(0, KeyMode.Major),
                    new TonalityOption(1, KeyMode.Major),
                    new TonalityOption(2, KeyMode.Major),
                    new TonalityOption(-1, KeyMode.Minor),
                    new TonalityOption(0, KeyMode.Minor)
                }
            };

            TonalityOption selected = options[random.Next(options.Length)];
            return (selected.KeyFifths, selected.Mode);
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }

        private static int Mod(int value, int divisor)
        {
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private static MoodSpec ResolveMood(string? moodId)
        {
            string mood = moodId?.Trim().ToLowerInvariant() ?? "calm";
            return mood switch
            {
                "positive" => new MoodSpec("Positive", 12, 106, 154, 4, 2, 4, new[] { 1, 2, 3, 1 }, new[] { 1, 2, 1, 0, 1 }, MoodTexture.Bright, false, false, false, false, false, false, new[] { new[] { 2, 2, 2, 2 }, new[] { 1, 1, 2, 2, 2 }, new[] { 2, 1, 1, 2, 2 } }),
                "sad" => new MoodSpec("Sad", -22, 44, 74, -6, 4, 2, new[] { -2, -1, 0 }, new[] { -1, 0, -1, -2 }, MoodTexture.Gentle, false, false, false, false, false, true, new[] { new[] { 6, 2 }, new[] { 4, 4 }, new[] { 3, 1, 4 } }),
                "sleep" => new MoodSpec("Sleep", -28, 34, 52, -6, 0, 1, new[] { 0, 1 }, new[] { 0, 0, 1 }, MoodTexture.Calm, false, false, false, true, false, true, new[] { new[] { 8 }, new[] { 6, 2 } }),
                "hopeful" => new MoodSpec("Hopeful", 10, 96, 140, 3, 2, 3, new[] { 1, 2, 1, 3 }, new[] { 1, 2, 0, 1 }, MoodTexture.Bright, true, false, false, false, false, false, new[] { new[] { 2, 2, 4 }, new[] { 4, 4 }, new[] { 2, 1, 1, 4 } }),
                "nostalgic" => new MoodSpec("Nostalgic", -10, 60, 92, -3, 2, 2, new[] { -1, 0, 1 }, new[] { -1, 0, -1, 0 }, MoodTexture.Gentle, true, false, false, false, false, true, new[] { new[] { 4, 4 }, new[] { 6, 2 }, new[] { 3, 1, 4 } }),
                "dreamy" => new MoodSpec("Dreamy", -12, 64, 96, 1, 2, 2, new[] { 0, 1, 2 }, new[] { 1, 0, 1, -1 }, MoodTexture.Airy, true, true, true, true, false, true, new[] { new[] { 6, 2 }, new[] { 8 }, new[] { 4, 2, 2 } }),
                "tense" => new MoodSpec("Tense", 16, 108, 160, 3, 4, 4, new[] { 2, -1, 2, -2 }, new[] { 2, -1, 1, -2, 2 }, MoodTexture.Tense, false, false, false, false, true, false, new[] { new[] { 2, 2, 2, 2 }, new[] { 1, 1, 2, 2, 2 }, new[] { 2, 1, 1, 2, 1, 1 } }),
                _ => new MoodSpec("Calm", -8, 66, 96, -2, 2, 2, new[] { 0, 1, 0 }, new[] { 0, 1, 0, -1 }, MoodTexture.Calm, true, false, false, false, false, true, new[] { new[] { 4, 4 }, new[] { 2, 2, 4 }, new[] { 6, 2 } })
            };
        }

        private static StyleSpec ResolveStyle(string? styleId, TimeSignature timeSignature)
        {
            string style = styleId?.Trim().ToLowerInvariant() ?? "pop";
            bool compound = ResolveMeasureUnits(timeSignature) == 6;
            int[][] calmCore = compound ? new[] { new[] { 3, 3 }, new[] { 2, 2, 2 }, new[] { 4, 2 } } : new[] { new[] { 4, 4 }, new[] { 2, 2, 4 }, new[] { 4, 2, 2 } };
            int[][] driveCore = compound ? new[] { new[] { 2, 2, 2 }, new[] { 1, 1, 2, 2 }, new[] { 1, 1, 1, 1, 2 } } : new[] { new[] { 2, 2, 2, 2 }, new[] { 1, 1, 2, 2, 2 }, new[] { 2, 1, 1, 2, 2 } };
            int[][] lift = compound ? new[] { new[] { 2, 2, 2 }, new[] { 1, 1, 2, 2 } } : new[] { new[] { 2, 2, 2, 2 }, new[] { 2, 1, 1, 2, 2 } };
            int[][] tenseLift = compound ? new[] { new[] { 1, 1, 1, 1, 1, 1 }, new[] { 2, 1, 1, 2 } } : new[] { new[] { 1, 1, 1, 1, 2, 2 }, new[] { 2, 1, 1, 2, 1, 1 } };

            return style switch
            {
                "folk" => new StyleSpec("Folk", 96, 58, 79, -1, 10, new[] { new[] { 1, 4, 1, 5 }, new[] { 6, 4, 1, 5 }, new[] { 1, 5, 4, 1 } }, driveCore, lift),
                "ambient" => new StyleSpec("Ambient", 82, 60, 84, -1, 11, new[] { new[] { 1, 6, 4, 1 }, new[] { 4, 1, 6, 5 }, new[] { 1, 5, 4, 1 } }, calmCore, lift),
                "dance" => new StyleSpec("Dance", 124, 61, 82, 0, 12, new[] { new[] { 1, 5, 6, 4 }, new[] { 6, 4, 1, 5 }, new[] { 1, 1, 6, 4 } }, driveCore, tenseLift),
                _ => new StyleSpec("Pop", 108, 60, 80, -1, 11, new[] { new[] { 1, 5, 6, 4 }, new[] { 1, 6, 4, 5 }, new[] { 6, 4, 1, 5 } }, driveCore, lift)
            };
        }

        private static VariantSpec ResolveVariant(int index)
        {
            return (index % 5) switch
            {
                1 => new VariantSpec("Lift", "denser and brighter", VariantTexture.Anthem, 1, 4, 2, 8, 0, 4, 1, false),
                2 => new VariantSpec("Atmosphere", "longer notes and more space", VariantTexture.Atmosphere, 2, 6, -3, -10, -2, 6, -1, true),
                3 => new VariantSpec("Nocturne", "lower register and longer breaths", VariantTexture.Atmosphere, 0, -3, -4, -12, -4, 3, -2, true),
                4 => new VariantSpec("Pulse", "tighter rhythm and sharper contour", VariantTexture.Tension, 1, 2, 1, 10, 0, 5, 2, false),
                _ => new VariantSpec("Narrative", "balanced lead with stable cadence", VariantTexture.Narrative, 0, 0, 0, 0, 0, 0, 0, false)
            };
        }

        private static string ResolveOpeningDynamic(MoodSpec mood, VariantSpec variant)
        {
            if (IsSadFamily(mood))
            {
                return "p";
            }

            return variant.Texture switch
            {
                VariantTexture.Atmosphere => "p",
                VariantTexture.Tension => "mf",
                VariantTexture.Anthem => "mf",
                _ => mood.Texture == MoodTexture.Calm ? "mp" : "mf"
            };
        }

        private static string ResolveClosingDynamic(MoodSpec mood, VariantSpec variant)
        {
            if (IsSadFamily(mood) || mood.Texture is MoodTexture.Calm or MoodTexture.Airy)
            {
                return "pp";
            }

            return variant.Texture == VariantTexture.Tension ? "mp" : "p";
        }

        private static string ResolveSectionDynamic(SectionEnergy energy, MoodSpec mood, VariantSpec variant, bool isFinalSection)
        {
            if (isFinalSection)
            {
                return ResolveClosingDynamic(mood, variant);
            }

            return energy switch
            {
                SectionEnergy.Statement => ResolveOpeningDynamic(mood, variant),
                SectionEnergy.Development => IsSadFamily(mood) ? "mp" : "mf",
                SectionEnergy.Contrast => mood.Texture == MoodTexture.Tense ? "f" : "mf",
                SectionEnergy.Climax => mood.Texture == MoodTexture.Tense ? "ff" : "f",
                SectionEnergy.Resolution => IsSadFamily(mood) ? "p" : "mp",
                _ => ResolveOpeningDynamic(mood, variant)
            };
        }

        private static bool IsSadFamily(MoodSpec mood)
        {
            return mood.DisplayName is "Sad" or "Nostalgic";
        }

        private static bool IsDreamyFamily(MoodSpec mood)
        {
            return mood.DisplayName is "Dreamy" or "Sleep";
        }

        private static bool IsCalmFamily(MoodSpec mood)
        {
            return mood.DisplayName is "Calm" or "Sleep" or "Dreamy";
        }

        private static bool IsEnergeticFamily(MoodSpec mood)
        {
            return mood.DisplayName is "Positive" or "Hopeful";
        }

        private enum ThemeFamily
        {
            A,
            B,
            C
        }

        private enum SectionEnergy
        {
            Statement,
            Development,
            Contrast,
            Climax,
            Resolution
        }

        private enum MeasureRole
        {
            Opening,
            Answer,
            Contrast,
            Cadence,
            Return,
            Climax,
            FinalCadence
        }

        private enum VariantTexture
        {
            Narrative,
            Anthem,
            Atmosphere,
            Tension
        }

        private enum MoodTexture
        {
            Calm,
            Gentle,
            Airy,
            Bright,
            Tense
        }

        private sealed record StyleSpec(string DisplayName, int DefaultTempo, int MinMidi, int MaxMidi, int MinDegree, int MaxDegree, int[][] Progressions, int[][] CorePatterns, int[][] LiftPatterns)
        {
            public int CenterMidi => (MinMidi + MaxMidi) / 2;
        }

        private sealed record MoodSpec(
            string DisplayName,
            int TempoOffset,
            int MinTempo,
            int MaxTempo,
            int RangeOffset,
            int StartDegree,
            int MaxLeapDegrees,
            int[] StrongMotion,
            int[] WeakMotion,
            MoodTexture Texture,
            bool UseAdd9,
            bool UseMaj7,
            bool UseSus2,
            bool PedalFriendly,
            bool UseSus4,
            bool FavorStepwise,
            int[][] CadencePatterns)
        {
            public bool UseSeventh => Texture == MoodTexture.Tense;
        }

        private sealed record VariantSpec(string DisplayName, string Description, VariantTexture Texture, int ProgressionBank, int MelodyOffset, int BassOffset, int TempoOffset, int RangeFloorOffset, int RangeCeilingOffset, int ContourBias, bool ForcePedal);

        private sealed record ChordPlan(int Degree, string Name, int[] StrongDegrees, int[] WeakDegrees, int[] CadenceDegrees, int[] HarmonyDegrees);

        private sealed record DurationSpec(int BaseTicks, int TotalTicks, int Dots);

        private sealed record ThemeMotif(List<int> Degrees, int[] Rhythm);

        private sealed record SectionBlueprint(int StartMeasure, int Length, ThemeFamily Theme, SectionEnergy Energy, bool IsFinalSection);

        private sealed record SectionPlan(int StartMeasure, int Length, ThemeFamily Theme, SectionEnergy Energy, bool IsFinalSection);

        private sealed record StructurePlan(int[] Progression, SectionPlan[] SectionMap);

        private readonly record struct TonalityOption(int KeyFifths, KeyMode Mode);

        private sealed class MelodyState
        {
            public MelodyState(int previousDegree)
            {
                PreviousDegree = previousDegree;
            }

            public int PreviousDegree { get; set; }
            public int PreviousDirection { get; set; }
            public int ConsecutiveLargeLeaps { get; set; }
            public bool ForceContraryStep { get; set; }
        }
    }

    public sealed class SmartComposeRequest
    {
        public string Title { get; set; } = "智能创作";
        public int Bpm { get; set; } = 112;
        public int Measures { get; set; } = 8;
        public int KeyFifths { get; set; }
        public KeyMode Mode { get; set; } = KeyMode.Major;
        public TimeSignature TimeSignature { get; set; } = new(4, 4);
        public string StyleId { get; set; } = "pop";
        public string MoodId { get; set; } = "calm";
        public string LengthId { get; set; } = "short";
        public bool IncludeBass { get; set; } = true;
        public bool AutoTonality { get; set; }
        public bool UseSustainPedal { get; set; } = true;
        public int Seed { get; set; }
    }

    public sealed class SmartComposeResult
    {
        public SmartComposeResult(ScoreProject project, string chordProgression, string summary, int seed)
        {
            Project = project;
            ChordProgression = chordProgression;
            Summary = summary;
            Seed = seed;
        }

        public ScoreProject Project { get; }
        public string ChordProgression { get; }
        public string Summary { get; }
        public int Seed { get; }
    }
}
