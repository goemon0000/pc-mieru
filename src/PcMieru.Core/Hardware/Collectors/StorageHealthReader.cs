// SATA SMART request layout adapted from goemon0000/parts-life (MIT) src/PartsLife.Checks/Storage/StorageProtocol.cs and StorageReader.cs.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Storage;

namespace PcMieru.Core.Hardware;

/// <summary>
/// Supplies raw health bytes from outside this process (the elevated Helper). NVMe: the 512-byte health log. SATA: the 512-byte
/// SMART table, optionally followed by the 512-byte thresholds. Null means the Helper could not read it.
/// </summary>
public interface IElevatedStorageReader
{
    Task<byte[]?> ReadRawAsync(int physicalDriveNumber, StorageHealthKind kind, CancellationToken ct);
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

    public Task<IReadOnlyList<DriveHealth>> ReadAsync(bool allowElevation, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var list = new List<DriveHealth>();
            foreach (var n in DriveNumbers())
            {
                ct.ThrowIfCancellationRequested();
                list.Add(await ReadOneAsync(n, allowElevation, ct).ConfigureAwait(false));
            }

            return (IReadOnlyList<DriveHealth>)list;
        }, ct);

    private static List<int> DriveNumbers() => Collectors.StorageCollector.PhysicalDriveNumbers().ToList();

    private async Task<DriveHealth> ReadOneAsync(int number, bool allowElevation, CancellationToken ct)
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
            return new DriveHealth(number, string.Empty, Value.Failed<StorageHealth>("ioctl:StorageDeviceProperty", "could not identify the drive"));
        }

        var model = StorageProtocol.JoinVendorModel(desc.Vendor, desc.Product);
        Value<StorageHealth> health;
        switch (desc.BusType)
        {
            case 17:
                health = await ReadNvmeAsync(path, number, allowElevation, ct).ConfigureAwait(false);
                break;
            case 3 or 11:
                health = await ReadSataAsync(path, number, allowElevation, ct).ConfigureAwait(false);
                break;
            default:
                health = Value.Unsupported<StorageHealth>("ioctl:StorageDeviceProperty", "health data is only read for NVMe and SATA drives; USB bridges, RAID and virtual disks are not guessed");
                break;
        }

        return new DriveHealth(number, model, health);
    }

    private async Task<Value<StorageHealth>> ReadNvmeAsync(string path, int number, bool allowElevation, CancellationToken ct)
    {
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
            return new Value<StorageHealth>(parsed.Data, parsed.Status, $"{parsed.Source} via {source}, handle access: {label}", parsed.Detail);
        }

        return await FromHelperAsync(number, StorageHealthKind.Nvme, allowElevation, sawDenied, source, ct).ConfigureAwait(false);
    }

    private async Task<Value<StorageHealth>> ReadSataAsync(string path, int number, bool allowElevation, CancellationToken ct)
    {
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

            var thresholds = SmartTable(h, StorageProtocol.SmartReadThresholds, (byte)number, out _);
            var parsed = StorageHealthParsers.FromSmartTables(data, thresholds);
            return new Value<StorageHealth>(parsed.Data, parsed.Status, $"{parsed.Source} via {source} (elevated process)", parsed.Detail);
        }

        return await FromHelperAsync(number, StorageHealthKind.Sata, allowElevation, true, source, ct).ConfigureAwait(false);
    }

    private async Task<Value<StorageHealth>> FromHelperAsync(int number, StorageHealthKind kind, bool allowElevation, bool denied, string source, CancellationToken ct)
    {
        if (!denied)
        {
            return Value.Unsupported<StorageHealth>(source, "not readable");
        }

        if (!allowElevation || elevated is null)
        {
            return Value.Denied<StorageHealth>(source, "Windows requires administrator rights for this drive's health data");
        }

        byte[]? raw;
        try
        {
            raw = await elevated.ReadRawAsync(number, kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return Probe.FromException<StorageHealth>(ex, "helper:" + kind);
        }

        if (raw is null)
        {
            return Value.Denied<StorageHealth>("helper:" + kind, "the elevated helper could not read this drive");
        }

        var parsed = kind == StorageHealthKind.Nvme ? StorageHealthParsers.FromNvmeLogPage(raw) : StorageHealthParsers.FromSmartBlob(raw);
        return new Value<StorageHealth>(parsed.Data, parsed.Status, parsed.Source + " via helper", parsed.Detail);
    }

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
