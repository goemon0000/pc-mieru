// Provenance: PartsLife.Checks.Build.WindowsReaders (pl-cloud 25e4571, src/PartsLife.Checks/Build/BuildCollector.cs) and SystemCheck.ReadCpus
// (src/PartsLife.Checks/SystemInfo/SystemCheck.cs), MIT, same owner. The questions (the SELECT texts and namespaces), their order, the
// chain of fall-backs (the three memory queries, dropping the columns an old Windows does not have; DXGI and then Win32_VideoController;
// MSFT_PhysicalDisk and then Win32_DiskDrive) and the way each value is read (the old "as string" and Convert rules, so the same machine
// gives the same material) are the old ones. WHEN a fall-back is taken is not exactly the old rule (DESIGN-A 9, "differences from the
// old version"): the old code fell back on any ManagementException, also after the time was gone, and an unexpected DXGI exception left
// the GPU empty; here a fall-back follows an error, a refusal or "not supported" (an unexpected DXGI exception too), and also a lane that
// refused at once while time was left, but never a real timeout or a stop. What is new: every question goes through the WMI reader with
// a deadline of its own (DESIGN-A 6.1), and a category that is out of time, denied or broken is reported with its reason
// (BuildCategoryUnreadException), not as an empty list.
using System.Globalization;
using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Build;

/// <summary>
/// Reads the raw material of the configuration code from Windows: only reads; no serial number is asked for. **Each category has a
/// deadline of 5 s** (<see cref="SystemWmiFacts.Budget"/>), and the fall-backs of one category (three memory queries, DXGI then the video
/// controller, the storage class then the disk drive) share it. A category that is out of time, or could not be read, is a
/// <see cref="BuildCategoryUnreadException"/> with the reason (the collector turns it into an empty category that is listed as unread);
/// a category that was read and has nothing is an empty list. The caller's cancellation is thrown as it is.
/// </summary>
internal sealed class WindowsBuildReaders
{
    private const string WmiScope = @"\\.\root\wmi";

    private readonly IWmiReader _wmi;
    private readonly IGpuReader _gpu;
    private readonly TimeProvider _time;

    public WindowsBuildReaders()
        : this(new ManagementWmiReader(), new DxgiGpuReader(), TimeProvider.System)
    {
    }

    public WindowsBuildReaders(IWmiReader wmi, IGpuReader gpu, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(time);
        _wmi = wmi;
        _gpu = gpu;
        _time = time;
    }

    // =====================================================================
    // The questions (the text is a constant of the code, never built from a value of the machine)
    // =====================================================================

    internal static readonly WmiQuery<string> CpuQuery = new(
        WmiScopes.Cimv2,
        "SELECT Name FROM Win32_Processor",
        static row => (Str(row["Name"]) ?? "").Trim());

    internal static readonly WmiQuery<(string? Manufacturer, string? Product)> BoardQuery = new(
        WmiScopes.Cimv2,
        "SELECT Manufacturer, Product FROM Win32_BaseBoard",
        static row => (Str(row["Manufacturer"]), Str(row["Product"])));

    private const string MemoryBase = "PartNumber, Manufacturer, Capacity, Speed, MemoryType";

    /// <summary>
    /// SMBIOSMemoryType and ConfiguredClockSpeed exist from newer Windows; on an older one WMI refuses the whole query, and then the
    /// columns it does not have are dropped (the last form has only the columns that Windows 7 has too). Newest first.
    /// </summary>
    internal static readonly IReadOnlyList<WmiQuery<RawMemory>> MemoryQueries =
    [
        MemoryQuery($"SELECT {MemoryBase}, SMBIOSMemoryType, ConfiguredClockSpeed FROM Win32_PhysicalMemory", smbios: true, configured: true),
        MemoryQuery($"SELECT {MemoryBase}, SMBIOSMemoryType FROM Win32_PhysicalMemory", smbios: true, configured: false),
        MemoryQuery($"SELECT {MemoryBase} FROM Win32_PhysicalMemory", smbios: false, configured: false),
    ];

    internal static readonly WmiQuery<RawGpu> VideoControllerQuery = new(
        WmiScopes.Cimv2,
        "SELECT Name, PNPDeviceID FROM Win32_VideoController",
        static row =>
        {
            string pnp = Str(row["PNPDeviceID"]) ?? "";
            return new RawGpu(Str(row["Name"]) ?? "", 0, pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase));
        });

    internal static readonly WmiQuery<RawDisk> PhysicalDiskQuery = new(
        WmiScopes.Storage,
        "SELECT FriendlyName, Model, BusType, MediaType, Size FROM MSFT_PhysicalDisk",
        static row => new RawDisk(Str(row["FriendlyName"]) ?? Str(row["Model"]) ?? "", ToInt(row["BusType"]), ToInt(row["MediaType"]), ToULong(row["Size"])));

    internal static readonly WmiQuery<RawDisk> DiskDriveQuery = new(
        WmiScopes.Cimv2,
        "SELECT Model, Size, InterfaceType, PNPDeviceID, MediaType FROM Win32_DiskDrive",
        static row => new RawDisk(
            Str(row["Model"]) ?? "",
            BuildRules.BusFromWin32(Str(row["InterfaceType"]), Str(row["PNPDeviceID"]), Str(row["MediaType"])),
            0,
            ToULong(row["Size"])));

    internal static readonly WmiQuery<RawMonitor> MonitorQuery = new(
        WmiScope,
        "SELECT UserFriendlyName, Active FROM WmiMonitorID",
        static row => new RawMonitor(BuildRules.DecodeWmiString(row["UserFriendlyName"] as ushort[]), row["Active"] as bool? ?? true));

    internal static readonly WmiQuery<RawOptical> OpticalQuery = new(
        WmiScopes.Cimv2,
        "SELECT Name, PNPDeviceID FROM Win32_CDROMDrive",
        static row => new RawOptical(Str(row["Name"]) ?? "", Str(row["PNPDeviceID"]) ?? ""));

    // =====================================================================
    // The sources
    // =====================================================================

    public async Task<IReadOnlyList<string>> CpusAsync(CancellationToken ct) =>
        RowsOrThrow(await AskAsync(CpuQuery, NewDeadline(), ct).ConfigureAwait(false));

    public async Task<(string? Manufacturer, string? Product)> BoardAsync(CancellationToken ct)
    {
        var rows = RowsOrThrow(await AskAsync(BoardQuery, NewDeadline(), ct).ConfigureAwait(false));
        return rows.Count > 0 ? rows[0] : (null, null);
    }

    public async Task<IReadOnlyList<RawMemory>> MemoryAsync(CancellationToken ct)
    {
        var deadline = NewDeadline();
        CheckReading<IReadOnlyList<RawMemory>>? reading = null;
        foreach (var query in MemoryQueries)
        {
            reading = await AskAsync(query, deadline, ct).ConfigureAwait(false);
            if (reading.HasValue) return reading.Value;
            if (!CanTryAnother(reading.Reason, deadline)) break;
        }

        throw Unread(reading); // the reason of the last form that was tried
    }

    /// <summary>
    /// DXGI (the same list as the load test: the VRAM is not capped at 4 GB, and the alias of a virtual display is gone). When DXGI gives
    /// nothing: Win32_VideoController (no VRAM: AdapterRAM is capped at 4 GB).
    /// </summary>
    public async Task<IReadOnlyList<RawGpu>> GpusAsync(CancellationToken ct)
    {
        var deadline = NewDeadline();
        var dxgi = await ReadGpuAsync(deadline, ct).ConfigureAwait(false);
        if (dxgi.HasValue && dxgi.Value.Count > 0) return dxgi.Value;
        if (!dxgi.HasValue && !CanTryAnother(dxgi.Reason, deadline)) throw Unread(dxgi);
        return RowsOrThrow(await AskAsync(VideoControllerQuery, deadline, ct).ConfigureAwait(false));
    }

    /// <summary>MSFT_PhysicalDisk (it has the bus and SSD or HDD). When it cannot be read, or has no row: Win32_DiskDrive.</summary>
    public async Task<IReadOnlyList<RawDisk>> DisksAsync(CancellationToken ct)
    {
        var deadline = NewDeadline();
        var storage = await AskAsync(PhysicalDiskQuery, deadline, ct).ConfigureAwait(false);
        if (storage.HasValue && storage.Value.Count > 0) return storage.Value;
        if (!storage.HasValue && !CanTryAnother(storage.Reason, deadline)) throw Unread(storage);
        return RowsOrThrow(await AskAsync(DiskDriveQuery, deadline, ct).ConfigureAwait(false));
    }

    /// <summary>WmiMonitorID (the name in the EDID). A monitor with no name is not counted (by the rules).</summary>
    public async Task<IReadOnlyList<RawMonitor>> MonitorsAsync(CancellationToken ct) =>
        RowsOrThrow(await AskAsync(MonitorQuery, NewDeadline(), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<RawOptical>> OpticalsAsync(CancellationToken ct) =>
        RowsOrThrow(await AskAsync(OpticalQuery, NewDeadline(), ct).ConfigureAwait(false));

    // =====================================================================
    // Helpers
    // =====================================================================

    private QueryDeadline NewDeadline() => QueryDeadline.After(_time, SystemWmiFacts.Budget);

    /// <summary>
    /// Another query may be tried after this one did not answer, unless the time is gone or the caller stopped. (The old code tried the next
    /// form on any ManagementException; a reason of error, denied or unsupported is where such an exception ends up.) A timeout with time
    /// left on the deadline is a lane that refused at once (no free slot, DESIGN-A 6.1): that is no reason to give up the category, so the
    /// fall-back is tried. A timeout with no time left is a real one, and so is a stop (partial): no fall-back.
    /// </summary>
    private static bool CanTryAnother(CheckReason? reason, QueryDeadline deadline) =>
        reason is CheckReason.Error or CheckReason.Denied or CheckReason.Unsupported
        || (reason is CheckReason.Timeout && !deadline.IsExpired);

    /// <summary>The rows, or the reason there are none: a category that could not be read is not an empty list.</summary>
    private static IReadOnlyList<T> RowsOrThrow<T>(CheckReading<IReadOnlyList<T>> reading) =>
        reading.HasValue ? reading.Value : throw Unread(reading);

    private static BuildCategoryUnreadException Unread<T>(CheckReading<T>? reading) => new(reading?.Reason ?? CheckReason.Error);

    private async Task<CheckReading<IReadOnlyList<T>>> AskAsync<T>(WmiQuery<T> query, QueryDeadline deadline, CancellationToken ct)
    {
        CheckReading<IReadOnlyList<T>> reading;
        try
        {
            reading = await _wmi.ReadAsync(query, deadline, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reading = CheckReading<IReadOnlyList<T>>.Failed();
        }

        ct.ThrowIfCancellationRequested(); // a stop by the caller is the one thing that is not an empty category
        return reading;
    }

    private async Task<CheckReading<IReadOnlyList<RawGpu>>> ReadGpuAsync(QueryDeadline deadline, CancellationToken ct)
    {
        CheckReading<IReadOnlyList<RawGpu>> reading;
        try
        {
            reading = await _gpu.ReadAsync(deadline, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reading = CheckReading<IReadOnlyList<RawGpu>>.Failed();
        }

        ct.ThrowIfCancellationRequested();
        return reading;
    }

    private static WmiQuery<RawMemory> MemoryQuery(string text, bool smbios, bool configured) => new(
        WmiScopes.Cimv2,
        text,
        row => new RawMemory(
            Str(row["PartNumber"]) ?? "",
            Str(row["Manufacturer"]) ?? "",
            ToULong(row["Capacity"]),
            (uint)ToInt(row["Speed"]),
            smbios ? ToInt(row["SMBIOSMemoryType"]) : 0,
            ToInt(row["MemoryType"]),
            configured ? (uint)ToInt(row["ConfiguredClockSpeed"]) : 0));

    // The old rules for reading a value, unchanged: "as string" (a value of another type is no text), and Convert with a 0 for what does not
    // convert. They are not WmiValues on purpose: the same machine must give the same material as before.
    private static string? Str(object? o) => o as string;

    private static int ToInt(object? o)
    {
        try { return o is null ? 0 : Convert.ToInt32(o, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static ulong ToULong(object? o)
    {
        try { return o is null ? 0 : Convert.ToUInt64(o, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }
}
