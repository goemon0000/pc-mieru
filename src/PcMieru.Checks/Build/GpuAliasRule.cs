// Provenance: PartsLife.Hardware.GpuAliasRule (pl-cloud 25e4571, src/Shared/GpuAliasRule.cs), MIT, same owner. The rule is the old one, line for
// line; the namespace, the name of the class and the comment are new. It is its own copy on purpose: the rule decides what the configuration
// code says about the graphics cards, and it must not change when a screen elsewhere changes its mind about what an alias is.
namespace PcMieru.Checks.Build;

/// <summary>
/// How to tell the alias of a graphics card from a card. A virtual display driver (VR streaming and the like) can make DXGI show one card a
/// second time under another LUID, and that one has no PCI address. **Of the adapters that have no PCI address, one whose dedicated memory is
/// the same as an adapter that has one, and whose name is the same (or is empty), is an alias.** D3DKMT gives no name for an alias, so an
/// empty name counts too. Two cards of the same model really plugged in both have an address, so neither is taken away.
/// </summary>
internal static class GpuAliasRule
{
    public static List<T> WithoutAliases<T>(IReadOnlyList<T> list, Func<T, string> name, Func<T, ulong> dedicatedBytes,
                                            Func<T, bool> hasPciAddress)
    {
        var result = new List<T>(list.Count);
        foreach (var a in list)
        {
            if (!IsAlias(a, list, name, dedicatedBytes, hasPciAddress))
            {
                result.Add(a);
            }
        }

        return result;
    }

    public static bool IsAlias<T>(T a, IReadOnlyList<T> all, Func<T, string> name, Func<T, ulong> dedicatedBytes,
                                  Func<T, bool> hasPciAddress)
    {
        if (hasPciAddress(a))
        {
            return false;
        }

        string n = name(a);
        ulong v = dedicatedBytes(a);
        foreach (var b in all)
        {
            if (hasPciAddress(b) && dedicatedBytes(b) == v && (n.Length == 0 || name(b) == n))
            {
                return true;
            }
        }

        return false;
    }
}
