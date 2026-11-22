// Provenance: PartsLife.Checks.Build.BuildConfig and BuildPart (pl-cloud 25e4571, src/PartsLife.Checks/Build/BuildConfig.cs), MIT, same owner.
// The part record, the category list with its order and "a category that cannot be read stays empty" are the old ones; the comments are in
// English, and what only the collector and the code builder use is internal (DESIGN-A 1: a small public surface).
namespace PcMieru.Checks.Build;

/// <summary>
/// One kind of part (how many of the same kind there are is <see cref="Q"/>). It maps one to one onto an element of the configuration code.
/// **A model name and numbers only.** There is no place for a serial number, a drive letter or the name of the PC.
/// </summary>
/// <param name="Name">The model name as the OS reports it (<c>n</c>).</param>
/// <param name="Cap">Capacity in GB (<c>cap</c>): per module for memory, decimal GB for storage.</param>
/// <param name="Speed">The memory speed in MT/s (<c>spd</c>).</param>
/// <param name="Type">The memory kind (DDR4 ...) or the storage interface (nvme / sata) (<c>t</c>).</param>
/// <param name="Mfr">The maker (<c>mfr</c>).</param>
/// <param name="Vram">The GPU memory in GB (<c>vram</c>).</param>
/// <param name="Q">How many (<c>q</c>). Not written when it is 1.</param>
public sealed record BuildPart(string Name, int? Cap = null, int? Speed = null, string? Type = null,
                               string? Mfr = null, int? Vram = null, int Q = 1)
{
    /// <summary>The key of "the same part": everything but the count.</summary>
    internal BuildPart Key => this with { Q = 1 };
}

/// <summary>
/// The parts of one machine, a list per category. **A category that could not be read stays empty** (it is never an error).
/// </summary>
public sealed class BuildConfig
{
    /// <summary>
    /// The categories this app can read, in the order the code writes them. Only names the receiver (the /my-config page of
    /// web-autoai.com) knows are used.
    /// </summary>
    /// <remarks>
    /// A read-only wrapper, not the array: a cast back to <c>string[]</c> fails, and the <c>IList&lt;string&gt;</c> view throws on a write,
    /// so nobody can reorder the categories (the order is the order of the code).
    /// </remarks>
    public static readonly IReadOnlyList<string> Categories = Array.AsReadOnly(new[]
    {
        "cpu", "gpu", "motherboard", "ram", "ssd", "hdd", "monitor", "optical-drive",
    });

    private static readonly IReadOnlyList<BuildPart> NoParts = Array.AsReadOnly(Array.Empty<BuildPart>());

    private readonly Dictionary<string, List<BuildPart>> _parts = new();

    /// <summary>The parts of a category (empty when there are none, or it was not read). A read-only view: it cannot be cast back to a list and changed.</summary>
    public IReadOnlyList<BuildPart> this[string category] =>
        _parts.TryGetValue(category, out var l) ? l.AsReadOnly() : NoParts;

    internal void Set(string category, IEnumerable<BuildPart> parts)
    {
        if (!Categories.Contains(category)) throw new ArgumentException($"Unknown category: {category}", nameof(category));
        _parts[category] = parts.ToList();
    }

    internal void Remove(string category) => _parts.Remove(category);

    /// <summary>The categories that have parts, in writing order.</summary>
    public IEnumerable<string> NonEmpty => Categories.Where(c => this[c].Count > 0);

    /// <summary>The number of parts in a category (the <c>q</c> values added up).</summary>
    public int Count(string category) => this[category].Sum(p => p.Q);

    public bool IsEmpty => !NonEmpty.Any();

    internal BuildConfig Clone()
    {
        var c = new BuildConfig();
        foreach (var (k, v) in _parts) c._parts[k] = v.ToList();
        return c;
    }
}
