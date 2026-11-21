using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Caption.Services;

namespace Caption.Views
{
    /// <summary>The small "Update ready, restart" card that appears above the tray.</summary>
    public sealed class UpdatePrompt : Window
    {
        public event EventHandler? RestartClicked;

        public UpdatePrompt(string version)
        {
            Title = "Caption update";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "TextBrush");

            var title = new TextBlock { Text = "Update ready", FontWeight = FontWeights.SemiBold, FontSize = 14 };
            var body = new TextBlock { Text = $"Caption {version} is downloaded and verified. Restart to update; your models and settings stay.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12), MaxWidth = 300 };
            body.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

            var restart = new Button { Content = "Restart", Padding = new Thickness(16, 7, 16, 7) };
            restart.SetResourceReference(StyleProperty, "AccentButton");
            restart.Click += (_, _) => { Close(); RestartClicked?.Invoke(this, EventArgs.Empty); };
            var later = new Button { Content = "Later", Margin = new Thickness(8, 0, 0, 0) };
            later.SetResourceReference(StyleProperty, "SoftButton");
            later.Click += (_, _) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(restart);
            buttons.Children.Add(later);

            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(body);
            stack.Children.Add(buttons);

            var card = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 14, 18, 14),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(12),
            };
            card.SetResourceReference(Border.BackgroundProperty, "SolidWindowBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 };
            Content = card;

            Loaded += (_, _) =>
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth - 8;
                Top = wa.Bottom - ActualHeight - 8;
            };
        }
    }
}
