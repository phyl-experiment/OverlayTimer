using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using SystemFonts = System.Windows.SystemFonts;
using FlowDirection = System.Windows.FlowDirection;

namespace OverlayTimer.UI
{
    /// <summary>
    /// TextBlock과 유사하지만 텍스트를 Geometry로 렌더링해 Stroke(외곽선)을 지원합니다.
    /// </summary>
    public sealed class OutlinedTextBlock : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                    OnTextualPropertyChanged));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(12.0,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                    OnTextualPropertyChanged));

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(FontWeights.Normal,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                    OnTextualPropertyChanged));

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                    OnTextualPropertyChanged));

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(Brushes.White,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => (FontWeight)GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public Brush? Stroke
        {
            get => (Brush?)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        private Geometry? _textGeometry;
        private Size _measuredSize;

        private static void OnTextualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((OutlinedTextBlock)d)._textGeometry = null;
        }

#pragma warning disable CS0618 // FormattedText(string, ..., double, ..., double) is obsolete
        private FormattedText BuildFormattedText()
        {
            double pixelsPerDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            return new FormattedText(
                Text ?? string.Empty,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal),
                Math.Max(1.0, FontSize),
                Brushes.Black,
                pixelsPerDip);
        }
#pragma warning restore CS0618

        private void EnsureGeometry()
        {
            if (_textGeometry != null)
                return;

            var ft = BuildFormattedText();
            _textGeometry = ft.BuildGeometry(new Point(0, 0));
            _measuredSize = new Size(ft.Width, ft.Height);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            EnsureGeometry();
            return _measuredSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            EnsureGeometry();

            if (_textGeometry == null || _textGeometry.IsEmpty())
                return;

            double st = StrokeThickness;
            Brush? stroke = Stroke;
            if (stroke != null && st > 0)
            {
                // Pen width = st*2 so that st pixels appear outside the fill boundary
                var pen = new Pen(stroke, st * 2) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, _textGeometry);
            }

            dc.DrawGeometry(Foreground, null, _textGeometry);
        }
    }
}
