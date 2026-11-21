using System;
using System.Collections.Generic;
using System.IO;

namespace Caption.Services
{
    /// <summary>
    /// Tiny diagnostic log at %LOCALAPPDATA%\Caption\caption.log (capped at ~512 KB, one backup).
    /// Errors that the app recovers from silently still land here, so problems can be traced.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 512 * 1024;

        private static readonly object Gate = new();
        private static readonly HashSet<string> Seen = new();

        /// <summary>Worker processes log with a prefix so both halves can share one file.</summary>
        public static string Prefix { get; set; } = "";

        public static string FilePath => Paths.LogFile;

        public static void Info(string message) => Write("INFO", message, null);

        public static void Warn(string message) => Write("WARN", message, null);

        public static void Error(string where, Exception ex) => Write("ERROR", where, ex);

        /// <summary>Logs a recoverable problem once per run, so a fault that repeats every second can't flood the file.</summary>
        public static void WarnOnce(string where, Exception ex)
        {
            lock (Gate)
            {
                if (!Seen.Add(where + "|" + ex.GetType().FullName)) return;
            }
            Write("WARN", where, ex);
        }

        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".old", overwrite: true);

                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Prefix}{message}";
                    if (ex != null) line += Environment.NewLine + ex;
                    using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    using var writer = new StreamWriter(stream);
                    writer.WriteLine(line);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
