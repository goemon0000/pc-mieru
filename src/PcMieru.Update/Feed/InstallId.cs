using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Update.Feed;

/// <summary>
/// Which install a state folder belongs to (DESIGN-B 4: <c>L\update\&lt;id&gt;</c>): the volume serial number and the file id of one file, as
/// GetFileInformationByHandle says them. Two copies of the app in two folders (or one folder on two drives) have two ids, so they do not share a state
/// file and do not take each other's lock; the same file under another name (a junction, a mapped drive letter) has the same id.
///
/// <b>This is the one function that names the file.</b> Until the gate file of step 3 exists it is PCMieru.exe; step 3 changes what <see cref="OfInstall"/>
/// passes (the gate file), and nothing else.
/// </summary>
public static class InstallId
{
    /// <summary>The id of the install whose program file is <paramref name="programFile"/> (step 3: the gate file). Null when the file cannot be opened.</summary>
    public static string? OfInstall(string programFile) => OfFile(programFile);

    /// <summary>The id of a file: "&lt;volume serial, 8 hex digits&gt;-&lt;file id, 16 hex digits&gt;", lower case. Null when it cannot be opened or asked.</summary>
    public static string? OfFile(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info)
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{info.VolumeSerialNumber:x8}-{info.FileIndexHigh:x8}{info.FileIndexLow:x8}")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The state folder of an install: <c>&lt;L&gt;\update\&lt;id&gt;</c>. Null when the id cannot be had.</summary>
    public static string? StateFolder(string localFolder, string programFile) =>
        OfInstall(programFile) is { } id ? Path.Combine(localFolder, "update", id) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
