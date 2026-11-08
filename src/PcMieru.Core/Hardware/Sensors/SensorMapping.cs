// The vendor result codes and the AMD name pairing follow goemon0000/parts-life (MIT) src/PartsLife.Checks/Gpu/Nvml.cs and Recording/Sources.Adlx.cs.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PcMieru.Core.Hardware.Native;

// SensorHost reaches the DXGI helpers (internal) to give each NVML/ADLX GPU the same LUID text the hardware list uses.
[assembly: InternalsVisibleTo("PCMieru.SensorHost")]

namespace PcMieru.Core.Hardware.Sensors;

internal enum VendorApi
{
    Nvml,
    Adlx,
}

/// <summary>
/// Turns a vendor library's number and result code into a <see cref="Value{T}"/>. The rule is the same everywhere: a reading that did not
/// happen is Unsupported/Denied/Timeout/Error with the reason, an impossible number is an Error, and nothing is replaced by zero.
/// Lives in Core (not in SensorHost) so it can be tested; the vendor DLLs themselves are only ever loaded inside SensorHost.
/// </summary>
internal static class SensorMapping
{
    /// <summary>The driver does not export the function at all (older driver). Not a vendor code.</summary>
    public const int FunctionMissing = -1;

    // NVML_ERROR_*
    private const int NvmlNotSupported = 3;
    private const int NvmlNoPermission = 4;
    private const int NvmlNotFound = 6;
    private const int NvmlTimeout = 10;
    private const int NvmlGpuLost = 15;
    private const int NvmlFunctionNotFound = 13;
    private const int NvmlFreqNotSupported = 24;

    // ADLX_RESULT
    private const int AdlxNotFound = 9;
    private const int AdlxNotSupported = 12;
    private const int AdlxGpuInactive = 14;

    public static ValueStatus StatusOf(VendorApi api, int code) => code switch
    {
        0 => ValueStatus.Ok,
        FunctionMissing => ValueStatus.Unsupported,
        _ when api == VendorApi.Nvml => code switch
        {
            NvmlNotSupported or NvmlNotFound or NvmlFunctionNotFound or NvmlFreqNotSupported => ValueStatus.Unsupported,
            NvmlNoPermission => ValueStatus.Denied,
            NvmlTimeout => ValueStatus.Timeout,
            NvmlGpuLost => ValueStatus.Gone,
            _ => ValueStatus.Error,
        },
        _ => code switch
        {
            AdlxNotSupported or AdlxNotFound or AdlxGpuInactive => ValueStatus.Unsupported,
            _ => ValueStatus.Error,
        },
    };

    public static string Describe(VendorApi api, int code)
    {
        if (code == FunctionMissing)
        {
            return "this driver does not provide the function";
        }

        var name = api == VendorApi.Nvml
            ? code switch
            {
                NvmlNotSupported => "not supported by this GPU",
                NvmlNoPermission => "no permission",
                NvmlNotFound => "not found",
                NvmlTimeout => "timed out",
                NvmlGpuLost => "the GPU fell off the bus",
                NvmlFunctionNotFound => "function not found",
                NvmlFreqNotSupported => "not supported by this GPU",
                _ => null,
            }
            : code switch
            {
                AdlxNotSupported => "not supported by this GPU",
                AdlxNotFound => "not found",
                AdlxGpuInactive => "the GPU is inactive",
                _ => null,
            };
        var vendor = api == VendorApi.Nvml ? "NVML" : "ADLX";
        return name is null ? $"{vendor} result {code}" : $"{vendor} result {code} ({name})";
    }

    /// <summary>A measured number. Out-of-range and non-finite values are reported as errors instead of being shown.</summary>
    public static Value<double> Number(VendorApi api, double number, int code, string source, double min, double max)
    {
        if (code != 0)
        {
            return Value.Missing<double>(StatusOf(api, code), source, Describe(api, code));
        }

        if (!double.IsFinite(number))
        {
            return Value.Failed<double>(source, "the driver returned a value that is not a number");
        }

        return number < min || number > max
            ? Value.Failed<double>(source, $"the driver returned an implausible value ({number.ToString("0.###", CultureInfo.InvariantCulture)})")
            : Value.Ok(number, source);
    }

    /// <summary>A measured number with a note on what it is (for example "enforced limit, may differ from the board default").</summary>
    public static Value<double> NumberNote(VendorApi api, double number, int code, string source, double min, double max, string note)
    {
        var v = Number(api, number, code, source, min, max);
        return v.Status == ValueStatus.Ok ? Probe.Note(v.Data, source, note) : v;
    }

    public static Value<int> Integer(VendorApi api, double number, int code, string source, int min, int max)
    {
        var v = Number(api, number, code, source, min, max);
        return v.Status == ValueStatus.Ok ? Value.Ok((int)Math.Round(v.Data), source) : Value.Missing<int>(v.Status, v.Source, v.Detail);
    }

    public static Value<long> Bytes(VendorApi api, double number, int code, string source)
    {
        var v = Number(api, number, code, source, 0, 1e15);
        return v.Status == ValueStatus.Ok ? Value.Ok((long)v.Data, source) : Value.Missing<long>(v.Status, v.Source, v.Detail);
    }

    public static Value<string> Text(VendorApi api, string? text, int code, string source)
    {
        if (code != 0)
        {
            return Value.Missing<string>(StatusOf(api, code), source, Describe(api, code));
        }

        var clean = text?.Trim();
        return string.IsNullOrEmpty(clean) ? Value.Unsupported<string>(source, "the driver returned an empty text") : Value.Ok(clean, source);
    }

    /// <summary>The reason a whole reading is missing, put on every field (used when the vendor library is absent or a GPU read failed as a whole).</summary>
    public static Value<T> Absent<T>(ValueStatus status, string source, string detail) => Value.Missing<T>(status, source, detail);

    /// <summary>
    /// Matches vendor-library GPUs to display adapters by name. Names are normalised (case, punctuation, a leading "AMD", the (TM)/(R) marks).
    /// A pair is made only when exactly one GPU on each side has that name, or when there is exactly one of each; if two GPUs cannot be told
    /// apart nothing is paired (<paramref name="ambiguous"/>), because showing another card's temperature is worse than showing none.
    /// </summary>
    public static Dictionary<int, int> PairByName(IReadOnlyList<string> adapterNames, IReadOnlyList<string> vendorNames, out bool ambiguous)
    {
        ambiguous = false;
        var result = new Dictionary<int, int>();
        for (var a = 0; a < adapterNames.Count; a++)
        {
            var n = NormalizeName(adapterNames[a]);
            var candidates = Enumerable.Range(0, vendorNames.Count).Where(i => NormalizeName(vendorNames[i]) == n).ToList();
            if (candidates.Count == 0 && adapterNames.Count == 1 && vendorNames.Count == 1)
            {
                candidates.Add(0);
            }

            var sameNameAdapters = adapterNames.Count(x => NormalizeName(x) == n);
            if (candidates.Count == 1 && sameNameAdapters == 1)
            {
                result[a] = candidates[0];
            }
            else if (candidates.Count > 1 || (candidates.Count == 1 && sameNameAdapters > 1))
            {
                ambiguous = true;
            }
        }

        // Two adapters must never claim the same vendor GPU.
        var duplicate = result.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (duplicate.Count > 0)
        {
            ambiguous = true;
            foreach (var key in result.Where(kv => duplicate.Contains(kv.Value)).Select(kv => kv.Key).ToList())
            {
                result.Remove(key);
            }
        }

        return result;
    }

    public static string NormalizeName(string name)
    {
        var s = name.Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase);
        var t = new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return t.StartsWith("amd", StringComparison.Ordinal) ? t[3..] : t;
    }
}

/// <summary>PCI position of a GPU written the same way by SensorHost (from NVML) and read by the client (to join with the display adapter list).</summary>
internal static partial class PciBusIds
{
    /// <summary>"0000:01:00": domain, bus, device in hex, like the first three groups of an NVML bus id.</summary>
    public static string Format(uint domain, uint bus, uint device) =>
        string.Create(CultureInfo.InvariantCulture, $"{domain:x4}:{bus:x2}:{device:x2}");

    public static bool TryParse(string? text, out uint bus, out uint device)
    {
        bus = device = 0;
        if (text is null)
        {
            return false;
        }

        var m = BusId().Match(text);
        if (!m.Success)
        {
            return false;
        }

        bus = uint.Parse(m.Groups["bus"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        device = uint.Parse(m.Groups["dev"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Reads the "bus 1, device 0, function 0" text the GPU list uses for its location.</summary>
    public static bool TryParseLocation(string? text, out uint bus, out uint device)
    {
        bus = device = 0;
        if (text is null)
        {
            return false;
        }

        var m = Location().Match(text);
        if (!m.Success)
        {
            return false;
        }

        bus = uint.Parse(m.Groups["bus"].Value, CultureInfo.InvariantCulture);
        device = uint.Parse(m.Groups["dev"].Value, CultureInfo.InvariantCulture);
        return true;
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{4}:(?<bus>[0-9a-fA-F]{2}):(?<dev>[0-9a-fA-F]{2})$")]
    private static partial Regex BusId();

    [GeneratedRegex(@"^bus (?<bus>\d{1,10}), device (?<dev>\d{1,10})(, function \d{1,10})?$")]
    private static partial Regex Location();
}
