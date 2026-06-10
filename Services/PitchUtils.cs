using System;

namespace MusicBox.Services
{
    public static class PitchUtils
    {
        public static double FrequencyToMidi(double frequency)
        {
            if (frequency <= 0) return 0;
            return 69 + 12 * Math.Log(frequency / 440.0, 2);
        }

        public static (int midi, double cents) FrequencyToMidiWithCents(double frequency)
        {
            var midiFloat = FrequencyToMidi(frequency);
            int midi = (int)Math.Round(midiFloat);
            double cents = (midiFloat - midi) * 100.0;
            return (midi, cents);
        }

        public static string MidiToName(int midi)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int note = ((midi % 12) + 12) % 12;
            int octave = midi / 12 - 1;
            return $"{names[note]}{octave}";
        }
    }
}
