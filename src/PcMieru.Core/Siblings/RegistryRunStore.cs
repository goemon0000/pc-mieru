using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PcMieru.Wire;

namespace PcMieru.Core.Siblings;

/// <summary>
/// The real store: the two keys of the signed-in user (HKCU). Nothing but a REAL run of PCMieru.exe constructs it (Program.cs of the GUI, inside the block that only a real start enters);
/// the smoke run, the measure run, the fakes and every test use <see cref="MemoryRunStore"/> and never name this class (source-text tests pin both, and <see cref="Constructed"/> lets
/// the smoke run prove that it was never built). It takes no path and no key: the two places are fixed here, so nothing can aim it at another place by accident.
/// </summary>
public sealed class RegistryRunStore : IRunStore
{
    private static int _constructed;

    private RegistryRunStore()
    {
        Interlocked.Increment(ref _constructed);
    }

    /// <summary>How many times a real store was built in this process. A smoke run, a measure run and a test expect 0.</summary>
    public static int Constructed => Volatile.Read(ref _constructed);

    /// <summary>The store of the signed-in user. Only a real run calls this (see the class comment).</summary>
    public static RegistryRunStore ForCurrentUser() => new();

    public RunValue? ReadRun(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedCodec.RunKeyPath, writable: false);
        return ReadString(key, name);
    }

    public void WriteRun(string name, string text, RegistryValueKind kind = RegistryValueKind.String)
    {
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            throw new ArgumentException("a Run value is a string", nameof(kind));
        }

        using var key = Registry.CurrentUser.CreateSubKey(StartupApprovedCodec.RunKeyPath, writable: true);
        key.SetValue(name, text, kind);
    }

    public void DeleteRun(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedCodec.RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public ApprovalValue? ReadApproval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: false);
        var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key is null || raw is null)
        {
            return null;
        }

        var kind = key.GetValueKind(name);
        return new ApprovalValue(kind, raw as byte[] ?? []);
    }

    public void WriteApproval(string name, byte[] bytes, RegistryValueKind kind = RegistryValueKind.Binary)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ApprovedPath, writable: true);
        key.SetValue(name, bytes, kind);
    }

    public void DeleteApproval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>
    /// Writes the two keys to the disk now (RegFlushKey). <c>RegistryKey.Flush()</c> is no help here: it does nothing for a key that this object did not write through (a key opened afresh is never "dirty"),
    /// and every call of this class opens its key afresh, so the handle goes to RegFlushKey directly. A key that is not there has nothing to flush.
    /// </summary>
    public void Flush()
    {
        FlushKey(StartupApprovedCodec.RunKeyPath);
        FlushKey(ApprovedPath);
    }

    private static void FlushKey(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        if (key is null)
        {
            return;
        }

        var error = NativeRegistry.RegFlushKey(key.Handle);
        if (error != 0)
        {
            throw error == 5 ? new UnauthorizedAccessException("RegFlushKey: access denied") : new IOException("RegFlushKey failed: " + error);
        }
    }

    private static string ApprovedPath => StartupApprovedCodec.ApprovedRoot + "\\" + StartupApprovedCodec.ApprovedSubKey(StartupProvider.RunHkcu);

    private static RunValue? ReadString(RegistryKey? key, string name)
    {
        var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key is null || raw is null)
        {
            return null;
        }

        var kind = key.GetValueKind(name);
        return new RunValue(kind, kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? raw as string : null);
    }
}

/// <summary>The one registry call the framework does not make for us (see <see cref="RegistryRunStore.Flush"/>).</summary>
internal static partial class NativeRegistry
{
    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int RegFlushKey(SafeRegistryHandle hKey);
}
