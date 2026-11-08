// Provenance: the open / retry / fallback order follows the owner's pl-sensors StorageReader (MIT, same owner).
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Wire;

/// <summary>
/// Reads SMART / NVMe health bytes from one physical drive and hands them back raw. Read-only, fixed commands:
/// SMART READ DATA / READ THRESHOLDS and the NVMe health and error log pages. Buses we cannot vouch for (USB
/// bridges, RAID, virtual disks) get nothing sent at all. No parsing here.
/// </summary>
public static class DiskOperations
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStoragePredictFailure = 0x002D1100;
    private const uint SmartRcvDriveData = 0x0007C088;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    public static ActionResult SmartRead(int drive)
    {
        var path = $@"\\.\PhysicalDrive{drive.ToString(CultureInfo.InvariantCulture)}";
        using var info = Native.CreateFile(path, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (info.IsInvalid)
        {
            return OpenFailure(drive, Marshal.GetLastPInvokeError());
        }

        var bus = ReadBus(info);
        if (bus is not (StorageProtocol.BusTypeAta or StorageProtocol.BusTypeSata))
        {
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.DiskUnsupportedBus, ("bus", StorageProtocol.BusName(bus)));
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal) { [HelperDataKeys.Bus] = "ata" };

        using (var rw = Native.CreateFile(path, GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0))
        {
            if (!rw.IsInvalid)
            {
                var page = Smart(rw, StorageProtocol.SmartReadData, (byte)drive);
                if (page is not null)
                {
                    data[HelperDataKeys.SmartData] = Convert.ToBase64String(page);
                    data[HelperDataKeys.SmartSource] = "smart-rcv-drive-data";
                    var thresholds = Smart(rw, StorageProtocol.SmartReadThresholds, (byte)drive);
                    if (thresholds is not null)
                    {
                        data[HelperDataKeys.SmartThresholds] = Convert.ToBase64String(thresholds);
                    }

                    return ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data);
                }
            }
        }

        using (var read = Native.CreateFile(path, GenericRead, FileShareReadWrite, 0, OpenExisting, 0, 0))
        {
            if (!read.IsInvalid)
            {
                var output = Query(read, IoctlStoragePredictFailure, [], 516 + 16, out _);
                if (output is not null)
                {
                    var (predict, vendor) = StorageProtocol.ExtractPredictFailure(output);
                    if (vendor is not null)
                    {
                        data[HelperDataKeys.SmartData] = Convert.ToBase64String(vendor);
                        data[HelperDataKeys.SmartSource] = "predict-failure";
                        data[HelperDataKeys.PredictFailure] = predict ? "true" : "false";
                        return ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data);
                    }
                }
            }
        }

        return ActionResult.Of(OperationCode.Error, MessageKeys.DiskNoData);
    }

    public static ActionResult NvmeHealth(int drive)
    {
        var path = $@"\\.\PhysicalDrive{drive.ToString(CultureInfo.InvariantCulture)}";
        using var info = Native.CreateFile(path, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (info.IsInvalid)
        {
            return OpenFailure(drive, Marshal.GetLastPInvokeError());
        }

        var bus = ReadBus(info);
        if (bus != StorageProtocol.BusTypeNvme)
        {
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.DiskUnsupportedBus, ("bus", StorageProtocol.BusName(bus)));
        }

        byte[]? health = null;
        byte[]? errors = null;
        var denied = false;
        using (var read = Native.CreateFile(path, GenericRead, FileShareReadWrite, 0, OpenExisting, 0, 0))
        {
            if (read.IsInvalid)
            {
                denied = Marshal.GetLastPInvokeError() == Native.ERROR_ACCESS_DENIED;
            }
            else
            {
                health = NvmeLog(read, StorageProtocol.NvmeLogHealthInfo, StorageProtocol.NvmeHealthLength, out var error);
                denied = health is null && error == Native.ERROR_ACCESS_DENIED;
                if (health is not null)
                {
                    errors = NvmeLog(read, StorageProtocol.NvmeLogErrorInfo, StorageProtocol.NvmeErrorEntryLength * StorageProtocol.NvmeErrorEntryCount, out _);
                }
            }
        }

        // Only when Windows said "access denied" is the handle reopened with read/write, as some drivers want.
        // The only thing sent through it is still a Get Log Page query.
        if (health is null && denied)
        {
            using var rw = Native.CreateFile(path, GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
            if (!rw.IsInvalid)
            {
                health = NvmeLog(rw, StorageProtocol.NvmeLogHealthInfo, StorageProtocol.NvmeHealthLength, out _);
                if (health is not null)
                {
                    errors = NvmeLog(rw, StorageProtocol.NvmeLogErrorInfo, StorageProtocol.NvmeErrorEntryLength * StorageProtocol.NvmeErrorEntryCount, out _);
                }
            }
        }

        if (health is null)
        {
            return denied
                ? ActionResult.Of(OperationCode.Denied, MessageKeys.DiskDenied)
                : ActionResult.Of(OperationCode.Error, MessageKeys.DiskNoData);
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HelperDataKeys.Bus] = "nvme",
            [HelperDataKeys.NvmeHealth] = Convert.ToBase64String(health),
        };
        if (errors is not null)
        {
            data[HelperDataKeys.NvmeErrors] = Convert.ToBase64String(errors);
        }

        return ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data);
    }

    private static ActionResult OpenFailure(int drive, int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => ActionResult.Of(OperationCode.NotFound, MessageKeys.DiskNotFound, ("drive", drive.ToString(CultureInfo.InvariantCulture))),
        Native.ERROR_ACCESS_DENIED => ActionResult.Of(OperationCode.Denied, MessageKeys.DiskDenied),
        _ => ActionResult.Of(OperationCode.Error, MessageKeys.DiskError, ("code", error.ToString(CultureInfo.InvariantCulture))),
    };

    private static int ReadBus(SafeFileHandle info)
    {
        var descriptor = Query(info, IoctlStorageQueryProperty, StorageProtocol.BuildDeviceQuery(), 1024, out _);
        return descriptor is null ? -1 : StorageProtocol.ParseBusType(descriptor);
    }

    private static byte[]? NvmeLog(SafeFileHandle handle, byte page, int length, out int error)
    {
        var buffer = StorageProtocol.BuildNvmeLogQuery(page, length);
        var output = Query(handle, IoctlStorageQueryProperty, buffer, buffer.Length, out error);
        return output is null ? null : StorageProtocol.ExtractProtocolData(output, length);
    }

    private static byte[]? Smart(SafeFileHandle handle, byte feature, byte number)
    {
        var input = StorageProtocol.BuildSmartRead(feature, number);
        var output = Query(handle, SmartRcvDriveData, input, StorageProtocol.SendCmdOutSize, out _, StorageProtocol.SendCmdInSize);
        return output is null ? null : StorageProtocol.ExtractSmart(output);
    }

    private static unsafe byte[]? Query(SafeFileHandle handle, uint code, byte[] input, int outputLength, out int error, int? inputLength = null)
    {
        var output = new byte[outputLength];
        var inLength = inputLength ?? input.Length;
        error = 0;
        fixed (byte* pi = input)
        fixed (byte* po = output)
        {
            if (!Native.DeviceIoControl(handle, code, inLength == 0 ? null : pi, inLength, po, output.Length, out var got, 0))
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            return got <= 0 ? null : got == output.Length ? output : output.AsSpan(0, got).ToArray();
        }
    }
}
