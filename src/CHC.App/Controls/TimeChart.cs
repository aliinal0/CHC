using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CHC.Core.History;
using CHC.Core.Localization;

namespace CHC.App.Controls;

/// <summary>Which value of each time slot a line follows.</summary>
public enum ChartValue
{
    Average,
    Max,
    Min,
}

/// <summary>One line of a <see cref="TimeChart"/>.</summary>
public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<HistoryPoint> Points, ChartValue Value = ChartValue.Average, bool Fill = false, bool Dashed = false)
{
    /// <summary>For the legend next to the chart.</summary>
    public SolidColorBrush Brush { get; } = Frozen(Color);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public double? ValueOf(HistoryPoint p) => Value switch
    {
        ChartValue.Max => p.Max ?? p.Average,
        ChartValue.Min => p.Min ?? p.Average,
        _ => p.Average,
    };
}

/// <summary>
/// The history chart: a few lines over a day, a week, a month or a year, a gap where nothing was recorded, and a
/// crosshair that shows every line's value under the mouse.
/// </summary>
public sealed class TimeChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>), null);
    public static readonly DependencyProperty FromProperty = Register(nameof(From), typeof(DateTime), DateTime.Today);
    public static readonly DependencyProperty ToProperty = Register(nameof(To), typeof(DateTime), DateTime.Today.AddDays(1));
    public static readonly DependencyProperty SlotProperty = Register(nameof(Slot), typeof(TimeSpan), TimeSpan.FromMinutes(5));
    public static readonly DependencyProperty FormatProperty = Register(nameof(Format), typeof(Func<double, string>), null);
    public static readonly DependencyProperty FloorProperty = Register(nameof(Floor), typeof(double?), null);
    public static readonly DependencyProperty CeilingProperty = Register(nameof(Ceiling), typeof(double?), null);
    public static readonly DependencyProperty MinSpanProperty = Register(nameof(MinSpan), typeof(double), 10.0);
    public static readonly DependencyProperty ReferenceProperty = Register(nameof(Reference), typeof(double?), null);
    public static readonly DependencyProperty ReferenceLabelProperty = Register(nameof(ReferenceLabel), typeof(string), null);
    public static readonly DependencyProperty ConnectProperty = Register(nameof(Connect), typeof(bool), false);
    public static readonly DependencyProperty ShowLegendProperty = Register(nameof(ShowLegend), typeof(bool), false);

    /// <summary>Names the lines on the chart itself, so that one that isn't CHC's own measurement can't be taken for one.</summary>
    public bool ShowLegend { get => (bool)GetValue(ShowLegendProperty); set => SetValue(ShowLegendProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? value) =>
        DependencyProperty.Register(name, type, typeof(TimeChart), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public DateTime From { get => (DateTime)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTime To { get => (DateTime)GetValue(ToProperty); set => SetValue(ToProperty, value); }

    /// <summary>The time one point covers; points further apart than one and a half slots are drawn with a gap.</summary>
    public TimeSpan Slot { get => (TimeSpan)GetValue(SlotProperty); set => SetValue(SlotProperty, value); }

    /// <summary>Formats a value for the axis and the crosshair ("72 °C", "340 W").</summary>
    public Func<double, string>? Format { get => (Func<double, string>?)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    /// <summary>The axis never goes below / above these (0 for loads and power, 100 for a percentage).</summary>
    public double? Floor { get => (double?)GetValue(FloorProperty); set => SetValue(FloorProperty, value); }
    public double? Ceiling { get => (double?)GetValue(CeilingProperty); set => SetValue(CeilingProperty, value); }

    /// <summary>Draws a dot for each point and joins them across gaps: for what happens now and then, like playing a game.</summary>
    public bool Connect { get => (bool)GetValue(ConnectProperty); set => SetValue(ConnectProperty, value); }

    /// <summary>The axis spans at least this much, so a quiet day doesn't look like a storm.</summary>
    public double MinSpan { get => (double)GetValue(MinSpanProperty); set => SetValue(MinSpanProperty, value); }

    /// <summary>A dashed line across the chart with a label (the power supply's rating on the power chart).</summary>
    public double? Reference { get => (double?)GetValue(ReferenceProperty); set => SetValue(ReferenceProperty, value); }
    public string? ReferenceLabel { get => (string?)GetValue(ReferenceLabelProperty); set => SetValue(ReferenceLabelProperty, value); }

    private Point? _mouse;
    private const double Left = 52, Right = 10, Top = 10, Bottom = 24;

    public TimeChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        // Transparent, not empty: the whole area takes the mouse for the crosshair.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < Left + Right + 40 || h < Top + Bottom + 30) return;

        var top = ShowLegend ? Top + LegendHeight : Top;
        var plot = new Rect(Left, top, w - Left - Right, h - top - Bottom);
        var text = Brush("TextSecondaryBrush", Color.FromRgb(150, 150, 150));
        var grid = new Pen(Brush("DividerBrush", Color.FromArgb(40, 128, 128, 128)), 1);
        grid.Freeze();
        var format = Format ?? (v => v.ToString("0", Loc.Instance.Culture));
        var series = Series ?? [];

        // ---- value axis
        var values = series.SelectMany(s => s.Points.Select(s.ValueOf)).OfType<double>().ToList();
        double low = values.Count > 0 ? values.Min() : 0, high = values.Count > 0 ? values.Max() : 1;
        if (Reference is { } reference) high = Math.Max(high, reference * 1.05);
        if (Floor is { } floor) low = Math.Min(floor, low);
        if (Ceiling is { } ceiling && high <= ceiling) high = Math.Max(high, Math.Min(ceiling, low + MinSpan));
        if (high - low < MinSpan)
        {
            var mid = (high + low) / 2;
            low = Floor is { } f ? Math.Max(f, mid - MinSpan / 2) : mid - MinSpan / 2;
            high = low + MinSpan;
        }
        var step = NiceStep((high - low) / 4);
        low = Math.Floor(low / step) * step;
        high = Math.Ceiling(high / step) * step;
        if (high <= low) high = low + step;
        double Y(double v) => plot.Bottom - (v - low) / (high - low) * plot.Height;

        for (var v = low; v <= high + step / 2; v += step)
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(format(v), text, 11);
            dc.DrawText(label, new Point(plot.Left - 8 - label.Width, y - label.Height / 2));
        }

        // ---- time axis
        var from = From;
        var to = To;
        var span = (to - from).TotalSeconds;
        if (span <= 0) return;
        double X(DateTime t) => plot.Left + (t - from).TotalSeconds / span * plot.Width;
        var culture = Loc.Instance.Culture;
        foreach (var (tick, label) in TimeTicks(from, to, culture))
        {
            var x = Math.Round(X(tick)) + 0.5;
            dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Text(label, text, 11);
            if (x + 4 + t.Width < w) dc.DrawText(t, new Point(x + 4, plot.Bottom + 5));
        }

        // ---- reference line
        if (Reference is { } level)
        {
            var warn = Palette.Brush(Core.Health.Severity.Warning);
            var dash = new Pen(warn, 1.2) { DashStyle = DashStyles.Dash };
            dash.Freeze();
            var y = Math.Round(Y(level)) + 0.5;
            dc.DrawLine(dash, new Point(plot.Left, y), new Point(plot.Right, y));
            if (ReferenceLabel is { Length: > 0 } label)
            {
                var t = Text(label, warn, 11, FontWeights.SemiBold);
                dc.DrawText(t, new Point(plot.Right - t.Width - 4, y - t.Height - 2));
            }
        }

        // ---- lines, broken where nothing was recorded
        var gap = TimeSpan.FromTicks(Slot.Ticks * 3 / 2);
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var s in series)
        {
            var points = s.Points.Where(p => s.ValueOf(p) is not null).ToList();
            if (points.Count == 0) continue;
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var l = line.Open())
            using (var a = area.Open())
            {
                var run = new List<Point>();
                void Flush()
                {
                    if (run.Count == 0) return;
                    if (run.Count == 1) run.Add(new Point(run[0].X + 1.5, run[0].Y)); // a lone point still shows
                    l.BeginFigure(run[0], false, false);
                    l.PolyLineTo(run.Skip(1).ToList(), true, true);
                    if (s.Fill)
                    {
                        a.BeginFigure(new Point(run[0].X, plot.Bottom), true, true);
                        a.PolyLineTo(run.Append(new Point(run[^1].X, plot.Bottom)).ToList(), false, false);
                    }
                    run.Clear();
                }
                DateTime? last = null;
                foreach (var p in points)
                {
                    if (!Connect && last is { } prev && p.Time - prev > gap) Flush();
                    // Each point stands in the middle of its slot.
                    run.Add(new Point(X(p.Time + Slot / 2), Y(s.ValueOf(p)!.Value)));
                    last = p.Time;
                }
                Flush();
            }
            line.Freeze();
            area.Freeze();
            var c = s.Color;
            if (s.Fill)
            {
                var fill = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            var pen = new Pen(new SolidColorBrush(c), 1.8) { LineJoin = PenLineJoin.Round };
            if (s.Dashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
            var ring = new Pen(pen.Brush, 1.5);
            ring.Freeze();
            if (Connect)
                foreach (var p in points)
                {
                    var dot = new Point(X(p.Time + Slot / 2), Y(s.ValueOf(p)!.Value));
                    if (s.Dashed) dc.DrawEllipse(Brush("CardBrush", Colors.Black), ring, dot, 3, 3);
                    else dc.DrawEllipse(pen.Brush, null, dot, 2.6, 2.6);
                }
        }
        dc.Pop();
        if (ShowLegend) DrawLegend(dc, series, plot, text);

        // ---- crosshair
        if (_mouse is not { } m || !plot.Contains(m)) return;
        var at = from + TimeSpan.FromSeconds((m.X - plot.Left) / plot.Width * span);
        var rows = new List<(ChartSeries Series, double Value, DateTime Time)>();
        foreach (var s in series)
        {
            var nearest = s.Points.Where(p => s.ValueOf(p) is not null).MinBy(p => Math.Abs((p.Time + Slot / 2 - at).Ticks));
            if (nearest is not null && Math.Abs((nearest.Time + Slot / 2 - at).Ticks) <= Slot.Ticks)
                rows.Add((s, s.ValueOf(nearest)!.Value, nearest.Time));
        }
        if (rows.Count == 0) return;
        var slot = rows[0].Time;
        var cx = Math.Round(X(slot + Slot / 2)) + 0.5;
        var cross = new Pen(Brush("TextMutedBrush", Color.FromArgb(120, 128, 128, 128)), 1) { DashStyle = DashStyles.Dash };
        cross.Freeze();
        dc.DrawLine(cross, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
        foreach (var r in rows)
            dc.DrawEllipse(new SolidColorBrush(r.Series.Color), new Pen(Brush("CardBrush", Colors.Black), 1.5), new Point(cx, Y(r.Value)), 4, 4);

        var primary = Brush("TextBrush", Colors.White);
        var title = Text(SlotLabel(slot, culture), primary, 12, FontWeights.SemiBold);
        var lines = rows.Select(r => (r.Series.Color, Text(r.Series.Name, text, 12), Text(format(r.Value), primary, 12, FontWeights.SemiBold))).ToList();
        var boxW = Math.Max(title.Width, lines.Max(l => 14 + l.Item2.Width + 12 + l.Item3.Width)) + 20;
        var boxH = 10 + title.Height + 4 + lines.Sum(l => l.Item2.Height + 2) + 8;
        var bx = cx + 12 + boxW > plot.Right ? cx - 12 - boxW : cx + 12;
        var by = Math.Clamp(m.Y - boxH / 2, plot.Top, Math.Max(plot.Top, plot.Bottom - boxH));
        var box = new Rect(bx, by, boxW, boxH);
        dc.DrawRoundedRectangle(Brush("CardBrush", Color.FromRgb(44, 44, 44)),
            new Pen(Brush("BorderStrongBrush", Color.FromArgb(60, 128, 128, 128)), 1), box, 6, 6);
        var ty = by + 8;
        dc.DrawText(title, new Point(bx + 10, ty));
        ty += title.Height + 4;
        foreach (var (color, name, value) in lines)
        {
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(bx + 14, ty + name.Height / 2), 4, 4);
            dc.DrawText(name, new Point(bx + 24, ty));
            dc.DrawText(value, new Point(box.Right - 10 - value.Width, ty));
            ty += name.Height + 2;
        }
    }

    /// <summary>The slot under the crosshair: a time of day for short spans, a date for long ones.</summary>
    private string SlotLabel(DateTime slot, CultureInfo culture)
    {
        var span = To - From;
        if (Slot >= TimeSpan.FromDays(1)) return slot.ToString("D", culture);
        var end = slot + Slot;
        var day = span > TimeSpan.FromDays(1.5) ? slot.ToString("ddd d MMM", culture) + "  " : "";
        return day + slot.ToString("t", culture) + "–" + end.ToString("t", culture);
    }

    private static IEnumerable<(DateTime Tick, string Label)> TimeTicks(DateTime from, DateTime to, CultureInfo culture)
    {
        var span = to - from;
        if (span <= TimeSpan.FromDays(1.5))
        {
            var t = from.Date.AddHours(Math.Ceiling(from.TimeOfDay.TotalHours / 3) * 3);
            for (; t < to; t = t.AddHours(3)) yield return (t, t.ToString("t", culture));
        }
        else if (span <= TimeSpan.FromDays(8))
        {
            for (var t = from.Date.AddDays(from.TimeOfDay > TimeSpan.Zero ? 1 : 0); t < to; t = t.AddDays(1)) yield return (t, t.ToString("ddd", culture));
        }
        else if (span <= TimeSpan.FromDays(40))
        {
            for (var t = from.Date.AddDays(from.TimeOfDay > TimeSpan.Zero ? 1 : 0); t < to; t = t.AddDays(1))
                if (t.Day % 5 == 1 && t.Day < 30) yield return (t, t.ToString("d MMM", culture));
        }
        else
        {
            for (var t = new DateTime(from.Year, from.Month, 1).AddMonths(1); t < to; t = t.AddMonths(1)) yield return (t, t.ToString("MMM", culture));
        }
    }

    /// <summary>1, 2, 2.5 or 5 times a power of ten: axis steps a person reads at a glance.</summary>
    private static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough)) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var f = rough / power;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * power;
    }

    private const double LegendHeight = 22;

    private void DrawLegend(DrawingContext dc, IReadOnlyList<ChartSeries> series, Rect plot, Brush text)
    {
        const double Sample = 20, Gap = 6, Between = 16;
        var x = plot.Right;
        foreach (var s in series.Reverse())
        {
            var name = Text(s.Name, text, 11.5);
            x -= name.Width;
            dc.DrawText(name, new Point(x, plot.Top - LegendHeight + 2));
            x -= Gap + Sample;
            var pen = new Pen(new SolidColorBrush(s.Color), 1.8);
            if (s.Dashed) pen.DashStyle = DashStyles.Dash;
            var y = plot.Top - LegendHeight + 2 + name.Height / 2;
            dc.DrawLine(pen, new Point(x, y), new Point(x + Sample, y));
            x -= Between;
        }
    }

    private FormattedText Text(string s, Brush brush, double size, FontWeight? weight = null) =>
        new(s, Loc.Instance.Culture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
