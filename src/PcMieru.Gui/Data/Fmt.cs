using System.Globalization;

namespace PcMieru.Gui;

/// <summary>Display formatting. A value that was not collected is a dash, never a zero.</summary>
internal static partial class Fmt
{
    public const string Dash = "-";

    /// <summary>Windows 10 has no efficiency mode by that name; it is shown as "low QoS" (SPEC 5.2). Settable so a test can show both wordings.</summary>
    internal static bool IsWindows10 { get; set; } = Environment.OSVersion.Version.Build < 22000;

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

    /// <summary>
    /// The size in megabytes whatever its size, with the digit groups of the language ("6,512 MB"): a column of these can be
    /// compared by eye, which one that switches between MB and GB cannot. One decimal below 100 MB, none above.
    /// </summary>
    public static string Megabytes(long bytes)
    {
        var megabytes = Math.Max(bytes, 0) / (1024.0 * 1024.0);
        return megabytes == 0
            ? "0 MB"
            : megabytes.ToString(megabytes >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture) + " MB";
    }

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

    /// <summary>Why a value is missing, for the tooltip of a grey dash: the status in words, then the detail and the source.</summary>
    public static string MissingTip<T>(Value<T> value)
    {
        var text = Strings.Format("gui.common.na.tip", new Dictionary<string, string> { ["status"] = Strings.Status(value.Status) });
        if (!string.IsNullOrWhiteSpace(value.Detail))
        {
            text += " - " + value.Detail;
        }

        return string.IsNullOrWhiteSpace(value.Source)
            ? text
            : text + Environment.NewLine + Strings.Format("gui.common.na.source", new Dictionary<string, string> { ["source"] = value.Source });
    }

    /// <summary>The text of a value, or a dash with the tooltip of its status when it is missing.</summary>
    public static string Text<T>(Value<T> value, Func<T, string> format) => value.TryGet(out var v) ? format(v) : Dash;

    public static string Started(ProcessKey key) =>
        key.CreateTime > 0 ? key.StartedAtUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) : Dash;

    /// <summary>The words for an operation code ("Denied" becomes a sentence a beginner can act on).</summary>
    public static string OperationText(PcMieru.Wire.OperationCode code) => Strings.Get("gui.op.code." + code);

    /// <summary>"target: result" with the message in brackets when there is one.</summary>
    public static string OperationLine(string target, OperationResult result) =>
        string.IsNullOrWhiteSpace(result.Message)
            ? Strings.Format("gui.op.result", Dict(("target", target), ("result", OperationText(result.Code))))
            : Strings.Format("gui.op.result.detail", Dict(("target", target), ("result", OperationText(result.Code)), ("message", result.Message)));

    public static string Int(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Args(params (string Name, string Value)[] values) => string.Join(", ", values.Select(v => v.Name + "=" + v.Value));

    public static Dictionary<string, string> Dict(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
}
