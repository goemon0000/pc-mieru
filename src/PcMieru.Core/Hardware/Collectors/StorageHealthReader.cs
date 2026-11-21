// SATA SMART request layout adapted from goemon0000/parts-life (MIT) src/PartsLife.Checks/Storage/StorageProtocol.cs and StorageReader.cs.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Storage;
using DiskIdentity = PcMieru.Wire.DiskIdentity;
using WireProtocol = PcMieru.Wire.StorageProtocol;

namespace PcMieru.Core.Hardware;

/// <summary>
/// Supplies raw health bytes from outside this process (the elevated Helper). NVMe: the 512-byte health log. SATA: the 512-byte
/// SMART table, optionally followed by the 512-byte thresholds. Null means the Helper could not read it.
/// </summary>
public interface IElevatedStorageReader
{
    Task<byte[]?> ReadRawAsync(int physicalDriveNumber, StorageHealthKind kind, CancellationToken ct);

    /// <summary>
    /// Several drives at once. The real reader answers them all with ONE elevated run (one UAC prompt); this default asks one by one
    /// and is only for readers that have no cheaper way. A drive that could not be read is null; a drive that was not asked is absent.
    /// </summary>
    async Task<IReadOnlyDictionary<int, byte[]?>> ReadRawManyAsync(IReadOnlyList<(int PhysicalDriveNumber, StorageHealthKind Kind)> drives, CancellationToken ct)
    {
        var all = new Dictionary<int, byte[]?>();
        foreach (var (number, kind) in drives)
        {
            all[number] = await ReadRawAsync(number, kind, ct).ConfigureAwait(false);
        }

        return all;
    }

    /// <summary>
    /// Like <see cref="ReadRawManyAsync"/> but each drive also brings what the reader knows besides the bytes: the identity it saw on
    /// the handle the bytes came through, and the error log counts. A reader that cannot tell (this default) answers with the bytes
    /// only, and the caller then treats the identity as not confirmed. Added after the first release: an implementation that does
    /// not know it keeps working through this default.
    /// </summary>
    async Task<IReadOnlyDictionary<int, ElevatedDriveRead>> ReadManyAsync(IReadOnlyList<(int PhysicalDriveNumber, StorageHealthKind Kind)> drives, CancellationToken ct)
    {
        var raw = await ReadRawManyAsync(drives, ct).ConfigureAwait(false);
        return raw.ToDictionary(p => p.Key, p => new ElevatedDriveRead(p.Value, null, null, null));
    }
}

/// <summary>
/// Real <see cref="IStorageHealthProvider"/>. NVMe is read in this process with the least access Windows accepts
/// (none, then read, then read+write only when Windows says access denied). Only a Get Log Page request can be built, so nothing
/// is ever written to the drive. SATA SMART needs administrator rights: read directly when this process is elevated, otherwise
/// through <see cref="IElevatedStorageReader"/> when elevation is allowed, otherwise reported as denied.
/// </summary>
public sealed class RealStorageHealthProvider(IElevatedStorageReader? elevated = null) : IStorageHealthProvider
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint SmartRcvDriveData = 0x0007C088;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// What reading one drive without the Helper came to: <see cref="Health"/> is the answer, or null when only the elevated Helper can
    /// give it (the drive is then waiting in the batch of <see cref="ResolveAsync"/>, which asks for all of them at once).
    /// </summary>
    internal sealed record Step(int Number, string Model, StorageHealthKind Kind, Value<StorageHealth>? Health)
    {
        /// <summary>
        /// Windows refused this drive's health data for lack of administrator rights in this process. True for a drive waiting for the Helper
        /// (<see cref="Health"/> is null) and for one that was refused and not handed on (elevation not requested, or no Helper). Counted by
        /// <see cref="CountElevation"/>.
        /// </summary>
        internal bool NeedsRights { get; init; }
    }

    /// <summary>
    /// How many drives needed administrator rights in this pass and how many of those the elevated re-read returned bytes for
    /// (<paramref name="resolved"/> is the answer of <see cref="ResolveWithEvidenceAsync"/> for the same <paramref name="steps"/>). A drive
    /// that waited for the Helper counts as needed whatever its flag; one that is not waiting counts only when it says it was refused.
    /// </summary>
    internal static (int Needed, int ReRead) CountElevation(IReadOnlyList<Step> steps, IReadOnlyList<Resolved> resolved)
    {
        var needed = 0;
        var reRead = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var waiting = steps[i].Health is null;
            if (!waiting && !steps[i].NeedsRights)
            {
                continue;
            }

            needed++;
            if (waiting && i < resolved.Count && resolved[i].Evidence?.Bytes is not null)
            {
                reRead++;
            }
        }

        return (needed, reRead);
    }

    /// <summary>One drive's health and, when the bytes behind it were taken, what came with them (identity on the same handle, error log counts).</summary>
    internal sealed record Resolved(DriveHealth Health, ElevatedDriveRead? Evidence);

    public Task<IReadOnlyList<DriveHealth>> ReadAsync(bool allowElevation, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var steps = new List<Step>();
            foreach (var n in DriveNumbers())
            {
                ct.ThrowIfCancellationRequested();
                steps.Add(ReadOne(n, allowElevation));
            }

            return await ResolveAsync(steps, elevated, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Every drive that waits for administrator rights is read by ONE call to the elevated reader, so the user sees one UAC prompt
    /// whether the PC has one such drive or five. Drives that already have an answer keep it, and the order of the list does not change.
    /// </summary>
    internal static async Task<IReadOnlyList<DriveHealth>> ResolveAsync(IReadOnlyList<Step> steps, IElevatedStorageReader? elevated, CancellationToken ct) =>
        (await ResolveWithEvidenceAsync(steps, null, elevated, ct).ConfigureAwait(false)).Select(r => r.Health).ToList();

    /// <summary>
    /// <see cref="ResolveAsync"/> that also keeps, for each drive, the evidence taken together with its bytes: <paramref name="local"/>
    /// for a drive read in this process (by drive number), the elevated reader's answer for a drive it read. A drive without bytes has no evidence.
    /// </summary>
    internal static async Task<IReadOnlyList<Resolved>> ResolveWithEvidenceAsync(
        IReadOnlyList<Step> steps,
        IReadOnlyDictionary<int, ElevatedDriveRead>? local,
        IElevatedStorageReader? elevated,
        CancellationToken ct)
    {
        var waiting = steps.Where(s => s.Health is null).ToList();
        IReadOnlyDictionary<int, ElevatedDriveRead>? many = null;
        Exception? failure = null;
        if (waiting.Count > 0 && elevated is not null)
        {
            try
            {
                many = await elevated.ReadManyAsync(waiting.Select(s => (s.Number, s.Kind)).ToList(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                failure = ex;
            }
        }

        var list = new List<Resolved>(steps.Count);
        foreach (var step in steps)
        {
            if (step.Health is { } known)
            {
                ElevatedDriveRead? own = null;
                local?.TryGetValue(step.Number, out own);
                list.Add(new Resolved(new DriveHealth(step.Number, step.Model, known), own));
                continue;
            }

            var source = "helper:" + step.Kind;
            ElevatedDriveRead? read = null;
            many?.TryGetValue(step.Number, out read);
            var bytes = read?.Bytes;
            Value<StorageHealth> health;
            if (failure is not null)
            {
                health = Probe.FromException<StorageHealth>(failure, source);
            }
            else if (bytes is null)
            {
                health = Value.Denied<StorageHealth>(source, "the elevated helper could not read this drive");
            }
            else
            {
                var parsed = step.Kind == StorageHealthKind.Nvme ? StorageHealthParsers.FromNvmeLogPage(bytes) : StorageHealthParsers.FromSmartBlob(bytes);
                health = new Value<StorageHealth>(parsed.Data, parsed.Status, parsed.Source + " via helper", parsed.Detail);
            }

            list.Add(new Resolved(new DriveHealth(step.Number, step.Model, health), bytes is null ? null : read));
        }

        return list;
    }

    private static List<int> DriveNumbers() => Collectors.StorageCollector.PhysicalDriveNumbers().ToList();

    private Step ReadOne(int number, bool allowElevation)
    {
        var path = $@"\\.\PhysicalDrive{number}";
        DeviceDescriptor? desc;
        using (var h = Kernel32.OpenDeviceNoAccess(path))
        {
            var raw = h.IsInvalid ? null : Ioctl.Call(h, Ioctl.QueryProperty, StorageProtocol.BuildPropertyQuery(StorageProtocol.StorageDeviceProperty), 1024, out _);
            desc = raw is null ? null : StorageProtocol.ParseDeviceDescriptor(raw);
        }

        if (desc is null)
        {
            return new Step(number, string.Empty, StorageHealthKind.Sata, Value.Failed<StorageHealth>("ioctl:StorageDeviceProperty", "could not identify the drive"));
        }

        return ReadByBus(path, number, StorageProtocol.JoinVendorModel(desc.Vendor, desc.Product), desc.BusType, allowElevation, null);
    }

    /// <summary>
    /// Health of one drive whose bus is known. <paramref name="evidence"/> (when given) receives what the read took through the same
    /// handle the health bytes came through: the bytes, the identity read AFTER them, the NVMe error log counts. It is called at most once,
    /// and not at all when no bytes were taken here. Nothing about the health answer changes because of it.
    /// </summary>
    internal Step ReadByBus(string path, int number, string model, int busType, bool allowElevation, Action<ElevatedDriveRead>? evidence)
    {
        Value<StorageHealth>? health;
        var kind = StorageHealthKind.Sata;
        var needsRights = false;
        switch (busType)
        {
            case 17:
                kind = StorageHealthKind.Nvme;
                health = ReadNvme(path, allowElevation, evidence, out needsRights);
                break;
            case 3 or 11:
                health = ReadSata(path, number, allowElevation, evidence, out needsRights);
                break;
            default:
                health = Value.Unsupported<StorageHealth>("ioctl:StorageDeviceProperty", "health data is only read for NVMe and SATA drives; USB bridges, RAID and virtual disks are not guessed");
                break;
        }

        return new Step(number, model, kind, health) { NeedsRights = needsRights };
    }

    /// <summary>
    /// Null means "only the elevated Helper can read this one": the drive is then handed to the batch in <see cref="ResolveAsync"/>.
    /// <paramref name="needsRights"/> is true when Windows refused the drive for lack of administrator rights (whether or not the Helper may be asked).
    /// </summary>
    private Value<StorageHealth>? ReadNvme(string path, bool allowElevation, Action<ElevatedDriveRead>? evidence, out bool needsRights)
    {
        needsRights = false;
        const string source = "ioctl:StorageDeviceProtocolSpecificProperty";
        var sawDenied = false;
        foreach (var (access, label) in new[] { (0u, "none"), (GenericRead, "read"), (GenericRead | GenericWrite, "read+write") })
        {
            using var h = Kernel32.CreateFileW(path, access, Kernel32.FileShareReadWrite, IntPtr.Zero, Kernel32.OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                if (Marshal.GetLastPInvokeError() == ErrorAccessDenied)
                {
                    sawDenied = true;
                    continue;
                }

                return Probe.FromWin32<StorageHealth>(Marshal.GetLastPInvokeError(), source, "the drive");
            }

            var query = StorageProtocol.BuildNvmeLogQuery(StorageProtocol.NvmeLogHealthInfo, StorageHealthParsers.NvmeHealthLogLength);
            var output = Ioctl.Call(h, Ioctl.QueryProperty, query, query.Length, out var error);
            if (output is null)
            {
                if (error == ErrorAccessDenied)
                {
                    sawDenied = true;
                    continue;
                }

                return error == ErrorInvalidParameter
                    ? Value.Unsupported<StorageHealth>(source, "the driver or bridge does not accept the NVMe health log request")
                    : Probe.FromWin32<StorageHealth>(error, source, "the NVMe health log");
            }

            var page = StorageProtocol.ExtractProtocolData(output, StorageHealthParsers.NvmeHealthLogLength);
            if (page is null)
            {
                return Value.Failed<StorageHealth>(source, "the driver answered with an unexpected structure");
            }

            var parsed = StorageHealthParsers.FromNvmeLogPage(page);
            if (evidence is not null)
            {
                // Only after the health page is in hand, and on this very handle: the error log counts, then the identity.
                var logQuery = StorageProtocol.BuildNvmeLogQuery(StorageProtocol.NvmeLogErrorInfo, WireProtocol.NvmeErrorLogLength);
                var log = Ioctl.Call(h, Ioctl.QueryProperty, logQuery, logQuery.Length, out _);
                var logPage = log is null ? null : StorageProtocol.ExtractProtocolData(log, WireProtocol.NvmeErrorLogLength);
                evidence(new ElevatedDriveRead(page, ReadIdentity(h), logPage is null ? null : InspectionParsers.ErrorLogOf(logPage), null));
            }

            return new Value<StorageHealth>(parsed.Data, parsed.Status, $"{parsed.Source} via {source}, handle access: {label}", parsed.Detail);
        }

        needsRights = sawDenied;
        return NeedsHelper(allowElevation, sawDenied, source);
    }

    private Value<StorageHealth>? ReadSata(string path, int number, bool allowElevation, Action<ElevatedDriveRead>? evidence, out bool needsRights)
    {
        needsRights = false;
        const string source = "ioctl:SMART_RCV_DRIVE_DATA";
        if (IsElevated())
        {
            using var h = Kernel32.CreateFileW(path, GenericRead | GenericWrite, Kernel32.FileShareReadWrite, IntPtr.Zero, Kernel32.OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                return Probe.FromWin32<StorageHealth>(Marshal.GetLastPInvokeError(), source, "the drive");
            }

            var data = SmartTable(h, StorageProtocol.SmartReadData, (byte)number, out var error);
            if (data is null)
            {
                return error != 0
                    ? Probe.FromWin32<StorageHealth>(error, source, "SMART data")
                    : Value.Unsupported<StorageHealth>(source, "the drive returned no SMART table (SMART disabled, or a bridge that does not pass it through)");
            }

            var thresholds = SmartTable(h, StorageProtocol.SmartReadThresholds, (byte)number, out var thresholdsError);
            var parsed = StorageHealthParsers.FromSmartTables(data, thresholds);
            if (evidence is not null)
            {
                var both = thresholds is null ? data : [.. data, .. thresholds];
                evidence(new ElevatedDriveRead(both, ReadIdentity(h), null, null, ThresholdsStatusOf(thresholds, thresholdsError)));
            }

            return new Value<StorageHealth>(parsed.Data, parsed.Status, $"{parsed.Source} via {source} (elevated process)", parsed.Detail);
        }

        needsRights = true;
        return NeedsHelper(allowElevation, true, source);
    }

    /// <summary>
    /// The answer when the drive can not be read here, or null when the elevated Helper may be asked (it is allowed, there is one, and
    /// Windows said the drive needs administrator rights). Asking happens once for all such drives, in <see cref="ResolveAsync"/>.
    /// </summary>
    private Value<StorageHealth>? NeedsHelper(bool allowElevation, bool denied, string source)
    {
        if (!denied)
        {
            return Value.Unsupported<StorageHealth>(source, "not readable");
        }

        return !allowElevation || elevated is null
            ? Value.Denied<StorageHealth>(source, "Windows requires administrator rights for this drive's health data")
            : null;
    }

    /// <summary>The identity as this handle sees it (v0.7.4's extraction), for the comparison with the one read before.</summary>
    internal static DiskIdentity? ReadIdentity(SafeFileHandle h) =>
        DiskIdentity.Read((code, input, outputLength) => Ioctl.Call(h, code, input.Length == 0 ? null : input, outputLength, out _), out _);

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Why the SMART READ THRESHOLDS table did not come (null when it did): the Win32 error of the IOCTL when there was one (access denied,
    /// a timeout, any other failure), and "unsupported" only for a drive that answered without a table (error 0: the status registers
    /// said no, or the answer was short).
    /// </summary>
    internal static ValueStatus? ThresholdsStatusOf(byte[]? thresholds, int error)
    {
        if (thresholds is not null)
        {
            return null;
        }

        const int ErrorSemTimeout = 121;
        const int ErrorTimeout = 1460;
        return error switch
        {
            0 => ValueStatus.Unsupported,
            ErrorSemTimeout or ErrorTimeout => ValueStatus.Timeout,
            _ => Probe.FromWin32<StorageHealth>(error, string.Empty, string.Empty).Status,
        };
    }

    /// <summary>SMART READ DATA / READ THRESHOLDS only: the request builder refuses any other ATA feature.</summary>
    private static byte[]? SmartTable(SafeFileHandle h, byte feature, byte drive, out int error)
    {
        if (feature is not (StorageProtocol.SmartReadData or StorageProtocol.SmartReadThresholds))
        {
            throw new ArgumentOutOfRangeException(nameof(feature));
        }

        var input = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0), 512);
        input[4] = feature;
        input[5] = 1;
        input[6] = 1;
        input[7] = 0x4F;
        input[8] = 0xC2;
        input[9] = (byte)(0xA0 | ((drive & 1) << 4));
        input[10] = 0xB0;
        input[12] = drive;
        var output = Ioctl.Call(h, SmartRcvDriveData, input, 16 + 512, out error);
        if (output is null || output.Length < 16 + 512 || output[4] != 0 || output[5] != 0)
        {
            return null;
        }

        var table = output.AsSpan(16, 512);
        return table.IndexOfAnyExcept((byte)0) < 0 ? null : table.ToArray();
    }
}
