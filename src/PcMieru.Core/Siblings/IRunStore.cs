using Microsoft.Win32;

namespace PcMieru.Core.Siblings;

/// <summary>
/// One value of <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> as it is stored: its registry type and, for a string type (REG_SZ or REG_EXPAND_SZ), the raw text with
/// environment variables NOT expanded. <see cref="Text"/> is null for any other type (the value exists, but it is not something we can read as a command line).
/// </summary>
public sealed record RunValue(RegistryValueKind Kind, string? Text);

/// <summary>One value of <c>...\Explorer\StartupApproved\Run</c> as it is stored: its registry type and its raw bytes (empty for a type that is not binary).</summary>
public sealed record ApprovalValue(RegistryValueKind Kind, byte[] Bytes);

/// <summary>
/// The four places the logon setting touches, by value name: the Run value and the StartupApproved\Run value (what Task Manager flips). The real implementation is
/// <see cref="RegistryRunStore"/>, which only a real run of the app constructs; every test and every smoke run gets <see cref="MemoryRunStore"/>. A method that cannot do its job throws
/// (<see cref="UnauthorizedAccessException"/> for "access denied", <see cref="IOException"/> or anything else for the rest): <see cref="PartyLogon"/> turns that into a result and never
/// lets it reach the screen. "Not there" is a normal answer (null), never an exception: a key that does not exist holds no value.
/// The shape (type and raw content for each of the four values) is what the later switch from the old value needs to journal and to put back exactly.
/// </summary>
public interface IRunStore
{
    /// <summary>The Run value of this name, or null when there is none (the key may be missing too).</summary>
    RunValue? ReadRun(string name);

    /// <summary>Writes the Run value (creates the key when it is missing). <paramref name="kind"/> is REG_SZ or REG_EXPAND_SZ.</summary>
    void WriteRun(string name, string text, RegistryValueKind kind = RegistryValueKind.String);

    /// <summary>Removes the Run value; no error when it is not there.</summary>
    void DeleteRun(string name);

    /// <summary>The StartupApproved\Run value of this name, or null when there is none (no stamp: Windows runs the entry).</summary>
    ApprovalValue? ReadApproval(string name);

    /// <summary>Writes the StartupApproved\Run value with exactly this type and these bytes (creates the key when it is missing).</summary>
    void WriteApproval(string name, byte[] bytes, RegistryValueKind kind = RegistryValueKind.Binary);

    /// <summary>Removes the StartupApproved\Run value; no error when it is not there.</summary>
    void DeleteApproval(string name);

    /// <summary>
    /// Makes what was written so far survive a power loss (the registry otherwise lazily writes its hive to the disk a while later). The switch calls it after its registry writes and before it marks the
    /// journal committed, and the recovery calls it after it put the values back and before it deletes the journal. The memory store does nothing (and counts the call).
    /// </summary>
    void Flush();
}
