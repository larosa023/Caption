using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Caption.Services;
using Caption.Whisper;

namespace Caption
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // The speech worker and the CI self-test never touch the UI.
            if (Has(args, "--worker")) return WorkerHost.Run();
            if (Has(args, "--selftest")) return SelfTest.Run(args);
            if (Has(args, "--subeval")) return SubEval.Run(args);
            int ui = Array.FindIndex(args, a => a == "--uitest");
            if (ui >= 0 && ui + 1 < args.Length) return UiTest.Run(args[ui + 1]);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Log.Error("Fatal unhandled exception", ex);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            if (Has(args, "--quit"))
            {
                Signal(App.QuitSignalName);
                return 0;
            }

            if (Has(args, "--uninstall"))
                return Uninstall(quiet: Has(args, "--quiet"));

            int afterUpdatePid = 0;
            string? previousVersion = null;
            int idx = Array.FindIndex(args, a => string.Equals(a, "--after-update", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < args.Length)
            {
                int.TryParse(args[idx + 1], out afterUpdatePid);
                if (idx + 2 < args.Length) previousVersion = args[idx + 2];
            }

            Mutex? mutex = null;
            if (afterUpdatePid == 0)
            {
                // First launch from Downloads: install per-user and hand over to the installed copy.
                var passThrough = args.Where(a => !string.Equals(a, "--installed", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (!Has(args, "--installed") && Installer.InstallAndRelaunchIfNeeded(passThrough)) return 0;

                mutex = new Mutex(true, App.MutexName, out bool first);
                if (!first)
                {
                    // Already running: ask it to show itself.
                    Native.AllowSetForegroundWindow(-1);
                    Signal(App.ShowSignalName);
                    mutex.Dispose();
                    return 0;
                }
            }

            var app = new App(startHidden: Has(args, "--tray"), afterUpdatePid, previousVersion, mutex);
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        private static bool Has(string[] args, string flag) =>
            args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        private static void Signal(string name)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(name);
                ev.Set();
            }
            catch
            {
                // Not running.
            }
        }

        private static int Uninstall(bool quiet)
        {
            bool removeData = false;
            if (!quiet)
            {
                if (MessageBox.Show("Uninstall Caption?", "Caption", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return 1;
                removeData = MessageBox.Show(
                    "Also delete downloaded models, settings and transcripts?\n\n" + Paths.DataDir,
                    "Caption", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            }
            // Close a running copy first.
            Signal(App.QuitSignalName);
            try
            {
                using var m = new Mutex(false, App.MutexName);
                if (m.WaitOne(8000)) m.ReleaseMutex();
            }
            catch (AbandonedMutexException) { }
            catch { }
            Installer.Uninstall(removeData);
            if (!quiet) MessageBox.Show("Caption was removed.", "Caption", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
    }
}
