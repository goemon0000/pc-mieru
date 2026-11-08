using System.Globalization;

namespace PcMieru.Gui;

/// <summary>Display formatting. A value that was not collected is a dash, never a zero.</summary>
internal static class Fmt
{
    public const string Dash = "-";

    public static string Percent(Value<double> value) =>
        value.TryGet(out var v) ? v.ToString("0.0", CultureInfo.CurrentCulture) + " %" : Dash;

    public static string Mhz(Value<double> value) =>
        value.TryGet(out var v) ? v.ToString("0", CultureInfo.CurrentCulture) + " MHz" : Dash;

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString(CultureInfo.CurrentCulture) + " B"
            : size.ToString(size >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Bytes(Value<long> value) => value.TryGet(out var v) ? Bytes(v) : Dash;

    public static string Rate(Value<double> value) =>
        value.TryGet(out var v) ? Bytes((long)Math.Max(0, v)) + "/s" : Dash;

    /// <summary>Read plus write. A dash only when neither side was collected.</summary>
    public static string Rate(Value<double> read, Value<double> write)
    {
        var haveRead = read.TryGet(out var r);
        var haveWrite = write.TryGet(out var w);
        return haveRead || haveWrite ? Bytes((long)Math.Max(0, (haveRead ? r : 0) + (haveWrite ? w : 0))) + "/s" : Dash;
    }

    public static string Uptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return Strings.Format("gui.fmt.uptime", new Dictionary<string, string>
        {
            ["days"] = ((int)span.TotalDays).ToString(CultureInfo.CurrentCulture),
            ["time"] = span.ToString("hh':'mm':'ss", CultureInfo.InvariantCulture),
        });
    }
}
