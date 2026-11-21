using System;
using System.IO;

namespace Caption.Services
{
    /// <summary>
    /// Where Caption keeps things. Normally %LOCALAPPDATA%\Caption; in portable mode (an empty file named
    /// "portable" next to Caption.exe) everything lives in .\data next to the exe instead.
    /// Models and the whisper engine live here, outside the exe, so updates never touch them.
    /// </summary>
    public static class Paths
    {
        public const string AppName = "Caption";

        public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Caption.exe");

        public static string ExeDir { get; } = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

        public static bool IsPortable { get; } = File.Exists(Path.Combine(ExeDir, "portable"));

        /// <summary>Where Caption installs itself (the same place Hangar puts portable suite apps).</summary>
        public static string InstallDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");

        public static string DataDir { get; } = IsPortable
            ? Path.Combine(ExeDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

        public static string ModelsDir => Sub("models");
        public static string EngineDir => Sub("engine");
        public static string ThemesDir => Sub("themes");
        public static string UpdatesDir => Sub("updates");
        public static string VersionsDir => Sub("versions");
        public static string TranscriptsDir => Sub("transcripts");

        /// <summary>Hangar's theme folder: Caption also lists the suite's shared theme files.</summary>
        public static string HangarThemesDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangar", "themes");

        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string LogFile => Path.Combine(DataDir, "caption.log");
        public static string StateFile => Path.Combine(DataDir, "state.json");

        private static string Sub(string name) => Path.Combine(DataDir, name);

        public static void EnsureDir(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }
}
