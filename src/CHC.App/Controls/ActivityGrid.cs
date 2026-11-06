using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CHC.App.Controls;

/// <summary>What the PC was doing in one hour.</summary>
public enum ActivityState : byte
{
    Off,
    Asleep,
    On,
    Game,
}

/// <summary>Days across, hours down.</summary>
public sealed class ActivityMap(DateTime firstDay, int days)
{
    public const int Hours = 24;

    private readonly ActivityState[] _cells = new ActivityState[days * Hours];
    private readonly bool[] _recorded = new bool[days * Hours];

    public DateTime FirstDay { get; } = firstDay;
    public int Days { get; } = days;

    /// <summary>CHC itself was recording in that hour; every other hour comes from Windows' records.</summary>
    public void SetRecorded(int day, int hour) => _recorded[day * Hours + hour] = true;

    public bool IsRecorded(int day, int hour) => _recorded[day * Hours + hour];

    public ActivityState this[int day, int hour]
    {
        get => _cells[day * Hours + hour];
        set => _cells[day * Hours + hour] = value;
    }
}

/// <summary>A map of when the PC was on: one column for each day, one row for each hour of the day. Clicking an hour selects it.</summary>
public sealed class ActivityGrid : FrameworkElement
{
    private const double LabelWidth = 34;
    private const double LabelHeight = 20;
    private const double Gap = 2;
    private const double MaxCell = 18;
    private const double MinCell = 6;
    private const double RowHeight = 14;

    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(nameof(Map), typeof(ActivityMap), typeof(ActivityGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SelectedDayProperty = DependencyProperty.Register(nameof(SelectedDay), typeof(int), typeof(ActivityGrid),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedHourProperty = DependencyProperty.Register(nameof(SelectedHour), typeof(int), typeof(ActivityGrid),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>An hour was clicked: its day's place in the map, and the hour.</summary>
    public event Action<int, int>? HourClicked;

    public ActivityMap? Map
    {
        get => (ActivityMap?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public int SelectedDay
    {
        get => (int)GetValue(SelectedDayProperty);
        set => SetValue(SelectedDayProperty, value);
    }

    public int SelectedHour
    {
        get => (int)GetValue(SelectedHourProperty);
        set => SetValue(SelectedHourProperty, value);
    }

    public ActivityGrid() => Cursor = Cursors.Hand;

    private double CellWidth(double width) => Map is { Days: > 0 } map ? Math.Clamp((width - LabelWidth) / map.Days, MinCell, MaxCell) : MaxCell;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 900 : available.Width;
        return new Size(width, LabelHeight + ActivityMap.Hours * RowHeight);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Map is not { } map) return;
        var at = e.GetPosition(this);
        var day = (int)((at.X - LabelWidth) / CellWidth(ActualWidth));
        var hour = (int)((at.Y - LabelHeight) / RowHeight);
        if (at.X >= LabelWidth && at.Y >= LabelHeight && day < map.Days && hour < ActivityMap.Hours) HourClicked?.Invoke(day, hour);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        // Clicks only land on what is drawn: without this, the gaps between the hours wouldn't take them.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Map is not { } map) return;
        var cell = CellWidth(ActualWidth);
        var off = Brush("SubtleBrush", Colors.Gray);
        var asleep = Brush("SubtleStrongBrush", Colors.DarkGray);
        var text = Brush("TextMutedBrush", Colors.Gray);
        var selection = new Pen(Brush("TextBrush", Colors.White), 1.5);
        var on = new SolidColorBrush(Palette.BrandColor);
        var game = new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x6A));
        var culture = CHC.Core.Localization.Loc.Instance.Culture;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface("Segoe UI");

        FormattedText Label(string s) => new(s, culture, FlowDirection.LeftToRight, face, 10.5, text, dpi);

        for (var hour = 0; hour < ActivityMap.Hours; hour += 6)
            dc.DrawText(Label(hour.ToString("00", culture) + ":00"), new Point(0, LabelHeight + hour * RowHeight - 1));

        var lastMonth = -1;
        for (var day = 0; day < map.Days; day++)
        {
            var date = map.FirstDay.AddDays(day);
            if (date.DayOfWeek == DayOfWeek.Monday || (day == 0 && lastMonth < 0))
            {
                dc.DrawText(Label(date.ToString("d MMM", culture)), new Point(LabelWidth + day * cell, 2));
                lastMonth = date.Month;
            }
            for (var hour = 0; hour < ActivityMap.Hours; hour++)
            {
                var brush = map[day, hour] switch
                {
                    ActivityState.Game => game,
                    ActivityState.On => on,
                    ActivityState.Asleep => asleep,
                    _ => off,
                };
                var rect = new Rect(LabelWidth + day * cell, LabelHeight + hour * RowHeight, Math.Max(1, cell - Gap), RowHeight - Gap);
                dc.DrawRoundedRectangle(brush, null, rect, 2, 2);
                if (map.IsRecorded(day, hour)) dc.DrawEllipse(Brushes.White, null, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), 2, 2);
            }
        }
        if (SelectedDay >= 0 && SelectedDay < map.Days && SelectedHour >= 0)
            dc.DrawRoundedRectangle(null, selection, new Rect(LabelWidth + SelectedDay * cell - 1, LabelHeight + SelectedHour * RowHeight - 1, Math.Max(1, cell - Gap) + 2, RowHeight - Gap + 2), 2, 2);
    }

    private Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
