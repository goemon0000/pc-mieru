using System.Globalization;
using PcMieru.Core;

namespace PcMieru.Cli;

/// <summary>
/// Units and rounding for the text output. A missing value never becomes 0: it is "-" in a table cell and the
/// localized status ("Access denied", "Unsupported" ...) on a labelled line. JSON output never goes through here.
/// </summary>
public static class CliFormat
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Binary units (1 KB = 1024 B), the way Windows shows sizes.</summary>
    public static string Bytes(double bytes)
    {
        if (double.IsNaN(bytes) || double.IsInfinity(bytes))
        {
            return "-";
        }

        var magnitude = Math.Abs(bytes);
        var unit = 0;
        while (magnitude >= 1024 && unit < ByteUnits.Length - 1)
        {
            magnitude /= 1024;
            unit++;
        }

        var signed = bytes < 0 ? -magnitude : magnitude;
        var text = unit == 0
            ? Math.Round(signed).ToString("0", CultureInfo.InvariantCulture)
            : signed.ToString(magnitude < 100 ? "0.0" : "0", CultureInfo.InvariantCulture);
        return text + " " + ByteUnits[unit];
    }

    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";

    public static string Percent(double percent) => CliText.Num(percent) + "%";

    public static string Mhz(double mhz) => CliText.Num(mhz, "0") + " MHz";

    /// <summary>The negotiated link speed, as the adapter reports it (not a measurement).</summary>
    public static string LinkSpeed(long bitsPerSec)
    {
        if (bitsPerSec >= 1_000_000_000)
        {
            return CliText.Num(bitsPerSec / 1e9, "0.##") + " Gbit/s";
        }

        if (bitsPerSec >= 1_000_000)
        {
            return CliText.Num(bitsPerSec / 1e6, "0.##") + " Mbit/s";
        }

        return CliText.Num(bitsPerSec / 1e3, "0.##") + " kbit/s";
    }

    public static string Duration(CliContext context, long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var days = ((long)span.TotalDays).ToString(CultureInfo.InvariantCulture);
        var hours = span.Hours.ToString(CultureInfo.InvariantCulture);
        var minutes = span.Minutes.ToString(CultureInfo.InvariantCulture);
        if (span.TotalDays >= 1)
        {
            return context.Format("cli.fmt.duration.dhm", ("d", days), ("h", hours), ("m", minutes));
        }

        return span.TotalHours >= 1
            ? context.Format("cli.fmt.duration.hm", ("h", hours), ("m", minutes))
            : context.Format("cli.fmt.duration.m", ("m", minutes));
    }

    public static string YesNo(CliContext context, bool value) => context.Text(value ? "cli.fmt.yes" : "cli.fmt.no");

    /// <summary>The formatted value, or the localized reason it is missing (for a labelled line).</summary>
    public static string OrStatus<T>(CliContext context, Value<T> value, Func<T, string> format) =>
        value.TryGet(out var data) ? format(data) : Strings.Status(value.Status, context.Language);

    /// <summary>The formatted value, or "-" (for a table cell or a part of a longer line).</summary>
    public static string OrDash<T>(Value<T> value, Func<T, string> format) =>
        value.TryGet(out var data) ? format(data) : "-";

    /// <summary>Two columns "label  value" where the label column is as wide as the widest label (display width).</summary>
    public static string LabelColumn(IReadOnlyList<(string Label, string Value)> lines, string indent = "")
    {
        var width = lines.Count == 0 ? 0 : lines.Max(l => CliText.Width(l.Label));
        var sb = new System.Text.StringBuilder();
        foreach (var (label, value) in lines)
        {
            var line = indent + CliText.PadRight(CliText.Printable(label), width) + "  " + CliText.Printable(value);
            sb.Append(line.TrimEnd()).Append('\n');
        }

        return sb.ToString();
    }
}
