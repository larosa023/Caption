using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Caption.Services;

namespace Caption.Views
{
    /// <summary>
    /// The floating caption bar: always on top, click-through, one or two lines (newest words win) or a
    /// scrolling box. "Move &amp; resize" makes it grabbable until Done is pressed.
    /// </summary>
    public sealed class CaptionBarWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly Border _root;
        private readonly Border _editFrame;
        private readonly Grid _content;
        private readonly TextBlock _original;
        private readonly TextBlock _main;
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _scrollLines;
        private readonly TextBlock _scrollPartial;
        private readonly Caption.Controls.OutlinedText _subtitle = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Caption.Controls.OutlinedText _subOriginal = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
        private readonly StackPanel _subtitlePanel = new();
        private DateTime _lastLineAt = DateTime.MinValue;
        private readonly Border _toolbar;
        private readonly DispatcherTimer _followTimer;
        private readonly DispatcherTimer _idleTimer;
        private readonly List<(string Text, string? Secondary)> _lines = new();
        private string _partial = "";
        private string? _notice;
        private bool _editing;
        private bool _visibleText;
        private IntPtr _hwnd;

        public event EventHandler? EditFinished;
        public event EventHandler? SettingsChanged;

        public CaptionBarWindow(AppSettings settings)
        {
            _settings = settings;
            Title = "Caption bar";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;

            _original = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72, Margin = new Thickness(0, 0, 0, 2), Visibility = Visibility.Collapsed };
            _main = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var stack = new StackPanel();
            stack.Children.Add(_original);
            stack.Children.Add(_main);

            _scrollLines = new StackPanel();
            _scrollPartial = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var scrollStack = new StackPanel();
            scrollStack.Children.Add(_scrollLines);
            scrollStack.Children.Add(_scrollPartial);
            _scroll = new ScrollViewer
            {
                Content = scrollStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Visibility = Visibility.Collapsed,
            };

            _content = new Grid();
            _content.Children.Add(stack);
            _content.Children.Add(_scroll);
            _subtitlePanel.Children.Add(_subOriginal);
            _subtitlePanel.Children.Add(_subtitle);
            _content.Children.Add(_subtitlePanel);

            _root = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 10, 20, 12), Child = _content };

            _editFrame = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _editFrame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

            _toolbar = BuildToolbar();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new Grid();
            bar.Children.Add(_root);
            bar.Children.Add(_editFrame);
            bar.Children.Add(MakeThumb(HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE, left: true));
            bar.Children.Add(MakeThumb(HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE, left: false));
            bar.Children.Add(MakeThumb(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS, left: false, vertical: true));
            Grid.SetRow(bar, 0);
            Grid.SetRow(_toolbar, 1);
            grid.Children.Add(bar);
            grid.Children.Add(_toolbar);
            Content = grid;

            MouseLeftButtonDown += (_, e) =>
            {
                if (!_editing || e.OriginalSource is Thumb || IsInToolbar(e.OriginalSource as DependencyObject)) return;
                try { DragMove(); } catch { }
                _settings.Bar.Position = "free";
                _settings.Bar.Left = Left;
                _settings.Bar.Top = Top;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
                UpdateToolbar();
            };

            _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _followTimer.Tick += (_, _) => Reposition();
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); if (!_editing && _partial.Length == 0) FadeText(false); };

            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED, true);
                ApplyClickThrough();
            };
            SizeChanged += (_, _) => { if (!_editing || _settings.Bar.Position != "free") Reposition(); };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;

            ApplyStyle();
            _root.Opacity = 0;
        }

        private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Reposition);

        public bool IsEditing => _editing;

        // ---------------------------------------------------------------- content

        public void SetLines(IEnumerable<(string Text, string? Secondary)> lines)
        {
            _lines.Clear();
            _lines.AddRange(lines);
            Render();
        }

        public void AddLine(string text, string? secondary)
        {
            _lines.Add((text, secondary));
            while (_lines.Count > 200) _lines.RemoveAt(0);
            _partial = _stable = _tentative = "";
            _lastLineAt = DateTime.Now;
            Render();
        }

        /// <summary>Replaces the newest line (an Ollama translation arrived).</summary>
        public void UpdateLastLine(string original, string text, string? secondary)
        {
            for (int i = _lines.Count - 1; i >= Math.Max(0, _lines.Count - 6); i--)
            {
                if (_lines[i].Text == original || _lines[i].Secondary == original)
                {
                    _lines[i] = (text, secondary);
                    Render();
                    return;
                }
            }
        }

        /// <summary>The line being spoken: confirmed words (solid) and newer words that may still change (dimmed).</summary>
        public void SetPartial(string stable, string tentative)
        {
            _stable = stable ?? "";
            _tentative = tentative ?? "";
            _partial = (_stable + " " + _tentative).Trim();
            Render();
        }

        private string _stable = "", _tentative = "";

        /// <summary>A short status shown in the bar ("Paused", "Listening to Discord").</summary>
        public void ShowNotice(string? text)
        {
            _notice = text;
            Render();
        }

        public void Clear()
        {
            _lines.Clear();
            _partial = _stable = _tentative = "";
            _notice = null;
            Render();
        }

        private void Render()
        {
            var s = _settings.Bar;
            bool scroll = s.Layout == "scroll";
            Brush dim = MakeTextBrush(s.DimPartial ? 0.62 : 1.0);
            bool hasText = _lines.Count > 0 || _partial.Length > 0 || !string.IsNullOrEmpty(_notice) || _editing;

            if (s.Layout == "subtitle")
            {
                hasText = RenderSubtitle();
            }
            else if (scroll)
            {
                _scrollLines.Children.Clear();
                foreach (var (text, secondary) in _lines.Skip(Math.Max(0, _lines.Count - 60)))
                {
                    if (secondary != null)
                        _scrollLines.Children.Add(new TextBlock { Text = secondary, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = Math.Max(10, s.FontSize * 0.7), TextAlignment = Align });
                    _scrollLines.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), TextAlignment = Align });
                }
                _scrollPartial.Inlines.Clear();
                if (_partial.Length > 0)
                {
                    if (_stable.Length > 0) _scrollPartial.Inlines.Add(new Run(_stable));
                    if (_tentative.Length > 0) _scrollPartial.Inlines.Add(new Run((_stable.Length > 0 ? " " : "") + _tentative) { Foreground = dim });
                }
                else if (_lines.Count == 0)
                {
                    _scrollPartial.Inlines.Add(new Run(_notice ?? (_editing ? "Captions appear here" : "")) { Foreground = MakeTextBrush(0.7) });
                }
                _scrollPartial.TextAlignment = Align;
                _scroll.ScrollToEnd();
            }
            else
            {
                int maxLines = s.Layout == "one" ? 1 : 2;
                string finalText;
                string partial = _tentative;
                if (_partial.Length > 0)
                {
                    // The finished line before stays in front, so text flows on instead of jumping.
                    finalText = ((_lines.Count > 0 ? _lines[^1].Text + " " : "") + _stable).Trim();
                }
                else if (_lines.Count > 0)
                {
                    finalText = maxLines == 2 && _lines.Count > 1 ? _lines[^2].Text + " " + _lines[^1].Text : _lines[^1].Text;
                }
                else
                {
                    finalText = _notice ?? (_editing ? "Captions appear here. Drag to move, pull the edges to resize." : "");
                }
                Fit(ref finalText, ref partial, maxLines);
                _main.Inlines.Clear();
                if (finalText.Length > 0) _main.Inlines.Add(new Run(finalText));
                if (partial.Length > 0)
                {
                    if (finalText.Length > 0) _main.Inlines.Add(new Run(" "));
                    _main.Inlines.Add(new Run(partial) { Foreground = dim });
                }
                _main.TextAlignment = Align;

                string? secondary = _partial.Length == 0 && _lines.Count > 0 ? _lines[^1].Secondary : null;
                _original.Text = secondary ?? "";
                _original.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
                _original.TextAlignment = Align;
            }

            if (hasText)
            {
                FadeText(true);
                _idleTimer.Stop();
                if (_partial.Length == 0 && !_editing)
                {
                    // Like real subtitles: a finished line stays up for about its reading time, then clears.
                    int chars = _lines.Count > 0 ? _lines[^1].Text.Length : 20;
                    double hold = Math.Clamp(chars / 14.0, 1.8, 5.0);
                    double left = hold - (DateTime.Now - _lastLineAt).TotalSeconds;
                    _idleTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.3, s.Layout == "subtitle" ? left : left + 2.5));
                    _idleTimer.Start();
                }
            }
            else
            {
                FadeText(false);
            }
        }

        private TextAlignment Align => _settings.Bar.Align == "left" ? TextAlignment.Left : TextAlignment.Center;

        /// <summary>Film-style: the current sentence as one or two balanced lines; returns whether anything shows.</summary>
        private bool RenderSubtitle()
        {
            var s = _settings.Bar;
            string stable = _stable, tentative = s.ShowTentative ? _tentative : "";
            string text;
            int stableWords;
            if ((stable + tentative).Trim().Length > 0)
            {
                text = (stable + " " + tentative).Trim();
                stableWords = Words(stable).Length;
            }
            else
            {
                bool fresh = _lines.Count > 0 && (DateTime.Now - _lastLineAt).TotalSeconds < Math.Clamp(_lines[^1].Text.Length / 14.0, 1.8, 5.0);
                text = fresh ? _lines[^1].Text : _notice ?? (_editing ? "Subtitles appear here. Drag to move, pull the edges to resize." : "");
                stableWords = int.MaxValue;
            }
            if (text.Length == 0)
            {
                _subtitle.SetText("", -1);
                _subOriginal.SetText("", -1);
                return false;
            }

            // Characters per line: the setting, or fewer if the bar is narrow.
            double avgChar = s.FontSize * 0.52;
            int maxChars = Math.Max(12, Math.Min(s.MaxCharsPerLine, (int)((s.Width - 40) / avgChar)));
            var (formatted, dimFrom) = FormatSubtitle(text, stableWords, maxChars);
            _subtitle.SetText(formatted, dimFrom);

            string? secondary = _partial.Length == 0 && _lines.Count > 0 && text == _lines[^1].Text ? _lines[^1].Secondary : null;
            _subOriginal.SetText(secondary == null ? "" : FormatSubtitle(secondary, int.MaxValue, (int)(maxChars * 1.4)).Text, -1);
            return true;
        }

        private static string[] Words(string t) => t.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// Breaks text into subtitle "cards" of at most two lines of maxChars, keeps the newest card, and balances
        /// its two lines (preferring a break after punctuation). Returns the text (lines joined by a newline) and where dimming starts.
        /// </summary>
        public static (string Text, int DimFrom) FormatSubtitle(string text, int stableWords, int maxChars)
        {
            var words = Words(text);
            if (words.Length == 0) return ("", -1);
            // Greedy lines, then cards of two lines.
            var lines = new List<(int From, int To)>();
            int start = 0, len = 0;
            for (int i = 0; i < words.Length; i++)
            {
                int add = (len == 0 ? 0 : 1) + words[i].Length;
                if (len > 0 && len + add > maxChars) { lines.Add((start, i)); start = i; len = words[i].Length; }
                else len += add;
            }
            lines.Add((start, words.Length));
            int cardStartLine = (lines.Count - 1) / 2 * 2;
            int from = lines[cardStartLine].From, to = words.Length;

            // Best single break inside the card.
            int total = Enumerable.Range(from, to - from).Sum(i => words[i].Length) + (to - from - 1);
            string joined;
            if (total <= maxChars)
            {
                joined = string.Join(' ', words, from, to - from);
            }
            else
            {
                int best = -1;
                double bestScore = double.MaxValue;
                int left = 0;
                for (int b = from + 1; b < to; b++)
                {
                    left += words[b - 1].Length + (b - 1 > from ? 1 : 0);
                    int right = total - left - 1;
                    if (left > maxChars || right > maxChars) continue;
                    char last = words[b - 1][^1];
                    double score = Math.Abs(left - right) - (last is ',' or '.' or '?' or '!' or ';' or ':' ? 8 : 0);
                    if (score < bestScore) { bestScore = score; best = b; }
                }
                if (best < 0) best = lines.Count > cardStartLine + 1 ? lines[cardStartLine + 1].From : to;
                joined = string.Join(' ', words, from, best - from) + (best < to ? "\n" + string.Join(' ', words, best, to - best) : "");
            }

            int dimFrom = -1;
            if (stableWords < words.Length)
            {
                int firstDim = Math.Max(stableWords, from);
                // Character offset of word firstDim in the joined text.
                int count = 0, pos = 0;
                for (int i = 0; i < joined.Length && count < firstDim - from; i++)
                {
                    if (joined[i] == ' ' || joined[i] == '\n') { count++; pos = i + 1; }
                }
                dimFrom = firstDim == from ? 0 : pos;
            }
            return (joined, dimFrom);
        }

        /// <summary>Drops words from the front until the text fits in maxLines (the newest words always show).</summary>
        private void Fit(ref string finalText, ref string partial, int maxLines)
        {
            double width = Math.Max(50, _settings.Bar.Width - _root.Padding.Left - _root.Padding.Right - 4);
            var s = _settings.Bar;
            var typeface = new Typeface(new FontFamily(s.FontFamily), FontStyles.Normal, s.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }

            double LineCount(string t)
            {
                var ft = new FormattedText(t.Length == 0 ? " " : t, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, s.FontSize, Brushes.White, dpi)
                { MaxTextWidth = width };
                var one = new FormattedText("Ag", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, s.FontSize, Brushes.White, dpi);
                return ft.Height / Math.Max(1, one.Height);
            }

            var fw = finalText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var pw = partial.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            // Start from a reasonable tail so this stays cheap.
            while (fw.Count + pw.Count > 80) { if (fw.Count > 0) fw.RemoveAt(0); else pw.RemoveAt(0); }
            while (fw.Count + pw.Count > 1 && LineCount(string.Join(' ', fw.Concat(pw))) > maxLines + 0.25)
            {
                if (fw.Count > 0) fw.RemoveAt(0); else pw.RemoveAt(0);
            }
            finalText = string.Join(' ', fw);
            partial = string.Join(' ', pw);
        }

        private void FadeText(bool show)
        {
            if (show == _visibleText && _root.Opacity == (show ? 1 : 0)) return;
            _visibleText = show;
            var anim = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 600));
            _root.BeginAnimation(OpacityProperty, anim);
        }

        // ---------------------------------------------------------------- style + position

        private Brush MakeTextBrush(double opacity)
        {
            var c = ThemeManager.ParseColor(StyleText, Colors.White);
            return ThemeManager.Brush(ThemeManager.WithAlpha(c, (float)opacity));
        }

        private string StyleText => _settings.Bar.Preset == "theme"
            ? (ThemeManager.IsDark ? "#F2F2F5" : "#16161A")
            : _settings.Bar.TextColor;

        public void ApplyStyle()
        {
            var s = _settings.Bar;
            var font = new FontFamily(s.FontFamily + ", Segoe UI");
            var weight = s.Bold ? FontWeights.SemiBold : FontWeights.Normal;
            var textBrush = MakeTextBrush(1);
            foreach (var tb in new[] { _main, _scrollPartial })
            {
                tb.FontFamily = font;
                tb.FontSize = s.FontSize;
                tb.FontWeight = weight;
                tb.Foreground = textBrush;
                tb.LineHeight = s.FontSize * 1.28;
                tb.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            }
            _scrollLines.SetValue(TextBlock.FontFamilyProperty, font);
            _scrollLines.SetValue(TextBlock.FontSizeProperty, s.FontSize);
            _scrollLines.SetValue(TextBlock.FontWeightProperty, weight);
            _scrollLines.SetValue(TextBlock.ForegroundProperty, textBrush);
            _original.FontFamily = font;
            _original.FontSize = Math.Max(10, s.FontSize * 0.68);
            _original.Foreground = textBrush;

            Color bg = s.Preset == "theme"
                ? ThemeManager.ParseColor(ThemeManager.Hex(ThemeManager.Resolved.Surface), Colors.Black)
                : ThemeManager.ParseColor(s.BackgroundColor, Colors.Black);
            _root.Background = ThemeManager.Brush(ThemeManager.WithAlpha(bg, (float)s.BackgroundOpacity));
            _root.BorderThickness = new Thickness(s.Preset == "theme" ? 1 : 0);
            _root.BorderBrush = ThemeManager.Brush(ThemeManager.WithAlpha(ThemeManager.AccentColor, 0.35f));

            _content.Effect = s.Shadow
                ? new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.95, Color = Colors.Black, RenderingBias = RenderingBias.Performance }
                : null;

            bool scroll = s.Layout == "scroll";
            bool subtitle = s.Layout == "subtitle";
            _scroll.Visibility = scroll ? Visibility.Visible : Visibility.Collapsed;
            _subtitlePanel.Visibility = subtitle ? Visibility.Visible : Visibility.Collapsed;
            ((StackPanel)_main.Parent).Visibility = scroll || subtitle ? Visibility.Collapsed : Visibility.Visible;
            _subOriginal.FontFamily = font;
            _subOriginal.FontSize = Math.Max(10, s.FontSize * 0.66);
            _subOriginal.FontWeight = FontWeights.Normal;
            _subOriginal.Fill = MakeTextBrush(0.85);
            _subOriginal.Outline = Math.Max(1, s.Outline * 0.7);
            _subOriginal.HorizontalAlignment = s.Align == "left" ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            _subOriginal.Refresh();
            _subtitle.FontFamily = font;
            _subtitle.FontSize = s.FontSize;
            _subtitle.FontWeight = weight;
            _subtitle.Fill = textBrush;
            _subtitle.DimFill = MakeTextBrush(s.DimPartial ? 0.72 : 1.0);
            _subtitle.Outline = s.Outline;
            _subtitle.Alignment = Align;
            _subtitle.HorizontalAlignment = s.Align == "left" ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            _subtitle.Refresh();
            _scroll.Height = scroll ? s.BoxHeight : double.NaN;
            Width = s.Width;
            _followTimer.IsEnabled = IsVisible && s.Position == "window";
            Render();
            Reposition();
            UpdateToolbar();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsVisibleProperty)
            {
                _followTimer.IsEnabled = IsVisible && _settings.Bar.Position == "window";
                if (IsVisible) Reposition();
            }
        }

        private void Reposition()
        {
            if (_hwnd == IntPtr.Zero) return;
            var s = _settings.Bar;
            double scale = 1.0;
            try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            double h = ActualHeight > 0 ? ActualHeight : 80;

            if (s.Position == "free" && s.Left.HasValue && s.Top.HasValue)
            {
                // Keep it on a screen that still exists.
                var wa = Native.GetWorkArea(IntPtr.Zero);
                double l = s.Left.Value, t = s.Top.Value;
                if (!OnAnyScreen(l, t, scale)) { l = wa.Left / scale + (wa.Width / scale - Width) / 2; t = wa.Bottom / scale - h - s.BottomMargin; }
                SetPos(l, t);
                return;
            }

            Native.RECT area;
            if (s.Position == "window" && TryGetForegroundBounds(out var win))
            {
                area = win;
                double winWidth = area.Width / scale;
                Width = Math.Min(s.Width, Math.Max(240, winWidth - 32));
            }
            else
            {
                area = Native.GetWorkArea(_hwnd);
                // Same monitor as the cursor when not following a window.
                if (s.Position == "bottom")
                {
                    Native.GetCursorPos(out var pt);
                    var mon = Native.MonitorFromPoint(pt, 2);
                    var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                    if (Native.GetMonitorInfo(mon, ref info)) area = info.rcWork;
                }
                Width = s.Width;
            }
            double left = area.Left / scale + (area.Width / scale - Width) / 2;
            double top = area.Bottom / scale - h - s.BottomMargin;
            if (top < area.Top / scale) top = area.Top / scale;
            SetPos(left, top);
        }

        private void SetPos(double left, double top)
        {
            if (Math.Abs(Left - left) > 0.5) Left = left;
            if (Math.Abs(Top - top) > 0.5) Top = top;
        }

        private static bool OnAnyScreen(double left, double top, double scale)
        {
            var pt = new Native.POINT { X = (int)(left * scale) + 20, Y = (int)(top * scale) + 10 };
            return Native.MonitorFromPoint(pt, 0 /* MONITOR_DEFAULTTONULL */) != IntPtr.Zero;
        }

        private bool TryGetForegroundBounds(out Native.RECT rect)
        {
            rect = default;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _hwnd || Native.IsIconic(fg) || !Native.IsWindowVisible(fg)) return _lastFollow(out rect);
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == Environment.ProcessId) return _lastFollow(out rect);
            var cls = new StringBuilder(64);
            Native.GetClassName(fg, cls, 64);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow")
                return _lastFollow(out rect);
            if (!Native.GetVisibleBounds(fg, out rect) || rect.Width < 200 || rect.Height < 120) return _lastFollow(out rect);
            _lastFollowRect = rect;
            _hasLastFollow = true;
            return true;
        }

        private Native.RECT _lastFollowRect;
        private bool _hasLastFollow;

        private bool _lastFollow(out Native.RECT rect)
        {
            rect = _lastFollowRect;
            return _hasLastFollow;
        }

        // ---------------------------------------------------------------- edit mode

        public void SetEditing(bool editing)
        {
            _editing = editing;
            _editFrame.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            _toolbar.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            ApplyClickThrough();
            RefreshThumbs();
            if (editing)
            {
                if (!IsVisible) Show();
                FadeText(true);
            }
            Render();
            UpdateToolbar();
        }

        private void ApplyClickThrough()
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT, !_editing);
        }

        private Border BuildToolbar()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(ToolButton("Bottom of screen", "bottom"));
            panel.Children.Add(ToolButton("Follow active window", "window"));
            panel.Children.Add(ToolButton("Free", "free"));
            var smaller = ToolButton("A−", "smaller");
            var bigger = ToolButton("A+", "bigger");
            panel.Children.Add(smaller);
            panel.Children.Add(bigger);
            var done = ToolButton("Done", "done");
            done.SetResourceReference(BackgroundProperty, "AccentBrush");
            done.SetResourceReference(ForegroundProperty, "OnAccentBrush");
            done.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(done);
            var b = new Border
            {
                Child = panel,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            b.SetResourceReference(Border.BackgroundProperty, "SolidWindowBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
            b.BorderThickness = new Thickness(1);
            return b;
        }

        private Button ToolButton(string text, string tag)
        {
            var btn = new Button { Content = text, Tag = tag, Margin = new Thickness(2), Padding = new Thickness(12, 6, 12, 6), FontSize = 12.5 };
            btn.SetResourceReference(StyleProperty, "SoftButton");
            btn.Click += (_, _) =>
            {
                var s = _settings.Bar;
                switch (tag)
                {
                    case "done":
                        SetEditing(false);
                        EditFinished?.Invoke(this, EventArgs.Empty);
                        return;
                    case "smaller":
                        s.FontSize = Math.Max(12, s.FontSize - 2);
                        break;
                    case "bigger":
                        s.FontSize = Math.Min(96, s.FontSize + 2);
                        break;
                    case "free":
                        s.Position = "free";
                        s.Left = Left;
                        s.Top = Top;
                        break;
                    default:
                        s.Position = tag;
                        break;
                }
                ApplyStyle();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            };
            return btn;
        }

        private void UpdateToolbar()
        {
            if (_toolbar.Child is not StackPanel panel) return;
            foreach (var child in panel.Children.OfType<Button>())
            {
                string tag = child.Tag as string ?? "";
                if (tag is "bottom" or "window" or "free")
                {
                    bool on = _settings.Bar.Position == tag;
                    if (on)
                    {
                        child.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
                        child.SetResourceReference(ForegroundProperty, "AccentBrush");
                    }
                    else
                    {
                        child.ClearValue(BackgroundProperty);
                        child.ClearValue(ForegroundProperty);
                    }
                }
            }
        }

        private static bool IsInToolbar(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is Button) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private Thumb MakeThumb(HorizontalAlignment h, VerticalAlignment v, Cursor cursor, bool left, bool vertical = false)
        {
            var t = new Thumb
            {
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Cursor = cursor,
                Width = vertical ? double.NaN : 10,
                Height = vertical ? 10 : double.NaN,
                Opacity = 0,
                Visibility = Visibility.Collapsed,
            };
            var rect = new FrameworkElementFactory(typeof(Rectangle));
            rect.SetValue(Shape.FillProperty, Brushes.Transparent);
            t.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = rect };
            t.Opacity = 1;
            t.DragDelta += (_, e) =>
            {
                var s = _settings.Bar;
                if (vertical)
                {
                    if (s.Layout != "scroll") return;
                    s.BoxHeight = Math.Clamp(s.BoxHeight + e.VerticalChange, 80, 1600);
                    _scroll.Height = s.BoxHeight;
                }
                else
                {
                    double change = left ? -e.HorizontalChange : e.HorizontalChange;
                    // Centered positions grow both ways.
                    double factor = s.Position == "free" ? 1 : 2;
                    s.Width = Math.Clamp(s.Width + change * factor, 240, 4000);
                    if (s.Position == "free" && left) { Left -= change; s.Left = Left; }
                    Width = s.Width;
                }
                Render();
            };
            t.DragCompleted += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
            _thumbs.Add(t);
            return t;
        }

        private readonly List<Thumb> _thumbs = new();

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Reposition();
        }

        public void RefreshThumbs()
        {
            foreach (var t in _thumbs) t.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
