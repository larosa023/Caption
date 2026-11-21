using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace Caption.Services
{
    /// <summary>
    /// Per-user install, the same way Hangar installs portable suite apps: the exe goes in
    /// %LOCALAPPDATA%\Programs\Caption, plus a Start menu shortcut and an Apps &amp; features entry.
    /// No admin rights, no services.
    /// </summary>
    public static class Installer
    {
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Caption";

        public static string ShortcutPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Caption.lnk");

        public static bool IsRunningFromInstallDir =>
            string.Equals(Path.GetFullPath(Paths.ExeDir).TrimEnd('\\'), Path.GetFullPath(Paths.InstallDir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Run from somewhere else (Downloads): copy to the install folder, register, and start that copy.
        /// Returns true if the caller should exit because the installed copy was started.
        /// </summary>
        public static bool InstallAndRelaunchIfNeeded(string[] args)
        {
            if (Paths.IsPortable || IsRunningFromInstallDir) return false;
            try
            {
                Directory.CreateDirectory(Paths.InstallDir);
                string target = Paths.InstalledExe;
                // Another copy may already be installed and running; if so, just hand over to it.
                if (File.Exists(target))
                {
                    var existing = VersionOf(target);
                    var mine = VersionOf(Paths.ExePath);
                    if (UpdateService.CompareVersions(mine, existing) > 0)
                    {
                        try { File.Copy(Paths.ExePath, target, true); }
                        catch (IOException)
                        {
                            // Running: let it update itself instead.
                        }
                    }
                }
                else
                {
                    File.Copy(Paths.ExePath, target, true);
                }
                Register(target);
                var psi = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Paths.InstallDir };
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--installed");
                Process.Start(psi);
                Log.Info($"Installed to {target}");
                return true;
            }
            catch (Exception ex)
            {
                // Can't install (locked down PC?): just run from where we are.
                Log.Error("Self-install", ex);
                return false;
            }
        }

        public static string VersionOf(string exe)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                return string.IsNullOrEmpty(info.ProductVersion) ? "0.0.0" : info.ProductVersion.Split('+')[0];
            }
            catch
            {
                return "0.0.0";
            }
        }

        /// <summary>Start menu shortcut + Apps &amp; features entry (kept current on every start).</summary>
        public static void Register(string exe)
        {
            try
            {
                CreateShortcut(ShortcutPath, exe, "Live captions and translation, 100% local");
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Start menu shortcut", ex);
            }
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true);
                if (key == null) return;
                key.SetValue("DisplayName", "Caption");
                key.SetValue("DisplayVersion", UpdateService.AppVersion);
                key.SetValue("Publisher", "larosa023");
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", Path.GetDirectoryName(exe) ?? "");
                key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
                key.SetValue("URLInfoAbout", "https://github.com/larosa023/Caption");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long kb = new FileInfo(exe).Length / 1024;
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Apps & features entry", ex);
            }
        }

        /// <summary>Removes shortcut, registry entries, startup entry; optionally the data folder; then deletes the exe.</summary>
        public static void Uninstall(bool removeData)
        {
            StartupManager.Apply(false);
            try { File.Delete(ShortcutPath); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (removeData && !Paths.IsPortable)
            {
                try { Directory.Delete(Paths.DataDir, true); } catch (Exception ex) { Log.Error("Removing data", ex); }
            }
            if (IsRunningFromInstallDir)
            {
                // A running exe can't delete itself: let cmd do it a moment after we exit.
                string dir = Paths.InstallDir;
                var psi = new ProcessStartInfo("cmd.exe",
                    $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                try { Process.Start(psi); } catch { }
            }
        }

        // ---------------------------------------------------------------- shell link

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, int flags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        private static void CreateShortcut(string lnk, string target, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
                ((IPersistFile)link).Save(lnk, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }
}
