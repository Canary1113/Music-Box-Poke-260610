using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MusicBox.Models;

namespace MusicBox.Services
{
    public sealed class ComposePreferenceService
    {
        private const int MinimumRatingsForTraining = 6;
        private const int FullModelConfidenceRatings = 18;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _storePath;
        private readonly string _legacyStorePath;
        private PreferenceStore? _store;

        public ComposePreferenceService()
        {
            string root = ResolveStoreRoot();
            _storePath = Path.Combine(root, "compose-preferences.json");

            string legacyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MusicBox");
            _legacyStorePath = Path.Combine(legacyRoot, "compose-preferences.json");
        }

        public IReadOnlyList<ComposeCandidateRanking> RankCandidates(SmartComposeRequest? request, IReadOnlyList<SmartComposeResult> generated)
        {
            EnsureLoaded();
            PreferenceStore store = _store ?? new PreferenceStore();
            List<ComposeRatingRecord> activeRatings = GetActiveRatings(store, request?.MoodId);
            TrainedPreferenceModel? model = TrainModel(activeRatings);
            double modelWeight = ResolveModelWeight(activeRatings.Count, model != null);

            var candidates = generated
                .Select(result =>
                {
                    ComposeFeatureVector features = ExtractFeatures(result.Project);
                    ComposeCategoryRating? savedRating = TryGetRating(result.Seed);
                    ComposePrediction prediction = Predict(model, features, request?.MoodId, modelWeight);
                    return new ComposeCandidateRanking(result, features, prediction, savedRating);
                })
                .ToList();

            AttachExplanations(candidates, request, modelWeight >= 0.45d);

            return candidates
                .OrderByDescending(c => c.Prediction.FinalScore)
                .ThenByDescending(c => c.Prediction.OverallScore)
                .ThenBy(c => c.Result.Seed)
                .ToList();
        }

        public void UpsertRating(SmartComposeRequest? request, SmartComposeResult result, ComposeCategoryRating rating)
        {
            EnsureLoaded();
            PreferenceStore store = _store ?? new PreferenceStore();
            ComposeFeatureVector features = ExtractFeatures(result.Project);
            ComposeCategoryRating normalized = NormalizeRating(rating);

            store.Ratings.RemoveAll(r => r.Seed == result.Seed);
            store.Ratings.Add(new ComposeRatingRecord
            {
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Seed = result.Seed,
                MoodId = request?.MoodId ?? string.Empty,
                LengthId = request?.LengthId ?? string.Empty,
                MelodyScore = normalized.Melody,
                RhythmScore = normalized.Rhythm,
                HarmonyScore = normalized.Harmony,
                MoodFitScore = normalized.MoodFit,
                OverallScore = normalized.Overall,
                FeatureRange = features.PitchRange,
                FeatureNoteDensity = features.NoteDensity,
                FeatureChordDensity = features.ChordDensity,
                FeatureLargeLeapRatio = features.LargeLeapRatio,
                FeatureBassShare = features.BassShare,
                FeatureRepetitionRatio = features.RepetitionRatio,
                FeatureDurationMismatch = features.DurationMismatch,
                FeatureRegisterCenter = features.RegisterCenter,
                FeatureRhythmVariance = features.RhythmVariance
            });

            TrimStore(store);
            SaveStore(store);
        }

        public bool RemoveRating(int seed)
        {
            EnsureLoaded();
            PreferenceStore store = _store ?? new PreferenceStore();
            int removed = store.Ratings.RemoveAll(r => r.Seed == seed);
            if (removed > 0)
            {
                SaveStore(store);
                return true;
            }

            return false;
        }

        public ComposeCategoryRating? TryGetRating(int seed)
        {
            EnsureLoaded();
            return _store?.Ratings
                .Where(r => r.Seed == seed)
                .OrderByDescending(r => r.CreatedAtUtc)
                .Select(ToCategoryRating)
                .FirstOrDefault();
        }

        public int GetRatingCount()
        {
            EnsureLoaded();
            return _store?.Ratings.Count ?? 0;
        }

        public int GetRelevantRatingCount(string? moodId)
        {
            EnsureLoaded();
            PreferenceStore store = _store ?? new PreferenceStore();
            return GetActiveRatings(store, moodId).Count;
        }

        private ComposePrediction Predict(TrainedPreferenceModel? model, ComposeFeatureVector features, string? moodId, double modelWeight)
        {
            ComposePrediction heuristic = BuildHeuristicPrediction(features, moodId);
            if (model == null || modelWeight <= 0.01d)
            {
                return heuristic;
            }

            double[] standardized = Standardize(features, model.Means, model.StdDevs);
            double[] expanded = ExpandFeatures(standardized);
            double melody = ClampScore(PredictSingle(model.MelodyWeights, expanded));
            double rhythm = ClampScore(PredictSingle(model.RhythmWeights, expanded));
            double harmony = ClampScore(PredictSingle(model.HarmonyWeights, expanded));
            double moodFit = ClampScore(PredictSingle(model.MoodFitWeights, expanded));
            double overall = ClampScore(PredictSingle(model.OverallWeights, expanded));
            double finalScore = ClampScore(
                overall * 0.26d
                + moodFit * 0.22d
                + melody * 0.22d
                + rhythm * 0.18d
                + harmony * 0.12d);
            ComposePrediction trained = new ComposePrediction
            {
                MelodyScore = melody,
                RhythmScore = rhythm,
                HarmonyScore = harmony,
                MoodFitScore = moodFit,
                OverallScore = overall,
                FinalScore = finalScore,
                ModelKind = LocalizationService.Translate("compose.model.trained")
            };

            return BlendPredictions(heuristic, trained, modelWeight);
        }

        private static ComposePrediction BuildHeuristicPrediction(ComposeFeatureVector features, string? moodId)
        {
            double melody = ClampScore(
                82d
                - features.LargeLeapRatio * 56d
                - Math.Max(0d, features.PitchRange - 18d) * 1.4d
                + ModerationBonus(features.RepetitionRatio, 0.18d, 20d));

            double rhythm = ClampScore(
                80d
                - features.DurationMismatch * 62d
                - features.RhythmVariance * 14d
                + ModerationBonus(features.NoteDensity / 10d, 0.72d, 15d));

            double harmony = ClampScore(
                76d
                + ModerationBonus(features.ChordDensity / 2.3d, 0.72d, 18d)
                + ModerationBonus(features.BassShare, 0.33d, 16d));

            double moodFit = ResolveMoodFitScore(moodId, features);
            double overall = ClampScore(
                melody * 0.30d
                + rhythm * 0.24d
                + harmony * 0.18d
                + moodFit * 0.28d);
            double finalScore = ClampScore(
                overall * 0.26d
                + moodFit * 0.22d
                + melody * 0.22d
                + rhythm * 0.18d
                + harmony * 0.12d);

            return new ComposePrediction
            {
                MelodyScore = melody,
                RhythmScore = rhythm,
                HarmonyScore = harmony,
                MoodFitScore = moodFit,
                OverallScore = overall,
                FinalScore = finalScore,
                ModelKind = LocalizationService.Translate("compose.model.heuristic")
            };
        }

        private static ComposePrediction BlendPredictions(ComposePrediction heuristic, ComposePrediction trained, double modelWeight)
        {
            double clampedWeight = Math.Clamp(modelWeight, 0d, 0.9d);
            double heuristicWeight = 1d - clampedWeight;
            string heuristicKind = LocalizationService.Translate("compose.model.heuristic");
            string trainedKind = LocalizationService.Translate("compose.model.trained");
            string modelKind = clampedWeight switch
            {
                >= 0.7d => trainedKind,
                <= 0.3d => heuristicKind,
                _ => $"{trainedKind} + {heuristicKind}"
            };

            return new ComposePrediction
            {
                MelodyScore = ClampScore(heuristic.MelodyScore * heuristicWeight + trained.MelodyScore * clampedWeight),
                RhythmScore = ClampScore(heuristic.RhythmScore * heuristicWeight + trained.RhythmScore * clampedWeight),
                HarmonyScore = ClampScore(heuristic.HarmonyScore * heuristicWeight + trained.HarmonyScore * clampedWeight),
                MoodFitScore = ClampScore(heuristic.MoodFitScore * heuristicWeight + trained.MoodFitScore * clampedWeight),
                OverallScore = ClampScore(heuristic.OverallScore * heuristicWeight + trained.OverallScore * clampedWeight),
                FinalScore = ClampScore(heuristic.FinalScore * heuristicWeight + trained.FinalScore * clampedWeight),
                ModelKind = modelKind
            };
        }

        private static double ResolveMoodFitScore(string? moodId, ComposeFeatureVector features)
        {
            string normalized = string.IsNullOrWhiteSpace(moodId)
                ? "calm"
                : moodId.Trim().ToLowerInvariant();
            double density = features.NoteDensity / 10d;
            double harmonyDensity = features.ChordDensity / 2.4d;
            double rhythmMotion = Math.Min(1.4d, features.RhythmVariance / 2.5d);

            return normalized switch
            {
                "sleep" => ClampScore(
                    74d
                    + ModerationBonus(features.LargeLeapRatio, 0.08d, 16d)
                    + ModerationBonus(density, 0.48d, 15d)
                    + ModerationBonus(rhythmMotion, 0.12d, 14d)
                    + ModerationBonus(features.BassShare, 0.30d, 8d)),
                "calm" => ClampScore(
                    74d
                    + ModerationBonus(features.LargeLeapRatio, 0.11d, 16d)
                    + ModerationBonus(density, 0.56d, 14d)
                    + ModerationBonus(harmonyDensity, 0.58d, 10d)
                    + ModerationBonus(rhythmMotion, 0.18d, 10d)),
                "positive" => ClampScore(
                    72d
                    + ModerationBonus(features.LargeLeapRatio, 0.18d, 12d)
                    + ModerationBonus(density, 0.73d, 16d)
                    + ModerationBonus(harmonyDensity, 0.74d, 12d)
                    + ModerationBonus(features.PitchRange / 24d, 0.72d, 10d)),
                "hopeful" => ClampScore(
                    73d
                    + ModerationBonus(features.LargeLeapRatio, 0.16d, 12d)
                    + ModerationBonus(density, 0.66d, 15d)
                    + ModerationBonus(harmonyDensity, 0.68d, 12d)
                    + ModerationBonus(features.RegisterCenter / 72d, 0.93d, 8d)),
                "sad" => ClampScore(
                    76d
                    + ModerationBonus(features.RepetitionRatio, 0.24d, 16d)
                    + ModerationBonus(density, 0.42d, 18d)
                    + ModerationBonus(harmonyDensity, 0.44d, 9d)
                    + ModerationBonus(features.PitchRange / 30d, 0.42d, 14d)
                    + ModerationBonus(features.RegisterCenter / 72d, 0.76d, 12d)
                    + ModerationBonus(features.LargeLeapRatio, 0.08d, 10d)),
                "nostalgic" => ClampScore(
                    74d
                    + ModerationBonus(features.RepetitionRatio, 0.22d, 14d)
                    + ModerationBonus(density, 0.54d, 12d)
                    + ModerationBonus(harmonyDensity, 0.60d, 10d)
                    + ModerationBonus(features.PitchRange / 30d, 0.60d, 10d)),
                "dreamy" => ClampScore(
                    73d
                    + ModerationBonus(features.LargeLeapRatio, 0.14d, 12d)
                    + ModerationBonus(density, 0.60d, 12d)
                    + ModerationBonus(harmonyDensity, 0.70d, 12d)
                    + ModerationBonus(rhythmMotion, 0.20d, 10d)),
                "tense" => ClampScore(
                    72d
                    + ModerationBonus(features.LargeLeapRatio, 0.28d, 15d)
                    + ModerationBonus(density, 0.82d, 18d)
                    + ModerationBonus(rhythmMotion, 0.44d, 14d)
                    + ModerationBonus(features.PitchRange / 28d, 0.82d, 10d)),
                _ => ClampScore(
                    72d
                    + ModerationBonus(features.LargeLeapRatio, 0.15d, 12d)
                    + ModerationBonus(density, 0.62d, 12d)
                    + ModerationBonus(harmonyDensity, 0.62d, 10d))
            };
        }

        private static double ModerationBonus(double value, double target, double amplitude)
        {
            double distance = Math.Abs(value - target);
            return Math.Max(-amplitude, amplitude - distance * amplitude * 2.4d);
        }

        private static double PredictSingle(double[] weights, double[] features)
        {
            double value = 0d;
            for (int i = 0; i < weights.Length && i < features.Length; i++)
            {
                value += weights[i] * features[i];
            }

            return value;
        }

        private static double ClampScore(double value)
        {
            return Math.Clamp(value, 0d, 100d);
        }

        private static double ResolveModelWeight(int sampleCount, bool hasModel)
        {
            if (!hasModel)
            {
                return 0d;
            }

            double progress = (sampleCount - MinimumRatingsForTraining) / (double)Math.Max(1, FullModelConfidenceRatings - MinimumRatingsForTraining);
            return 0.34d + Math.Clamp(progress, 0d, 1d) * 0.50d;
        }

        private static TrainedPreferenceModel? TrainModel(List<ComposeRatingRecord> samples)
        {
            if (samples.Count < MinimumRatingsForTraining)
            {
                return null;
            }

            double[][] raw = samples.Select(ToRawFeatureVector).ToArray();
            (double[] means, double[] stdDevs) = ComputeStandardization(raw);
            double[][] inputs = raw.Select(row => ExpandFeatures(Standardize(row, means, stdDevs))).ToArray();

            return new TrainedPreferenceModel
            {
                Means = means,
                StdDevs = stdDevs,
                MelodyWeights = TrainRegression(inputs, samples.Select(s => (double)s.MelodyScore).ToArray()),
                RhythmWeights = TrainRegression(inputs, samples.Select(s => (double)s.RhythmScore).ToArray()),
                HarmonyWeights = TrainRegression(inputs, samples.Select(s => (double)s.HarmonyScore).ToArray()),
                MoodFitWeights = TrainRegression(inputs, samples.Select(s => ResolveMoodFitTarget(s)).ToArray()),
                OverallWeights = TrainRegression(inputs, samples.Select(s => (double)s.OverallScore).ToArray())
            };
        }

        private static double[] TrainRegression(double[][] inputs, double[] targets)
        {
            int rowCount = inputs.Length;
            int columnCount = inputs[0].Length;
            var weights = new double[columnCount];
            weights[0] = targets.Average();

            const int epochs = 420;
            const double lambda = 0.012d;
            double learningRate = 0.038d;

            for (int epoch = 0; epoch < epochs; epoch++)
            {
                var gradient = new double[columnCount];
                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    double prediction = 0d;
                    double[] row = inputs[rowIndex];
                    for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                    {
                        prediction += weights[columnIndex] * row[columnIndex];
                    }

                    double error = prediction - targets[rowIndex];
                    for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                    {
                        gradient[columnIndex] += error * row[columnIndex];
                    }
                }

                double invCount = 2d / Math.Max(1, rowCount);
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    double regularization = columnIndex == 0 ? 0d : 2d * lambda * weights[columnIndex];
                    weights[columnIndex] -= learningRate * (gradient[columnIndex] * invCount + regularization);
                }

                learningRate *= 0.995d;
            }

            return weights;
        }

        private static (double[] Means, double[] StdDevs) ComputeStandardization(double[][] raw)
        {
            int columnCount = raw[0].Length;
            var means = new double[columnCount];
            var stdDevs = new double[columnCount];

            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                means[columnIndex] = raw.Average(row => row[columnIndex]);
                double variance = raw.Average(row =>
                {
                    double delta = row[columnIndex] - means[columnIndex];
                    return delta * delta;
                });
                stdDevs[columnIndex] = Math.Max(0.25d, Math.Sqrt(variance));
            }

            return (means, stdDevs);
        }

        private static double[] Standardize(double[] raw, double[] means, double[] stdDevs)
        {
            var output = new double[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                output[i] = (raw[i] - means[i]) / Math.Max(0.25d, stdDevs[i]);
            }

            return output;
        }

        private static double[] Standardize(ComposeFeatureVector features, double[] means, double[] stdDevs)
        {
            return Standardize(ToRawFeatureVector(features), means, stdDevs);
        }

        private static double[] ExpandFeatures(double[] standardized)
        {
            var expanded = new List<double>(1 + standardized.Length * 2 + 6) { 1d };
            expanded.AddRange(standardized);
            expanded.AddRange(standardized.Select(v => v * v));
            if (standardized.Length >= 8)
            {
                expanded.Add(standardized[0] * standardized[3]);
                expanded.Add(standardized[1] * standardized[6]);
                expanded.Add(standardized[2] * standardized[4]);
                expanded.Add(standardized[5] * standardized[3]);
                expanded.Add(standardized[1] * standardized[2]);
                expanded.Add(standardized[7] * standardized[6]);
            }

            return expanded.ToArray();
        }

        private static void AttachExplanations(List<ComposeCandidateRanking> candidates, SmartComposeRequest? request, bool trainedModel)
        {
            if (candidates.Count == 0)
            {
                return;
            }

            double avgMelody = candidates.Average(c => c.Prediction.MelodyScore);
            double avgRhythm = candidates.Average(c => c.Prediction.RhythmScore);
            double avgHarmony = candidates.Average(c => c.Prediction.HarmonyScore);
            double avgMoodFit = candidates.Average(c => c.Prediction.MoodFitScore);
            double avgOverall = candidates.Average(c => c.Prediction.OverallScore);
            double bestFinal = candidates.Max(c => c.Prediction.FinalScore);
            double minMismatch = candidates.Min(c => c.Features.DurationMismatch);
            double minLeap = candidates.Min(c => c.Features.LargeLeapRatio);

            foreach (ComposeCandidateRanking candidate in candidates)
            {
                candidate.Prediction.CreationReason = BuildCreationReason(request, candidate.Features);
                candidate.Prediction.RankingReason = BuildRankingReason(
                    candidate,
                    avgMelody,
                    avgRhythm,
                    avgHarmony,
                    avgMoodFit,
                    avgOverall,
                    bestFinal,
                    minMismatch,
                    minLeap,
                    trainedModel);
            }
        }

        private static string BuildCreationReason(SmartComposeRequest? request, ComposeFeatureVector features)
        {
            string moodId = string.IsNullOrWhiteSpace(request?.MoodId) ? "calm" : request!.MoodId;
            string moodLabel = LocalizationService.Translate($"compose.mood.{moodId}");
            var fragments = new List<string>();

            if (features.LargeLeapRatio < 0.16d)
            {
                fragments.Add(LocalizationService.Translate("compose.reason.smooth_melody"));
            }
            else if (features.LargeLeapRatio > 0.30d)
            {
                fragments.Add(LocalizationService.Translate("compose.reason.bold_melody"));
            }

            if (features.NoteDensity < 6.3d)
            {
                fragments.Add(LocalizationService.Translate("compose.reason_spacious_rhythm"));
            }
            else if (features.NoteDensity > 8.2d)
            {
                fragments.Add(LocalizationService.Translate("compose.reason_dense_rhythm"));
            }

            if (features.ChordDensity > 1.45d)
            {
                fragments.Add(LocalizationService.Translate("compose.reason_rich_harmony"));
            }
            else
            {
                fragments.Add(LocalizationService.Translate("compose.reason_light_harmony"));
            }

            string detail = string.Join(LocalizationService.Translate("compose.reason.separator"), fragments.Take(3));
            return string.Format(LocalizationService.Translate("compose.reason.creation_template"), moodLabel, detail);
        }

        private static string BuildRankingReason(
            ComposeCandidateRanking candidate,
            double avgMelody,
            double avgRhythm,
            double avgHarmony,
            double avgMoodFit,
            double avgOverall,
            double bestFinal,
            double minMismatch,
            double minLeap,
            bool trainedModel)
        {
            var categoryDiffs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [LocalizationService.Translate("compose.rate.melody")] = candidate.Prediction.MelodyScore - avgMelody,
                [LocalizationService.Translate("compose.rate.rhythm")] = candidate.Prediction.RhythmScore - avgRhythm,
                [LocalizationService.Translate("compose.rate.harmony")] = candidate.Prediction.HarmonyScore - avgHarmony,
                [LocalizationService.Translate("compose.rate.mood_fit")] = candidate.Prediction.MoodFitScore - avgMoodFit,
                [LocalizationService.Translate("compose.rate.overall")] = candidate.Prediction.OverallScore - avgOverall
            };

            KeyValuePair<string, double> leadCategoryPair = categoryDiffs
                .OrderByDescending(entry => entry.Value)
                .First();
            string leadCategory = leadCategoryPair.Value > 0.75d
                ? leadCategoryPair.Key
                : LocalizationService.Translate("compose.reason.balanced_profile");

            string positionReason = Math.Abs(candidate.Prediction.FinalScore - bestFinal) < 0.25d
                ? LocalizationService.Translate("compose.reason.top_rank")
                : LocalizationService.Translate("compose.reason.above_average_rank");

            string supportReason = candidate.Prediction.MoodFitScore >= avgMoodFit + 2d
                ? LocalizationService.Translate("compose.reason.more_mood_aligned")
                : candidate.Features.DurationMismatch <= minMismatch + 0.02d
                ? LocalizationService.Translate("compose.reason.more_coordinated")
                : candidate.Features.LargeLeapRatio <= minLeap + 0.03d
                    ? LocalizationService.Translate("compose.reason.more_stable")
                    : LocalizationService.Translate("compose.reason.more_aligned");

            string modelPrefix = trainedModel
                ? LocalizationService.Translate("compose.reason.trained_model")
                : LocalizationService.Translate("compose.reason.heuristic_model");

            return string.Format(
                LocalizationService.Translate("compose.reason.ranking_template"),
                modelPrefix,
                positionReason,
                leadCategory,
                supportReason);
        }

        private static ComposeFeatureVector ExtractFeatures(ScoreProject project)
        {
            var notes = project.Notes
                .Where(n => !n.IsRest && n.DurationTicks > 0)
                .OrderBy(n => n.StartTick)
                .ThenBy(n => n.Voice)
                .ToList();

            int ppq = Math.Max(1, project.Ppq);
            int ticksPerMeasure = Math.Max(1, project.TimeSignature.TicksPerMeasure(ppq));
            int totalTicks = notes.Count == 0
                ? ticksPerMeasure
                : Math.Max(ticksPerMeasure, notes.Max(n => n.StartTick + Math.Max(1, n.DurationTicks)));
            int measureCount = Math.Max(1, (int)Math.Ceiling(totalTicks / (double)ticksPerMeasure));
            if (notes.Count == 0)
            {
                return new ComposeFeatureVector();
            }

            int minMidi = notes.Min(n => n.Midi);
            int maxMidi = notes.Max(n => n.Midi);
            double pitchRange = maxMidi - minMidi;
            double noteDensity = notes.Count / (double)measureCount;
            double registerCenter = notes.Average(n => n.Midi);

            var onsetGroups = notes.GroupBy(n => n.StartTick).Select(g => g.ToList()).ToList();
            double chordDensity = onsetGroups.Average(g => g.Count);
            double bassShare = notes.Count(n => n.Voice > 1 || n.Midi < 60) / (double)notes.Count;

            var melody = notes
                .Where(n => n.Voice <= 1 || n.PreferTrebleStaff == true)
                .GroupBy(n => n.StartTick)
                .Select(g => g.OrderByDescending(n => n.Midi).First())
                .OrderBy(n => n.StartTick)
                .ToList();
            if (melody.Count < 2)
            {
                melody = onsetGroups
                    .Select(g => g.OrderByDescending(n => n.Midi).First())
                    .OrderBy(n => n.StartTick)
                    .ToList();
            }

            int leapCount = 0;
            int repeatCount = 0;
            var durationUnits = new List<double>();
            for (int i = 1; i < melody.Count; i++)
            {
                int delta = Math.Abs(melody[i].Midi - melody[i - 1].Midi);
                if (delta >= 7)
                {
                    leapCount++;
                }

                if (melody[i].Midi == melody[i - 1].Midi)
                {
                    repeatCount++;
                }
            }

            foreach (NoteEvent note in melody)
            {
                durationUnits.Add(note.DurationTicks / (double)Math.Max(1, ppq));
            }

            double rhythmVariance = durationUnits.Count <= 1
                ? 0d
                : Math.Sqrt(durationUnits.Average(d =>
                {
                    double delta = d - durationUnits.Average();
                    return delta * delta;
                }));

            double largeLeapRatio = melody.Count <= 1 ? 0d : leapCount / (double)(melody.Count - 1);
            double repetitionRatio = melody.Count <= 1 ? 0d : repeatCount / (double)(melody.Count - 1);

            double durationMismatch = onsetGroups
                .Where(g => g.Count > 1)
                .Select(g =>
                {
                    int minDuration = g.Min(n => Math.Max(1, n.DurationTicks));
                    int maxDuration = g.Max(n => Math.Max(1, n.DurationTicks));
                    return (maxDuration - minDuration) / (double)maxDuration;
                })
                .DefaultIfEmpty(0d)
                .Average();

            return new ComposeFeatureVector
            {
                PitchRange = pitchRange,
                NoteDensity = noteDensity,
                ChordDensity = chordDensity,
                LargeLeapRatio = largeLeapRatio,
                BassShare = bassShare,
                RepetitionRatio = repetitionRatio,
                DurationMismatch = durationMismatch,
                RegisterCenter = registerCenter,
                RhythmVariance = rhythmVariance
            };
        }

        private static List<ComposeRatingRecord> GetActiveRatings(PreferenceStore store, string? moodId)
        {
            List<ComposeRatingRecord> moodFiltered = store.Ratings
                .Where(r => string.IsNullOrWhiteSpace(moodId) || string.Equals(r.MoodId, moodId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.CreatedAtUtc)
                .ToList();

            if (moodFiltered.Count >= 4)
            {
                return moodFiltered;
            }

            return store.Ratings.OrderBy(r => r.CreatedAtUtc).ToList();
        }

        private void EnsureLoaded()
        {
            if (_store != null)
            {
                return;
            }

            try
            {
                if (File.Exists(_storePath))
                {
                    string json = File.ReadAllText(_storePath);
                    _store = JsonSerializer.Deserialize<PreferenceStore>(json, _jsonOptions) ?? new PreferenceStore();
                    return;
                }

                if (!string.Equals(_legacyStorePath, _storePath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(_legacyStorePath))
                {
                    string legacyJson = File.ReadAllText(_legacyStorePath);
                    PreferenceStore migrated = JsonSerializer.Deserialize<PreferenceStore>(legacyJson, _jsonOptions) ?? new PreferenceStore();
                    SaveStore(migrated);
                    _store = migrated;
                    return;
                }
            }
            catch
            {
            }

            _store = new PreferenceStore();
        }

        private static string ResolveStoreRoot()
        {
            string? projectRoot = TryFindProjectRoot(AppContext.BaseDirectory);
            string? oneDriveRoot = ResolveOneDriveRoot();
            if (!string.IsNullOrWhiteSpace(projectRoot)
                && IsUnderDirectory(projectRoot, oneDriveRoot))
            {
                return projectRoot;
            }

            if (!string.IsNullOrWhiteSpace(oneDriveRoot))
            {
                return Path.Combine(oneDriveRoot, "MusicBox");
            }

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                return projectRoot;
            }

            string currentDirectory = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                return currentDirectory;
            }

            return AppContext.BaseDirectory;
        }

        private static string? ResolveOneDriveRoot()
        {
            string?[] candidates =
            [
                Environment.GetEnvironmentVariable("OneDrive"),
                Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Environment.GetEnvironmentVariable("OneDriveConsumer")
            ];

            foreach (string? candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string? TryFindProjectRoot(string? startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            DirectoryInfo? current = new DirectoryInfo(startDirectory);
            while (current != null)
            {
                if (current.GetFiles("*.csproj").Any()
                    && current.GetFiles("MainWindow.xaml").Any())
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsUnderDirectory(string? path, string? root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            string normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private void SaveStore(PreferenceStore store)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                string json = JsonSerializer.Serialize(store, _jsonOptions);
                File.WriteAllText(_storePath, json);
                _store = store;
            }
            catch
            {
            }
        }

        private static void TrimStore(PreferenceStore store)
        {
            const int maxRatings = 1200;
            if (store.Ratings.Count <= maxRatings)
            {
                return;
            }

            store.Ratings = store.Ratings
                .OrderByDescending(r => r.CreatedAtUtc)
                .Take(maxRatings)
                .OrderBy(r => r.CreatedAtUtc)
                .ToList();
        }

        private static ComposeCategoryRating NormalizeRating(ComposeCategoryRating rating)
        {
            return new ComposeCategoryRating
            {
                Melody = Math.Clamp(rating.Melody, 0, 100),
                Rhythm = Math.Clamp(rating.Rhythm, 0, 100),
                Harmony = Math.Clamp(rating.Harmony, 0, 100),
                MoodFit = Math.Clamp(rating.MoodFit, 0, 100),
                Overall = Math.Clamp(rating.Overall, 0, 100)
            };
        }

        private static ComposeCategoryRating ToCategoryRating(ComposeRatingRecord record)
        {
            return new ComposeCategoryRating
            {
                Melody = record.MelodyScore,
                Rhythm = record.RhythmScore,
                Harmony = record.HarmonyScore,
                MoodFit = (int)Math.Round(ResolveMoodFitTarget(record)),
                Overall = record.OverallScore
            };
        }

        private static double ResolveMoodFitTarget(ComposeRatingRecord record)
        {
            return record.MoodFitScore ?? record.OverallScore;
        }

        private static double[] ToRawFeatureVector(ComposeFeatureVector vector)
        {
            return
            [
                vector.PitchRange,
                vector.NoteDensity,
                vector.ChordDensity,
                vector.LargeLeapRatio,
                vector.BassShare,
                vector.RepetitionRatio,
                vector.DurationMismatch,
                vector.RegisterCenter,
                vector.RhythmVariance
            ];
        }

        private static double[] ToRawFeatureVector(ComposeRatingRecord record)
        {
            return
            [
                record.FeatureRange,
                record.FeatureNoteDensity,
                record.FeatureChordDensity,
                record.FeatureLargeLeapRatio,
                record.FeatureBassShare,
                record.FeatureRepetitionRatio,
                record.FeatureDurationMismatch,
                record.FeatureRegisterCenter,
                record.FeatureRhythmVariance
            ];
        }

        private sealed class PreferenceStore
        {
            public List<ComposeRatingRecord> Ratings { get; set; } = new();
        }

        private sealed class ComposeRatingRecord
        {
            public DateTimeOffset CreatedAtUtc { get; set; }
            public int Seed { get; set; }
            public string MoodId { get; set; } = string.Empty;
            public string LengthId { get; set; } = string.Empty;
            public int MelodyScore { get; set; }
            public int RhythmScore { get; set; }
            public int HarmonyScore { get; set; }
            public int? MoodFitScore { get; set; }
            public int OverallScore { get; set; }
            public double FeatureRange { get; set; }
            public double FeatureNoteDensity { get; set; }
            public double FeatureChordDensity { get; set; }
            public double FeatureLargeLeapRatio { get; set; }
            public double FeatureBassShare { get; set; }
            public double FeatureRepetitionRatio { get; set; }
            public double FeatureDurationMismatch { get; set; }
            public double FeatureRegisterCenter { get; set; }
            public double FeatureRhythmVariance { get; set; }
        }

        private sealed class TrainedPreferenceModel
        {
            public double[] Means { get; init; } = Array.Empty<double>();
            public double[] StdDevs { get; init; } = Array.Empty<double>();
            public double[] MelodyWeights { get; init; } = Array.Empty<double>();
            public double[] RhythmWeights { get; init; } = Array.Empty<double>();
            public double[] HarmonyWeights { get; init; } = Array.Empty<double>();
            public double[] MoodFitWeights { get; init; } = Array.Empty<double>();
            public double[] OverallWeights { get; init; } = Array.Empty<double>();
        }
    }

    public sealed class ComposeFeatureVector
    {
        public double PitchRange { get; set; }
        public double NoteDensity { get; set; }
        public double ChordDensity { get; set; }
        public double LargeLeapRatio { get; set; }
        public double BassShare { get; set; }
        public double RepetitionRatio { get; set; }
        public double DurationMismatch { get; set; }
        public double RegisterCenter { get; set; }
        public double RhythmVariance { get; set; }
    }

    public sealed class ComposeCategoryRating
    {
        public int Melody { get; set; }
        public int Rhythm { get; set; }
        public int Harmony { get; set; }
        public int MoodFit { get; set; }
        public int Overall { get; set; }
    }

    public sealed class ComposePrediction
    {
        public double MelodyScore { get; set; }
        public double RhythmScore { get; set; }
        public double HarmonyScore { get; set; }
        public double MoodFitScore { get; set; }
        public double OverallScore { get; set; }
        public double FinalScore { get; set; }
        public string CreationReason { get; set; } = string.Empty;
        public string RankingReason { get; set; } = string.Empty;
        public string ModelKind { get; set; } = string.Empty;
    }

    public sealed class ComposeCandidateRanking
    {
        public ComposeCandidateRanking(
            SmartComposeResult result,
            ComposeFeatureVector features,
            ComposePrediction prediction,
            ComposeCategoryRating? savedRating)
        {
            Result = result;
            Features = features;
            Prediction = prediction;
            SavedRating = savedRating;
        }

        public SmartComposeResult Result { get; }
        public ComposeFeatureVector Features { get; }
        public ComposePrediction Prediction { get; }
        public ComposeCategoryRating? SavedRating { get; }
    }
}
