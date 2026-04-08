using System;
using System.Globalization;
using AzureBackup.Core;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AzureBackup.ViewModels;

public class EqualityConverter : IValueConverter
{
    public string? CompareValue { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() == CompareValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool hasOrphans)
        {
            // Orange/red for warning conditions (e.g., orphans exist), gray when clean
            return hasOrphans ? Brushes.Orange : Brushes.Gray;
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRunning)
        {
            return isRunning ? "Running" : "Stopped";
        }
        return "Unknown";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToViewModeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool useTreeView)
        {
            // Show what clicking will switch TO (opposite of current state)
            return useTreeView ? "List" : "Tree";
        }
        return "View";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BytesToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return FormatHelper.FormatBytes(bytes);
        }
        if (value is int intBytes)
        {
            return FormatHelper.FormatBytes(intBytes);
        }
        return "0 B";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts pending count to a color - orange if pending > 0, otherwise default.
/// </summary>
public class PendingToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int pending && pending > 0)
        {
            return Brushes.Orange;
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an array length to a slider maximum (length - 1).
/// Used to bind Slider.Maximum to MemoryLimitSteps.Length.
/// </summary>
public class SliderMaxConverter : IValueConverter
{
    public static readonly SliderMaxConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int length && length > 0)
            return length - 1;
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a color name string (e.g., "LimeGreen", "Orange", "Red") to an Avalonia <see cref="IBrush"/>.
/// </summary>
public class NameToBrushConverter : IValueConverter
{
    public static readonly NameToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "LimeGreen" => Brushes.LimeGreen,
            "Orange" => Brushes.Orange,
            "Red" => Brushes.Red,
            _ => Brushes.Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

