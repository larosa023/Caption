using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Caption.Captioning;
using Caption.Services;
using Caption.ViewModels;
using Caption.Views;

namespace Caption
{
    /// <summary>
    /// "Caption.exe --uitest &lt;out.txt&gt;": CI smoke test for the UI. Opens every window and page in every
    /// theme, exercises the caption bar, and reports XAML or binding errors.
    /// </summary>
    internal static class UiTest
    {
        private sealed class BindingErrors : TraceListener
        {
            public readonly StringBuilder Text = new();
            public int Count;
            public override void Write(string? message) { }
            public override void WriteLine(string? message)
            {
                if (message == null) return;
                Count++;
                if (Count <= 40) Text.AppendLine("binding: " + message);
            }
        }

        public static int Run(string outFile)
        {
            var report = new StringBuilder();
            int failures = 0;
            void Line(string s) { report.AppendLine(s); File.WriteAllText(outFile, report.ToString()); }

            var bindings = new BindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) => { failures++; Line("UNHANDLED: " + e.Exception); e.Handled = true; };
            app.Startup += async (_, _) =>
            {
                try
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Caption;component/Views/Styles.xaml", UriKind.Absolute)
                    });
                    var settings = new AppSettings();
                    settings.Normalize();
                    ThemeManager.Initialize(app.Resources, settings.Theme, settings.Accent);
                    var updater = new UpdateService(settings);
                    var controller = new CaptionController(settings, app.Dispatcher);
                    var vm = new MainViewModel(settings, controller, updater);
                    var window = new MainWindow(vm);
                    window.Show();
                    await Pump();
                    Line("main window: ok");

                    foreach (var page in new[] { "home", "profiles", "bar", "model", "translation", "appearance", "hotkeys", "general" })
                    {
                        vm.Page = page;
                        await Pump();
                        Line($"page {page}: ok");
                    }
                    vm.Page = "profiles";
                    foreach (var src in new[] { "device", "apps", "system" })
                    {
                        if (vm.SelectedProfile != null) vm.SelectedProfile.Source = src;
                        await Pump();
                    }
                    foreach (var t in ThemeManager.BuiltinThemes)
                    {
                        ThemeManager.Apply(t.Id, "theme");
                        await Pump();
                    }
                    Line($"themes: {ThemeManager.BuiltinThemes.Count} applied");

                    var bar = new CaptionBarWindow(settings);
                    bar.Show();
                    bar.AddLine("And so my fellow Americans, ask not what your country can do for you.", "Original line");
                    bar.SetPartial("ask what you can do for your country");
                    await Pump();
                    foreach (var layout in new[] { "one", "two", "scroll" })
                    {
                        settings.Bar.Layout = layout;
                        foreach (var pos in new[] { "bottom", "window", "free" })
                        {
                            settings.Bar.Position = pos;
                            bar.ApplyStyle();
                            await Pump();
                        }
                    }
                    foreach (var preset in new[] { "classic", "cinema", "minimal", "contrast", "theme" })
                    {
                        vm.ApplyBarPreset(preset);
                        bar.ApplyStyle();
                        await Pump();
                    }
                    bar.SetEditing(true);
                    await Pump();
                    bar.SetEditing(false);
                    Line($"caption bar: ok ({bar.ActualWidth:0}x{bar.ActualHeight:0})");

                    controller.Transcript.Add(new CaptionLine { Number = 1, Wall = DateTime.Now, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = "Hello there", Language = "en" });
                    var transcript = new TranscriptWindow(controller, settings);
                    transcript.Show();
                    await Pump();
                    string srt = Transcript.ToSrt(controller.Transcript.Lines);
                    Line("transcript: ok, srt " + (srt.Contains("00:00:00,000 --> 00:00:02,000") ? "ok" : "BAD"));
                    if (!srt.Contains("-->")) failures++;

                    var prompt = new UpdatePrompt("9.9.9");
                    prompt.Show();
                    await Pump();
                    prompt.Close();
                    Line("update prompt: ok");

                    transcript.Close();
                    bar.Close();
                    window.AllowClose();
                    window.Close();
                }
                catch (Exception ex)
                {
                    failures++;
                    Line("ERROR: " + ex);
                }
                Line($"binding errors: {bindings.Count}");
                if (bindings.Count > 0) Line(bindings.Text.ToString());
                Line(failures == 0 ? "UITEST PASSED" : $"UITEST FAILED ({failures})");
                app.Shutdown(failures == 0 ? 0 : 1);
            };
            return app.Run();
        }

        private static async Task Pump()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
}
