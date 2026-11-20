// Provenance: the provider names, the level sets and the Kernel-Power event id are those of the owner's parts-life EventCounts
// (src/PartsLife.Checks/SystemInfo/EventCounts.cs), MIT, same owner. The XPath is built from these fixed tables only (no user text).

using System.Globalization;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// The event questions the system check asks (DESIGN-A §6.1, §14.1): one entry per item id, each a fixed provider set, a fixed level set
/// and, for the unexpected-restart count, a fixed event id.
/// </summary>
internal enum EventCountKind
{
    /// <summary>system.eventlog.whea7d: WHEA-Logger, critical and error.</summary>
    Whea,

    /// <summary>system.eventlog.whea_warning7d: WHEA-Logger, warning.</summary>
    WheaWarning,

    /// <summary>system.eventlog.disk7d</summary>
    Disk,

    /// <summary>system.eventlog.disk_warning7d</summary>
    DiskWarning,

    /// <summary>system.eventlog.ntfs7d: both provider names of NTFS.</summary>
    Ntfs,

    /// <summary>system.eventlog.ntfs_warning7d</summary>
    NtfsWarning,

    /// <summary>system.eventlog.stornvme7d</summary>
    Stornvme,

    /// <summary>system.eventlog.stornvme_warning7d</summary>
    StornvmeWarning,

    /// <summary>system.eventlog.volmgr7d</summary>
    Volmgr,

    /// <summary>system.eventlog.volmgr_warning7d</summary>
    VolmgrWarning,

    /// <summary>system.eventlog.kp41_7d: Kernel-Power event 41 (the machine restarted without a clean shutdown), any level.</summary>
    KernelPower41,
}

/// <summary>
/// One fixed count of the System log over a window of 7 days. The window start is given by the caller and is the same for every query
/// of a run, so the counts of one run describe the same 7 days. The XPath is built from the fixed tables below plus that one
/// timestamp, rendered in a fixed culture-free format: there is no way to put a string of the user, of the machine or of the log into it.
/// </summary>
internal sealed class EventCountQuery
{
    /// <summary>The length of the window (DESIGN-A §14.1: the last 7 days).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>Every count is of the System log.</summary>
    public const string SystemChannel = "System";

    private const string WheaProvider = "Microsoft-Windows-WHEA-Logger";

    private static readonly int[] CriticalAndError = [1, 2];
    private static readonly int[] Warning = [3];
    private static readonly int[] AnyLevel = [1, 2, 3, 4];

    private EventCountQuery(EventCountKind kind, string itemId, string[] providers, int[] levels, int? eventId, DateTime windowStartUtc)
    {
        Kind = kind;
        ItemId = itemId;
        WindowStartUtc = windowStartUtc;
        XPath = BuildXPath(providers, levels, eventId, windowStartUtc);
    }

    public EventCountKind Kind { get; }

    /// <summary>The item id of the registry (CheckItemRegistry) this count is the value of.</summary>
    public string ItemId { get; }

    /// <summary>The start of the 7-day window, in UTC.</summary>
    public DateTime WindowStartUtc { get; }

    public string Channel => SystemChannel;

    public string XPath { get; }

    /// <summary>The 11 kinds, in the order of the enum.</summary>
    public static IReadOnlyList<EventCountKind> AllKinds { get; } = Enum.GetValues<EventCountKind>();

    /// <summary>The query of <paramref name="kind"/> over the 7 days that start at <paramref name="windowStartUtc"/>.</summary>
    public static EventCountQuery Create(EventCountKind kind, DateTime windowStartUtc)
    {
        if (windowStartUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The start of the window must be a UTC time.", nameof(windowStartUtc));
        }

        return kind switch
        {
            EventCountKind.Whea => new(kind, "system.eventlog.whea7d", [WheaProvider], CriticalAndError, null, windowStartUtc),
            EventCountKind.WheaWarning => new(kind, "system.eventlog.whea_warning7d", [WheaProvider], Warning, null, windowStartUtc),
            EventCountKind.Disk => new(kind, "system.eventlog.disk7d", ["disk"], CriticalAndError, null, windowStartUtc),
            EventCountKind.DiskWarning => new(kind, "system.eventlog.disk_warning7d", ["disk"], Warning, null, windowStartUtc),
            EventCountKind.Ntfs => new(kind, "system.eventlog.ntfs7d", ["Ntfs", "Microsoft-Windows-Ntfs"], CriticalAndError, null, windowStartUtc),
            EventCountKind.NtfsWarning => new(kind, "system.eventlog.ntfs_warning7d", ["Ntfs", "Microsoft-Windows-Ntfs"], Warning, null, windowStartUtc),
            EventCountKind.Stornvme => new(kind, "system.eventlog.stornvme7d", ["stornvme"], CriticalAndError, null, windowStartUtc),
            EventCountKind.StornvmeWarning => new(kind, "system.eventlog.stornvme_warning7d", ["stornvme"], Warning, null, windowStartUtc),
            EventCountKind.Volmgr => new(kind, "system.eventlog.volmgr7d", ["volmgr"], CriticalAndError, null, windowStartUtc),
            EventCountKind.VolmgrWarning => new(kind, "system.eventlog.volmgr_warning7d", ["volmgr"], Warning, null, windowStartUtc),
            EventCountKind.KernelPower41 => new(kind, "system.eventlog.kp41_7d", ["Microsoft-Windows-Kernel-Power"], AnyLevel, 41, windowStartUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>The start of the 7-day window that ends at <paramref name="nowUtc"/>.</summary>
    public static DateTime WindowStartFor(DateTime nowUtc) => nowUtc.ToUniversalTime() - Window;

    // Provider, levels, event id, then the time: the order of the old query. Only fixed text and one timestamp go in.
    private static string BuildXPath(string[] providers, int[] levels, int? eventId, DateTime windowStartUtc)
    {
        var provider = string.Join(" or ", providers.Select(name => "@Name='" + name + "'"));
        var level = string.Join(" or ", levels.Select(value => "Level=" + value.ToString(CultureInfo.InvariantCulture)));
        var id = eventId is { } e ? " and EventID=" + e.ToString(CultureInfo.InvariantCulture) : "";
        var start = windowStartUtc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);
        return $"*[System[Provider[{provider}] and ({level}){id} and TimeCreated[@SystemTime>='{start}']]]";
    }
}
