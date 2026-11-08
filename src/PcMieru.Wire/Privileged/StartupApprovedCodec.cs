using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PcMieru.Wire;

[JsonConverter(typeof(CamelCaseEnumConverter<ApprovalState>))]
public enum ApprovalState
{
    /// <summary>No approval value exists (Windows runs the entry) or its flag says enabled.</summary>
    Enabled,

    Disabled,

    /// <summary>The value has a type, length or flag we do not know. Shown, never rewritten.</summary>
    Unknown,
}

/// <summary>
/// The StartupApproved value Windows keeps next to Run keys and Startup folders (what Task Manager flips).
/// REG_BINARY, 12 bytes: first DWORD is the state (2 or 6 enabled, 3 or 7 disabled), last 8 bytes a FILETIME
/// of when it was disabled. Anything else is "unknown" and is read-only for us (SPEC section 5.4).
/// </summary>
public static class StartupApprovedCodec
{
    public const int Length = 12;

    public static ApprovalState Decode(RegistryValueKind kind, byte[]? bytes)
    {
        if (bytes is null)
        {
            // The caller passes null only for "the value does not exist": Windows treats that as enabled.
            return ApprovalState.Enabled;
        }

        if (kind != RegistryValueKind.Binary || bytes.Length != Length)
        {
            return ApprovalState.Unknown;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes) switch
        {
            2 or 6 => ApprovalState.Enabled,
            3 or 7 => ApprovalState.Disabled,
            _ => ApprovalState.Unknown,
        };
    }

    /// <summary>Disable = first DWORD 3 and the FILETIME of now.</summary>
    public static byte[] EncodeDisabled(long nowFileTime)
    {
        var bytes = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 3);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), nowFileTime);
        return bytes;
    }

    /// <summary>Enable = first DWORD 2 and a zero FILETIME.</summary>
    public static byte[] EncodeEnabled()
    {
        var bytes = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        return bytes;
    }

    /// <summary>The FILETIME stored in a disabled value, or 0.</summary>
    public static long DisabledAt(byte[] bytes) =>
        bytes.Length == Length ? BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(4)) : 0;

    /// <summary>
    /// Where Windows keeps the approval for each registration place, below
    /// <c>Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved</c>. Null for places we do not toggle.
    /// </summary>
    public static string? ApprovedSubKey(StartupProvider provider) => provider switch
    {
        StartupProvider.RunHkcu or StartupProvider.RunHklm => "Run",
        StartupProvider.RunHklm32 => "Run32",
        StartupProvider.StartupFolderUser or StartupProvider.StartupFolderCommon => "StartupFolder",
        _ => null,
    };

    public const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
}
