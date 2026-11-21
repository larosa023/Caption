using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Caption.Captioning;
using Caption.Services;
using Microsoft.Win32;

namespace Caption.Views
{
    public partial class TranscriptWindow : Window
    {
        private readonly CaptionController _controller;
        private readonly AppSettings _settings;
        private readonly ICollectionView _view;
        private ScrollViewer? _scroller;
        private bool _follow = true;

        public TranscriptWindow(CaptionController controller, AppSettings settings)
        {
            InitializeComponent();
            _controller = controller;
            _settings = settings;
            ThemeManager.Register(this);

            _view = CollectionViewSource.GetDefaultView(controller.Transcript.Lines);
            List.ItemsSource = _view;
            controller.Transcript.Lines.CollectionChanged += OnLinesChanged;
            controller.PartialChanged += OnPartial;
            UpdateCounts();

            if (settings.TranscriptWidth is double w && settings.TranscriptHeight is double h)
            {
                Width = Math.Max(MinWidth, w);
                Height = Math.Max(MinHeight, h);
            }
            if (settings.TranscriptLeft is double l && settings.TranscriptTop is double t)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
            }

            Loaded += (_, _) =>
            {
                _scroller = FindScroller(List);
                if (_scroller != null)
                    _scroller.ScrollChanged += (_, e) =>
                    {
                        // Follow new lines only while scrolled to the bottom.
                        if (e.ExtentHeightChange == 0) _follow = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 4;
                    };
                ScrollToEnd();
                OnPartial(controller.Partial);
            };
            Closed += (_, _) =>
            {
                controller.Transcript.Lines.CollectionChanged -= OnLinesChanged;
                controller.PartialChanged -= OnPartial;
            };
            Closing += (_, _) =>
            {
                if (WindowState == WindowState.Normal)
                {
                    _settings.TranscriptLeft = Left;
                    _settings.TranscriptTop = Top;
                    _settings.TranscriptWidth = Width;
                    _settings.TranscriptHeight = Height;
                    SettingsStore.Save(_settings);
                }
            };
            PreviewKeyDown += OnKey;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && !SearchBox.IsKeyboardFocusWithin) { Copy_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; }
        }

        private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateCounts();
            if (_follow && string.IsNullOrEmpty(SearchBox.Text)) ScrollToEnd();
        }

        private void OnPartial(string text)
        {
            PartialText.Text = text;
            PartialBox.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateCounts()
        {
            int n = _controller.Transcript.Lines.Count;
            CountText.Text = n == 0 ? "" : n == 1 ? "1 line" : $"{n:N0} lines";
            EmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ScrollToEnd()
        {
            if (List.Items.Count > 0) List.ScrollIntoView(List.Items[^1]);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = SearchBox.Text.Trim();
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Filter = q.Length == 0
                ? null
                : o => o is CaptionLine l && (l.Display.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                              (l.Secondary?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false));
            if (q.Length == 0) ScrollToEnd();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var lines = List.SelectedItems.Count > 0
                ? List.SelectedItems.Cast<CaptionLine>().OrderBy(l => l.Number)
                : _view.Cast<CaptionLine>();
            string text = Transcript.ToText(lines);
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); } catch (Exception ex) { Log.WarnOnce("Clipboard", ex); }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_controller.Transcript.Lines.Count == 0) return;
            var dlg = new SaveFileDialog
            {
                Title = "Save transcript",
                FileName = "Transcript " + _controller.Transcript.StartedAt.ToString("yyyy-MM-dd HHmm"),
                Filter = "Text file (*.txt)|*.txt|Subtitles (*.srt)|*.srt",
                DefaultExt = ".txt",
                AddExtension = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                string path = dlg.FileName;
                if (dlg.FilterIndex == 2 && !path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) path = Path.ChangeExtension(path, ".srt");
                _controller.Transcript.Save(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save: " + ex.Message, "Caption", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_controller.Transcript.Lines.Count == 0) return;
            if (MessageBox.Show(this, "Clear the transcript? Save it first if you want to keep it.", "Caption",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                _controller.Transcript.Clear();
        }

        private static ScrollViewer? FindScroller(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var found = FindScroller(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                if (found != null) return found;
            }
            return null;
        }
    }
}
