using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Drivers.Detection;

namespace UniversalRGB.Desktop.Converters;

internal static class Palette
{
    public static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    public static Color ToWpf(this RgbColor c) => Color.FromRgb(c.R, c.G, c.B);

    public static RgbColor ToRgb(this Color c) => new(c.R, c.G, c.B);
}

/// <summary>bool → Visibility. Pass "invert" as parameter to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is "invert")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ parameter is "invert";
}

/// <summary>null/empty → Collapsed. Pass "invert" to show only when null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "invert")
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>RgbColor or WPF Color → SolidColorBrush.</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RgbColor rgb => new SolidColorBrush(rgb.ToWpf()),
        Color color => new SolidColorBrush(color),
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Device / requirement status → foreground brush, or the soft background with parameter "soft".</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter is "soft";
        var key = value switch
        {
            DeviceStatus.Ready => "Green",
            DeviceStatus.DriverRequired => "Amber",
            DeviceStatus.SetupRequired => "Blue",
            DeviceStatus.ConflictDetected => "Red",
            DeviceStatus.Unsupported => "Muted",
            DeviceStatus.Offline => "Dim",
            RequirementState.Ok => "Green",
            RequirementState.Missing => "Red",
            RequirementState.Warning => "Amber",
            RequirementState.Info => "Blue",
            true => "Green",
            false => "Dim",
            _ => "Muted",
        };

        if (!soft)
        {
            return Palette.Brush(key);
        }

        return key switch
        {
            "Green" => Palette.Brush("GreenSoft"),
            "Amber" => Palette.Brush("AmberSoft"),
            "Blue" => Palette.Brush("BlueSoft"),
            "Red" => Palette.Brush("RedSoft"),
            _ => new SolidColorBrush(Color.FromArgb(0x10, 0xA1, 0xA1, 0xAA)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum values → friendly text.</summary>
public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DeviceStatus.Ready => "Ready",
        DeviceStatus.DriverRequired => "Driver Required",
        DeviceStatus.SetupRequired => "Setup Required",
        DeviceStatus.ConflictDetected => "Conflict Detected",
        DeviceStatus.Unsupported => "Unsupported",
        DeviceStatus.Offline => "Offline",
        RequirementState.Ok => "OK",
        RequirementState.Missing => "Missing",
        RequirementState.Warning => "Attention",
        RequirementState.Info => "Info",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value equals one of the '|'-separated parameter values.</summary>
public sealed class EqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        var options = (parameter?.ToString() ?? "").Split('|');
        return text is not null && options.Contains(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Compares a value with the converter parameter (for radio-button style selection).</summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        return targetType.IsEnum ? Enum.Parse(targetType, parameter.ToString()!) : parameter;
    }
}
