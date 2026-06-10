using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace MusicBox.Services
{
    public sealed class DetectedAudioNote
    {
        public int Midi { get; init; }
        public double FrequencyHz { get; init; }
        public double StartSeconds { get; init; }
        public double DurationSeconds { get; init; }
    }

    public sealed class AudioRecognitionProgress
    {
        public int Percent { get; init; }
        public string Stage { get; init; } = string.Empty;
        public int ProcessedFrames { get; init; }
        public int TotalFrames { get; init; }
    }

    public enum AudioRecognitionMode
    {
        MelodyFocus,
        Balanced,
        Dense
    }

    public static class AudioPitchRecognizer
    {
        private const int MelodyRegisterFloorMidi = 55;
        private const int MelodyRegisterCenterMidi = 76;

        private sealed class RecognitionOptions
        {
            public AudioRecognitionMode Mode { get; init; }
            public double RmsScale { get; init; }
            public double MinAdaptiveRms { get; init; }
            public double MaxAdaptiveRms { get; init; }
            public double MaxZeroCrossingRate { get; init; }
            public int BridgeMaxFrames { get; init; }
            public double BridgeMinConfidence { get; init; }
            public double MinMergedDurationSeconds { get; init; }
            public double MinCleanDurationSeconds { get; init; }
            public double SamePitchMergeGapSeconds { get; init; }
            public bool AllowSyntheticLowerOctave { get; init; }
            public int SyntheticLowerMaxMidi { get; init; }
            public int RefineLowerOctaveMaxMidi { get; init; }
            public float SpectralPeakThresholdFactor { get; init; }
            public double LowHarmonicSalienceThreshold { get; init; }
            public double LowFundamentalThreshold { get; init; }
            public double LowSupportBonus { get; init; }
            public bool PreferMonophonic { get; init; }
            public double MinFrequencyHz { get; init; }
            public double MaxFrequencyHz { get; init; }

            public static RecognitionOptions ForMode(AudioRecognitionMode mode)
            {
                return mode switch
                {
                    AudioRecognitionMode.Balanced => new RecognitionOptions
                    {
                        Mode = mode,
                        RmsScale = 0.80d,
                        MinAdaptiveRms = 0.0038d,
                        MaxAdaptiveRms = 0.019d,
                        MaxZeroCrossingRate = 0.50d,
                        BridgeMaxFrames = 4,
                        BridgeMinConfidence = 0.34d,
                        MinMergedDurationSeconds = 0.070d,
                        MinCleanDurationSeconds = 0.085d,
                        SamePitchMergeGapSeconds = 0.14d,
                        AllowSyntheticLowerOctave = true,
                        SyntheticLowerMaxMidi = 64,
                        RefineLowerOctaveMaxMidi = 64,
                        SpectralPeakThresholdFactor = 0.16f,
                        LowHarmonicSalienceThreshold = 0.092d,
                        LowFundamentalThreshold = 0.030d,
                        LowSupportBonus = 0.04d,
                        PreferMonophonic = true,
                        MinFrequencyHz = 65.41d,
                        MaxFrequencyHz = 1760d
                    },
                    AudioRecognitionMode.Dense => new RecognitionOptions
                    {
                        Mode = mode,
                        RmsScale = 0.70d,
                        MinAdaptiveRms = 0.0034d,
                        MaxAdaptiveRms = 0.018d,
                        MaxZeroCrossingRate = 0.52d,
                        BridgeMaxFrames = 6,
                        BridgeMinConfidence = 0.30d,
                        MinMergedDurationSeconds = 0.060d,
                        MinCleanDurationSeconds = 0.075d,
                        SamePitchMergeGapSeconds = 0.095d,
                        AllowSyntheticLowerOctave = true,
                        SyntheticLowerMaxMidi = 64,
                        RefineLowerOctaveMaxMidi = 64,
                        SpectralPeakThresholdFactor = 0.14f,
                        LowHarmonicSalienceThreshold = 0.082d,
                        LowFundamentalThreshold = 0.025d,
                        LowSupportBonus = 0.08d,
                        PreferMonophonic = false,
                        MinFrequencyHz = 55d,
                        MaxFrequencyHz = 2093d
                    },
                    _ => new RecognitionOptions
                    {
                        Mode = AudioRecognitionMode.MelodyFocus,
                        RmsScale = 0.94d,
                        MinAdaptiveRms = 0.0048d,
                        MaxAdaptiveRms = 0.021d,
                        MaxZeroCrossingRate = 0.46d,
                        BridgeMaxFrames = 2,
                        BridgeMinConfidence = 0.40d,
                        MinMergedDurationSeconds = 0.075d,
                        MinCleanDurationSeconds = 0.095d,
                        SamePitchMergeGapSeconds = 0.18d,
                        AllowSyntheticLowerOctave = false,
                        SyntheticLowerMaxMidi = 58,
                        RefineLowerOctaveMaxMidi = 58,
                        SpectralPeakThresholdFactor = 0.18f,
                        LowHarmonicSalienceThreshold = 0.108d,
                        LowFundamentalThreshold = 0.038d,
                        LowSupportBonus = 0d,
                        PreferMonophonic = true,
                        MinFrequencyHz = 82.41d,
                        MaxFrequencyHz = 1760d
                    }
                };
            }
        }

        private readonly struct PitchCandidate
        {
            public PitchCandidate(int midi, double frequencyHz, float strength)
            {
                Midi = Math.Clamp(midi, 24, 108);
                FrequencyHz = Math.Max(1d, frequencyHz);
                Strength = Math.Max(0.01f, strength);
            }

            public int Midi { get; }
            public double FrequencyHz { get; }
            public float Strength { get; }
        }

        private readonly struct CandidateVote
        {
            public CandidateVote(double score, double weightedFrequency, double weight)
            {
                Score = score;
                WeightedFrequency = weightedFrequency;
                Weight = weight;
            }

            public double Score { get; }
            public double WeightedFrequency { get; }
            public double Weight { get; }

            public CandidateVote Add(double score, double frequency, double weight)
            {
                double safeWeight = Math.Max(0.0001d, weight);
                return new CandidateVote(
                    Score + score,
                    WeightedFrequency + frequency * safeWeight,
                    Weight + safeWeight);
            }

            public double ResolveFrequency(int midi)
            {
                return Weight > 0.0001d
                    ? WeightedFrequency / Weight
                    : MidiToFrequency(midi);
            }
        }

        private readonly struct FrameAnalysis
        {
            public FrameAnalysis(int startSample, double rms, double harmonicity, double flatness, double peakRatio, double zcr, PitchCandidate[] candidates, double onsetStrength = 0d)
            {
                StartSample = startSample;
                Rms = rms;
                Harmonicity = Math.Clamp(harmonicity, 0d, 1d);
                Flatness = Math.Clamp(flatness, 0d, 1d);
                PeakRatio = Math.Clamp(peakRatio, 0d, 1d);
                ZeroCrossingRate = Math.Clamp(zcr, 0d, 1d);
                Candidates = candidates ?? Array.Empty<PitchCandidate>();
                OnsetStrength = Math.Max(0d, onsetStrength);
            }

            public int StartSample { get; }
            public double Rms { get; }
            public double Harmonicity { get; }
            public double Flatness { get; }
            public double PeakRatio { get; }
            public double ZeroCrossingRate { get; }
            public double OnsetStrength { get; }
            public PitchCandidate[] Candidates { get; }
            public bool HasPitch => Candidates.Length > 0;
            public int PrimaryMidi => HasPitch ? Candidates[0].Midi : 0;
            public float PrimaryStrength => HasPitch ? Candidates[0].Strength : 0f;

            public double Confidence
            {
                get
                {
                    double primary = Math.Clamp(PrimaryStrength, 0d, 1d);
                    double zcrPenalty = Math.Clamp(1d - ZeroCrossingRate * 2.2d, 0d, 1d);
                    return Math.Clamp(
                        Harmonicity * 0.44d
                        + PeakRatio * 0.24d
                        + (1d - Flatness) * 0.24d
                        + zcrPenalty * 0.08d
                        + primary * 0.10d,
                        0d,
                        1d);
                }
            }

            public FrameAnalysis WithoutPitch()
            {
                return new FrameAnalysis(StartSample, Rms, Harmonicity, Flatness, PeakRatio, ZeroCrossingRate, Array.Empty<PitchCandidate>(), OnsetStrength);
            }

            public FrameAnalysis WithCandidates(PitchCandidate[] candidates)
            {
                return new FrameAnalysis(StartSample, Rms, Harmonicity, Flatness, PeakRatio, ZeroCrossingRate, candidates, OnsetStrength);
            }
        }

        public static IReadOnlyList<DetectedAudioNote> DetectNotesFromAudio(
            string audioPath,
            IProgress<AudioRecognitionProgress>? progress = null,
            AudioRecognitionMode mode = AudioRecognitionMode.MelodyFocus,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (rawSamples, sampleRate) = ReadMonoSamples(audioPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (rawSamples.Length == 0 || sampleRate <= 0)
            {
                return Array.Empty<DetectedAudioNote>();
            }

            float[] samples = PreprocessSamples(rawSamples, sampleRate);
            cancellationToken.ThrowIfCancellationRequested();
            RecognitionOptions options = RecognitionOptions.ForMode(mode);
            try
            {
                var advancedNotes = DetectNotesFromPreparedSamples(samples, sampleRate, progress, options, cancellationToken);
                if (advancedNotes.Count > 0)
                {
                    return advancedNotes;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress?.Report(new AudioRecognitionProgress
                {
                    Percent = 73,
                    Stage = $"AdvancedFailed: {ex.GetType().Name}: {ex.Message}",
                    ProcessedFrames = 0,
                    TotalFrames = 0
                });
                // Fall back to a simpler monophonic path when the advanced tracker rejects a file.
            }

            progress?.Report(new AudioRecognitionProgress { Percent = 74, Stage = "Fallback", ProcessedFrames = 0, TotalFrames = 0 });
            try
            {
                return DetectNotesFallback(samples, sampleRate, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return DetectNotesFixedArray256(samples, sampleRate, progress, cancellationToken);
            }
        }

        private static IReadOnlyList<DetectedAudioNote> DetectNotesFromPreparedSamples(
            float[] samples,
            int sampleRate,
            IProgress<AudioRecognitionProgress>? progress,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            int frameSize = Math.Clamp((int)(sampleRate * 0.046), 1024, 4096);
            int hopSize = Math.Max(192, frameSize / 3);
            int totalFrames = Math.Max(1, ((samples.Length - frameSize) / hopSize) + 1);
            double minFreq = options.MinFrequencyHz;
            double maxFreq = options.MaxFrequencyHz;
            double adaptiveMinRms = Math.Clamp(
                EstimateAdaptiveMinRms(samples, frameSize, hopSize) * options.RmsScale,
                options.MinAdaptiveRms,
                options.MaxAdaptiveRms);

            var frames = new List<FrameAnalysis>(totalFrames);
            int frameIndex = 0;
            double previousRms = 0d;
            for (int start = 0; start + frameSize < samples.Length; start += hopSize, frameIndex++)
            {
                if ((frameIndex & 7) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int percent = Math.Clamp((int)Math.Round(frameIndex / (double)Math.Max(1, totalFrames) * 68d), 0, 68);
                    progress?.Report(new AudioRecognitionProgress { Percent = percent, Stage = "Analyzing", ProcessedFrames = frameIndex, TotalFrames = totalFrames });
                }

                double rms = ComputeRms(samples, start, frameSize);
                double zcr = ComputeZeroCrossingRate(samples, start, frameSize);
                double onsetStrength = CalculateOnsetStrength(previousRms, rms);
                previousRms = rms;
                if (rms < adaptiveMinRms || zcr > options.MaxZeroCrossingRate)
                {
                    frames.Add(new FrameAnalysis(start, rms, 0d, 1d, 0d, zcr, Array.Empty<PitchCandidate>(), onsetStrength));
                    continue;
                }

                var (autoFreq, autoScore) = EstimatePitchAutocorrelation(samples, start, frameSize, sampleRate, minFreq, maxFreq);
                var (yinFreq, yinScore) = EstimatePitchNormalizedDifference(samples, start, frameSize, sampleRate, minFreq, maxFreq);
                var (spectralCandidates, flatness, peakRatio) = AnalyzeSpectrum(samples, start, frameSize, sampleRate, minFreq, maxFreq, maxNotes: 8, options);
                var candidates = spectralCandidates.ToList();
                double pitchScore = Math.Max(autoScore, yinScore * 0.95d);

                if (autoFreq > 0)
                {
                    var (autoMidi, _) = PitchUtils.FrequencyToMidiWithCents(autoFreq);
                    AddOrBoostCandidateByMidi(candidates, autoMidi, autoFreq, 1.25f);
                }

                if (yinFreq > 0d && yinScore >= 0.16d)
                {
                    var (yinMidi, _) = PitchUtils.FrequencyToMidiWithCents(yinFreq);
                    AddOrBoostCandidateByMidi(
                        candidates,
                        yinMidi,
                        yinFreq,
                        (float)Math.Clamp(0.45d + yinScore * 0.85d, 0.35d, 1.45d));
                    BoostAgreedPitchCandidates(candidates, autoFreq, yinFreq, spectralCandidates);
                    ResolveOctaveConflict(candidates, autoFreq, yinFreq, spectralCandidates);
                }

                RebalanceCandidatesByContinuity(candidates, options);
                PitchCandidate[] ordered = candidates
                    .OrderByDescending(c => c.Strength)
                    .ThenByDescending(c => c.Midi)
                    .Take(7)
                    .ToArray();

                if (IsNoisyFrame(rms, zcr, autoScore, flatness, peakRatio, ordered, options))
                {
                    ordered = Array.Empty<PitchCandidate>();
                }
                else if (options.PreferMonophonic)
                {
                    ordered = SelectMonophonicCandidates(ordered, autoFreq, options);
                }

                frames.Add(new FrameAnalysis(start, rms, pitchScore, flatness, peakRatio, zcr, ordered, onsetStrength));
            }

            frames = StabilizeMonophonicTrajectory(frames, options);
            frames = SmoothMonophonicFrames(frames, options);
            frames = BridgeTinyVoicingGaps(frames, options);
            frames = SuppressIsolatedVoicedFrames(frames);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AudioRecognitionProgress { Percent = 72, Stage = "Grouping", ProcessedFrames = frameIndex, TotalFrames = totalFrames });

            var merged = MergeFrames(frames, sampleRate, frameSize, progress, options);
            var stabilized = StabilizeVoices(merged);
            var cleaned = CleanShortNoiseNotes(stabilized, options);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AudioRecognitionProgress { Percent = 88, Stage = "Refining", ProcessedFrames = totalFrames, TotalFrames = totalFrames });
            var refined = RefineDetectedNotes(samples, sampleRate, cleaned, progress, options, cancellationToken);
            var finalNotes = CleanShortNoiseNotes(StabilizeVoices(refined), options);

            progress?.Report(new AudioRecognitionProgress { Percent = 100, Stage = "Done", ProcessedFrames = totalFrames, TotalFrames = totalFrames });
            return finalNotes;
        }

        private static IReadOnlyList<DetectedAudioNote> DetectNotesFixedArray256(
            float[] samples,
            int sampleRate,
            IProgress<AudioRecognitionProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            if (samples == null || samples.Length < 512 || sampleRate <= 0)
            {
                return Array.Empty<DetectedAudioNote>();
            }

            int frameSize = Math.Clamp((int)(sampleRate * 0.072d), 1024, 4096);
            int hopSize = Math.Max(128, frameSize / 3);
            int totalFrames = Math.Max(1, ((Math.Max(samples.Length - frameSize, 0)) / hopSize) + 1);
            double minRms = Math.Max(0.0032d, EstimateAdaptiveMinRms(samples, frameSize, hopSize) * 0.58d);
            const double minFreq = 55d;
            const double maxFreq = 1760d;
            var notes = new List<DetectedAudioNote>();
            int[] midiBuckets = new int[256];
            double[] midiFreqSum = new double[256];

            int currentMidi = -1;
            int currentStart = 0;
            int currentEnd = 0;
            double currentFreqSum = 0d;
            double currentFreqWeight = 0d;
            int currentFrames = 0;

            void FlushCurrent()
            {
                if (currentMidi < 0 || currentFrames < 2)
                {
                    currentMidi = -1;
                    currentStart = 0;
                    currentEnd = 0;
                    currentFreqSum = 0d;
                    currentFreqWeight = 0d;
                    currentFrames = 0;
                    return;
                }

                double durationSeconds = Math.Max(0d, (currentEnd - currentStart) / (double)sampleRate);
                if (durationSeconds >= 0.100d)
                {
                    int safeMidi = Math.Clamp(currentMidi, 0, 255);
                    double freq = currentFreqWeight > 1e-6d ? currentFreqSum / currentFreqWeight : MidiToFrequency(Math.Clamp(safeMidi, 24, 108));
                    notes.Add(new DetectedAudioNote
                    {
                        Midi = Math.Clamp(safeMidi, 24, 108),
                        FrequencyHz = freq,
                        StartSeconds = currentStart / (double)sampleRate,
                        DurationSeconds = durationSeconds
                    });
                }

                currentMidi = -1;
                currentStart = 0;
                currentEnd = 0;
                currentFreqSum = 0d;
                currentFreqWeight = 0d;
                currentFrames = 0;
            }

            int frameIndex = 0;
            for (int start = 0; start + frameSize <= samples.Length; start += hopSize, frameIndex++)
            {
                if ((frameIndex & 7) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int percent = 78 + Math.Clamp((int)Math.Round(frameIndex / (double)Math.Max(1, totalFrames) * 20d), 0, 20);
                    progress?.Report(new AudioRecognitionProgress { Percent = percent, Stage = "Fallback256", ProcessedFrames = frameIndex, TotalFrames = totalFrames });
                }

                Array.Clear(midiBuckets, 0, midiBuckets.Length);
                Array.Clear(midiFreqSum, 0, midiFreqSum.Length);

                double rms = ComputeRms(samples, start, frameSize);
                if (rms < minRms)
                {
                    FlushCurrent();
                    continue;
                }

                try
                {
                    var (yinFreq, yinScore) = EstimatePitchNormalizedDifference(samples, start, frameSize, sampleRate, minFreq, maxFreq);
                    if (yinFreq > 0d && yinScore >= 0.08d)
                    {
                        var (yinMidi, _) = PitchUtils.FrequencyToMidiWithCents(yinFreq);
                        int bucket = Math.Clamp(yinMidi, 0, 255);
                        midiBuckets[bucket] += 3;
                        midiFreqSum[bucket] += yinFreq * 3d;
                    }
                }
                catch
                {
                }

                try
                {
                    var (autoFreq, autoScore) = EstimatePitchAutocorrelation(samples, start, frameSize, sampleRate, minFreq, maxFreq);
                    if (autoFreq > 0d && autoScore >= 0.08d)
                    {
                        var (autoMidi, _) = PitchUtils.FrequencyToMidiWithCents(autoFreq);
                        int bucket = Math.Clamp(autoMidi, 0, 255);
                        midiBuckets[bucket] += 2;
                        midiFreqSum[bucket] += autoFreq * 2d;
                    }
                }
                catch
                {
                }

                int bestBucket = -1;
                int bestVotes = 0;
                for (int i = 0; i < midiBuckets.Length; i++)
                {
                    if (midiBuckets[i] > bestVotes)
                    {
                        bestVotes = midiBuckets[i];
                        bestBucket = i;
                    }
                }

                if (bestBucket < 0 || bestVotes <= 0)
                {
                    FlushCurrent();
                    continue;
                }

                bool continueRun = currentMidi >= 0 && Math.Abs(bestBucket - currentMidi) <= 2 && start - currentEnd <= hopSize * 2;
                if (!continueRun)
                {
                    FlushCurrent();
                    currentMidi = bestBucket;
                    currentStart = start;
                }

                if (currentMidi < 0)
                {
                    currentMidi = bestBucket;
                    currentStart = start;
                }

                currentEnd = start + frameSize;
                currentFreqSum += midiFreqSum[bestBucket];
                currentFreqWeight += Math.Max(1, midiBuckets[bestBucket]);
                currentFrames++;
            }

            FlushCurrent();
            var cleaned = CleanShortNoiseNotes(StabilizeVoices(notes));
            progress?.Report(new AudioRecognitionProgress { Percent = 100, Stage = "Done", ProcessedFrames = totalFrames, TotalFrames = totalFrames });
            return cleaned;
        }
        private static IReadOnlyList<DetectedAudioNote> DetectNotesFallback(
            float[] samples,
            int sampleRate,
            IProgress<AudioRecognitionProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            if (samples.Length < 512 || sampleRate <= 0)
            {
                return Array.Empty<DetectedAudioNote>();
            }

            int frameSize = Math.Clamp((int)(sampleRate * 0.060d), 1024, 4096);
            int hopSize = Math.Max(128, frameSize / 4);
            int totalFrames = Math.Max(1, ((Math.Max(samples.Length - frameSize, 0)) / hopSize) + 1);
            double adaptiveMinRms = Math.Max(0.0038d, EstimateAdaptiveMinRms(samples, frameSize, hopSize) * 0.72d);
            const double minFreq = 55d;
            const double maxFreq = 1760d;
            var output = new List<DetectedAudioNote>();

            int? runMidi = null;
            int runStartSample = 0;
            int runEndSample = 0;
            double freqSum = 0d;
            double weightSum = 0d;
            double confidenceSum = 0d;
            int voicedFrames = 0;

            void FlushRun()
            {
                if (!runMidi.HasValue || voicedFrames < 2)
                {
                    runMidi = null;
                    runStartSample = 0;
                    runEndSample = 0;
                    freqSum = 0d;
                    weightSum = 0d;
                    confidenceSum = 0d;
                    voicedFrames = 0;
                    return;
                }

                double startSeconds = runStartSample / (double)sampleRate;
                double durationSeconds = Math.Max(0d, (runEndSample - runStartSample) / (double)sampleRate);
                double meanConfidence = confidenceSum / Math.Max(1, voicedFrames);
                if (durationSeconds >= 0.100d && meanConfidence >= 0.22d)
                {
                    int safeMidi = Math.Clamp(runMidi.Value, 24, 108);
                    double resolvedFreq = weightSum > 1e-6d ? freqSum / weightSum : MidiToFrequency(safeMidi);
                    output.Add(new DetectedAudioNote
                    {
                        Midi = safeMidi,
                        FrequencyHz = resolvedFreq,
                        StartSeconds = startSeconds,
                        DurationSeconds = durationSeconds
                    });
                }

                runMidi = null;
                runStartSample = 0;
                runEndSample = 0;
                freqSum = 0d;
                weightSum = 0d;
                confidenceSum = 0d;
                voicedFrames = 0;
            }

            int frameIndex = 0;
            for (int start = 0; start + frameSize <= samples.Length; start += hopSize, frameIndex++)
            {
                if ((frameIndex & 7) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int percent = 74 + Math.Clamp((int)Math.Round(frameIndex / (double)Math.Max(1, totalFrames) * 24d), 0, 24);
                    progress?.Report(new AudioRecognitionProgress { Percent = percent, Stage = "Fallback", ProcessedFrames = frameIndex, TotalFrames = totalFrames });
                }

                double rms = ComputeRms(samples, start, frameSize);
                double zcr = ComputeZeroCrossingRate(samples, start, frameSize);
                if (rms < adaptiveMinRms || zcr > 0.42d)
                {
                    FlushRun();
                    continue;
                }

                var (yinFreq, yinScore) = EstimatePitchNormalizedDifference(samples, start, frameSize, sampleRate, minFreq, maxFreq);
                var (autoFreq, autoScore) = EstimatePitchAutocorrelation(samples, start, frameSize, sampleRate, minFreq, maxFreq);
                double resolvedFreq = 0d;
                double resolvedConfidence = 0d;
                if (yinFreq > 0d && autoFreq > 0d)
                {
                    double yinWeight = 0.80d + yinScore * 0.90d;
                    double autoWeight = 0.45d + autoScore * 0.65d;
                    resolvedFreq = (yinFreq * yinWeight + autoFreq * autoWeight) / Math.Max(0.01d, yinWeight + autoWeight);
                    resolvedConfidence = Math.Max(yinScore, autoScore * 0.92d);
                }
                else if (yinFreq > 0d)
                {
                    resolvedFreq = yinFreq;
                    resolvedConfidence = yinScore;
                }
                else if (autoFreq > 0d)
                {
                    resolvedFreq = autoFreq;
                    resolvedConfidence = autoScore * 0.88d;
                }

                if (resolvedFreq <= 0d || resolvedConfidence < 0.15d)
                {
                    FlushRun();
                    continue;
                }

                var (midi, _) = PitchUtils.FrequencyToMidiWithCents(resolvedFreq);
                int safeMidi = Math.Clamp(midi, 24, 108);
                bool canContinueRun = runMidi.HasValue
                    && Math.Abs(safeMidi - runMidi.Value) <= 1
                    && start - runEndSample <= hopSize * 2;
                if (!canContinueRun)
                {
                    FlushRun();
                }

                if (!runMidi.HasValue)
                {
                    runMidi = safeMidi;
                    runStartSample = start;
                    runEndSample = start + frameSize;
                }
                else
                {
                    runMidi = (int)Math.Round((runMidi.Value * voicedFrames + safeMidi) / (double)Math.Max(1, voicedFrames + 1));
                    runEndSample = start + frameSize;
                }

                double voteWeight = Math.Max(0.10d, resolvedConfidence);
                freqSum += resolvedFreq * voteWeight;
                weightSum += voteWeight;
                confidenceSum += resolvedConfidence;
                voicedFrames++;
            }

            FlushRun();
            var cleaned = CleanShortNoiseNotes(StabilizeVoices(output));
            if (cleaned.Count == 0)
            {
                progress?.Report(new AudioRecognitionProgress { Percent = 100, Stage = "Done", ProcessedFrames = totalFrames, TotalFrames = totalFrames });
                return cleaned;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var refined = RefineDetectedNotes(samples, sampleRate, cleaned, progress, cancellationToken);
            var finalNotes = CleanShortNoiseNotes(StabilizeVoices(refined));
            progress?.Report(new AudioRecognitionProgress { Percent = 100, Stage = "Done", ProcessedFrames = totalFrames, TotalFrames = totalFrames });
            return finalNotes;
        }

        public static IReadOnlyList<DetectedAudioNote> DetectNotesFromWav(string wavPath, IProgress<AudioRecognitionProgress>? progress = null)
        {
            return DetectNotesFromAudio(wavPath, progress);
        }

        private static List<FrameAnalysis> SuppressIsolatedVoicedFrames(IReadOnlyList<FrameAnalysis> frames)
        {
            var output = frames.ToList();
            int i = 0;
            while (i < output.Count)
            {
                if (!IsFrameVoiced(output[i]))
                {
                    i++;
                    continue;
                }

                int runStart = i;
                while (i < output.Count && IsFrameVoiced(output[i]))
                {
                    i++;
                }

                int runLen = i - runStart;
                if (runLen < 2)
                {
                    for (int j = runStart; j < i; j++)
                    {
                        output[j] = output[j].WithoutPitch();
                    }
                }
            }

            return output;
        }

        private static List<FrameAnalysis> StabilizeMonophonicTrajectory(IReadOnlyList<FrameAnalysis> frames, RecognitionOptions options)
        {
            if (!options.PreferMonophonic || frames.Count == 0)
            {
                return frames.ToList();
            }

            var output = frames.ToList();
            int i = 0;
            while (i < output.Count)
            {
                if (!IsFrameVoiced(output[i]))
                {
                    i++;
                    continue;
                }

                int runStart = i;
                while (i < output.Count && IsFrameVoiced(output[i]))
                {
                    i++;
                }

                int runEnd = i - 1;
                int frameCount = runEnd - runStart + 1;
                if (frameCount <= 0)
                {
                    continue;
                }

                var candidatesByFrame = new List<PitchCandidate[]>(frameCount);
                bool allFramesHaveCandidates = true;
                for (int idx = runStart; idx <= runEnd; idx++)
                {
                    PitchCandidate[] candidates = GetTrajectoryCandidates(output[idx]);
                    if (candidates.Length == 0)
                    {
                        allFramesHaveCandidates = false;
                        break;
                    }

                    candidatesByFrame.Add(candidates);
                }

                if (!allFramesHaveCandidates || candidatesByFrame.Count != frameCount)
                {
                    continue;
                }

                var scoreTable = new double[frameCount][];
                var backTable = new int[frameCount][];
                for (int f = 0; f < frameCount; f++)
                {
                    int candidateCount = candidatesByFrame[f].Length;
                    scoreTable[f] = new double[candidateCount];
                    backTable[f] = new int[candidateCount];
                    for (int c = 0; c < candidateCount; c++)
                    {
                        scoreTable[f][c] = double.NegativeInfinity;
                        backTable[f][c] = -1;
                    }
                }

                FrameAnalysis firstFrame = output[runStart];
                for (int c = 0; c < candidatesByFrame[0].Length; c++)
                {
                    scoreTable[0][c] = ScoreTrajectoryCandidate(candidatesByFrame[0][c], firstFrame.Confidence, options);
                }

                for (int f = 1; f < frameCount; f++)
                {
                    FrameAnalysis frame = output[runStart + f];
                    PitchCandidate[] current = candidatesByFrame[f];
                    PitchCandidate[] previous = candidatesByFrame[f - 1];

                    for (int cur = 0; cur < current.Length; cur++)
                    {
                        double baseScore = ScoreTrajectoryCandidate(current[cur], frame.Confidence, options);
                        double best = double.NegativeInfinity;
                        int bestPrev = -1;
                        for (int prev = 0; prev < previous.Length; prev++)
                        {
                            double transition = ScoreTrajectoryTransition(previous[prev].Midi, current[cur].Midi);
                            double score = scoreTable[f - 1][prev] + baseScore + transition;
                            if (score > best)
                            {
                                best = score;
                                bestPrev = prev;
                            }
                        }

                        scoreTable[f][cur] = best;
                        backTable[f][cur] = bestPrev;
                    }
                }

                int lastFrameIndex = frameCount - 1;
                int bestLast = 0;
                double bestLastScore = double.NegativeInfinity;
                for (int c = 0; c < scoreTable[lastFrameIndex].Length; c++)
                {
                    if (scoreTable[lastFrameIndex][c] > bestLastScore)
                    {
                        bestLastScore = scoreTable[lastFrameIndex][c];
                        bestLast = c;
                    }
                }

                int state = bestLast;
                for (int f = lastFrameIndex; f >= 0; f--)
                {
                    PitchCandidate selected = candidatesByFrame[f][Math.Clamp(state, 0, candidatesByFrame[f].Length - 1)];
                    int absoluteIndex = runStart + f;
                    output[absoluteIndex] = output[absoluteIndex].WithCandidates(new[] { selected });
                    state = f > 0 ? backTable[f][Math.Clamp(state, 0, backTable[f].Length - 1)] : -1;
                    if (state < 0 && f > 0)
                    {
                        state = 0;
                    }
                }
            }

            return output;
        }

        private static List<FrameAnalysis> SmoothMonophonicFrames(IReadOnlyList<FrameAnalysis> frames, RecognitionOptions options)
        {
            if (!options.PreferMonophonic || frames.Count == 0)
            {
                return frames.ToList();
            }

            var output = frames.ToList();
            for (int i = 0; i < output.Count; i++)
            {
                if (!IsFrameVoiced(output[i]))
                {
                    continue;
                }

                var neighborhood = new List<int>(5);
                for (int j = Math.Max(0, i - 2); j <= Math.Min(output.Count - 1, i + 2); j++)
                {
                    if (IsFrameVoiced(output[j]))
                    {
                        neighborhood.Add(output[j].PrimaryMidi);
                    }
                }

                if (neighborhood.Count >= 3)
                {
                    neighborhood.Sort();
                    int medianMidi = neighborhood[neighborhood.Count / 2];
                    int currentMidi = output[i].PrimaryMidi;
                    if (Math.Abs(currentMidi - medianMidi) >= 2 && output[i].Confidence < 0.70d)
                    {
                        float strength = Math.Clamp(output[i].PrimaryStrength * 0.96f, 0.05f, 1.8f);
                        output[i] = output[i].WithCandidates(new[] { new PitchCandidate(medianMidi, MidiToFrequency(medianMidi), strength) });
                    }
                }

                if (output[i].Confidence < 0.30d && i > 0 && i < output.Count - 1 && IsFrameVoiced(output[i - 1]) && IsFrameVoiced(output[i + 1]))
                {
                    int prevMidi = output[i - 1].PrimaryMidi;
                    int nextMidi = output[i + 1].PrimaryMidi;
                    int curMidi = output[i].PrimaryMidi;
                    if (Math.Abs(prevMidi - nextMidi) <= 1 && Math.Abs(curMidi - prevMidi) >= 4 && Math.Abs(curMidi - nextMidi) >= 4)
                    {
                        output[i] = output[i].WithoutPitch();
                    }
                }
            }

            return output;
        }

        private static List<FrameAnalysis> BridgeTinyVoicingGaps(IReadOnlyList<FrameAnalysis> frames, RecognitionOptions options)
        {
            if (!options.PreferMonophonic || frames.Count < 3)
            {
                return frames.ToList();
            }

            var output = frames.ToList();
            int i = 1;
            while (i < output.Count - 1)
            {
                if (IsFrameVoiced(output[i]))
                {
                    i++;
                    continue;
                }

                int gapStart = i;
                while (i < output.Count - 1 && !IsFrameVoiced(output[i]))
                {
                    i++;
                }

                int gapEnd = i - 1;
                int gapLength = gapEnd - gapStart + 1;
                if (gapStart <= 0 || i >= output.Count || gapLength > options.BridgeMaxFrames)
                {
                    continue;
                }

                FrameAnalysis prev = output[gapStart - 1];
                FrameAnalysis next = output[i];
                if (!IsFrameVoiced(prev) || !IsFrameVoiced(next))
                {
                    continue;
                }

                if (Math.Abs(prev.PrimaryMidi - next.PrimaryMidi) > 2)
                {
                    continue;
                }

                if (Math.Min(prev.Confidence, next.Confidence) < options.BridgeMinConfidence)
                {
                    continue;
                }

                int midi = (int)Math.Round((prev.PrimaryMidi + next.PrimaryMidi) / 2d);
                float strength = Math.Clamp((prev.PrimaryStrength + next.PrimaryStrength) * 0.42f, 0.05f, 1.4f);
                for (int fill = gapStart; fill <= gapEnd; fill++)
                {
                    output[fill] = output[fill].WithCandidates(new[] { new PitchCandidate(midi, MidiToFrequency(midi), strength) });
                }
            }

            return output;
        }

        private static IReadOnlyList<DetectedAudioNote> MergeFrames(
            IReadOnlyList<FrameAnalysis> frames,
            int sampleRate,
            int frameSize,
            IProgress<AudioRecognitionProgress>? progress,
            RecognitionOptions options)
        {
            var notes = new List<DetectedAudioNote>();
            double minDurationSeconds = options.MinMergedDurationSeconds;
            int totalFrames = Math.Max(1, frames.Count);
            int i = 0;

            while (i < frames.Count)
            {
                if (!IsFrameVoiced(frames[i]))
                {
                    i++;
                    continue;
                }

                int startIndex = i;
                double confidenceSum = 0d;
                var midiVotes = new Dictionary<int, int>();
                var weightedFreq = new Dictionary<int, double>();
                var weightedPower = new Dictionary<int, double>();
                int dominantMidi = frames[i].PrimaryMidi;

                while (i < frames.Count && IsFrameVoiced(frames[i]))
                {
                    FrameAnalysis frame = frames[i];
                    if (ShouldSplitForPitchChange(frames, i, dominantMidi) || IsLikelyOnset(frames, i, startIndex))
                    {
                        break;
                    }

                    confidenceSum += frame.Confidence;
                    for (int c = 0; c < frame.Candidates.Length; c++)
                    {
                        PitchCandidate candidate = frame.Candidates[c];
                        midiVotes.TryGetValue(candidate.Midi, out int vote);
                        midiVotes[candidate.Midi] = vote + (c == 0 ? 2 : 1);

                        weightedFreq.TryGetValue(candidate.Midi, out double freq);
                        weightedPower.TryGetValue(candidate.Midi, out double power);
                        float weight = Math.Max(0.05f, candidate.Strength) * (c == 0 ? 1.50f : 0.70f);
                        weightedFreq[candidate.Midi] = freq + candidate.FrequencyHz * weight;
                        weightedPower[candidate.Midi] = power + weight;
                    }

                    dominantMidi = ResolveDominantMidi(midiVotes, dominantMidi, options);
                    i++;
                }

                int frameCount = Math.Max(1, i - startIndex);
                if (frameCount <= 0 || midiVotes.Count == 0)
                {
                    continue;
                }

                if (frameCount < 3)
                {
                    continue;
                }

                double meanConfidence = confidenceSum / frameCount;
                if (meanConfidence < 0.34d)
                {
                    continue;
                }

                int endSample = frames[Math.Max(startIndex, i - 1)].StartSample + frameSize;
                double startSeconds = frames[startIndex].StartSample / (double)sampleRate;
                double durationSeconds = Math.Max(0d, (endSample - frames[startIndex].StartSample) / (double)sampleRate);
                if (durationSeconds < minDurationSeconds)
                {
                    continue;
                }

                int primaryMidi = ResolveDominantMidi(midiVotes, dominantMidi, options);
                int totalVotes = midiVotes.Values.Sum();
                midiVotes.TryGetValue(primaryMidi, out int primaryVotes);
                if (primaryVotes < Math.Max(3, (int)Math.Ceiling(totalVotes * 0.36d)))
                {
                    continue;
                }

                double primaryFreq = weightedPower.TryGetValue(primaryMidi, out double pPower) && pPower > 0d
                    ? weightedFreq[primaryMidi] / pPower
                    : MidiToFrequency(primaryMidi);

                notes.Add(new DetectedAudioNote
                {
                    Midi = Math.Clamp(primaryMidi, 24, 108),
                    FrequencyHz = primaryFreq,
                    StartSeconds = startSeconds,
                    DurationSeconds = durationSeconds
                });

                if ((i & 7) == 0)
                {
                    int percent = 72 + Math.Clamp((int)Math.Round(i / (double)Math.Max(1, totalFrames) * 25d), 0, 25);
                    progress?.Report(new AudioRecognitionProgress
                    {
                        Percent = percent,
                        Stage = "Grouping",
                        ProcessedFrames = i,
                        TotalFrames = totalFrames
                    });
                }
            }

            return notes
                .OrderBy(n => n.StartSeconds)
                .ThenByDescending(n => n.Midi)
                .ToList();
        }

        private static bool IsFrameVoiced(FrameAnalysis frame)
        {
            if (!frame.HasPitch)
            {
                return false;
            }

            if (frame.Confidence < 0.25d)
            {
                return false;
            }

            if (frame.Flatness > 0.58d && frame.Harmonicity < 0.60d)
            {
                return false;
            }

            return true;
        }

        private static bool IsNoisyFrame(
            double rms,
            double zcr,
            double harmonicity,
            double flatness,
            double peakRatio,
            IReadOnlyList<PitchCandidate> candidates,
            RecognitionOptions options)
        {
            if (candidates.Count == 0)
            {
                return true;
            }

            float primaryStrength = candidates[0].Strength;
            bool weakHarmonic = harmonicity < 0.50d;
            bool flatSpectrum = flatness > 0.55d;
            bool weakPeaks = peakRatio < 0.14d;
            float reliableThreshold = options.Mode == AudioRecognitionMode.MelodyFocus ? 0.34f : 0.28f;
            float midCandidateThreshold = options.Mode == AudioRecognitionMode.MelodyFocus ? 0.28f : 0.22f;
            bool reliableCandidate = primaryStrength >= reliableThreshold
                || candidates.Any(c => c.Strength >= midCandidateThreshold && c.Midi is >= 60 and <= 92);

            if (rms < 0.0065d && weakHarmonic && !reliableCandidate) return true;
            if (zcr > 0.30d && harmonicity < 0.68d && !reliableCandidate) return true;
            if (flatness > 0.62d && weakHarmonic && !reliableCandidate) return true;
            if (flatSpectrum && weakHarmonic && primaryStrength < 0.24f) return true;
            if (flatness > 0.54d && peakRatio < 0.13d && primaryStrength < 0.30f) return true;
            if (weakPeaks && harmonicity < 0.58d && primaryStrength < 0.24f) return true;
            if (primaryStrength < 0.09f && harmonicity < 0.64d) return true;

            return false;
        }

        private static double CalculateOnsetStrength(double previousRms, double currentRms)
        {
            if (previousRms <= 1e-6d)
            {
                return currentRms > 0.006d ? currentRms : 0d;
            }

            double riseRatio = currentRms / Math.Max(1e-6d, previousRms);
            double riseAmount = Math.Max(0d, currentRms - previousRms);
            return Math.Max(riseAmount, Math.Max(0d, riseRatio - 1d) * 0.012d);
        }

        private static PitchCandidate[] SelectMonophonicCandidates(IReadOnlyList<PitchCandidate> ordered, double autoFreq, RecognitionOptions options)
        {
            if (ordered == null || ordered.Count == 0)
            {
                return Array.Empty<PitchCandidate>();
            }

            int safeAutoMidi = -1;
            if (autoFreq > 0d)
            {
                (int autoMidi, _) = PitchUtils.FrequencyToMidiWithCents(autoFreq);
                safeAutoMidi = Math.Clamp(autoMidi, 24, 108);
            }

            float strongest = ordered.Max(c => c.Strength);
            float strongestUpper = ordered.Where(c => c.Midi >= 60).Select(c => c.Strength).DefaultIfEmpty(0f).Max();
            float strongestLow = ordered.Where(c => c.Midi < 60).Select(c => c.Strength).DefaultIfEmpty(0f).Max();
            bool hasClearLowEvidence = strongestLow >= Math.Max(0.24f, strongest * 0.80f)
                && strongestLow >= strongestUpper * 0.88f;
            bool hasClearUpperEvidence = strongestUpper >= Math.Max(0.22f, strongest * 0.58f);

            var scored = new List<(PitchCandidate Candidate, double Score)>(ordered.Count * 2);
            for (int i = 0; i < ordered.Count; i++)
            {
                PitchCandidate candidate = ordered[i];
                double score = Math.Clamp(candidate.Strength, 0.02d, 2.0d) * 1.12d
                    + ScoreAdaptiveRegister(candidate.Midi, hasClearLowEvidence, hasClearUpperEvidence, options);
                if (safeAutoMidi >= 0)
                {
                    int distance = Math.Abs(candidate.Midi - safeAutoMidi);
                    double autoReward = Math.Max(0d, 0.92d - distance * 0.18d);
                    if (options.Mode == AudioRecognitionMode.MelodyFocus
                        && safeAutoMidi < 60
                        && candidate.Midi < 60
                        && hasClearUpperEvidence)
                    {
                        autoReward *= 0.25d;
                        score -= 0.42d;
                    }

                    score += autoReward;
                    if (candidate.Midi - safeAutoMidi >= 12)
                    {
                        score -= options.Mode == AudioRecognitionMode.MelodyFocus && hasClearUpperEvidence ? 0.05d : 0.18d;
                    }
                    else if (safeAutoMidi - candidate.Midi >= 12)
                    {
                        score -= 0.52d;
                    }
                }

                scored.Add((candidate, score));
                if (options.AllowSyntheticLowerOctave && candidate.Midi >= 36 && candidate.Strength >= 0.15f)
                {
                    int lowerMidi = candidate.Midi - 12;
                    double lowerFreq = Math.Max(1d, candidate.FrequencyHz * 0.5d);
                    if (lowerMidi > options.SyntheticLowerMaxMidi && !hasClearLowEvidence)
                    {
                        continue;
                    }

                    bool lowerAlreadyInOrdered = ordered.Any(c => Math.Abs(c.Midi - lowerMidi) <= 1);
                    bool lowerMatchesAuto = safeAutoMidi >= 0 && Math.Abs(lowerMidi - safeAutoMidi) <= 2;
                    bool canUseLowerAuto = lowerMatchesAuto && (lowerAlreadyInOrdered || lowerMidi <= options.SyntheticLowerMaxMidi || hasClearLowEvidence);
                    float lowerStrength = Math.Clamp(
                        candidate.Strength * (lowerAlreadyInOrdered ? 0.68f : (canUseLowerAuto ? 0.58f : (hasClearLowEvidence ? 0.50f : 0.32f))),
                        0.04f,
                        0.82f);
                    double lowerPenalty = lowerAlreadyInOrdered ? 0.34d : (canUseLowerAuto ? 0.42d : (hasClearLowEvidence ? 0.56d : 0.90d));
                    double lowerScore = Math.Clamp(lowerStrength, 0.02d, 2.0d) * 1.12d
                        + ScoreAdaptiveRegister(lowerMidi, hasClearLowEvidence, hasClearUpperEvidence, options)
                        - lowerPenalty;
                    if (safeAutoMidi >= 0 && (canUseLowerAuto || lowerAlreadyInOrdered || hasClearLowEvidence))
                    {
                        int lowerDistance = Math.Abs(lowerMidi - safeAutoMidi);
                        lowerScore += Math.Max(0d, 0.92d - lowerDistance * 0.18d);
                        if (lowerMidi - safeAutoMidi >= 12)
                        {
                            lowerScore -= 0.18d;
                        }
                        else if (safeAutoMidi - lowerMidi >= 12)
                        {
                            lowerScore -= 0.34d;
                        }
                    }

                    scored.Add((new PitchCandidate(lowerMidi, lowerFreq, lowerStrength), lowerScore));
                }
            }

            var selected = scored
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Candidate.Strength)
                .ThenBy(item => safeAutoMidi >= 0 ? Math.Abs(item.Candidate.Midi - safeAutoMidi) : 0)
                .GroupBy(item => item.Candidate.Midi)
                .Select(g => g.First())
                .Take(4)
                .Select(item => item.Candidate)
                .ToList();

            return selected.Count == 0 ? Array.Empty<PitchCandidate>() : selected.ToArray();
        }

        private static void RebalanceCandidatesByContinuity(IList<PitchCandidate> candidates, RecognitionOptions options)
        {
            if (candidates.Count == 0 || !options.AllowSyntheticLowerOctave)
            {
                return;
            }

            // Keep lower-octave alternatives available, but lightly: for melody extraction
            // they should not overpower a real upper voice.
            PitchCandidate[] snapshot = candidates.ToArray();
            foreach (PitchCandidate candidate in snapshot)
            {
                if (candidate.Midi < 60 || candidate.Strength < 0.30f)
                {
                    continue;
                }

                int lowerMidi = candidate.Midi - 12;
                if (lowerMidi < 24)
                {
                    continue;
                }

                bool lowerAlreadyPresent = candidates.Any(c => Math.Abs(c.Midi - lowerMidi) <= 1);
                if (lowerMidi > options.SyntheticLowerMaxMidi)
                {
                    continue;
                }

                float lowerStrength = lowerAlreadyPresent
                    ? Math.Clamp(candidate.Strength * 0.08f, 0.02f, 0.18f)
                    : Math.Clamp(candidate.Strength * 0.14f, 0.02f, 0.24f);
                AddOrBoostCandidate(candidates, new PitchCandidate(lowerMidi, candidate.FrequencyHz * 0.5d, lowerStrength));
            }
        }

        private static PitchCandidate[] GetTrajectoryCandidates(FrameAnalysis frame)
        {
            if (!frame.HasPitch)
            {
                return Array.Empty<PitchCandidate>();
            }

            var deduped = frame.Candidates
                .OrderByDescending(c => c.Strength)
                .ThenByDescending(c => c.Midi)
                .GroupBy(c => c.Midi)
                .Select(g => g.First())
                .Take(4)
                .ToList();

            return deduped.Count == 0 ? Array.Empty<PitchCandidate>() : deduped.ToArray();
        }

        private static double ScoreTrajectoryCandidate(PitchCandidate candidate, double confidence, RecognitionOptions options)
        {
            double strength = Math.Clamp(candidate.Strength, 0.02d, 2.0d);
            double stableRangeBonus = Math.Max(0d, 1d - Math.Abs(candidate.Midi - MelodyRegisterCenterMidi) / 34d)
                * (options.Mode == AudioRecognitionMode.MelodyFocus ? 0.12d : 0.06d);
            return strength * 0.98d
                + Math.Clamp(confidence, 0d, 1d) * 1.02d
                + stableRangeBonus
                + ScoreMelodyRegister(candidate.Midi, options);
        }

        private static double ScoreAdaptiveRegister(int midi, bool hasClearLowEvidence, bool hasClearUpperEvidence, RecognitionOptions options)
        {
            double score = ScoreMelodyRegister(midi, options);
            if (options.Mode == AudioRecognitionMode.MelodyFocus)
            {
                if (midi < 60)
                {
                    score -= 0.24d;
                }
                else if (midi is >= 72 and <= 92)
                {
                    score += 0.10d;
                }

                return score;
            }

            if (hasClearLowEvidence)
            {
                if (midi < 60)
                {
                    score += options.Mode == AudioRecognitionMode.Dense ? 0.34d : 0.20d;
                }
                else if (midi >= 72)
                {
                    score -= options.Mode == AudioRecognitionMode.Dense ? 0.22d : 0.10d;
                }
            }
            else if (hasClearUpperEvidence && midi < 55)
            {
                score -= 0.12d;
            }

            return score;
        }

        private static double ScoreMelodyRegister(int midi)
        {
            return ScoreMelodyRegister(midi, RecognitionOptions.ForMode(AudioRecognitionMode.Balanced));
        }

        private static double ScoreMelodyRegister(int midi, RecognitionOptions options)
        {
            if (options.Mode == AudioRecognitionMode.MelodyFocus)
            {
                if (midi < 48)
                {
                    return -1.15d;
                }

                if (midi < MelodyRegisterFloorMidi)
                {
                    return -0.72d;
                }

                if (midi < 60)
                {
                    return -0.32d;
                }

                if (midi <= 88)
                {
                    return Math.Max(0d, 1d - Math.Abs(midi - MelodyRegisterCenterMidi) / 28d) * 0.34d;
                }

                if (midi <= 96)
                {
                    return 0.10d;
                }

                return -0.20d;
            }

            if (options.Mode == AudioRecognitionMode.Balanced)
            {
                if (midi < 48)
                {
                    return -0.72d;
                }

                if (midi < MelodyRegisterFloorMidi)
                {
                    return -0.38d;
                }

                if (midi < 60)
                {
                    return -0.14d;
                }

                if (midi <= 88)
                {
                    return Math.Max(0d, 1d - Math.Abs(midi - MelodyRegisterCenterMidi) / 28d) * 0.22d;
                }

                if (midi <= 96)
                {
                    return 0.05d;
                }

                return -0.14d;
            }

            if (midi < 48)
            {
                return -0.46d;
            }

            if (midi < MelodyRegisterFloorMidi)
            {
                return -0.16d;
            }

            if (midi < 60)
            {
                return 0.02d;
            }

            if (midi <= 88)
            {
                return Math.Max(0d, 1d - Math.Abs(midi - MelodyRegisterCenterMidi) / 28d) * 0.16d;
            }

            if (midi <= 96)
            {
                return 0.02d;
            }

            return -0.12d;
        }

        private static double ScoreTrajectoryTransition(int prevMidi, int nextMidi)
        {
            int delta = Math.Abs(nextMidi - prevMidi);
            if (delta <= 2)
            {
                return 0.46d;
            }

            if (delta <= 5)
            {
                return 0.06d - (delta - 2) * 0.16d;
            }

            if (delta <= 9)
            {
                return -0.62d - (delta - 5) * 0.27d;
            }

            double octavePenalty = delta >= 12 ? 1.20d : 0d;
            return -1.85d - (delta - 9) * 0.40d - octavePenalty;
        }

        private static bool ShouldSplitForPitchChange(IReadOnlyList<FrameAnalysis> frames, int index, int dominantMidi)
        {
            if (index <= 0 || index >= frames.Count)
            {
                return false;
            }

            FrameAnalysis current = frames[index];
            if (!IsFrameVoiced(current))
            {
                return false;
            }

            if (Math.Abs(current.PrimaryMidi - dominantMidi) <= 2 || current.Confidence < 0.34d)
            {
                return false;
            }

            int confirmations = 0;
            for (int j = index; j < Math.Min(frames.Count, index + 3); j++)
            {
                FrameAnalysis probe = frames[j];
                if (!IsFrameVoiced(probe))
                {
                    break;
                }

                if (Math.Abs(probe.PrimaryMidi - dominantMidi) > 2 && probe.Confidence >= 0.32d)
                {
                    confirmations++;
                }
            }

            return confirmations >= 2;
        }

        private static bool IsLikelyOnset(IReadOnlyList<FrameAnalysis> frames, int index, int runStart)
        {
            if (index <= runStart + 1 || index <= 0 || index >= frames.Count)
            {
                return false;
            }

            FrameAnalysis prev = frames[index - 1];
            FrameAnalysis cur = frames[index];
            if (!IsFrameVoiced(prev) || !IsFrameVoiced(cur))
            {
                return false;
            }

            bool enoughGapFromStart = index - runStart >= 3;
            if (!enoughGapFromStart || Math.Abs(cur.PrimaryMidi - prev.PrimaryMidi) > 1)
            {
                return false;
            }

            double rmsRise = cur.Rms / Math.Max(1e-6d, prev.Rms);
            bool clearEnergyAttack = rmsRise >= 1.55d && cur.Rms - prev.Rms >= 0.006d;
            bool confidenceAttack = cur.Confidence - prev.Confidence >= 0.20d;
            bool onsetPropertyAttack = cur.OnsetStrength >= 0.012d && cur.OnsetStrength >= prev.OnsetStrength * 1.35d;

            return clearEnergyAttack || confidenceAttack || onsetPropertyAttack;
        }

        private static int ResolveDominantMidi(IReadOnlyDictionary<int, int> votes, int fallback)
        {
            return ResolveDominantMidi(votes, fallback, RecognitionOptions.ForMode(AudioRecognitionMode.Balanced));
        }

        private static int ResolveDominantMidi(IReadOnlyDictionary<int, int> votes, int fallback, RecognitionOptions options)
        {
            if (votes.Count == 0)
            {
                return Math.Clamp(fallback, 24, 108);
            }

            return votes
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => Math.Abs(kvp.Key - fallback))
                .ThenByDescending(kvp => ScoreMelodyRegister(kvp.Key, options))
                .ThenByDescending(kvp => kvp.Key)
                .First()
                .Key;
        }

        private static double EstimateAdaptiveMinRms(float[] samples, int frameSize, int hopSize)
        {
            if (samples.Length < frameSize || frameSize <= 0 || hopSize <= 0)
            {
                return 0.0052d;
            }

            var rmsValues = new List<double>(Math.Min(3000, samples.Length / hopSize + 1));
            for (int start = 0; start + frameSize < samples.Length; start += hopSize)
            {
                rmsValues.Add(ComputeRms(samples, start, frameSize));
                if (rmsValues.Count >= 3000)
                {
                    break;
                }
            }

            if (rmsValues.Count == 0)
            {
                return 0.0052d;
            }

            rmsValues.Sort();
            int p22Index = (int)Math.Round((rmsValues.Count - 1) * 0.22d);
            p22Index = Math.Clamp(p22Index, 0, rmsValues.Count - 1);
            double p22 = rmsValues[p22Index];
            return Math.Clamp(p22 * 2.35d, 0.0052d, 0.024d);
        }

        private static IReadOnlyList<DetectedAudioNote> StabilizeVoices(IReadOnlyList<DetectedAudioNote> notes)
        {
            if (notes == null || notes.Count == 0)
            {
                return Array.Empty<DetectedAudioNote>();
            }

            var output = new List<DetectedAudioNote>(notes.Count);
            var previousByVoice = new Dictionary<int, int>();
            var groups = notes
                .GroupBy(n => Math.Round(n.StartSeconds, 3))
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in groups)
            {
                var ordered = group.OrderBy(n => n.Midi).ToList();
                for (int voice = 0; voice < ordered.Count; voice++)
                {
                    int? prev = previousByVoice.TryGetValue(voice, out int prevMidi) ? prevMidi : null;
                    int stabilizedMidi = StabilizeOctaveJump(ordered[voice].Midi, prev);
                    previousByVoice[voice] = stabilizedMidi;

                    output.Add(new DetectedAudioNote
                    {
                        Midi = stabilizedMidi,
                        FrequencyHz = stabilizedMidi == ordered[voice].Midi && ordered[voice].FrequencyHz > 0d
                            ? ordered[voice].FrequencyHz
                            : MidiToFrequency(stabilizedMidi),
                        StartSeconds = ordered[voice].StartSeconds,
                        DurationSeconds = ordered[voice].DurationSeconds
                    });
                }
            }

            return output
                .OrderBy(n => n.StartSeconds)
                .ThenByDescending(n => n.Midi)
                .ToList();
        }

        private static IReadOnlyList<DetectedAudioNote> CleanShortNoiseNotes(IReadOnlyList<DetectedAudioNote> notes)
        {
            return CleanShortNoiseNotes(notes, RecognitionOptions.ForMode(AudioRecognitionMode.Balanced));
        }

        private static IReadOnlyList<DetectedAudioNote> CleanShortNoiseNotes(IReadOnlyList<DetectedAudioNote> notes, RecognitionOptions options)
        {
            if (notes.Count == 0)
            {
                return notes;
            }

            var filtered = notes
                .Where(n => n.DurationSeconds >= options.MinCleanDurationSeconds)
                .OrderBy(n => n.StartSeconds)
                .ThenByDescending(n => n.Midi)
                .ToList();
            if (filtered.Count <= 2)
            {
                return filtered;
            }

            var merged = new List<DetectedAudioNote>(filtered.Count);
            foreach (DetectedAudioNote note in filtered)
            {
                if (merged.Count == 0)
                {
                    merged.Add(note);
                    continue;
                }

                DetectedAudioNote last = merged[^1];
                double lastEnd = last.StartSeconds + last.DurationSeconds;
                double gap = note.StartSeconds - lastEnd;
                if (note.Midi == last.Midi && gap >= -0.01d && gap <= options.SamePitchMergeGapSeconds)
                {
                    double newEnd = Math.Max(lastEnd, note.StartSeconds + note.DurationSeconds);
                    double newDuration = Math.Max(0.02d, newEnd - last.StartSeconds);
                    double weightedFreq =
                        (last.FrequencyHz * Math.Max(0.01d, last.DurationSeconds) + note.FrequencyHz * Math.Max(0.01d, note.DurationSeconds))
                        / Math.Max(0.02d, last.DurationSeconds + note.DurationSeconds);

                    merged[^1] = new DetectedAudioNote
                    {
                        Midi = last.Midi,
                        FrequencyHz = weightedFreq,
                        StartSeconds = last.StartSeconds,
                        DurationSeconds = newDuration
                    };
                    continue;
                }

                merged.Add(note);
            }

            var output = new List<DetectedAudioNote>(merged.Count);
            for (int i = 0; i < merged.Count; i++)
            {
                DetectedAudioNote current = merged[i];
                if (current.DurationSeconds < 0.16d && i > 0 && i < merged.Count - 1)
                {
                    DetectedAudioNote prev = merged[i - 1];
                    DetectedAudioNote next = merged[i + 1];
                    bool bridge = prev.Midi == next.Midi
                        && Math.Abs(prev.StartSeconds + prev.DurationSeconds - current.StartSeconds) < 0.09d
                        && Math.Abs(current.StartSeconds + current.DurationSeconds - next.StartSeconds) < 0.09d;
                    if (bridge)
                    {
                        continue;
                    }

                    bool isolatedLeap =
                        Math.Abs(current.Midi - prev.Midi) >= 10
                        && Math.Abs(current.Midi - next.Midi) >= 10
                        && Math.Abs(prev.Midi - next.Midi) <= 2;
                    if (isolatedLeap)
                    {
                        continue;
                    }

                    double prevGap = current.StartSeconds - (prev.StartSeconds + prev.DurationSeconds);
                    double nextGap = next.StartSeconds - (current.StartSeconds + current.DurationSeconds);
                    bool isolatedShortBlip =
                        current.DurationSeconds < 0.16d
                        && prevGap > 0.05d
                        && nextGap > 0.05d;
                    if (isolatedShortBlip)
                    {
                        continue;
                    }

                    bool shortUnstableNeighbor =
                        current.DurationSeconds < 0.14d
                        && Math.Abs(current.Midi - prev.Midi) >= 7
                        && Math.Abs(current.Midi - next.Midi) >= 7;
                    if (shortUnstableNeighbor)
                    {
                        continue;
                    }
                }

                output.Add(current);
            }

            return CorrectOctaveOutliers(output);
        }

        private static IReadOnlyList<DetectedAudioNote> CorrectOctaveOutliers(IReadOnlyList<DetectedAudioNote> notes)
        {
            if (notes.Count < 3)
            {
                return notes;
            }

            var output = notes.ToList();
            for (int i = 1; i < output.Count - 1; i++)
            {
                DetectedAudioNote prev = output[i - 1];
                DetectedAudioNote current = output[i];
                DetectedAudioNote next = output[i + 1];

                int neighborCenter = (int)Math.Round((prev.Midi + next.Midi) * 0.5d);
                int currentDelta = Math.Abs(current.Midi - neighborCenter);
                if (currentDelta < 9 || Math.Abs(prev.Midi - next.Midi) > 4)
                {
                    continue;
                }

                int bestMidi = current.Midi;
                int bestDelta = currentDelta;
                foreach (int shift in new[] { -12, 12 })
                {
                    int candidate = current.Midi + shift;
                    if (candidate < 24 || candidate > 108)
                    {
                        continue;
                    }

                    int delta = Math.Abs(candidate - neighborCenter);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestMidi = candidate;
                    }
                }

                if (bestMidi == current.Midi || bestDelta >= currentDelta || bestDelta > 4)
                {
                    continue;
                }

                output[i] = new DetectedAudioNote
                {
                    Midi = bestMidi,
                    FrequencyHz = MidiToFrequency(bestMidi),
                    StartSeconds = current.StartSeconds,
                    DurationSeconds = current.DurationSeconds
                };
            }

            return output;
        }

        private static IReadOnlyList<DetectedAudioNote> RefineDetectedNotes(
            float[] samples,
            int sampleRate,
            IReadOnlyList<DetectedAudioNote> notes,
            IProgress<AudioRecognitionProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            return RefineDetectedNotes(samples, sampleRate, notes, progress, RecognitionOptions.ForMode(AudioRecognitionMode.Balanced), cancellationToken);
        }

        private static IReadOnlyList<DetectedAudioNote> RefineDetectedNotes(
            float[] samples,
            int sampleRate,
            IReadOnlyList<DetectedAudioNote> notes,
            IProgress<AudioRecognitionProgress>? progress,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (notes.Count == 0)
            {
                return notes;
            }

            const double minFreq = 55d;
            const double maxFreq = 1760d;
            var refined = new List<DetectedAudioNote>(notes.Count);
            for (int i = 0; i < notes.Count; i++)
            {
                if ((i & 3) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int percent = 88 + Math.Clamp((int)Math.Round((i + 1) / (double)Math.Max(1, notes.Count) * 10d), 0, 10);
                    progress?.Report(new AudioRecognitionProgress
                    {
                        Percent = percent,
                        Stage = "Refining",
                        ProcessedFrames = i + 1,
                        TotalFrames = notes.Count
                    });
                }

                refined.Add(RefineDetectedNote(samples, sampleRate, notes, i, minFreq, maxFreq, options));
            }

            return refined
                .OrderBy(n => n.StartSeconds)
                .ThenByDescending(n => n.Midi)
                .ToList();
        }

        private static DetectedAudioNote RefineDetectedNote(
            float[] samples,
            int sampleRate,
            IReadOnlyList<DetectedAudioNote> notes,
            int index,
            double minFreq,
            double maxFreq,
            RecognitionOptions options)
        {
            DetectedAudioNote note = notes[index];
            if (samples.Length < 512 || note.DurationSeconds <= 0.04d)
            {
                return note;
            }

            int startSample = Math.Clamp((int)Math.Round(note.StartSeconds * sampleRate), 0, Math.Max(0, samples.Length - 1));
            int endSample = Math.Clamp((int)Math.Round((note.StartSeconds + note.DurationSeconds) * sampleRate), startSample + 1, samples.Length);
            int padding = Math.Max(24, (int)Math.Round(sampleRate * 0.012d));
            startSample = Math.Max(0, startSample - padding);
            endSample = Math.Min(samples.Length, endSample + padding);

            int segmentLength = endSample - startSample;
            if (segmentLength < 256)
            {
                return note;
            }

            int trimStart = Math.Clamp(startSample + (int)Math.Round(segmentLength * 0.16d), startSample, endSample - 128);
            int trimEnd = Math.Clamp(endSample - (int)Math.Round(segmentLength * 0.10d), trimStart + 128, endSample);
            int analysisLength = trimEnd - trimStart;
            if (analysisLength < 256)
            {
                trimStart = startSample;
                trimEnd = endSample;
                analysisLength = trimEnd - trimStart;
                if (analysisLength < 256)
                {
                    return note;
                }
            }

            double rms = ComputeRms(samples, trimStart, analysisLength);
            if (rms < 0.0035d)
            {
                return note;
            }

            var scoreByMidi = new Dictionary<int, CandidateVote>();
            AddPitchVote(scoreByMidi, note.Midi, note.FrequencyHz, 0.82d, 0.72d);

            var (yinFreq, yinScore) = EstimatePitchNormalizedDifference(samples, trimStart, analysisLength, sampleRate, minFreq, maxFreq);
            if (yinFreq > 0d)
            {
                var (yinMidi, _) = PitchUtils.FrequencyToMidiWithCents(yinFreq);
                AddPitchVote(scoreByMidi, yinMidi, yinFreq, 1.45d + yinScore * 0.9d, 1.10d + yinScore * 0.55d);
            }

            var (autoFreq, autoScore) = EstimatePitchAutocorrelation(samples, trimStart, analysisLength, sampleRate, minFreq, maxFreq);
            if (autoFreq > 0d)
            {
                var (autoMidi, _) = PitchUtils.FrequencyToMidiWithCents(autoFreq);
                AddPitchVote(scoreByMidi, autoMidi, autoFreq, 1.15d + autoScore * 0.8d, 0.95d + autoScore * 0.45d);
            }

            var (spectralCandidates, flatness, peakRatio) = AnalyzeSpectrum(samples, trimStart, analysisLength, sampleRate, minFreq, maxFreq, maxNotes: 6, options);
            foreach (PitchCandidate candidate in spectralCandidates)
            {
                double score = 0.58d + Math.Clamp(candidate.Strength, 0.02f, 2.0f) * 0.92d;
                if (flatness < 0.35d)
                {
                    score += (0.35d - flatness) * 0.65d;
                }

                if (peakRatio > 0.30d)
                {
                    score += (peakRatio - 0.30d) * 0.45d;
                }

                AddPitchVote(scoreByMidi, candidate.Midi, candidate.FrequencyHz, score, Math.Max(0.45d, candidate.Strength));
                if (options.AllowSyntheticLowerOctave && candidate.Midi >= 36 && candidate.Strength >= 0.16f)
                {
                    int lowerMidi = candidate.Midi - 12;
                    if (lowerMidi > options.RefineLowerOctaveMaxMidi && note.Midi > 69)
                    {
                        continue;
                    }

                    AddPitchVote(
                        scoreByMidi,
                        lowerMidi,
                        candidate.FrequencyHz * 0.5d,
                        score * 0.42d,
                        Math.Max(0.30d, candidate.Strength * 0.55d));
                }
            }

            if (scoreByMidi.Count == 0)
            {
                return note;
            }

            int? prevMidi = index > 0 ? notes[index - 1].Midi : null;
            int? nextMidi = index + 1 < notes.Count ? notes[index + 1].Midi : null;

            int bestMidi = note.Midi;
            double bestScore = double.NegativeInfinity;
            foreach ((int midi, CandidateVote vote) in scoreByMidi)
            {
                double score = vote.Score;
                score += Math.Max(0d, 0.85d - Math.Abs(midi - note.Midi) * 0.09d);
                if (prevMidi.HasValue)
                {
                    score += ScoreRefinedTransition(prevMidi.Value, midi);
                }

                if (nextMidi.HasValue)
                {
                    score += ScoreRefinedTransition(midi, nextMidi.Value);
                }

                if (midi - note.Midi >= 12)
                {
                    score -= 0.45d;
                }

                if (note.Midi - midi >= 12)
                {
                    score -= 0.18d;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMidi = midi;
                }
            }

            if (scoreByMidi.TryGetValue(bestMidi, out CandidateVote bestVote))
            {
                return new DetectedAudioNote
                {
                    Midi = Math.Clamp(bestMidi, 24, 108),
                    FrequencyHz = bestVote.ResolveFrequency(bestMidi),
                    StartSeconds = note.StartSeconds,
                    DurationSeconds = note.DurationSeconds
                };
            }

            return note;
        }

        private static void AddPitchVote(
            Dictionary<int, CandidateVote> scoreByMidi,
            int midi,
            double frequency,
            double score,
            double weight)
        {
            int safeMidi = Math.Clamp(midi, 24, 108);
            double safeFrequency = Math.Max(1d, frequency);
            double safeScore = Math.Max(0.02d, score);
            double safeWeight = Math.Max(0.05d, weight);
            if (!scoreByMidi.TryGetValue(safeMidi, out CandidateVote current))
            {
                current = new CandidateVote(0d, 0d, 0d);
            }

            scoreByMidi[safeMidi] = current.Add(safeScore, safeFrequency, safeWeight);
        }

        private static double ScoreRefinedTransition(int fromMidi, int toMidi)
        {
            int delta = Math.Abs(fromMidi - toMidi);
            if (delta <= 2)
            {
                return 0.30d;
            }

            if (delta <= 5)
            {
                return 0.12d - (delta - 2) * 0.08d;
            }

            if (delta <= 9)
            {
                return -0.18d - (delta - 5) * 0.12d;
            }

            return -0.80d - (delta - 9) * 0.14d;
        }
        private static (IReadOnlyList<PitchCandidate> Candidates, double Flatness, double PeakRatio) AnalyzeSpectrum(
            float[] samples,
            int start,
            int length,
            int sampleRate,
            double minFreq,
            double maxFreq,
            int maxNotes)
        {
            return AnalyzeSpectrum(samples, start, length, sampleRate, minFreq, maxFreq, maxNotes, RecognitionOptions.ForMode(AudioRecognitionMode.Balanced));
        }

        private static (IReadOnlyList<PitchCandidate> Candidates, double Flatness, double PeakRatio) AnalyzeSpectrum(
            float[] samples,
            int start,
            int length,
            int sampleRate,
            double minFreq,
            double maxFreq,
            int maxNotes,
            RecognitionOptions options)
        {
            int fftSize = NextPowerOfTwo(Math.Clamp(length * 2, 2048, 8192));
            int m = (int)Math.Log2(fftSize);
            var fft = new Complex[fftSize];
            int available = Math.Min(Math.Min(length, samples.Length - start), fftSize);
            if (available <= 32)
            {
                return (Array.Empty<PitchCandidate>(), 1d, 0d);
            }

            for (int i = 0; i < available; i++)
            {
                float window = 0.5f - 0.5f * (float)Math.Cos(2d * Math.PI * i / Math.Max(1, available - 1));
                fft[i].X = samples[start + i] * window;
                fft[i].Y = 0f;
            }

            for (int i = available; i < fftSize; i++)
            {
                fft[i].X = 0f;
                fft[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, m, fft);

            int minBin = Math.Max(1, (int)Math.Floor(minFreq * fftSize / sampleRate));
            int maxBin = Math.Min((fftSize / 2) - 2, (int)Math.Ceiling(maxFreq * fftSize / sampleRate));
            if (maxBin <= minBin)
            {
                return (Array.Empty<PitchCandidate>(), 1d, 0d);
            }

            var magnitude = new float[fftSize / 2];
            double sumMag = 0d;
            double sumLog = 0d;
            for (int bin = minBin - 1; bin <= maxBin + 1; bin++)
            {
                float re = fft[bin].X;
                float im = fft[bin].Y;
                float mag = MathF.Sqrt(re * re + im * im);
                magnitude[bin] = mag;
                if (bin >= minBin && bin <= maxBin)
                {
                    double v = Math.Max(1e-9, mag);
                    sumMag += v;
                    sumLog += Math.Log(v);
                }
            }

            int binCount = Math.Max(1, maxBin - minBin + 1);
            double arithmetic = sumMag / binCount;
            double geometric = Math.Exp(sumLog / binCount);
            double flatness = arithmetic > 0d ? geometric / arithmetic : 1d;

            var peaks = new List<(float Mag, double Freq, float Score)>();
            for (int bin = minBin + 1; bin < maxBin - 1; bin++)
            {
                float mag = magnitude[bin];
                if (mag < 1e-6f) continue;
                if (mag <= magnitude[bin - 1] || mag < magnitude[bin + 1]) continue;

                double freq = bin * sampleRate / (double)fftSize;
                float biasLow = (float)(1.0 / Math.Sqrt(Math.Max(55d, freq)));
                float score = mag * (1.55f + 25f * biasLow);
                peaks.Add((mag, freq, score));
            }

            if (peaks.Count == 0)
            {
                return (Array.Empty<PitchCandidate>(), flatness, 0d);
            }

            float maxMag = peaks.Max(p => p.Mag);
            float meanMag = peaks.Average(p => p.Mag);
            double peakRatio = Math.Clamp((maxMag / Math.Max(1e-6f, meanMag) - 1d) / 14d, 0d, 1d);
            float threshold = maxMag * options.SpectralPeakThresholdFactor;

            var selected = new List<PitchCandidate>(Math.Max(1, maxNotes));
            foreach (var peak in peaks.OrderByDescending(p => p.Score))
            {
                if (peak.Mag < threshold)
                {
                    continue;
                }

                var (midi, _) = PitchUtils.FrequencyToMidiWithCents(peak.Freq);
                int safeMidi = Math.Clamp(midi, 24, 108);
                if (selected.Any(c => Math.Abs(c.Midi - safeMidi) <= 1))
                {
                    continue;
                }

                if (IsLikelyHarmonic(peak.Freq, selected) && selected.Count > 0)
                {
                    continue;
                }

                float strength = peak.Mag / Math.Max(1e-6f, maxMag);
                selected.Add(new PitchCandidate(safeMidi, peak.Freq, strength));
                if (selected.Count >= maxNotes)
                {
                    break;
                }
            }

            foreach (PitchCandidate candidate in AnalyzeHarmonicSalience(magnitude, sampleRate, fftSize, minFreq, maxFreq, maxNotes + 2, options))
            {
                AddOrBoostCandidate(selected, candidate);
            }

            return (selected
                .OrderByDescending(c => c.Strength)
                .ThenBy(c => c.Midi)
                .Take(maxNotes)
                .ToList(), flatness, peakRatio);
        }

        private static IReadOnlyList<PitchCandidate> AnalyzeHarmonicSalience(
            IReadOnlyList<float> magnitude,
            int sampleRate,
            int fftSize,
            double minFreq,
            double maxFreq,
            int maxNotes,
            RecognitionOptions options)
        {
            if (magnitude.Count == 0 || sampleRate <= 0 || fftSize <= 0)
            {
                return Array.Empty<PitchCandidate>();
            }

            float maxMagnitude = 0f;
            for (int i = 1; i < magnitude.Count; i++)
            {
                maxMagnitude = Math.Max(maxMagnitude, magnitude[i]);
            }

            if (maxMagnitude <= 1e-7f)
            {
                return Array.Empty<PitchCandidate>();
            }

            int minMidi = Math.Clamp((int)Math.Floor(PitchUtils.FrequencyToMidi(minFreq)) - 1, 24, 108);
            int maxMidi = Math.Clamp((int)Math.Ceiling(PitchUtils.FrequencyToMidi(maxFreq)) + 1, 24, 108);
            var scored = new List<PitchCandidate>();
            for (int midi = minMidi; midi <= maxMidi; midi++)
            {
                double f0 = MidiToFrequency(midi);
                if (f0 < minFreq || f0 > maxFreq)
                {
                    continue;
                }

                double score = 0d;
                double weightSum = 0d;
                int supportedPartials = 0;
                double fundamental = InterpolatedMagnitude(magnitude, f0, sampleRate, fftSize) / maxMagnitude;
                for (int harmonic = 1; harmonic <= 8; harmonic++)
                {
                    double freq = f0 * harmonic;
                    if (freq >= sampleRate * 0.48d || freq > maxFreq * 5.2d)
                    {
                        break;
                    }

                    double normalized = InterpolatedMagnitude(magnitude, freq, sampleRate, fftSize) / maxMagnitude;
                    double weight = 1d / Math.Pow(harmonic, 0.72d);
                    score += normalized * weight;
                    weightSum += weight;
                    if (harmonic > 1 && normalized > 0.045d)
                    {
                        supportedPartials++;
                    }
                }

                if (weightSum <= 0d)
                {
                    continue;
                }

                double salience = score / weightSum;
                double minSalience = midi < 60 ? options.LowHarmonicSalienceThreshold : 0.105d;
                double minFundamental = midi < 60 ? options.LowFundamentalThreshold : 0.035d;
                if (salience < minSalience || (fundamental < minFundamental && supportedPartials < 2))
                {
                    continue;
                }

                double supportBonus = Math.Min(0.34d, supportedPartials * 0.055d);
                if (midi is >= 48 and < 60 && supportedPartials >= 2)
                {
                    supportBonus += options.LowSupportBonus;
                }

                float strength = (float)Math.Clamp(salience * 1.70d + supportBonus, 0.04d, 1.65d);
                scored.Add(new PitchCandidate(midi, f0, strength));
            }

            return scored
                .OrderByDescending(c => c.Strength)
                .ThenByDescending(c => ScoreMelodyRegister(c.Midi, options))
                .ThenBy(c => options.Mode == AudioRecognitionMode.MelodyFocus ? -c.Midi : c.Midi)
                .Take(Math.Max(1, maxNotes))
                .ToArray();
        }

        private static double InterpolatedMagnitude(IReadOnlyList<float> magnitude, double frequency, int sampleRate, int fftSize)
        {
            double bin = frequency * fftSize / Math.Max(1d, sampleRate);
            int low = (int)Math.Floor(bin);
            int high = low + 1;
            if (low < 0 || high >= magnitude.Count)
            {
                return 0d;
            }

            double t = bin - low;
            return magnitude[low] * (1d - t) + magnitude[high] * t;
        }

        private static void AddOrBoostCandidate(IList<PitchCandidate> candidates, PitchCandidate candidate)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                PitchCandidate current = candidates[i];
                if (Math.Abs(current.Midi - candidate.Midi) > 0)
                {
                    continue;
                }

                double frequency = (current.FrequencyHz * current.Strength + candidate.FrequencyHz * candidate.Strength)
                    / Math.Max(0.01d, current.Strength + candidate.Strength);
                float strength = Math.Clamp(current.Strength + candidate.Strength * 0.72f, 0.04f, 1.90f);
                candidates[i] = new PitchCandidate(current.Midi, frequency, strength);
                return;
            }

            candidates.Add(candidate);
        }

        private static void AddOrBoostCandidateByMidi(
            IList<PitchCandidate> candidates,
            int midi,
            double frequency,
            float strength,
            int tolerance = 1)
        {
            int safeMidi = Math.Clamp(midi, 24, 108);
            int idx = candidates.ToList().FindIndex(c => Math.Abs(c.Midi - safeMidi) <= tolerance);
            if (idx >= 0)
            {
                PitchCandidate old = candidates[idx];
                double oldWeight = Math.Max(0.05f, old.Strength);
                double newWeight = Math.Max(0.05f, strength);
                double mixedFreq = (old.FrequencyHz * oldWeight + frequency * newWeight) / (oldWeight + newWeight);
                candidates[idx] = new PitchCandidate(old.Midi, mixedFreq, Math.Clamp(old.Strength + strength, 0.04f, 1.95f));
            }
            else
            {
                candidates.Add(new PitchCandidate(safeMidi, frequency, strength));
            }
        }

        private static void BoostAgreedPitchCandidates(
            IList<PitchCandidate> candidates,
            double autoFreq,
            double yinFreq,
            IReadOnlyList<PitchCandidate> spectralCandidates)
        {
            if (candidates.Count == 0)
            {
                return;
            }

            int autoMidi = FrequencyToSafeMidi(autoFreq);
            int yinMidi = FrequencyToSafeMidi(yinFreq);
            for (int i = 0; i < candidates.Count; i++)
            {
                PitchCandidate candidate = candidates[i];
                int agreements = 0;
                if (autoMidi >= 0 && Math.Abs(candidate.Midi - autoMidi) <= 1) agreements++;
                if (yinMidi >= 0 && Math.Abs(candidate.Midi - yinMidi) <= 1) agreements++;
                if (spectralCandidates.Any(c => Math.Abs(c.Midi - candidate.Midi) <= 1)) agreements++;
                if (agreements >= 2)
                {
                    float boost = agreements >= 3 ? 0.32f : 0.18f;
                    candidates[i] = new PitchCandidate(candidate.Midi, candidate.FrequencyHz, Math.Clamp(candidate.Strength + boost, 0.04f, 1.95f));
                }
            }
        }

        private static void ResolveOctaveConflict(
            IList<PitchCandidate> candidates,
            double autoFreq,
            double yinFreq,
            IReadOnlyList<PitchCandidate> spectralCandidates)
        {
            int autoMidi = FrequencyToSafeMidi(autoFreq);
            int yinMidi = FrequencyToSafeMidi(yinFreq);
            if (autoMidi < 0 || yinMidi < 0 || Math.Abs(autoMidi - yinMidi) != 12)
            {
                return;
            }

            double autoSupport = ScoreSpectralMidiSupport(autoMidi, spectralCandidates);
            double yinSupport = ScoreSpectralMidiSupport(yinMidi, spectralCandidates);
            if (Math.Abs(autoSupport - yinSupport) < 0.08d)
            {
                return;
            }

            int preferredMidi = autoSupport > yinSupport ? autoMidi : yinMidi;
            int weakerMidi = preferredMidi == autoMidi ? yinMidi : autoMidi;
            for (int i = 0; i < candidates.Count; i++)
            {
                PitchCandidate candidate = candidates[i];
                if (Math.Abs(candidate.Midi - preferredMidi) <= 1)
                {
                    candidates[i] = new PitchCandidate(candidate.Midi, candidate.FrequencyHz, Math.Clamp(candidate.Strength + 0.24f, 0.04f, 1.95f));
                }
                else if (Math.Abs(candidate.Midi - weakerMidi) <= 1)
                {
                    candidates[i] = new PitchCandidate(candidate.Midi, candidate.FrequencyHz, Math.Clamp(candidate.Strength * 0.62f, 0.04f, 1.95f));
                }
            }
        }

        private static int FrequencyToSafeMidi(double frequency)
        {
            if (frequency <= 0d || double.IsNaN(frequency) || double.IsInfinity(frequency))
            {
                return -1;
            }

            var (midi, _) = PitchUtils.FrequencyToMidiWithCents(frequency);
            return Math.Clamp(midi, 24, 108);
        }

        private static double ScoreSpectralMidiSupport(int midi, IReadOnlyList<PitchCandidate> spectralCandidates)
        {
            double score = 0d;
            foreach (PitchCandidate candidate in spectralCandidates)
            {
                int distance = Math.Abs(candidate.Midi - midi);
                if (distance <= 1)
                {
                    score += candidate.Strength * (distance == 0 ? 1.0d : 0.72d);
                    continue;
                }

                int harmonicDistance = Math.Abs(candidate.Midi - (midi + 12));
                if (harmonicDistance <= 1)
                {
                    score += candidate.Strength * 0.46d;
                }
            }

            return score;
        }

        private static bool IsLikelyHarmonic(double frequency, IReadOnlyList<PitchCandidate> selected)
        {
            for (int i = 0; i < selected.Count; i++)
            {
                double baseFreq = selected[i].FrequencyHz;
                double ratio = Math.Max(frequency, baseFreq) / Math.Max(1d, Math.Min(frequency, baseFreq));
                int nearest = (int)Math.Round(ratio);
                if (nearest is >= 2 and <= 6 && Math.Abs(ratio - nearest) <= 0.06d)
                {
                    return true;
                }
            }
            return false;
        }

        private static int NextPowerOfTwo(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        private static (double Freq, double Score) EstimatePitchNormalizedDifference(float[] samples, int start, int length, int sampleRate, double minFreq, double maxFreq)
        {
            int minTau = (int)Math.Floor(sampleRate / maxFreq);
            int maxTau = (int)Math.Ceiling(sampleRate / minFreq);
            if (maxTau >= length / 2) maxTau = length / 2 - 1;
            if (minTau < 2 || maxTau <= minTau) return (0d, 0d);

            var diff = new double[maxTau + 1];
            for (int tau = 1; tau <= maxTau; tau++)
            {
                double d = 0d;
                for (int i = 0; i < length / 2; i++)
                {
                    double val = samples[start + i] - samples[start + i + tau];
                    d += val * val;
                }
                diff[tau] = d;
            }

            var cmndf = new double[maxTau + 1];
            cmndf[0] = 1d;
            double runningSum = 0d;
            for (int tau = 1; tau <= maxTau; tau++)
            {
                runningSum += diff[tau];
                cmndf[tau] = diff[tau] / (runningSum / tau);
            }

            int bestTau = -1;
            double threshold = 0.14d;
            for (int tau = minTau; tau <= maxTau; tau++)
            {
                if (cmndf[tau] < threshold)
                {
                    bestTau = tau;
                    while (bestTau + 1 <= maxTau && cmndf[bestTau + 1] < cmndf[bestTau])
                    {
                        bestTau++;
                    }
                    break;
                }
            }

            if (bestTau < 0)
            {
                double minVal = double.MaxValue;
                for (int tau = minTau; tau <= maxTau; tau++)
                {
                    if (cmndf[tau] < minVal)
                    {
                        minVal = cmndf[tau];
                        bestTau = tau;
                    }
                }
            }

            if (bestTau < minTau || bestTau > maxTau) return (0d, 0d);

            double x1 = cmndf[bestTau - 1];
            double x2 = cmndf[bestTau];
            double x3 = cmndf[bestTau + 1];
            double denom = x3 + x1 - 2d * x2;
            double refinedTau = Math.Abs(denom) < 1e-6d ? bestTau : bestTau + (x1 - x3) / (2d * denom);
            double freq = sampleRate / refinedTau;
            double score = Math.Clamp(1d - cmndf[bestTau], 0d, 1d);
            return (freq, score);
        }

        private static (double Freq, double Score) EstimatePitchAutocorrelation(float[] samples, int start, int length, int sampleRate, double minFreq, double maxFreq)
        {
            int minTau = (int)Math.Floor(sampleRate / maxFreq);
            int maxTau = (int)Math.Ceiling(sampleRate / minFreq);
            if (maxTau >= length / 2) maxTau = length / 2 - 1;
            if (minTau < 2 || maxTau <= minTau) return (0d, 0d);

            double energy = 0d;
            for (int i = 0; i < length / 2; i++) energy += samples[start + i] * samples[start + i];
            if (energy < 1e-7d) return (0d, 0d);

            int bestTau = -1;
            double maxCorr = -1d;
            for (int tau = minTau; tau <= maxTau; tau++)
            {
                double corr = 0d;
                for (int i = 0; i < length / 2; i++)
                {
                    corr += samples[start + i] * samples[start + i + tau];
                }
                double normalized = corr / energy;
                if (normalized > maxCorr)
                {
                    maxCorr = normalized;
                    bestTau = tau;
                }
            }

            if (bestTau < 0 || maxCorr < 0.25d) return (0d, 0d);
            return (sampleRate / (double)bestTau, Math.Clamp(maxCorr, 0d, 1d));
        }

        private static double ComputeRms(float[] samples, int start, int length)
        {
            if (length <= 0) return 0d;
            double sum = 0d;
            for (int i = 0; i < length; i++) sum += samples[start + i] * samples[start + i];
            return Math.Sqrt(sum / length);
        }

        private static double ComputeZeroCrossingRate(float[] samples, int start, int length)
        {
            if (length <= 1) return 0d;
            int count = 0;
            for (int i = 1; i < length; i++)
            {
                if ((samples[start + i - 1] > 0 && samples[start + i] <= 0) || (samples[start + i - 1] < 0 && samples[start + i] >= 0))
                    count++;
            }
            return count / (double)(length - 1);
        }

        private static (float[] Samples, int SampleRate) ReadMonoSamples(string path)
        {
            using var reader = new AudioFileReader(path);
            int sampleCount = (int)reader.Length / (reader.WaveFormat.BitsPerSample / 8);
            var samples = new float[sampleCount];
            int read = reader.Read(samples, 0, sampleCount);
            if (reader.WaveFormat.Channels == 1) return (samples.Take(read).ToArray(), reader.WaveFormat.SampleRate);

            var mono = new float[read / reader.WaveFormat.Channels];
            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0f;
                for (int c = 0; c < reader.WaveFormat.Channels; c++) sum += samples[i * reader.WaveFormat.Channels + c];
                mono[i] = sum / reader.WaveFormat.Channels;
            }
            return (mono, reader.WaveFormat.SampleRate);
        }

        private static float[] PreprocessSamples(float[] samples, int sampleRate)
        {
            if (samples.Length == 0) return samples;
            var output = new float[samples.Length];
            float alpha = 0.96f;
            output[0] = samples[0];
            for (int i = 1; i < samples.Length; i++) output[i] = samples[i] - alpha * samples[i - 1];
            return output;
        }

        private static int StabilizeOctaveJump(int currentMidi, int? previousMidi)
        {
            if (!previousMidi.HasValue) return currentMidi;
            int diff = currentMidi - previousMidi.Value;
            if (Math.Abs(diff) == 12 || Math.Abs(diff) == 24) return previousMidi.Value;
            return currentMidi;
        }

        private static double MidiToFrequency(int midi) => 440d * Math.Pow(2d, (midi - 69) / 12d);
    }

    internal static class PitchUtils
    {
        public static double FrequencyToMidi(double freq) => 69d + 12d * Math.Log2(freq / 440d);
        public static (int Midi, int Cents) FrequencyToMidiWithCents(double freq)
        {
            double raw = FrequencyToMidi(freq);
            int midi = (int)Math.Round(raw);
            int cents = (int)Math.Round((raw - midi) * 100d);
            return (midi, cents);
        }
    }
}
