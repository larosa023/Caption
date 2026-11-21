using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Caption.Services
{
    /// <summary>A suite theme (same fields and file format as Hangar's themes, see Hangar docs/THEMES.md).</summary>
    public sealed class ThemeDef
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public bool Dark = true;
        public string Backdrop = "none";   // mica | acrylic | none
        public float Transparency;
        public float SurfaceOpacity = 1f;
        public uint Bg, Sidebar, Surface, SurfaceHover, SurfaceStrong, Text, Subtext, Faint;
        /// <summary>0 = the Windows accent.</summary>
        public uint Accent;
        public bool FollowWindows;
        public bool Custom;
        public string? File;

        public ThemeDef Copy() => (ThemeDef)MemberwiseClone();
    }

    /// <summary>
    /// The 14 suite themes, accent colors and custom JSON theme files, switched live through
    /// DynamicResource brushes. Windows get Mica/Acrylic on Windows 11 where the theme asks for it.
    /// </summary>
    public static class ThemeManager
    {
        public static readonly uint[] AccentSwatches = { 0x4F8CFF, 0x9B6CF2, 0xE86A92, 0xFF5D5D, 0xFF8A4C, 0xF2B84B, 0x3FB98E, 0x2EC4C4, 0xA0A0AA };

        private static readonly List<ThemeDef> Builtins = BuildBuiltins();
        private static readonly List<WeakReference<Window>> Windows = new();
        private static ResourceDictionary? _resources;
        private static bool _listening;
        private static string _themeId = "dark";
        private static string _accent = "theme";

        public static IReadOnlyList<ThemeDef> BuiltinThemes => Builtins;
        public static List<ThemeDef> CustomThemes { get; private set; } = new();

        public static ThemeDef Current { get; private set; } = Builtins[0];
        /// <summary>The theme actually shown ("Windows" resolves to Light or Dark).</summary>
        public static ThemeDef Resolved { get; private set; } = Builtins[0];
        public static bool IsDark { get; private set; } = true;
        public static bool UsesBackdrop { get; private set; }
        public static Color AccentColor { get; private set; } = Color.FromRgb(0x4F, 0x8C, 0xFF);

        public static event EventHandler? ThemeChanged;

        private static ThemeDef Def(string id, string name, string desc, bool dark, string backdrop, float transparency,
            uint bg, uint sidebar, uint surface, uint hover, uint strong, uint text, uint subtext, uint faint, uint accent) => new()
        {
            Id = id, Name = name, Description = desc, Dark = dark, Backdrop = backdrop, Transparency = transparency,
            Bg = bg, Sidebar = sidebar, Surface = surface, SurfaceHover = hover, SurfaceStrong = strong,
            Text = text, Subtext = subtext, Faint = faint, Accent = accent
        };

        private static List<ThemeDef> BuildBuiltins()
        {
            var list = new List<ThemeDef>
            {
                Def("dark", "Dark", "The default", true, "mica", 0.20f,
                    0x16161A, 0x111114, 0x222228, 0x2A2A31, 0x34343C, 0xF2F2F5, 0xA8A8B3, 0x6E6E7A, 0),
                Def("light", "Light", "Bright and clean", false, "mica", 0.20f,
                    0xF5F5F7, 0xEBEBEF, 0xFFFFFF, 0xF7F7FA, 0xE6E6EC, 0x16161A, 0x5C5C68, 0x8E8E9A, 0),
            };
            var sys = list[0].Copy();
            sys.Id = "system"; sys.Name = "Windows"; sys.Description = "Light or dark, like Windows"; sys.FollowWindows = true;
            list.Add(sys);
            list.Add(Def("midnight", "Midnight", "Pure black for OLED screens", true, "none", 0f,
                0x000000, 0x000000, 0x0E0E10, 0x16161A, 0x202025, 0xF5F5F7, 0xA0A0AA, 0x64646E, 0x7AA2FF));
            var glass = Def("glass", "Glass", "See-through, over your wallpaper", true, "acrylic", 0.55f,
                0x141418, 0x0E0E12, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xF7F7FA, 0xC2C2CC, 0x8A8A96, 0);
            glass.SurfaceOpacity = 0.07f;
            list.Add(glass);
            list.Add(Def("frost", "Frost", "Cool arctic blues", true, "mica", 0.12f,
                0x2E3440, 0x272C36, 0x3B4252, 0x434C5E, 0x4C566A, 0xECEFF4, 0xC0C8D6, 0x7B8698, 0x88C0D0));
            list.Add(Def("twilight", "Twilight", "Deep violet", true, "mica", 0.12f,
                0x1E1B2E, 0x19162A, 0x2A2640, 0x332E4D, 0x3E3859, 0xEEEAF7, 0xB4ADC9, 0x7D7596, 0xB794F6));
            list.Add(Def("forest", "Forest", "Evergreen and calm", true, "mica", 0.12f,
                0x141B17, 0x101612, 0x1D2721, 0x24302A, 0x2E3B33, 0xE8F0EA, 0xA7B8AD, 0x6C7F72, 0x5FD08B));
            list.Add(Def("sunset", "Sunset", "Warm and cozy", true, "mica", 0.12f,
                0x1C1614, 0x17110F, 0x2A201C, 0x332722, 0x3E302A, 0xF6EEE9, 0xC4B3A9, 0x8B776C, 0xFF8A4C));
            list.Add(Def("ocean", "Ocean", "Deep teal", true, "mica", 0.12f,
                0x0F1A20, 0x0B151A, 0x16252D, 0x1C2E37, 0x243843, 0xE6F1F5, 0xA3BAC4, 0x66808C, 0x3CC8C8));
            list.Add(Def("rose", "Rose", "Soft pink on plum", true, "mica", 0.12f,
                0x1D1519, 0x181115, 0x2A1F25, 0x33262D, 0x3E2E37, 0xF7ECF1, 0xC7AFBA, 0x8D7380, 0xFF7AA8));
            list.Add(Def("graphite", "Graphite", "Monochrome, no color", true, "none", 0f,
                0x1A1A1A, 0x141414, 0x262626, 0x2E2E2E, 0x3A3A3A, 0xF0F0F0, 0xABABAB, 0x707070, 0xE6E6E6));
            list.Add(Def("paper", "Paper", "Warm light, easy on the eyes", false, "none", 0f,
                0xF4EFE6, 0xEAE3D6, 0xFBF8F2, 0xF6F1E7, 0xE5DCCB, 0x2B241B, 0x6B5F50, 0x9A8E7E, 0xB5651D));
            list.Add(Def("mist", "Mist", "Cool light gray-blue", false, "mica", 0.15f,
                0xEEF2F6, 0xE3E9EF, 0xFAFCFE, 0xF3F7FA, 0xDCE4EC, 0x17202A, 0x56636F, 0x8A97A3, 0x2F7DD1));
            return list;
        }

        public static IEnumerable<ThemeDef> AllThemes => Builtins.Concat(CustomThemes);

        public static ThemeDef Find(string id) =>
            AllThemes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Builtins[0];

        // ---------------------------------------------------------------- applying

        public static void Initialize(ResourceDictionary resources, string themeId, string accent)
        {
            _resources = resources;
            LoadCustomThemes();
            if (!_listening)
            {
                _listening = true;
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color &&
                        e.Category != UserPreferenceCategory.VisualStyle) return;
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(_themeId, _accent)));
                };
            }
            Apply(themeId, accent);
        }

        public static void Apply(string themeId, string accent)
        {
            _themeId = themeId;
            _accent = accent;
            var def = Find(themeId);
            Current = def;
            if (def.FollowWindows) def = Builtins[SystemUsesLightTheme() ? 1 : 0];
            Resolved = def;

            bool dark = def.Dark;
            IsDark = dark;
            bool backdrop = def.Backdrop != "none" && BackdropSupported;
            UsesBackdrop = backdrop;

            Color acc = ToColor(ResolveAccent(def, accent));
            if (dark && Luma(acc) < 0.30) acc = Mix(acc, Colors.White, 0.35);
            if (!dark && Luma(acc) > 0.55) acc = Mix(acc, Colors.Black, 0.30);
            AccentColor = acc;
            Color accHover = dark ? Mix(acc, Colors.White, 0.12) : Mix(acc, Colors.Black, 0.10);
            Color onAccent = Luma(acc) > 0.55 ? Color.FromRgb(0x10, 0x10, 0x14) : Colors.White;

            float tr = backdrop ? def.Transparency : 0f;
            Color bg = WithAlpha(ToColor(def.Bg), 1 - tr);
            Color sidebar = WithAlpha(ToColor(def.Sidebar), backdrop ? Math.Max(0.25f, 1 - tr * 2.2f) : 1f);
            Color surface, hover, strong;
            if (def.SurfaceOpacity < 1f)
            {
                float o = def.SurfaceOpacity;
                Color Layer(uint c, float k) => backdrop
                    ? WithAlpha(ToColor(c), Math.Min(1f, o * k))
                    : Mix(ToColor(def.Bg), ToColor(c), Math.Min(1f, o * k));
                surface = Layer(def.Surface, 1f);
                hover = Layer(def.SurfaceHover, 1.6f);
                strong = Layer(def.SurfaceStrong, 2.3f);
            }
            else
            {
                surface = ToColor(def.Surface);
                hover = ToColor(def.SurfaceHover);
                strong = ToColor(def.SurfaceStrong);
            }
            Color text = ToColor(def.Text), subtext = ToColor(def.Subtext), faint = ToColor(def.Faint);
            Color divider = dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0);
            Color dividerStrong = dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0);

            var r = _resources;
            if (r != null)
            {
                r["WindowBrush"] = Brush(bg);
                r["SolidWindowBrush"] = Brush(ToColor(def.Bg));
                r["SidebarBrush"] = Brush(sidebar);
                r["SurfaceBrush"] = Brush(surface);
                r["Surface2Brush"] = Brush(hover);
                r["HoverBrush"] = Brush(dark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0));
                r["StrongBrush"] = Brush(strong);
                r["BorderBrush"] = Brush(divider);
                r["BorderStrongBrush"] = Brush(dividerStrong);
                r["TextBrush"] = Brush(text);
                r["SubtextBrush"] = Brush(subtext);
                r["FaintBrush"] = Brush(faint);
                r["AccentBrush"] = Brush(acc);
                r["AccentHoverBrush"] = Brush(accHover);
                r["OnAccentBrush"] = Brush(onAccent);
                r["AccentSoftBrush"] = Brush(WithAlpha(acc, 0.16f));
                r["TrackBrush"] = Brush(strong);
                r["ScrollThumbBrush"] = Brush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0, 0, 0));
                r["GoodBrush"] = Brush(dark ? Rgb(0x5BD18B) : Rgb(0x1F9D57));
                r["WarnBrush"] = Brush(dark ? Rgb(0xF2B84B) : Rgb(0xB7791F));
                r["DangerBrush"] = Brush(dark ? Rgb(0xFF6B6B) : Rgb(0xD64545));
                r["InfoBrush"] = Brush(def.Accent != 0 && accent == "theme" ? acc : dark ? Rgb(0x6CB4FF) : Rgb(0x2F6FDB));
                r["AccentColor"] = acc;
            }

            foreach (var w in LiveWindows()) ApplyBackdrop(w);
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Gives a window the theme's backdrop (Mica/Acrylic) and title bar mode. Call once after creation.</summary>
        public static void Register(Window window)
        {
            Windows.Add(new WeakReference<Window>(window));
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyBackdrop(window);
            else window.SourceInitialized += (_, _) => ApplyBackdrop(window);
        }

        private static IEnumerable<Window> LiveWindows()
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in Windows)
                if (w.TryGetTarget(out var win)) yield return win;
        }

        public static bool BackdropSupported => Native.WindowsBuild >= 22621;

        private static void ApplyBackdrop(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = IsDark ? 1 : 0;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                var source = HwndSource.FromHwnd(hwnd);
                if (BackdropSupported)
                {
                    int type = UsesBackdrop ? (Resolved.Backdrop == "acrylic" ? 3 : 2) : 1;
                    Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
                }
                if (source?.CompositionTarget != null)
                    source.CompositionTarget.BackgroundColor = UsesBackdrop ? Colors.Transparent : ToColor(Resolved.Bg);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Window backdrop", ex);
            }
        }

        // ---------------------------------------------------------------- accent

        public static uint ResolveAccent(ThemeDef def, string accent)
        {
            if (accent.Length == 7 && accent[0] == '#' && TryParseHex(accent, out uint c)) return c;
            if (accent == "theme" && def.Accent != 0) return def.Accent & 0xFFFFFF;
            return SystemAccent();
        }

        public static uint SystemAccent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("AccentColor") is int abgr)
                {
                    uint v = (uint)abgr;
                    uint r = v & 0xFF, g = (v >> 8) & 0xFF, b = (v >> 16) & 0xFF;
                    return (r << 16) | (g << 8) | b;
                }
            }
            catch
            {
            }
            return 0x4F8CFF;
        }

        public static bool SystemUsesLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int value) return value == 1;
            }
            catch
            {
            }
            return false;
        }

        // ---------------------------------------------------------------- theme files

        /// <summary>Reads custom themes from Caption's themes folder and Hangar's shared one.</summary>
        public static void LoadCustomThemes()
        {
            var list = new List<ThemeDef>();
            foreach (var dir in new[] { Paths.ThemesDir, Paths.HangarThemesDir })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.GetFiles(dir, "*.json"))
                    {
                        if (TryParseTheme(File.ReadAllText(file), out var t, out string err))
                        {
                            t.File = file;
                            if (!list.Any(o => o.Id == t.Id)) list.Add(t);
                        }
                        else
                        {
                            Log.Info($"Theme {Path.GetFileName(file)}: {err}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("Loading themes", ex);
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            CustomThemes = list;
        }

        public static bool TryParseTheme(string text, out ThemeDef t, out string error)
        {
            var th = new ThemeDef();
            t = th;
            error = "";
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            }
            catch
            {
                error = "not valid JSON";
                return false;
            }
            if (root is not JsonObject o) { error = "not valid JSON"; return false; }
            string Str(JsonObject obj, string key, string def = "") =>
                obj[key] is JsonValue v && v.TryGetValue(out string? s) && s != null ? s : def;
            double Num(string key, double def) =>
                o[key] is JsonValue v && v.TryGetValue(out double d) ? d : def;

            t.Name = Str(o, "name").Trim();
            if (t.Name.Length == 0) { error = "missing \"name\""; return false; }
            t.Id = "custom:" + Str(o, "id", t.Name).ToLowerInvariant();
            t.Description = Str(o, "description", "Your theme");
            t.Dark = !string.Equals(Str(o, "mode", "dark"), "light", StringComparison.OrdinalIgnoreCase);
            t.Backdrop = Str(o, "backdrop", "none").ToLowerInvariant();
            if (t.Backdrop is not ("mica" or "acrylic")) t.Backdrop = "none";
            t.Transparency = (float)Math.Clamp(Num("transparency", 0.15), 0, 0.9);
            t.SurfaceOpacity = (float)Math.Clamp(Num("surfaceOpacity", 1.0), 0.02, 1.0);
            var b = Builtins[t.Dark ? 0 : 1];
            t.Bg = b.Bg; t.Sidebar = b.Sidebar; t.Surface = b.Surface; t.SurfaceHover = b.SurfaceHover;
            t.SurfaceStrong = b.SurfaceStrong; t.Text = b.Text; t.Subtext = b.Subtext; t.Faint = b.Faint;
            if (o["colors"] is JsonObject c)
            {
                var map = new (string key, Action<uint> set)[]
                {
                    ("background", v => th.Bg = v), ("sidebar", v => th.Sidebar = v), ("surface", v => th.Surface = v),
                    ("surfaceHover", v => th.SurfaceHover = v), ("surfaceStrong", v => th.SurfaceStrong = v),
                    ("text", v => th.Text = v), ("subtext", v => th.Subtext = v), ("faint", v => th.Faint = v),
                };
                foreach (var (key, set) in map)
                {
                    string s = Str(c, key);
                    if (s.Length == 0) continue;
                    if (!TryParseHex(s, out uint v)) { error = "bad color for " + key; return false; }
                    set(v);
                }
                string a = Str(c, "accent", "windows").ToLowerInvariant();
                if (a != "windows")
                {
                    if (!TryParseHex(a, out uint av)) { error = "bad accent color"; return false; }
                    t.Accent = av == 0 ? 0x010101 : av; // pure black is a real choice
                }
            }
            t.Custom = true;
            return true;
        }

        /// <summary>Writes the current theme as a theme file (Hangar format) and returns its path.</summary>
        public static string SaveCurrentAsFile()
        {
            var def = Find(_themeId);
            if (def.FollowWindows) def = Builtins[SystemUsesLightTheme() ? 1 : 0];
            uint accent = ResolveAccent(def, _accent);
            bool windowsAccent = _accent == "windows" || (_accent == "theme" && def.Accent == 0);
            var colors = new JsonObject
            {
                ["background"] = Hex(def.Bg), ["sidebar"] = Hex(def.Sidebar), ["surface"] = Hex(def.Surface),
                ["surfaceHover"] = Hex(def.SurfaceHover), ["surfaceStrong"] = Hex(def.SurfaceStrong),
                ["text"] = Hex(def.Text), ["subtext"] = Hex(def.Subtext), ["faint"] = Hex(def.Faint),
                ["accent"] = windowsAccent ? "windows" : Hex(accent),
            };
            string name = def.Custom ? def.Name : def.Name + " (copy)";
            var root = new JsonObject
            {
                ["schema"] = 1,
                ["name"] = name,
                ["description"] = def.Description,
                ["mode"] = def.Dark ? "dark" : "light",
                ["backdrop"] = def.Backdrop,
                ["transparency"] = Math.Round(def.Transparency, 2),
            };
            if (def.SurfaceOpacity < 1f) root["surfaceOpacity"] = Math.Round(def.SurfaceOpacity, 2);
            root["colors"] = colors;
            Directory.CreateDirectory(Paths.ThemesDir);
            string safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string path = Path.Combine(Paths.ThemesDir, safe + ".json");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(Paths.ThemesDir, $"{safe} {i}.json");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        // ---------------------------------------------------------------- color helpers

        public static bool TryParseHex(string s0, out uint value)
        {
            value = 0;
            string s = s0.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length != 6) return false;
            return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        public static string Hex(uint c) => "#" + (c & 0xFFFFFF).ToString("X6");

        public static Color Rgb(uint c) => Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        private static Color ToColor(uint c) => Rgb(c);

        public static Color WithAlpha(Color c, float a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

        public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }
    }
}
