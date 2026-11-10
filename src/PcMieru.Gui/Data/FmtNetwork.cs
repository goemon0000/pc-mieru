using System.Globalization;

namespace PcMieru.Gui;

/// <summary>
/// How a network rate is written (SPEC 5.1 "ネットワークの単位"). Two things are chosen: bits or bytes, and whether the
/// prefix follows the size of the number or stays at mega. Network figures are decimal (1 kbps = 1000 bps) in every
/// mode, as the link speed a router or provider quotes is; the 1024-based disk and memory figures are not touched.
/// </summary>
internal enum NetworkUnit
{
    /// <summary>Bits per second, the prefix follows the size: bps, kbps, Mbps, Gbps. The default.</summary>
    AutoBits,

    /// <summary>Always megabits per second.</summary>
    FixedMegabits,

    /// <summary>Bytes per second, the prefix follows the size: B/s, kB/s, MB/s, GB/s.</summary>
    AutoBytes,

    /// <summary>Always megabytes per second.</summary>
    FixedMegabytes,
}

internal static class NetworkUnits
{
    public static readonly NetworkUnit[] All = [NetworkUnit.AutoBits, NetworkUnit.FixedMegabits, NetworkUnit.AutoBytes, NetworkUnit.FixedMegabytes];

    /// <summary>The key of the text that names a choice ("Auto (bps)", "MB/s (fixed)").</summary>
    public static string LabelKey(NetworkUnit unit) => "gui.netunit." + unit.ToString().ToLowerInvariant();

    public static string Label(NetworkUnit unit) => Strings.Get(LabelKey(unit));

    public static bool IsBits(NetworkUnit unit) => unit is NetworkUnit.AutoBits or NetworkUnit.FixedMegabits;

    /// <summary>
    /// How many bytes per second the value 1 of the unit's base is: a bit is an eighth of a byte. A graph axis rounds its
    /// maximum in these steps, so a bits axis reads "1 Mbps / 500 kbps" and not "1.6 Mbps / 800 kbps".
    /// </summary>
    public static double AxisStep(NetworkUnit unit) => IsBits(unit) ? 0.125 : 1.0;
}

internal static partial class Fmt
{
    private static readonly string[] BitPrefixes = ["bps", "kbps", "Mbps", "Gbps", "Tbps"];
    private static readonly string[] BytePrefixes = ["B/s", "kB/s", "MB/s", "GB/s", "TB/s"];

    /// <summary>
    /// A rate given in bytes per second, in the chosen unit. A negative or non-number rate counts as zero. Automatic units
    /// keep three significant digits ("1.23 Mbps", "12.3 Mbps", "123 Mbps"), so a column of them keeps one width; a value that
    /// would round up to 1000 moves to the next prefix. Fixed units keep two decimals so the points line up in a column, and a
    /// rate too small for that says so ("&lt;0.01 Mbps") instead of showing a zero for traffic that exists.
    /// </summary>
    public static string NetRate(double bytesPerSecond, NetworkUnit unit)
    {
        var rate = double.IsNaN(bytesPerSecond) || bytesPerSecond < 0 ? 0 : bytesPerSecond;
        var bits = NetworkUnits.IsBits(unit);
        var value = bits ? rate * 8 : rate;
        var names = bits ? BitPrefixes : BytePrefixes;
        if (unit is NetworkUnit.FixedMegabits or NetworkUnit.FixedMegabytes)
        {
            return FixedText(value / 1_000_000, names[2]);
        }

        var step = 0;
        var scaled = value;
        var decimals = 0;
        while (true)
        {
            decimals = step == 0 ? 0 : scaled < 10 ? 2 : scaled < 100 ? 1 : 0;
            var rounded = Math.Round(scaled, decimals, MidpointRounding.AwayFromZero);
            if (rounded >= 1000 && step < names.Length - 1)
            {
                scaled /= 1000;
                step++;
                continue;
            }

            // 9.996 rounds to 10.00: one digit too many for the width, so it is written with one decimal less.
            if (decimals == 2 && rounded >= 10)
            {
                decimals = 1;
            }
            else if (decimals == 1 && rounded >= 100)
            {
                decimals = 0;
            }

            return rounded.ToString("F" + decimals, CultureInfo.CurrentCulture) + " " + names[step];
        }
    }

    private static string FixedText(double mega, string name)
    {
        var rounded = Math.Round(mega, 2, MidpointRounding.AwayFromZero);
        if (rounded == 0 && mega > 0)
        {
            return "<" + 0.01.ToString("F2", CultureInfo.CurrentCulture) + " " + name;
        }

        return rounded.ToString("N2", CultureInfo.CurrentCulture) + " " + name;
    }

    public static string NetRate(Value<double> value, NetworkUnit unit) =>
        value.TryGet(out var v) ? NetRate(v, unit) : Dash;

    /// <summary>Send plus receive. A dash only when neither side was collected.</summary>
    public static string NetRate(Value<double> send, Value<double> receive, NetworkUnit unit)
    {
        var haveSend = send.TryGet(out var s);
        var haveReceive = receive.TryGet(out var r);
        return haveSend || haveReceive ? NetRate((haveSend ? s : 0) + (haveReceive ? r : 0), unit) : Dash;
    }
}
