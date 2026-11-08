using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>
/// Native entry points for the lazy per-process facts (owner, services, signature, icon). Not used on the per-tick path.
/// Everything is resolved from System32 only.
/// </summary>
internal static unsafe partial class Win32
{
    public const uint TokenQuery = 0x0008;
    public const int TokenUserClass = 1;
    public const int ProcessCommandLineInformationClass = 60;
    public const int ProcessPowerThrottlingClass = 4;
    public const uint PowerThrottlingExecutionSpeed = 0x1;

    public const ushort MachineI386 = 0x014c;
    public const ushort MachineArmNt = 0x01c4;
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int informationClass, void* buffer, uint length, out uint returned);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupAccountSid(nint system, void* sid, char* name, ref uint nameLength, char* domain, ref uint domainLength, out int use);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint OpenSCManager(nint machine, nint database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumServicesStatusEx(nint manager, int infoLevel, uint serviceType, uint state, byte* buffer, uint size, out uint needed, out uint returned, ref uint resume, nint group);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int WinVerifyTrust(nint window, ref Guid action, void* data);

    [LibraryImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint WTHelperGetProvSignerFromChain(nint provData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint WTHelperGetProvCertFromChain(nint signer, uint certIndex);

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminAcquireContext2(out nint catAdmin, nint subsystem, string? hashAlgorithm, nint policy, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminCalcHashFromFileHandle2(nint catAdmin, nint file, ref uint hashLength, byte* hash, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint CryptCATAdminEnumCatalogFromHash(nint catAdmin, byte* hash, uint hashLength, uint flags, ref nint previous);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATCatalogInfoFromContext(nint catalog, ref CatalogInfo info, uint flags);

    [LibraryImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminReleaseCatalogContext(nint catAdmin, nint catalog, uint flags);

    [LibraryImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminReleaseContext(nint catAdmin, uint flags);

    [LibraryImport("crypt32.dll", EntryPoint = "CertGetNameStringW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CertGetNameString(nint cert, uint type, uint flags, void* parameter, char* name, uint length);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetObjectW(nint handle, int size, out BitmapInfoBasic bitmap);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetDIBits(nint dc, nint bitmap, uint startScan, uint scanLines, byte* bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetClassName(nint window, char* buffer, int max);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint FindWindowEx(nint parent, nint after, string? className, string? title);

    public const uint ShgfiIcon = 0x100;
    public const uint ShgfiSmallIcon = 0x1;
    public const uint ShgfiLargeIcon = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    public struct CatalogInfo
    {
        public uint Size;
        public fixed char CatalogFile[260];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    /// <summary>BITMAP: only the leading fields are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoBasic
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorUsed;
        public uint ColorImportant;
    }

    /// <summary>WINTRUST_DATA (x64: 80 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WintrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint Union;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WintrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WintrustCatalogInfo
    {
        public uint Size;
        public uint CatalogVersion;
        public nint CatalogFilePath;
        public nint MemberTag;
        public nint MemberFilePath;
        public nint MemberFile;
        public nint CalculatedFileHash;
        public uint CalculatedFileHashLength;
        public nint CatalogContext;
        public nint CatAdmin;
    }
}
