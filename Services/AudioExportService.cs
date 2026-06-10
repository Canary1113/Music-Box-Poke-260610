using System;
using System.IO;
using System.Linq;
using MusicBox.Models;
using NAudio.Wave;

namespace MusicBox.Services
{
    public sealed class AudioExportService
    {
        private const int SampleRate = 44100;
        private const short BitsPerSample = 16;
        private const short ChannelCount = 2;
        private const float ClipGuard = 0.88f;
        private const float MasterGain = 0.29f;
        private const double TailSeconds = 0.88d;

        public void ExportWav(ScoreProject project, string path)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Audio export path is required.", nameof(path));

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PlaybackNoteSpan[] spans = ProjectPlaybackTimelineBuilder.BuildNoteSpans(project).ToArray();
            if (spans.Length == 0)
            {
                throw new InvalidOperationException("No playable notes are available for audio export.");
            }

            double secondsPerTick = 60d / Math.Max(20, project.Bpm) / Math.Max(1, project.Ppq);
            double durationSeconds = spans.Max(static span => span.SustainedEndTick) * secondsPerTick + TailSeconds;
            int totalSamples = Math.Max(1, (int)Math.Ceiling(durationSeconds * SampleRate));
            var leftBuffer = new float[totalSamples];
            var rightBuffer = new float[totalSamples];

            foreach (PlaybackNoteSpan span in spans)
            {
                MixPianoLikeNote(leftBuffer, rightBuffer, span, secondsPerTick);
            }

            LimitAndPolish(leftBuffer, rightBuffer);
            WritePcm16Wave(path, leftBuffer, rightBuffer);
        }

        private static void MixPianoLikeNote(float[] leftBuffer, float[] rightBuffer, PlaybackNoteSpan span, double secondsPerTick)
        {
            double startSeconds = span.StartTick * secondsPerTick;
            double keyUpSeconds = Math.Max(startSeconds + (1d / SampleRate), span.KeyUpTick * secondsPerTick);
            double sustainEndSeconds = Math.Max(keyUpSeconds, span.SustainedEndTick * secondsPerTick);
            int startSample = Math.Clamp((int)Math.Floor(startSeconds * SampleRate), 0, leftBuffer.Length - 1);
            int endSample = Math.Clamp((int)Math.Ceiling(sustainEndSeconds * SampleRate), startSample + 1, leftBuffer.Length);

            double frequency = 440d * Math.Pow(2d, (span.Midi - 69) / 12d);
            double velocityRatio = span.Velocity / 127d;
            double attackSeconds = 0.0028d + 0.002d * (1d - velocityRatio);
            double heldDuration = Math.Max(1d / SampleRate, keyUpSeconds - startSeconds);
            double pedalTailDuration = Math.Max(0d, sustainEndSeconds - keyUpSeconds);
            double inharmonicity = 0.000015d * Math.Max(0.4d, frequency / 220d);
            double noteEnergy = (0.07d + 0.12d * velocityRatio) * MasterGain;
            double pan = Math.Clamp((span.Midi - 60) / 42d, -0.34d, 0.34d);
            double leftGain = Math.Sqrt(0.5d * (1d - pan));
            double rightGain = Math.Sqrt(0.5d * (1d + pan));
            double sparkleWidth = 0.016d + 0.009d * Math.Abs(pan);

            double[] partialWeights = { 0.82d, 0.70d, 0.48d, 0.31d, 0.20d, 0.13d, 0.08d };
            double[] partialPhases = new double[partialWeights.Length];
            double[] partialSteps = new double[partialWeights.Length];
            for (int i = 0; i < partialWeights.Length; i++)
            {
                int harmonic = i + 1;
                double stretched = harmonic * frequency * Math.Sqrt(1d + inharmonicity * harmonic * harmonic);
                partialSteps[i] = (Math.PI * 2d * stretched) / SampleRate;
                partialPhases[i] = 0.17d * i;
            }

            for (int sampleIndex = startSample; sampleIndex < endSample; sampleIndex++)
            {
                double elapsed = (sampleIndex - startSample) / (double)SampleRate;
                double envelope = ComputePianoEnvelope(elapsed, heldDuration, pedalTailDuration, attackSeconds, frequency, velocityRatio);
                if (envelope <= 1e-6d)
                {
                    continue;
                }

                double pedalMix = heldDuration <= 0d ? 0d : Math.Clamp((elapsed - heldDuration) / Math.Max(1d / SampleRate, pedalTailDuration + 0.020d), 0d, 1d);
                double body = 0d;
                for (int i = 0; i < partialWeights.Length; i++)
                {
                    double harmonicDecay = Math.Exp(-elapsed * ((1.35d + frequency / 1820d) + i * (2.45d + frequency / 760d)));
                    double pedalLift = i >= 1 ? 1d + pedalMix * 0.52d : 1d;
                    body += partialWeights[i] * harmonicDecay * pedalLift * Math.Sin(partialPhases[i]);
                    partialPhases[i] += partialSteps[i];
                }

                double hammer =
                    0.095d * Math.Sin(partialPhases[1] * 4.0d + 0.41d) * Math.Exp(-elapsed * 175d) +
                    0.070d * Math.Sin(partialPhases[2] * 5.9d + 0.19d) * Math.Exp(-elapsed * 245d) +
                    0.030d * Math.Sin(partialPhases[Math.Min(4, partialPhases.Length - 1)] * 7.2d + 0.07d) * Math.Exp(-elapsed * 325d);

                double afterRelease = Math.Max(0d, elapsed - heldDuration);
                double pedalChime =
                    pedalMix * 0.128d * Math.Sin(partialPhases[Math.Min(3, partialPhases.Length - 1)] * 1.6d + 0.22d) * Math.Exp(-afterRelease * 4.6d) +
                    pedalMix * 0.080d * Math.Sin(partialPhases[Math.Min(5, partialPhases.Length - 1)] * 1.28d + 0.31d) * Math.Exp(-afterRelease * 5.8d) +
                    pedalMix * 0.046d * Math.Sin(partialPhases[Math.Min(6, partialPhases.Length - 1)] * 1.12d + 0.11d) * Math.Exp(-afterRelease * 6.9d);

                double warmth = 0.020d * Math.Sin(partialPhases[0] * 0.5d + 0.12d) * Math.Exp(-elapsed * 5.4d);
                double stereoSparkle =
                    sparkleWidth * Math.Sin(partialPhases[1] * 3.6d + 0.25d) * Math.Exp(-elapsed * 150d) +
                    0.008d * Math.Sin(partialPhases[2] * 2.2d + 0.41d) * Math.Exp(-elapsed * 35d);
                double sample = noteEnergy * envelope * (0.79d * body + hammer + warmth + pedalChime);
                leftBuffer[sampleIndex] += (float)(sample * leftGain - noteEnergy * envelope * stereoSparkle);
                rightBuffer[sampleIndex] += (float)(sample * rightGain + noteEnergy * envelope * stereoSparkle);
            }
        }

        private static double ComputePianoEnvelope(double elapsed, double heldDuration, double pedalTailDuration, double attackSeconds, double frequency, double velocityRatio)
        {
            if (elapsed < 0d)
            {
                return 0d;
            }

            double attack = attackSeconds <= 0d ? 1d : Math.Clamp(elapsed / attackSeconds, 0d, 1d);
            double attackCurve = Math.Sin(attack * Math.PI * 0.5d);

            double baseDecayRate = 1.5d + frequency / 880d + (1d - velocityRatio) * 0.7d;
            double holdLevel = Math.Exp(-Math.Min(elapsed, heldDuration) * baseDecayRate);

            if (elapsed <= heldDuration)
            {
                return attackCurve * holdLevel;
            }

            double afterKeyRelease = elapsed - heldDuration;
            double releaseDecayRate = 8.5d + frequency / 420d;
            double releasedLevel = holdLevel * Math.Exp(-afterKeyRelease * releaseDecayRate);

            if (pedalTailDuration <= 1e-6d)
            {
                return attackCurve * releasedLevel;
            }

            double pedalBlend = Math.Exp(-afterKeyRelease * (3.1d + frequency / 1500d));
            double pedalResonance = holdLevel * (0.68d + 0.16d * velocityRatio) * pedalBlend;
            double pedalBloom = holdLevel * 0.18d * (1d - Math.Exp(-afterKeyRelease * 14d)) * Math.Exp(-afterKeyRelease * (2.25d + frequency / 2200d));
            if (afterKeyRelease > pedalTailDuration)
            {
                double afterPedalRelease = afterKeyRelease - pedalTailDuration;
                double pedalReleaseFade = Math.Exp(-afterPedalRelease * (9.2d + frequency / 650d));
                pedalResonance *= pedalReleaseFade;
                pedalBloom *= pedalReleaseFade;
            }

            return attackCurve * (releasedLevel + pedalResonance + pedalBloom);
        }

        private static void LimitAndPolish(float[] leftBuffer, float[] rightBuffer)
        {
            if (leftBuffer.Length == 0 || rightBuffer.Length == 0)
            {
                return;
            }

            HighPassInPlace(leftBuffer);
            HighPassInPlace(rightBuffer);

            float peak = 0f;
            for (int i = 0; i < leftBuffer.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs(leftBuffer[i]));
                peak = Math.Max(peak, Math.Abs(rightBuffer[i]));
            }

            if (peak <= 1e-6f)
            {
                return;
            }

            float gain = peak > ClipGuard ? ClipGuard / peak : 1f;
            ShapeChannelInPlace(leftBuffer, gain);
            ShapeChannelInPlace(rightBuffer, gain);
        }

        private static void HighPassInPlace(float[] buffer)
        {
            float previousInput = 0f;
            float previousOutput = 0f;
            const float highPass = 0.992f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float input = buffer[i];
                float output = highPass * (previousOutput + input - previousInput);
                buffer[i] = output;
                previousInput = input;
                previousOutput = output;
            }
        }

        private static void ShapeChannelInPlace(float[] buffer, float gain)
        {
            float previousShaped = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float shaped = buffer[i] * gain;
                float brightened = shaped + 0.10f * (shaped - previousShaped);
                buffer[i] = (float)Math.Tanh(brightened * 0.92f);
                previousShaped = shaped;
            }
        }

        private static void WritePcm16Wave(string path, float[] leftBuffer, float[] rightBuffer)
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, BitsPerSample, ChannelCount));
            int sampleCount = Math.Min(leftBuffer.Length, rightBuffer.Length);
            byte[] bytes = new byte[sampleCount * ChannelCount * sizeof(short)];

            for (int i = 0; i < sampleCount; i++)
            {
                short left = (short)Math.Round(Math.Clamp(leftBuffer[i], -1f, 1f) * short.MaxValue);
                short right = (short)Math.Round(Math.Clamp(rightBuffer[i], -1f, 1f) * short.MaxValue);
                int offset = i * ChannelCount * sizeof(short);
                BitConverter.TryWriteBytes(bytes.AsSpan(offset, sizeof(short)), left);
                BitConverter.TryWriteBytes(bytes.AsSpan(offset + sizeof(short), sizeof(short)), right);
            }

            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();
        }
    }
}
