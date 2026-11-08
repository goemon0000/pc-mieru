using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace PcMieru.Core.Collection;

/// <summary>
/// The model name of a physical disk (IOCTL_STORAGE_QUERY_PROPERTY with zero access: it only asks the driver for its
/// descriptor, needs no admin and cannot read or write the disk). Used once per disk to give the PDH "3 C:" a real name.
/// </summary>
internal static unsafe partial class StorageModel
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private static readonly nint InvalidHandle = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    /// <summary>"Vendor Product" for PhysicalDrive{index}, or null when the driver does not say.</summary>
    public static string? Read(int index)
    {
        var handle = CreateFile($@"\\.\PhysicalDrive{index}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle == InvalidHandle || handle == 0)
        {
            return null;
        }

        try
        {
            // STORAGE_PROPERTY_QUERY: PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0), one spare byte.
            Span<byte> query = stackalloc byte[12];
            query.Clear();
            Span<byte> output = stackalloc byte[1024];
            bool ok;
            uint returned;
            fixed (byte* q = query)
            fixed (byte* o = output)
            {
                ok = DeviceIoControl(handle, IoctlStorageQueryProperty, q, (uint)query.Length, o, (uint)output.Length, out returned, 0);
            }

            return ok ? Parse(output[..(int)Math.Min(returned, (uint)output.Length)]) : null;
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset at 12 and ProductIdOffset at 16 point at NUL-terminated ASCII inside the
    /// same buffer. Offsets are checked; an offset of 0 means "not present".
    /// </summary>
    internal static string? Parse(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 28)
        {
            return null;
        }

        var vendor = AsciiAt(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]));
        var product = AsciiAt(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]));

        // NVMe and SATA drivers fill the vendor with a bus name ("NVMe", "ATA") that says nothing about the maker.
        if (vendor is "NVMe" or "ATA" or "SCSI" or "USB")
        {
            vendor = string.Empty;
        }

        var text = $"{vendor} {product}".Trim();
        return text.Length == 0 ? null : text;
    }

    private static string AsciiAt(ReadOnlySpan<byte> buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        var rest = buffer[(int)offset..];
        var end = rest.IndexOf((byte)0);
        if (end >= 0)
        {
            rest = rest[..end];
        }

        return Encoding.ASCII.GetString(rest).Trim();
    }
}
