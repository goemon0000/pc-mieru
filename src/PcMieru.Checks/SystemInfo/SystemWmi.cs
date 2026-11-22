// Provenance: the page file and video controller questions are those of the owner's pl-cloud SystemCheck (src/PartsLife.Checks/SystemInfo/SystemCheck.cs
// at 25e4571, MIT, same owner: ReadPagefile, ReadVideo); the deadline per question and the tolerant mappers are new (DESIGN-A 6.1).

namespace PcMieru.Checks.SystemInfo;

/// <summary>One row of <c>Win32_PageFileUsage</c>. The name is only for putting the files in a fixed order; it is never written to a result.</summary>
internal sealed record PageFileRow(string? Name, long? AllocatedMb, long? CurrentMb, long? PeakMb);

/// <summary>One row of <c>Win32_VideoController</c>: its PNP id (only to find the adapter Core listed, never written) and its configuration error code.</summary>
internal sealed record VideoControllerRow(string? PnpId, int? ErrorCode);

/// <summary>What the system check asks WMI for, as readings. Each reading that did not come has its reason.</summary>
internal sealed record SystemWmiFacts(
    CheckReading<IReadOnlyList<PageFileRow>> PageFiles,
    CheckReading<bool> PageFileAutomatic,
    CheckReading<IReadOnlyList<VideoControllerRow>> VideoControllers,
    DiskHealthReading DiskHealth)
{
    /// <summary>The deadline of one question, and of the two page file questions together (DESIGN-A 6.1).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public static readonly WmiQuery<PageFileRow> PageFileQuery = new(
        WmiScopes.Cimv2,
        "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage",
        static row => new PageFileRow(WmiValues.Text(row["Name"]), WmiValues.Long(row["AllocatedBaseSize"]), WmiValues.Long(row["CurrentUsage"]), WmiValues.Long(row["PeakUsage"])));

    /// <summary>One row, one property: whether Windows manages the page file by itself. A row without the property is a null.</summary>
    public static readonly WmiQuery<bool?> AutomaticQuery = new(
        WmiScopes.Cimv2,
        "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem",
        static row => WmiValues.Bool(row["AutomaticManagedPagefile"]));

    public static readonly WmiQuery<VideoControllerRow> VideoQuery = new(
        WmiScopes.Cimv2,
        "SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_VideoController",
        static row => new VideoControllerRow(WmiValues.Text(row["PNPDeviceID"]), WmiValues.Int(row["ConfigManagerErrorCode"])));

    /// <summary>
    /// The four WMI questions, one after the other (the lane has two slots, and a stuck one must not take both): the two page file questions
    /// share one 5 s deadline (the second gets what the first left), the video controllers and the disk health each have their own. A
    /// question that fails is a reason, never an exception (a stop by the caller is the only exception, and it is passed on).
    /// </summary>
    public static async Task<SystemWmiFacts> ReadAsync(IWmiReader wmi, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(time);

        var pageFileDeadline = QueryDeadline.After(time, Budget);
        var files = await TryReadAsync(wmi, PageFileQuery, pageFileDeadline, ct).ConfigureAwait(false);
        var automatic = Single(await TryReadAsync(wmi, AutomaticQuery, pageFileDeadline, ct).ConfigureAwait(false));
        var video = await TryReadAsync(wmi, VideoQuery, QueryDeadline.After(time, Budget), ct).ConfigureAwait(false);
        var health = await DiskHealthReader.ReadAsync(wmi, time, ct).ConfigureAwait(false);
        return new SystemWmiFacts(files, automatic, video, health);
    }

    /// <summary>Nothing came, for one reason (a stop, for a test).</summary>
    public static SystemWmiFacts Missing(CheckReason reason) => new(
        CheckReading<IReadOnlyList<PageFileRow>>.Missing(reason),
        CheckReading<bool>.Missing(reason),
        CheckReading<IReadOnlyList<VideoControllerRow>>.Missing(reason),
        DiskHealthReading.Missing(reason));

    // Win32_ComputerSystem has one row. No row, more than one, or no value in it: Windows did not say.
    private static CheckReading<bool> Single(CheckReading<IReadOnlyList<bool?>> rows)
    {
        if (!rows.HasValue)
        {
            return CheckReading<bool>.Missing(rows.Reason!.Value);
        }

        return rows.Value.Count == 1 && rows.Value[0] is { } automatic ? CheckReading<bool>.Ok(automatic) : CheckReading<bool>.Failed();
    }

    private static async Task<CheckReading<IReadOnlyList<T>>> TryReadAsync<T>(IWmiReader wmi, WmiQuery<T> query, QueryDeadline deadline, CancellationToken ct)
    {
        try
        {
            return await wmi.ReadAsync(query, deadline, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CheckReading<IReadOnlyList<T>>.Failed();
        }
    }
}
