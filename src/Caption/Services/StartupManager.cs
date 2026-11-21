using Microsoft.Win32;

namespace Caption.Services
{
    /// <summary>Adds/removes Caption from "start with Windows" (per-user Run key, no admin, no service).</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Caption";

        public static void Apply(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                    key?.SetValue(ValueName, $"\"{Paths.ExePath}\" --tray");
                }
                else
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                    key?.DeleteValue(ValueName, false);
                }
            }
            catch
            {
                // Registry access can fail under unusual policies; not fatal.
            }
        }
    }
}
