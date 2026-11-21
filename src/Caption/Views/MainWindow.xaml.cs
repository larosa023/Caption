using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Caption.Services;
using Caption.ViewModels;

namespace Caption.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private bool _allowClose;
        private string _lastSettingsPage = "profiles";

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            ThemeManager.Register(this);

            var s = vm.Settings;
            if (s.MainWidth is double w && s.MainHeight is double h)
            {
                Width = Math.Max(MinWidth, w);
                Height = Math.Max(MinHeight, h);
            }
            if (s.MainLeft is double l && s.MainTop is double t && IsOnScreen(l, t))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
            }

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Page))
                {
                    if (vm.Page != "home") _lastSettingsPage = vm.Page;
                    PageScroller.ScrollToTop();
                }
            };

            PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemComma || e.Key == Key.Decimal))
                {
                    OpenSettings();
                    e.Handled = true;
                }
            };
        }

        public void OpenSettings()
        {
            _vm.Page = _lastSettingsPage;
            ShowFront();
        }

        public void ShowFront()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Native.SetForegroundWindow(hwnd);
        }

        public void AllowClose() => _allowClose = true;

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveState();
            if (!_allowClose)
            {
                // Closing the window keeps Caption in the tray.
                e.Cancel = true;
                Hide();
                Native.TrimWorkingSet();
            }
            base.OnClosing(e);
        }

        public void SaveState()
        {
            if (WindowState != WindowState.Normal || !IsLoaded) return;
            var s = _vm.Settings;
            s.MainLeft = Left;
            s.MainTop = Top;
            s.MainWidth = Width;
            s.MainHeight = Height;
            SettingsStore.Save(s);
        }

        private static bool IsOnScreen(double left, double top)
        {
            var pt = new Native.POINT { X = (int)left + 40, Y = (int)top + 20 };
            return Native.MonitorFromPoint(pt, 0) != IntPtr.Zero;
        }
    }
}
