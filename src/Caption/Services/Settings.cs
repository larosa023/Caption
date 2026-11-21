using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caption.Services
{
    public sealed class AppSettings
    {
        // ----- General -----
        public bool StartWithWindows { get; set; }
        /// <summary>Bumped when defaults change, so older settings files get the new ones.</summary>
        public int SettingsVersion { get; set; }
        public bool FirstRunDone { get; set; }

        // ----- Updates -----
        public bool AutoUpdate { get; set; } = true;
        /// <summary>"stable" or "beta" (beta also gets versions with a "-", like 0.2.0-beta.1).</summary>
        public string UpdateChannel { get; set; } = "stable";
        public List<string> SkipVersions { get; set; } = new();

        // ----- Appearance -----
        /// <summary>Theme id: dark, light, system, midnight, glass, frost, ... or "custom:&lt;name&gt;".</summary>
        public string Theme { get; set; } = "dark";
        /// <summary>"theme", "windows", or "#RRGGBB".</summary>
        public string Accent { get; set; } = "theme";

        // ----- Model / engine -----
        public string Model { get; set; } = "";
        /// <summary>"auto" (GPU if there's an NVIDIA card), "gpu" or "cpu".</summary>
        public string Processor { get; set; } = "auto";
        /// <summary>Keep the model in VRAM while captions are off (faster start, uses memory).</summary>
        public bool KeepModelLoaded { get; set; }

        // ----- Listening -----
        /// <summary>How often the line being spoken is re-read, in ms (lower = snappier, more GPU work).</summary>
        public int UpdateIntervalMs { get; set; } = 300;
        /// <summary>Silence that ends a line, in ms.</summary>
        public int PauseToEndMs { get; set; } = 900;
        /// <summary>Longest line before it's committed, in seconds.</summary>
        public int MaxLineSeconds { get; set; } = 8;
        public double VadThreshold { get; set; } = 0.5;
        public bool ShowSoundLabels { get; set; }
        public bool FilterHallucinations { get; set; } = true;
        public bool AutoSaveTranscripts { get; set; }

        // ----- Ollama -----
        public string OllamaUrl { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "";

        // ----- Profiles -----
        public string ActiveProfile { get; set; } = "everything";
        public List<Profile> Profiles { get; set; } = new();

        // ----- Caption bar -----
        public CaptionStyle Bar { get; set; } = new();

        // ----- Hotkeys -----
        public HotkeyBinding? ToggleHotkey { get; set; } = HotkeyBinding.Create(HotkeyBinding.Win | HotkeyBinding.Alt, 0x43); // C
        public HotkeyBinding? PauseHotkey { get; set; } = HotkeyBinding.Create(HotkeyBinding.Win | HotkeyBinding.Alt, 0x50); // P
        public HotkeyBinding? MoveHotkey { get; set; }
        public HotkeyBinding? TranscriptHotkey { get; set; }

        // ----- Windows -----
        public double? MainLeft { get; set; }
        public double? MainTop { get; set; }
        public double? MainWidth { get; set; }
        public double? MainHeight { get; set; }
        public double? TranscriptLeft { get; set; }
        public double? TranscriptTop { get; set; }
        public double? TranscriptWidth { get; set; }
        public double? TranscriptHeight { get; set; }

        [JsonIgnore]
        public Profile CurrentProfile =>
            Profiles.FirstOrDefault(p => p.Id == ActiveProfile) ?? Profiles.First();

        internal void Normalize()
        {
            if (SettingsVersion < 2)
            {
                // 0.1.1: captions keep whole sentences together and update faster.
                if (UpdateIntervalMs == 400) UpdateIntervalMs = 300;
                if (PauseToEndMs == 600) PauseToEndMs = 900;
                SettingsVersion = 2;
            }
            SkipVersions ??= new List<string>();
            Profiles ??= new List<Profile>();
            Profiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id));
            foreach (var p in Profiles) p.Normalize();
            if (Profiles.Count == 0) Profiles.AddRange(Profile.Defaults());
            if (!Profiles.Any(p => p.Id == ActiveProfile)) ActiveProfile = Profiles[0].Id;
            Bar ??= new CaptionStyle();
            Bar.Normalize();
            Theme = string.IsNullOrWhiteSpace(Theme) ? "dark" : Theme;
            Accent = string.IsNullOrWhiteSpace(Accent) ? "theme" : Accent;
            if (UpdateChannel is not ("stable" or "beta")) UpdateChannel = "stable";
            if (Processor is not ("auto" or "gpu" or "cpu")) Processor = "auto";
            UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 200, 2000);
            PauseToEndMs = Math.Clamp(PauseToEndMs, 250, 2500);
            MaxLineSeconds = Math.Clamp(MaxLineSeconds, 3, 25);
            VadThreshold = Math.Clamp(VadThreshold, 0.1, 0.95);
            OllamaUrl = string.IsNullOrWhiteSpace(OllamaUrl) ? "http://localhost:11434" : OllamaUrl.Trim().TrimEnd('/');
            OllamaModel ??= "";
            Model ??= "";
            if (ToggleHotkey is { IsValid: false }) ToggleHotkey = null;
            if (PauseHotkey is { IsValid: false }) PauseHotkey = null;
            if (MoveHotkey is { IsValid: false }) MoveHotkey = null;
            if (TranscriptHotkey is { IsValid: false }) TranscriptHotkey = null;
        }
    }

    /// <summary>What to listen to and how to show it, e.g. "Streams" or "Calls".</summary>
    public sealed class Profile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name { get; set; } = "New profile";

        /// <summary>"system" (everything on the default output), "device" (one output) or "apps" (only these apps).</summary>
        public string Source { get; set; } = "system";
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        /// <summary>Exe names, e.g. "Discord.exe". Each one's whole process tree is captured.</summary>
        public List<string> Apps { get; set; } = new();

        public bool IncludeMic { get; set; }
        /// <summary>Empty = default microphone.</summary>
        public string MicDeviceId { get; set; } = "";
        public string MicDeviceName { get; set; } = "";

        /// <summary>"auto" or a Whisper language code ("en", "ja", ...).</summary>
        public string Language { get; set; } = "auto";

        /// <summary>"off", "english" (Whisper translate) or "ollama" (to TargetLanguage through local Ollama).</summary>
        public string Translate { get; set; } = "off";
        public string TargetLanguage { get; set; } = "English";
        /// <summary>Show the original line too, above the translation.</summary>
        public bool ShowOriginal { get; set; }

        public Profile Clone()
        {
            var p = (Profile)MemberwiseClone();
            p.Apps = new List<string>(Apps);
            return p;
        }

        internal void Normalize()
        {
            Apps ??= new List<string>();
            Apps = Apps.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (Source is not ("system" or "device" or "apps")) Source = "system";
            if (Translate is not ("off" or "english" or "ollama")) Translate = "off";
            Name = string.IsNullOrWhiteSpace(Name) ? "Profile" : Name.Trim();
            Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim().ToLowerInvariant();
            TargetLanguage = string.IsNullOrWhiteSpace(TargetLanguage) ? "English" : TargetLanguage.Trim();
            DeviceId ??= ""; DeviceName ??= ""; MicDeviceId ??= ""; MicDeviceName ??= "";
        }

        public static IEnumerable<Profile> Defaults()
        {
            yield return new Profile { Id = "everything", Name = "Everything", Source = "system" };
            yield return new Profile
            {
                Id = "streams", Name = "Streams", Source = "apps",
                Apps = new List<string> { "Prism.exe", "Drift.exe", "chrome.exe", "msedge.exe", "firefox.exe" },
                Translate = "english"
            };
            yield return new Profile
            {
                Id = "calls", Name = "Calls", Source = "apps",
                Apps = new List<string> { "Discord.exe", "Driftcord.exe" },
                IncludeMic = true
            };
        }
    }

    public sealed class CaptionStyle
    {
        /// <summary>classic, cinema, minimal, contrast, theme or custom.</summary>
        public string Preset { get; set; } = "classic";
        public string FontFamily { get; set; } = "Segoe UI Variable Display";
        public double FontSize { get; set; } = 28;
        public bool Bold { get; set; } = true;
        public string TextColor { get; set; } = "#FFFFFF";
        public string BackgroundColor { get; set; } = "#000000";
        public double BackgroundOpacity { get; set; } = 0.72;
        public bool Shadow { get; set; }

        /// <summary>"one", "two" or "scroll".</summary>
        public string Layout { get; set; } = "two";
        /// <summary>"bottom" (of the screen), "window" (bottom of the active window) or "free".</summary>
        public string Position { get; set; } = "bottom";
        public double Width { get; set; } = 960;
        /// <summary>Height of the scrolling box.</summary>
        public double BoxHeight { get; set; } = 240;
        public double? Left { get; set; }
        public double? Top { get; set; }
        /// <summary>Gap between the bar and the bottom of the screen / window.</summary>
        public double BottomMargin { get; set; } = 72;
        /// <summary>"center" or "left".</summary>
        public string Align { get; set; } = "center";
        /// <summary>Show the line still being spoken slightly dimmed.</summary>
        public bool DimPartial { get; set; } = true;

        internal void Normalize()
        {
            FontSize = Math.Clamp(FontSize, 10, 96);
            BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
            Width = Math.Clamp(Width, 240, 4000);
            BoxHeight = Math.Clamp(BoxHeight, 80, 2000);
            BottomMargin = Math.Clamp(BottomMargin, 0, 1000);
            if (Layout is not ("one" or "two" or "scroll")) Layout = "two";
            if (Position is not ("bottom" or "window" or "free")) Position = "bottom";
            if (Align is not ("center" or "left")) Align = "center";
            FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI Variable Display" : FontFamily;
            TextColor = string.IsNullOrWhiteSpace(TextColor) ? "#FFFFFF" : TextColor;
            BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColor) ? "#000000" : BackgroundColor;
            Preset ??= "custom";
        }

        public CaptionStyle Clone() => (CaptionStyle)MemberwiseClone();
    }

    /// <summary>A key plus modifiers. Modifier bits match both WPF ModifierKeys and RegisterHotKey.</summary>
    public sealed class HotkeyBinding
    {
        public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

        public int Modifiers { get; set; }

        /// <summary>Windows virtual-key code.</summary>
        public int Key { get; set; }

        [JsonIgnore]
        public bool IsValid => Key > 0 && Key < 256;

        public static HotkeyBinding Create(int modifiers, int key) => new() { Modifiers = modifiers, Key = key };

        public bool SameAs(HotkeyBinding? other) =>
            other != null && other.Modifiers == Modifiers && other.Key == Key;
    }

    /// <summary>Loads and saves settings.json in the data folder.</summary>
    public static class SettingsStore
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static AppSettings Load()
        {
            AppSettings? settings = null;
            try
            {
                if (File.Exists(Paths.SettingsFile))
                    settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options);
            }
            catch (Exception ex)
            {
                Log.Error("Reading settings", ex);
                try { File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".bad", true); } catch { }
            }
            settings ??= new AppSettings();
            settings.Normalize();
            return settings;
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                // Write to a temp file and swap it in, so a crash mid-write can't corrupt settings.
                Directory.CreateDirectory(Paths.DataDir);
                string temp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, Paths.SettingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Saving settings", ex);
            }
        }
    }
}
