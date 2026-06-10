using System;
using System.IO;

namespace MusicBox.Services
{
    internal static class DebugTrace
    {
        private static readonly object SyncRoot = new();
        private static readonly string LogDirectory = Path.Combine(Path.GetTempPath(), "MusicMagic");
        private static readonly string LogPathValue = Path.Combine(LogDirectory, "compose-debug.log");

        public static string LogPath => LogPathValue;

        public static void Write(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(
                        LogPathValue,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
            catch
            {
            }
        }
    }
}
