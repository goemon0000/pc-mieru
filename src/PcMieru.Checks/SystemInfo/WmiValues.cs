using System.Globalization;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// Tolerant readers of the values of a WMI row, for the row mappers of the checks. A property that is missing or not what it should be is
/// null (or empty), never an exception: a mapper that throws makes the whole read an error, so one odd row would cost the answer of the others.
/// </summary>
internal static class WmiValues
{
    /// <summary>The trimmed text of a value; null when it is empty.</summary>
    public static string? Text(object? value)
    {
        var text = value is IConvertible convertible ? SafeString(convertible) : value?.ToString();
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>A whole number (a WMI uint32 and a text of digits too); null when the value has none or does not fit.</summary>
    public static long? Long(object? value)
    {
        if (value is string text)
        {
            return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        if (value is bool or not IConvertible)
        {
            return null;
        }

        try
        {
            return ((IConvertible)value).ToInt64(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>A whole number that fits an int; null otherwise.</summary>
    public static int? Int(object? value) => Long(value) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;

    /// <summary>A true or false value; null when it is neither.</summary>
    public static bool? Bool(object? value) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s.Trim(), out var parsed) => parsed,
        _ => null,
    };

    /// <summary>The whole numbers of an array property (the ones that are numbers); a single number is a list of one; nothing is an empty list.</summary>
    public static IReadOnlyList<int> Ints(object? value)
    {
        if (value is not Array array)
        {
            return Int(value) is { } single ? [single] : [];
        }

        var numbers = new List<int>(array.Length);
        foreach (var element in array)
        {
            if (Int(element) is { } number)
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    private static string? SafeString(IConvertible value)
    {
        try
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
