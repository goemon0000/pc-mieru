using System.Globalization;
using PcMieru.Checks.SystemInfo;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>
/// The two real readers on this machine: the System-log counts (all eleven fixed queries, so every XPath is shown to be accepted by Windows)
/// and one WMI question, each with a 5 s deadline. They only read. Run on purpose, not by default (<c>--filter Category=RealMachine</c>).
/// What it prints is counts and reasons: no event, no row value, no machine name.
/// </summary>
public sealed class SystemInfoRealMachineTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task The_real_event_log_and_the_real_wmi_answer_inside_a_5_second_deadline_or_say_why_not()
    {
        var clock = TimeProvider.System;
        var counter = new WevtEventCounter();
        var windowStart = EventCountQuery.WindowStartFor(clock.GetUtcNow().UtcDateTime);

        // Events: every fixed query once, each with its own 5 s deadline. A count (possibly 0) or a reason; never an error from a bad XPath.
        foreach (var kind in EventCountQuery.AllKinds)
        {
            var query = EventCountQuery.Create(kind, windowStart);
            var started = clock.GetTimestamp();
            var reading = await counter.CountAsync(query, QueryDeadline.After(clock, Budget), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            var took = clock.GetElapsedTime(started);

            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"event {query.ItemId}: {(reading.HasValue ? "count " + reading.Value : "no count, reason " + reading.Reason)} in {took.TotalMilliseconds:0} ms"));
            Assert.True(took < Budget + TimeSpan.FromSeconds(2), $"{query.ItemId} took {took}");
            Assert.True(reading.HasValue, $"{query.ItemId}: reason {reading.Reason}");
            Assert.InRange(reading.Value, 0, WevtEventCounter.DefaultMaxCount);
        }

        // A count that is not zero: the same real calls with a broad fixed query (every System event of the last day), so that several batches
        // of real handles are enumerated, closed and counted, and the end is still reached inside the deadline.
        var broad = new WevtEventCounter(
            new WholeWindowApi(new NativeEventLogApi(), $"*[System[TimeCreated[@SystemTime>='{clock.GetUtcNow().UtcDateTime.AddDays(-1):yyyy-MM-ddTHH:mm:ss.fffZ}']]]"),
            QueryLanes.EventLog);
        var broadReading = await broad.CountAsync(EventCountQuery.Create(EventCountKind.Disk, windowStart), QueryDeadline.After(clock, Budget), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"event broad last day: {(broadReading.HasValue ? "count " + broadReading.Value : "no count, reason " + broadReading.Reason)}"));
        Assert.True(broadReading.HasValue, $"broad query: reason {broadReading.Reason}");
        Assert.True(broadReading.Value > WevtEventCounter.BatchSize, "a day of the System log should be more than one batch");

        // WMI: one read of the operating system. The build number is mapped but never printed.
        var wmi = new ManagementWmiReader();
        var question = new WmiQuery<int>(
            WmiScopes.Cimv2,
            "SELECT BuildNumber FROM Win32_OperatingSystem",
            row => int.Parse(Convert.ToString(row["BuildNumber"], CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture));
        var wmiStarted = clock.GetTimestamp();
        var rows = await wmi.ReadAsync(question, QueryDeadline.After(clock, Budget), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var wmiTook = clock.GetElapsedTime(wmiStarted);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wmi Win32_OperatingSystem: {(rows.HasValue ? "rows " + rows.Value.Count : "no rows, reason " + rows.Reason)} in {wmiTook.TotalMilliseconds:0} ms"));
        Assert.True(rows.HasValue, $"reason {rows.Reason}");
        Assert.Single(rows.Value);
        Assert.True(rows.Value[0] > 0);

        // Everything that was asked has returned: the lanes hold nothing.
        Assert.True(QueryTest.Eventually(() => QueryLanes.EventLog.Held == 0 && QueryLanes.Wmi.Held == 0), "a lane still holds a slot");
        output.WriteLine("lanes: event log held 0, wmi held 0");
    }

    /// <summary>
    /// Every provider name in the fixed table of <see cref="EventCountQuery"/> is a provider this machine knows. A misspelt name is a valid
    /// XPath that matches nothing, so the count would be a quiet 0 for ever; this is the only thing that would show it. Read only: the
    /// publisher metadata is opened and closed, and for a classic (pre-manifest) provider such as "disk" the registry key of the System
    /// log source is looked at.
    /// </summary>
    [Fact]
    [Trait("Category", "RealMachine")]
    public void Every_provider_name_of_the_event_table_is_registered_on_the_System_log()
    {
        var names = EventCountQuery.AllKinds
            .SelectMany(kind => EventCountQuery.Create(kind, EventCountQuery.WindowStartFor(DateTime.UtcNow)).Providers)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(7, names.Length); // WHEA-Logger, disk, Ntfs, Microsoft-Windows-Ntfs, stornvme, volmgr, Kernel-Power

        // The check itself must be able to say no: a misspelling of a real name is neither a publisher nor a classic source.
        const string Misspelt = "Microsoft-Windows-WHEA-Loger";
        Assert.False(PublisherMetadata.Opens(Misspelt) || ClassicSourceKeyExists(Misspelt), "the check cannot tell a misspelt provider from a real one");

        foreach (var name in names)
        {
            var viaMetadata = PublisherMetadata.Opens(name);
            var viaRegistry = ClassicSourceKeyExists(name);
            output.WriteLine($"provider {name}: publisher metadata {(viaMetadata ? "yes" : "no")}, classic source key {(viaRegistry ? "yes" : "no")}");
            Assert.True(viaMetadata || viaRegistry, $"the provider '{name}' is not registered on this machine: a count of it would be 0 for ever");
        }
    }

    private static bool ClassicSourceKeyExists(string name)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\System\" + name, writable: false);
        return key is not null;
    }

    /// <summary>EvtOpenPublisherMetadata and EvtClose, only to ask whether a provider name is registered.</summary>
    private static class PublisherMetadata
    {
        public static bool Opens(string provider)
        {
            var handle = EvtOpenPublisherMetadata(0, provider, null, 0, 0);
            if (handle == 0)
            {
                return false;
            }

            _ = EvtClose(handle);
            return true;
        }

        [System.Runtime.InteropServices.DllImport("wevtapi.dll", EntryPoint = "EvtOpenPublisherMetadata", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern nint EvtOpenPublisherMetadata(nint session, string publisherId, string? logFilePath, int locale, int flags);

        [System.Runtime.InteropServices.DllImport("wevtapi.dll", EntryPoint = "EvtClose")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool EvtClose(nint handle);
    }

    /// <summary>The real wevtapi, with the XPath of the query replaced by another fixed one (a test seam only: the counter itself never takes a text from outside).</summary>
    private sealed class WholeWindowApi(IEventLogApi inner, string xpath) : IEventLogApi
    {
        public int Query(string channel, string ignored, int flags, out nint resultSet) => inner.Query(channel, xpath, flags, out resultSet);

        public int Next(nint resultSet, int timeoutMs, Span<nint> events, out int returned) => inner.Next(resultSet, timeoutMs, events, out returned);

        public void Close(nint handle) => inner.Close(handle);
    }
}
