using System.Globalization;

namespace PcMieru.Core.Collection;

/// <summary>One physical engine: adapter LUID, physical adapter index and engine index (SPEC 4: the engine's identity).</summary>
internal readonly record struct GpuEngineKey(ulong Luid, uint Phys, uint Eng);

/// <summary>
/// Parses PDH GPU instance names as tokens, not by position:
/// <c>pid_1234_luid_0x00000000_0x000166CA_phys_0_eng_3_engtype_Copy</c> (GPU Engine) and
/// <c>luid_0x00000000_0x000166CA_phys_0</c> (GPU Adapter Memory). A name with a key it does not know, a missing
/// key or a malformed number is rejected as a whole, so an unknown future format is "not collected" and never a wrong number.
/// </summary>
internal static class GpuCounterName
{
    /// <summary>The same text DXGI/SensorHost use for an adapter: <c>luid_0x{high:x8}_0x{low:x8}</c>.</summary>
    public static string AdapterId(ulong luid) => string.Create(CultureInfo.InvariantCulture, $"luid_0x{(uint)(luid >> 32):x8}_0x{(uint)luid:x8}");

    public static bool TryParseEngine(ReadOnlySpan<char> name, out uint pid, out GpuEngineKey key, out ReadOnlySpan<char> engineType)
    {
        pid = 0;
        key = default;
        engineType = default;
        var havePid = false;
        var haveLuid = false;
        var havePhys = false;
        var haveEng = false;
        var haveType = false;
        ulong luid = 0;
        uint phys = 0;
        uint eng = 0;

        while (!name.IsEmpty)
        {
            var field = NextToken(ref name);
            switch (field)
            {
                case "pid":
                    if (havePid || !TryDecimal(NextToken(ref name), out pid))
                    {
                        return false;
                    }

                    havePid = true;
                    break;
                case "luid":
                    if (haveLuid || !TryLuid(NextToken(ref name), NextToken(ref name), out luid))
                    {
                        return false;
                    }

                    haveLuid = true;
                    break;
                case "phys":
                    if (havePhys || !TryDecimal(NextToken(ref name), out phys))
                    {
                        return false;
                    }

                    havePhys = true;
                    break;
                case "eng":
                    if (haveEng || !TryDecimal(NextToken(ref name), out eng))
                    {
                        return false;
                    }

                    haveEng = true;
                    break;
                case "engtype":
                    // The type is the rest of the name: some types contain underscores.
                    if (haveType || name.IsEmpty)
                    {
                        return false;
                    }

                    engineType = name;
                    name = default;
                    haveType = true;
                    break;
                default:
                    return false;
            }
        }

        if (!(havePid && haveLuid && havePhys && haveEng && haveType))
        {
            return false;
        }

        key = new GpuEngineKey(luid, phys, eng);
        return true;
    }

    /// <summary>The adapter memory instance: <c>luid_0x..._0x..._phys_N</c>. The physical index is returned so linked adapters can be summed.</summary>
    public static bool TryParseAdapter(ReadOnlySpan<char> name, out ulong luid, out uint phys)
    {
        luid = 0;
        phys = 0;
        var haveLuid = false;
        var havePhys = false;
        while (!name.IsEmpty)
        {
            var field = NextToken(ref name);
            switch (field)
            {
                case "luid":
                    if (haveLuid || !TryLuid(NextToken(ref name), NextToken(ref name), out luid))
                    {
                        return false;
                    }

                    haveLuid = true;
                    break;
                case "phys":
                    if (havePhys || !TryDecimal(NextToken(ref name), out phys))
                    {
                        return false;
                    }

                    havePhys = true;
                    break;
                default:
                    return false;
            }
        }

        return haveLuid && havePhys;
    }

    private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> rest)
    {
        var cut = rest.IndexOf('_');
        ReadOnlySpan<char> token;
        if (cut < 0)
        {
            token = rest;
            rest = default;
        }
        else
        {
            token = rest[..cut];
            rest = rest[(cut + 1)..];
        }

        return token;
    }

    private static bool TryDecimal(ReadOnlySpan<char> text, out uint value) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryLuid(ReadOnlySpan<char> high, ReadOnlySpan<char> low, out ulong luid)
    {
        luid = 0;
        if (!TryHex(high, out var h) || !TryHex(low, out var l))
        {
            return false;
        }

        luid = ((ulong)h << 32) | l;
        return true;
    }

    private static bool TryHex(ReadOnlySpan<char> text, out uint value)
    {
        value = 0;
        if (text.Length < 3 || text[0] != '0' || (text[1] != 'x' && text[1] != 'X'))
        {
            return false;
        }

        return uint.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
