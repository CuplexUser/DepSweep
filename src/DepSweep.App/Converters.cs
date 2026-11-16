using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DepSweep.Core.Models;

namespace DepSweep.App;

/// <summary>Maps a <see cref="ProjectStatus"/> to a foreground brush.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x8A, 0x00)));
    private static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x7C, 0xD6)));
    private static readonly Brush Gray = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ProjectStatus.Clean or ProjectStatus.Fixed => Green,
        ProjectStatus.Vulnerable or ProjectStatus.Error => Red,
        ProjectStatus.Partial or ProjectStatus.Unchanged or ProjectStatus.WouldFix or ProjectStatus.Skipped => Amber,
        ProjectStatus.Queued or ProjectStatus.Scanning or ProjectStatus.Fixing => Blue,
        _ => Gray,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>Dims a severity badge when its count is zero or unknown.</summary>
public sealed class CountOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? 1.0 : 0.25;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is non-null (or a non-empty string); "invert" as parameter flips it.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is string s ? s.Length > 0 : value is not null;
        if (parameter as string == "invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bool to Visibility with optional "invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter as string == "invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"NotScanned" → "Not scanned".</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (char.IsUpper(c) && sb.Length > 0)
            {
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Severity.Critical => "CriticalBrush",
            Severity.High => "HighBrush",
            Severity.Moderate => "ModerateBrush",
            _ => "LowBrush",
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
