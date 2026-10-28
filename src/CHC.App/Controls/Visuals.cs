using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CHC.Core.Health;

namespace CHC.App.Controls;

/// <summary>Colours used for health states; mid tones that read well on light and dark themes.</summary>
public static class Palette
{
    public static readonly Color GoodColor = Color.FromRgb(0x2E, 0xB8, 0x6A);
    public static readonly Color InfoColor = Color.FromRgb(0x3B, 0x8E, 0xF5);
    public static readonly Color WarningColor = Color.FromRgb(0xE8, 0xA3, 0x17);
    public static readonly Color CriticalColor = Color.FromRgb(0xE5, 0x48, 0x4D);
    public static readonly Color BrandColor = Color.FromRgb(0x22, 0xC5, 0xE0);

    public static Color For(Severity severity) => severity switch
    {
        Severity.Info => InfoColor,
        Severity.Warning => WarningColor,
        Severity.Critical => CriticalColor,
        _ => GoodColor,
    };

    private static readonly Dictionary<(Severity, byte), SolidColorBrush> Cache = [];

    public static SolidColorBrush Brush(Severity severity, byte alpha = 255)
    {
        if (Cache.TryGetValue((severity, alpha), out var brush)) return brush;
        var c = For(severity);
        brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        Cache[(severity, alpha)] = brush;
        return brush;
    }
}

/// <summary>Circular gauge for the health score; animates when the value changes.</summary>
public sealed class ScoreRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ScoreRing), new PropertyMetadata(0.0, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(ScoreRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ScoreRing), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(ScoreRing), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ScoreRing), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (ScoreRing)d;
        var animation = new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ring.BeginAnimation(DisplayValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var radius = (size - Thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        var fraction = Math.Clamp((double)GetValue(DisplayValueProperty) / 100.0, 0, 0.9999);
        if (fraction <= 0) return;
        var angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var pen = new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>Small area chart for recent load history.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeColorProperty = DependencyProperty.Register(
        nameof(StrokeColor), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Palette.BrandColor, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public Color StrokeColor { get => (Color)GetValue(StrokeColorProperty); set => SetValue(StrokeColorProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var step = ActualWidth / Math.Max(1, Capacity - 1);
        var offset = ActualWidth - step * (values.Count - 1);
        var max = Math.Max(Maximum, 1);
        Point P(int i) => new(offset + i * step, ActualHeight - Math.Clamp(values[i] / max, 0, 1) * (ActualHeight - 2) - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(P(0), false, false);
            a.BeginFigure(new Point(offset, ActualHeight), true, true);
            a.LineTo(P(0), false, false);
            for (var i = 1; i < values.Count; i++)
            {
                l.LineTo(P(i), true, true);
                a.LineTo(P(i), false, false);
            }
            a.LineTo(new Point(ActualWidth, ActualHeight), false, false);
        }
        line.Freeze();
        area.Freeze();

        var c = StrokeColor;
        var fill = new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Thin rounded progress bar (0-100).</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(MeterBar), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MeterBar), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    static MeterBar() => HeightProperty.OverrideMetadata(typeof(MeterBar), new FrameworkPropertyMetadata(8.0));

    protected override void OnRender(DrawingContext dc)
    {
        var r = ActualHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        var width = ActualWidth * Math.Clamp(Value, 0, 100) / 100;
        if (width > 0)
            dc.DrawRoundedRectangle(BarBrush, null, new Rect(0, 0, Math.Max(width, ActualHeight), ActualHeight), r, r);
    }
}

/// <summary>UniformGrid that picks its column count from the available width.</summary>
public sealed class ResponsiveGrid : UniformGrid
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var columns = double.IsInfinity(constraint.Width) ? 1 : (int)Math.Max(1, constraint.Width / MinItemWidth);
        columns = Math.Min(columns, MaxColumns);
        if (Columns != columns) Columns = columns;
        return base.MeasureOverride(constraint);
    }
}
