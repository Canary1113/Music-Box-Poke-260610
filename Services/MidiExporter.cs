using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicBox.Models;

namespace MusicBox.Services
{
    public sealed class MidiExporter
    {
        private readonly struct TimedMidiEvent
        {
            public TimedMidiEvent(int tick, int order, byte[] data)
            {
                Tick = tick;
                Order = order;
                Data = data;
            }

            public int Tick { get; }
            public int Order { get; }
            public byte[] Data { get; }
        }

        public void Export(ScoreProject project, string path)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);

            int ppq = Math.Clamp(project.Ppq, 24, 9600);
            int bpm = Math.Clamp(project.Bpm, 20, 300);
            int tempoMicroseconds = (int)Math.Round(60_000_000d / bpm);

            var events = new List<TimedMidiEvent>
            {
                // Tempo meta event.
                new TimedMidiEvent(0, 0, new byte[]
                {
                    0xFF, 0x51, 0x03,
                    (byte)((tempoMicroseconds >> 16) & 0xFF),
                    (byte)((tempoMicroseconds >> 8) & 0xFF),
                    (byte)(tempoMicroseconds & 0xFF)
                }),
                // Time signature meta event.
                new TimedMidiEvent(0, 1, BuildTimeSignatureMeta(project.TimeSignature)),
                // Program change to Acoustic Grand Piano on channel 1.
                new TimedMidiEvent(0, 2, new byte[] { 0xC0, 0x00 })
            };

            foreach (var note in project.Notes.Where(n => !n.IsRest))
            {
                int pitch = Math.Clamp(GetEffectiveMidi(note, GetEffectiveKeySignatureFifthsAtTick(project, note.StartTick)), 0, 127);
                int startTick = Math.Max(0, note.StartTick);
                int duration = Math.Max(1, note.DurationTicks);
                int endTick = startTick + duration;

                events.Add(new TimedMidiEvent(startTick, 10, new byte[] { 0x90, (byte)pitch, 0x64 }));
                events.Add(new TimedMidiEvent(endTick, 5, new byte[] { 0x80, (byte)pitch, 0x00 }));
            }

            events.Sort((a, b) =>
            {
                int tickCompare = a.Tick.CompareTo(b.Tick);
                return tickCompare != 0 ? tickCompare : a.Order.CompareTo(b.Order);
            });

            using var trackStream = new MemoryStream();
            using (var trackWriter = new BinaryWriter(trackStream))
            {
                int lastTick = 0;
                foreach (var midiEvent in events)
                {
                    WriteVariableLength(trackWriter, midiEvent.Tick - lastTick);
                    trackWriter.Write(midiEvent.Data);
                    lastTick = midiEvent.Tick;
                }

                WriteVariableLength(trackWriter, 0);
                trackWriter.Write((byte)0xFF);
                trackWriter.Write((byte)0x2F);
                trackWriter.Write((byte)0x00);
            }

            byte[] trackData = trackStream.ToArray();
            using var fs = File.Create(path);
            using var writer = new BinaryWriter(fs);

            // MThd chunk.
            writer.Write(new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d' });
            WriteInt32BigEndian(writer, 6);
            WriteInt16BigEndian(writer, 0); // format 0
            WriteInt16BigEndian(writer, 1); // one track
            WriteInt16BigEndian(writer, ppq);

            // MTrk chunk.
            writer.Write(new byte[] { (byte)'M', (byte)'T', (byte)'r', (byte)'k' });
            WriteInt32BigEndian(writer, trackData.Length);
            writer.Write(trackData);
        }

        private static int GetAccidentalSemitoneOffset(NoteAccidental accidental)
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

        private static int QuantizeToNaturalMidi(int midi)
        {
            int clamped = Math.Clamp(midi, 0, 127);
            int octaveBlock = clamped / 12;
            int pitchClass = clamped % 12;
            int[] naturalPitchClasses = { 0, 2, 4, 5, 7, 9, 11 };

            int best = naturalPitchClasses[0];
            int bestDiff = Math.Abs(pitchClass - best);
            for (int i = 1; i < naturalPitchClasses.Length; i++)
            {
                int candidate = naturalPitchClasses[i];
                int diff = Math.Abs(pitchClass - candidate);
                if (diff < bestDiff || (diff == bestDiff && candidate < best))
                {
                    best = candidate;
                    bestDiff = diff;
                }
            }

            return Math.Clamp(octaveBlock * 12 + best, 0, 127);
        }

        private static int GetKeySignatureSemitoneOffset(int naturalMidi, int fifths)
        {
            if (fifths == 0) return 0;

            int pitchClass = QuantizeToNaturalMidi(naturalMidi) % 12;
            if (fifths > 0)
            {
                int[] sharpOrder = { 5, 0, 7, 2, 9, 4, 11 }; // F C G D A E B
                int count = Math.Min(fifths, sharpOrder.Length);
                for (int i = 0; i < count; i++)
                {
                    if (pitchClass == sharpOrder[i]) return 1;
                }
            }
            else
            {
                int[] flatOrder = { 11, 4, 9, 2, 7, 0, 5 }; // B E A D G C F
                int count = Math.Min(Math.Abs(fifths), flatOrder.Length);
                for (int i = 0; i < count; i++)
                {
                    if (pitchClass == flatOrder[i]) return -1;
                }
            }

            return 0;
        }

        private static int GetEffectiveMidi(NoteEvent note, int keySignatureFifths)
        {
            int naturalMidi = QuantizeToNaturalMidi(note.Midi - GetAccidentalSemitoneOffset(note.Accidental));
            int offset = note.Accidental switch
            {
                NoteAccidental.DoubleSharp => 2,
                NoteAccidental.Sharp => 1,
                NoteAccidental.Flat => -1,
                NoteAccidental.DoubleFlat => -2,
                NoteAccidental.Natural => 0,
                _ => GetKeySignatureSemitoneOffset(naturalMidi, keySignatureFifths)
            };
            return Math.Clamp(naturalMidi + offset, 0, 127);
        }

        private static int GetEffectiveKeySignatureFifthsAtTick(ScoreProject project, int tick)
        {
            int safeTick = Math.Max(0, tick);
            int active = Math.Clamp(project.KeySignature.Fifths, -7, 7);
            if (project.KeySignatureChanges == null || project.KeySignatureChanges.Count == 0)
            {
                return active;
            }

            foreach (var change in project.KeySignatureChanges
                .Where(c => c != null && c.Tick > 0)
                .OrderBy(c => c.Tick))
            {
                if (change.Tick <= safeTick)
                {
                    active = Math.Clamp(change.Fifths, -7, 7);
                }
                else
                {
                    break;
                }
            }

            return active;
        }

        private static byte[] BuildTimeSignatureMeta(TimeSignature timeSignature)
        {
            int numerator = Math.Clamp(timeSignature.Numerator, 1, 12);
            int denominator = timeSignature.Denominator switch
            {
                1 => 1,
                2 => 2,
                4 => 4,
                8 => 8,
                16 => 16,
                _ => 4
            };

            byte dd = denominator switch
            {
                1 => 0,
                2 => 1,
                4 => 2,
                8 => 3,
                16 => 4,
                _ => 2
            };

            return new byte[]
            {
                0xFF, 0x58, 0x04,
                (byte)numerator,
                dd,
                24,
                8
            };
        }

        private static void WriteInt16BigEndian(BinaryWriter writer, int value)
        {
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }

        private static void WriteInt32BigEndian(BinaryWriter writer, int value)
        {
            writer.Write((byte)((value >> 24) & 0xFF));
            writer.Write((byte)((value >> 16) & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }

        private static void WriteVariableLength(BinaryWriter writer, int value)
        {
            value = Math.Max(0, value);
            int buffer = value & 0x7F;

            while ((value >>= 7) > 0)
            {
                buffer <<= 8;
                buffer |= ((value & 0x7F) | 0x80);
            }

            while (true)
            {
                writer.Write((byte)(buffer & 0xFF));
                if ((buffer & 0x80) != 0)
                {
                    buffer >>= 8;
                }
                else
                {
                    break;
                }
            }
        }
    }
}
