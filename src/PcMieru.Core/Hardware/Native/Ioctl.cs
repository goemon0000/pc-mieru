using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Core.Hardware.Native;

internal static class Ioctl
{
    public const uint QueryProperty = 0x002D1400;
    public const uint GetDriveGeometryEx = 0x000700A0;
    public const uint GetDriveLayoutEx = 0x00070050;
    public const uint GetPartitionInfoEx = 0x00070048;
    public const uint GetVolumeDiskExtents = 0x00560000;
    public const uint GetDeviceNumber = 0x002D1080;

    /// <summary>DeviceIoControl into a fresh buffer. Null when it failed; the Win32 error is in <paramref name="error"/>.</summary>
    public static byte[]? Call(SafeFileHandle handle, uint code, byte[]? input, int outputLength, out int error)
    {
        var output = new byte[outputLength];
        error = 0;
        if (!Kernel32.DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0), output, (uint)output.Length, out var got, IntPtr.Zero))
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        return got == 0 ? null : got == output.Length ? output : output.AsSpan(0, (int)got).ToArray();
    }
}
