using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace PcMieru.Wire;

/// <summary>What the approval value was before we first touched it, so the change can be put back exactly.</summary>
public sealed record StartupOriginal(bool Existed, RegistryValueKind Kind, byte[]? Bytes)
{
    public const string KeyExisted = "undo.existed";
    public const string KeyKind = "undo.kind";
    public const string KeyBytes = "undo.bytes";

    public IReadOnlyDictionary<string, string> ToData() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [KeyExisted] = Existed ? "true" : "false",
        [KeyKind] = Kind.ToString(),
        [KeyBytes] = Bytes is null ? "" : Convert.ToBase64String(Bytes),
    };

    public static StartupOriginal? FromData(IReadOnlyDictionary<string, string>? data)
    {
        if (data is null
            || !data.TryGetValue(KeyExisted, out var existed)
            || !data.TryGetValue(KeyKind, out var kind)
            || !data.TryGetValue(KeyBytes, out var bytes)
            || !Enum.TryParse<RegistryValueKind>(kind, out var parsedKind))
        {
            return null;
        }

        try
        {
            return new StartupOriginal(existed == "true", parsedKind, bytes.Length == 0 ? null : Convert.FromBase64String(bytes));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Flips an entry in the StartupApproved store (what Task Manager's Startup tab does). It only ever writes one value
/// below <c>StartupApproved\{Run,Run32,StartupFolder}</c> under a root the caller passes: never the Run key, never
/// anything else, never a delete. The caller checks that the entry itself exists before calling.
/// </summary>
public static class StartupApprovedToggle
{
    public static ActionResult Apply(
        RegistryKey root, string approvedRoot, StartupProvider provider, string name, bool enable, long nowFileTime, out StartupOriginal? original)
    {
        original = null;
        var sub = StartupApprovedCodec.ApprovedSubKey(provider);
        if (sub is null)
        {
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.StartupListOnly);
        }

        var named = ("name", name);
        try
        {
            var path = approvedRoot + "\\" + sub;
            var current = Read(root, path, name, out var kind, out var bytes);
            if (current == ApprovalState.Unknown)
            {
                return ActionResult.Of(OperationCode.Unsupported, MessageKeys.StartupUnknownState, named);
            }

            if (enable)
            {
                if (current == ApprovalState.Enabled)
                {
                    return ActionResult.Of(OperationCode.Ok, MessageKeys.StartupAlreadyEnabled, named);
                }
            }
            else if (current == ApprovalState.Disabled)
            {
                return ActionResult.Of(OperationCode.Ok, MessageKeys.StartupAlreadyDisabled, named);
            }

            original = new StartupOriginal(bytes is not null, kind, bytes);
            using var key = root.CreateSubKey(path, writable: true);
            key.SetValue(name, enable ? StartupApprovedCodec.EncodeEnabled() : StartupApprovedCodec.EncodeDisabled(nowFileTime), RegistryValueKind.Binary);

            // Read it back: "the call returned" is not "the value is what we meant".
            var after = Read(root, path, name, out _, out _);
            if (after != (enable ? ApprovalState.Enabled : ApprovalState.Disabled))
            {
                return ActionResult.Of(OperationCode.Error, MessageKeys.StartupError, ("code", "not-applied")).WithData(original.ToData());
            }

            return ActionResult.Of(OperationCode.Ok, enable ? MessageKeys.StartupEnabled : MessageKeys.StartupDisabled, named)
                .WithData(original.ToData());
        }
        catch (UnauthorizedAccessException)
        {
            return ActionResult.Of(OperationCode.Denied, MessageKeys.StartupDenied, named);
        }
        catch (SecurityException)
        {
            return ActionResult.Of(OperationCode.Denied, MessageKeys.StartupDenied, named);
        }
        catch (IOException ex)
        {
            return ActionResult.Of(OperationCode.Error, MessageKeys.StartupError, ("code", ex.HResult.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Puts the value back as it was: the exact type and bytes, or gone if it did not exist.</summary>
    public static ActionResult Restore(
        RegistryKey root, string approvedRoot, StartupProvider provider, string name, StartupOriginal original)
    {
        var sub = StartupApprovedCodec.ApprovedSubKey(provider);
        if (sub is null)
        {
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.StartupListOnly);
        }

        var named = ("name", name);
        try
        {
            var path = approvedRoot + "\\" + sub;
            if (original.Existed)
            {
                if (original.Bytes is null)
                {
                    return ActionResult.Of(OperationCode.Error, MessageKeys.StartupError, ("code", "no-original-bytes"));
                }

                using var key = root.CreateSubKey(path, writable: true);
                key.SetValue(name, original.Bytes, original.Kind);
            }
            else
            {
                using var key = root.OpenSubKey(path, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }

            return ActionResult.Of(OperationCode.Ok, MessageKeys.StartupRestored, named);
        }
        catch (UnauthorizedAccessException)
        {
            return ActionResult.Of(OperationCode.Denied, MessageKeys.StartupDenied, named);
        }
        catch (SecurityException)
        {
            return ActionResult.Of(OperationCode.Denied, MessageKeys.StartupDenied, named);
        }
    }

    /// <summary>The approval state of one entry. A missing key or value is "enabled" (Windows runs it).</summary>
    public static ApprovalState Read(RegistryKey root, string path, string name, out RegistryValueKind kind, out byte[]? bytes)
    {
        kind = RegistryValueKind.None;
        bytes = null;
        using var key = root.OpenSubKey(path, writable: false);
        var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key is null || raw is null)
        {
            return ApprovalState.Enabled;
        }

        kind = key.GetValueKind(name);
        bytes = raw as byte[];
        return kind != RegistryValueKind.Binary || bytes is null ? ApprovalState.Unknown : StartupApprovedCodec.Decode(kind, bytes);
    }
}

/// <summary>Whether the entry itself is there (the Helper refuses to create an approval for something that does not exist).</summary>
public static class StartupEntryProbe
{
    public static bool Exists(StartupProvider provider, string name)
    {
        switch (provider)
        {
            case StartupProvider.RunHklm:
                return RunValueExists(RegistryView.Registry64, name);
            case StartupProvider.RunHklm32:
                return RunValueExists(RegistryView.Registry32, name);
            case StartupProvider.StartupFolderCommon:
                {
                    var folder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                    if (string.IsNullOrEmpty(folder) || name.AsSpan().IndexOfAny('\\', '/', ':') >= 0)
                    {
                        return false;
                    }

                    return File.Exists(Path.Combine(folder, name));
                }

            default:
                return false;
        }
    }

    private static bool RunValueExists(RegistryView view, string name)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var run = hive.OpenSubKey(StartupApprovedCodec.RunKeyPath, writable: false);
        return run?.GetValue(name) is not null;
    }
}
