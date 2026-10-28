using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CHC.App.Controls;
using CHC.Core.Health;
using CHC.Core.Reporting;
using Wpf.Ui.Controls;

namespace CHC.App.Converters;

/// <summary>Severity → solid brush. ConverterParameter = alpha (0-255) for tinted backgrounds.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var alpha = parameter is string s && byte.TryParse(s, out var a) ? a : (byte)255;
        return Palette.Brush(value as Severity? ?? Severity.Good, alpha);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SeverityColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Palette.For(value as Severity? ?? Severity.Good);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SeveritySymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as Severity?) switch
    {
        Severity.Info => SymbolRegular.Info24,
        Severity.Warning => SymbolRegular.Warning24,
        Severity.Critical => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.CheckmarkCircle24,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Severity → localized name. Use with a MultiBinding on the language so it updates.</summary>
public sealed class SeverityTextConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Severity s ? ReportWriter.SeverityName(s) : "";

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Convert(values.FirstOrDefault(), targetType, parameter, culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Visible when the value is "truthy": true, non-empty string, non-empty collection, non-zero number, non-null object.
/// ConverterParameter="Invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            double d => d != 0,
            ICollection c => c.Count > 0,
            IEnumerable e => e.Cast<object>().Any(),
            _ => true,
        };
        if (parameter is "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
