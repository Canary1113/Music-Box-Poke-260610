using MusicBox.Models;

namespace MusicBox.Services
{
    public static class NoteLengthUtils
    {
        public static int ToTicks(NoteLength length, int ppq)
        {
            return length switch
            {
                NoteLength.None => 0,
                NoteLength.Whole => 4 * ppq,
                NoteLength.Half => 2 * ppq,
                NoteLength.Quarter => ppq,
                NoteLength.DottedQuarter => ppq + ppq / 2,
                NoteLength.Eighth => ppq / 2,
                NoteLength.DottedEighth => ppq / 2 + ppq / 4,
                NoteLength.Sixteenth => ppq / 4,
                NoteLength.ThirtySecond => ppq / 8,
                _ => ppq
            };
        }
    }
}
