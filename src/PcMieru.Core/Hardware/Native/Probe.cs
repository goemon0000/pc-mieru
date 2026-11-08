using System.ComponentModel;

namespace PcMieru.Core.Hardware.Native;

/// <summary>
/// Small helpers shared by every collector. They keep the rule "never fake a zero": a read that did not happen
/// becomes Unsupported/Denied/Error/Gone with a short English detail, never a default value.
/// </summary>
internal static class Probe
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorNotSupported = 50;
    public const int ErrorInvalidFunction = 1;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorFileNotFound = 2;
    public const int ErrorNotFound = 1168;

    public static Value<T> Ok<T>(T value, string source) => Value.Ok(value, source);

    public static Value<T> FromWin32<T>(int error, string source, string what)
    {
        var detail = $"{what}: Win32 error {error}";
        return error switch
        {
            ErrorAccessDenied => Value.Denied<T>(source, detail),
            ErrorNotSupported or ErrorInvalidFunction or ErrorNotFound => Value.Unsupported<T>(source, detail),
            _ => Value.Failed<T>(source, detail),
        };
    }

    public static Value<T> FromException<T>(Exception ex, string source)
    {
        return ex switch
        {
            UnauthorizedAccessException => Value.Denied<T>(source, "access denied"),
            Win32Exception w => FromWin32<T>(w.NativeErrorCode, source, "native call"),
            OperationCanceledException => Value.Missing<T>(ValueStatus.Timeout, source, "cancelled"),
            _ => Value.Failed<T>(source, ex.GetType().Name),
        };
    }

    /// <summary>Runs a read. A null result means "the platform has no such thing" (Unsupported), an exception maps to Denied/Error.</summary>
    public static Value<T> Of<T>(string source, Func<T?> read, string? absentDetail = null)
        where T : class
    {
        try
        {
            var value = read();
            return value is null ? Value.Unsupported<T>(source, absentDetail ?? "not reported") : Value.Ok(value, source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FromException<T>(ex, source);
        }
    }

    public static Value<T> OfStruct<T>(string source, Func<T?> read, string? absentDetail = null)
        where T : struct
    {
        try
        {
            var value = read();
            return value is null ? Value.Unsupported<T>(source, absentDetail ?? "not reported") : Value.Ok(value.Value, source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FromException<T>(ex, source);
        }
    }

    public static Value<string> Text(string source, string? text, string? absentDetail = null) =>
        string.IsNullOrWhiteSpace(text)
            ? Value.Unsupported<string>(source, absentDetail ?? "not reported")
            : Value.Ok(text.Trim(), source);

    /// <summary>Values firmware vendors leave as template text. They are real SMBIOS strings but carry no information.</summary>
    public static Value<string> FirmwareText(string source, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Value.Unsupported<string>(source, "empty in firmware");
        }

        var trimmed = text.Trim();
        return IsPlaceholder(trimmed)
            ? Value.Unsupported<string>(source, "firmware left a template string")
            : Value.Ok(trimmed, source);
    }

    public static bool IsPlaceholder(string text)
    {
        string[] known =
        [
            "To Be Filled By O.E.M.", "To be filled by O.E.M.", "Default string", "Default String", "System Product Name",
            "System manufacturer", "System Manufacturer", "System Version", "System Serial Number", "Not Specified",
            "None", "N/A", "Unknown", "Base Board Serial Number", "Chassis Serial Number", "Chassis Manufacturer",
            "Chassis Version", "Type1ProductConfigId", "O.E.M.", "OEM", "Not Applicable", "0", "00000000",
        ];
        foreach (var k in known)
        {
            if (string.Equals(text, k, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
