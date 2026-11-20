using System.Text.Json.Serialization;

namespace PcMieru.Checks;

/// <summary>
/// How many items carry each verdict. Five keys, Info included, every key always present (DESIGN-A §3).
/// Always recomputable from the items: <see cref="From"/>. A stored result whose counts differ is rejected.
/// Only the five positional values are JSON (the history entry uses this type as it is); the helpers below are not.
/// </summary>
public sealed record CheckCounts(int Ok, int Caution, int Check, int Unavailable, int Info)
{
    public static CheckCounts Zero { get; } = new(0, 0, 0, 0, 0);

    [JsonIgnore]
    public int Total => Ok + Caution + Check + Unavailable + Info;

    public static CheckCounts From(IEnumerable<CheckItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int ok = 0, caution = 0, check = 0, unavailable = 0, info = 0;
        foreach (var item in items)
        {
            switch (item.Verdict)
            {
                case ItemVerdict.Ok: ok++; break;
                case ItemVerdict.Caution: caution++; break;
                case ItemVerdict.Check: check++; break;
                case ItemVerdict.Unavailable: unavailable++; break;
                case ItemVerdict.Info: info++; break;
                default: throw new ArgumentOutOfRangeException(nameof(items), item.Verdict, "Unknown item verdict.");
            }
        }

        return new CheckCounts(ok, caution, check, unavailable, info);
    }

    [JsonIgnore]
    public bool IsNonNegative => Ok >= 0 && Caution >= 0 && Check >= 0 && Unavailable >= 0 && Info >= 0;
}
