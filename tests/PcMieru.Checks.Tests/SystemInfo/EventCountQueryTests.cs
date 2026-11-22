using System.Globalization;
using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

public sealed class EventCountQueryTests
{
    private static readonly DateTime Start = new(2026, 11, 14, 3, 4, 5, 678, DateTimeKind.Utc);

    private const string Time = "TimeCreated[@SystemTime>='2026-11-14T03:04:05.678Z']";

    // The golden strings. Written out in full on purpose: they must not be built the way the code builds them.
    public static TheoryData<string, string, string> Golden => new()
    {
        { nameof(EventCountKind.Whea), "system.eventlog.whea7d",
            $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and {Time}]]" },
        { nameof(EventCountKind.WheaWarning), "system.eventlog.whea_warning7d",
            $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=3) and {Time}]]" },
        { nameof(EventCountKind.Disk), "system.eventlog.disk7d",
            $"*[System[Provider[@Name='disk'] and (Level=1 or Level=2) and {Time}]]" },
        { nameof(EventCountKind.DiskWarning), "system.eventlog.disk_warning7d",
            $"*[System[Provider[@Name='disk'] and (Level=3) and {Time}]]" },
        { nameof(EventCountKind.Ntfs), "system.eventlog.ntfs7d",
            $"*[System[Provider[@Name='Ntfs' or @Name='Microsoft-Windows-Ntfs'] and (Level=1 or Level=2) and {Time}]]" },
        { nameof(EventCountKind.NtfsWarning), "system.eventlog.ntfs_warning7d",
            $"*[System[Provider[@Name='Ntfs' or @Name='Microsoft-Windows-Ntfs'] and (Level=3) and {Time}]]" },
        { nameof(EventCountKind.Stornvme), "system.eventlog.stornvme7d",
            $"*[System[Provider[@Name='stornvme'] and (Level=1 or Level=2) and {Time}]]" },
        { nameof(EventCountKind.StornvmeWarning), "system.eventlog.stornvme_warning7d",
            $"*[System[Provider[@Name='stornvme'] and (Level=3) and {Time}]]" },
        { nameof(EventCountKind.Volmgr), "system.eventlog.volmgr7d",
            $"*[System[Provider[@Name='volmgr'] and (Level=1 or Level=2) and {Time}]]" },
        { nameof(EventCountKind.VolmgrWarning), "system.eventlog.volmgr_warning7d",
            $"*[System[Provider[@Name='volmgr'] and (Level=3) and {Time}]]" },
        { nameof(EventCountKind.KernelPower41), "system.eventlog.kp41_7d",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (Level=1 or Level=2 or Level=3 or Level=4) and EventID=41 and {Time}]]" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void The_xpath_of_every_fixed_query_is_exactly_this(string kindName, string itemId, string xpath)
    {
        var kind = Enum.Parse<EventCountKind>(kindName);
        var query = EventCountQuery.Create(kind, Start);

        Assert.Equal(xpath, query.XPath);
        Assert.Equal(itemId, query.ItemId);
        Assert.Equal("System", query.Channel);
        Assert.Equal(kind, query.Kind);
        Assert.Equal(Start, query.WindowStartUtc);
    }

    [Fact]
    public void Every_kind_has_a_golden_string_and_a_registered_item()
    {
        var golden = Golden.Select(row => Enum.Parse<EventCountKind>((string)row[0])).ToHashSet();

        Assert.Equal(11, EventCountQuery.AllKinds.Count);
        Assert.True(EventCountQuery.AllKinds.All(golden.Contains), "a kind has no golden xpath");

        foreach (var kind in EventCountQuery.AllKinds)
        {
            var itemId = EventCountQuery.Create(kind, Start).ItemId;
            Assert.True(CheckItemRegistry.IsKnown(itemId), $"{itemId} is not in the item registry");
        }

        Assert.Equal(11, EventCountQuery.AllKinds.Select(kind => EventCountQuery.Create(kind, Start).XPath).Distinct().Count());
    }

    [Fact]
    public void The_window_is_fixed_by_a_timestamp_and_not_by_the_time_the_query_runs()
    {
        var xpath = EventCountQuery.Create(EventCountKind.Disk, Start).XPath;

        Assert.DoesNotContain("timediff", xpath, StringComparison.Ordinal); // timediff(@SystemTime) is relative to the moment of evaluation
        Assert.Contains("@SystemTime>='2026-11-14T03:04:05.678Z'", xpath, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_starts_seven_days_before_now_in_utc()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.FromDays(7), EventCountQuery.Window);
        Assert.Equal(new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc), EventCountQuery.WindowStartFor(now));
    }

    [Fact]
    public void A_window_end_that_is_not_utc_is_refused()
    {
        Assert.Throws<ArgumentException>(() => EventCountQuery.WindowStartFor(new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => EventCountQuery.WindowStartFor(new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void A_start_that_is_not_utc_is_refused()
    {
        Assert.Throws<ArgumentException>(() => EventCountQuery.Create(EventCountKind.Disk, new DateTime(2026, 11, 14, 3, 4, 5, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => EventCountQuery.Create(EventCountKind.Disk, new DateTime(2026, 11, 14, 3, 4, 5, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void The_time_text_does_not_depend_on_the_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA"); // another calendar and other digits
            Assert.Equal(
                $"*[System[Provider[@Name='disk'] and (Level=1 or Level=2) and {Time}]]",
                EventCountQuery.Create(EventCountKind.Disk, Start).XPath);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Tolerate_query_errors_is_documented_as_0x1000_and_is_never_passed()
    {
        Assert.Equal(0x1, EventLogFlags.QueryChannelPath);
        Assert.Equal(0x1000, EventLogFlags.QueryTolerateQueryErrors);
        Assert.Equal(EventLogFlags.QueryChannelPath, EventLogFlags.Query);
        Assert.Equal(0, EventLogFlags.Query & EventLogFlags.QueryTolerateQueryErrors);
    }
}
