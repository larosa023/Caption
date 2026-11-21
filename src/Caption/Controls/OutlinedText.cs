using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Caption.Controls
{
    /// <summary>
    /// Film-style subtitle text: letters with a solid outline (readable on any picture), and an optional
    /// dimmed tail for words that are still settling.
    /// </summary>
    public sealed class OutlinedText : FrameworkElement
    {
        private string _text = "";
        private int _dimFrom = -1;
        private FormattedText? _ft;

        public FontFamily FontFamily { get; set; } = new("Segoe UI");
        public double FontSize { get; set; } = 34;
        public FontWeight FontWeight { get; set; } = FontWeights.SemiBold;
        public Brush Fill { get; set; } = Brushes.White;
        public Brush DimFill { get; set; } = Brushes.Gray;
        public Brush OutlineBrush { get; set; } = Brushes.Black;
        public double Outline { get; set; } = 3;
        public TextAlignment Alignment { get; set; } = TextAlignment.Center;

        /// <summary>Sets the text (lines separated by '\n'); characters from dimFrom on are drawn dimmed.</summary>
        public void SetText(string text, int dimFrom)
        {
            _text = text ?? "";
            _dimFrom = dimFrom;
            _ft = null;
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void Refresh()
        {
            _ft = null;
            InvalidateMeasure();
            InvalidateVisual();
        }

        private FormattedText Build(double width)
        {
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            var ft = new FormattedText(_text.Length == 0 ? " " : _text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Fill, dpi)
            {
                TextAlignment = Alignment,
                LineHeight = FontSize * 1.22,
            };
            if (!double.IsInfinity(width) && width > 0) ft.MaxTextWidth = width;
            if (_dimFrom >= 0 && _dimFrom < _text.Length) ft.SetForegroundBrush(DimFill, _dimFrom, _text.Length - _dimFrom);
            return ft;
        }

        protected override Size MeasureOverride(Size available)
        {
            double width = double.IsInfinity(available.Width) ? 2000 : Math.Max(10, available.Width);
            _ft = Build(width - Outline * 2);
            if (_text.Length == 0) return new Size(0, 0);
            return new Size(Math.Min(width, _ft.WidthIncludingTrailingWhitespace + Outline * 2), _ft.Height + Outline * 2);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_text.Length == 0) return;
            double width = Math.Max(10, ActualWidth - Outline * 2);
            var ft = Build(width);
            var origin = new Point(Outline, Outline);
            if (Outline > 0)
            {
                var geometry = ft.BuildGeometry(origin);
                var pen = new Pen(OutlineBrush, Outline * 2) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
            dc.DrawText(ft, origin);
        }
    }
}
