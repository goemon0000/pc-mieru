using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>One startup entry as found, before approval state and the user-facing record are added.</summary>
/// <param name="Provider">Where it was found.</param>
/// <param name="Name">The registry value name, the file name in the Startup folder, or the task name.</param>
/// <param name="Command">What it runs (the registry text unexpanded, the shortcut's target, the task's first action).</param>
/// <param name="IdName">The part after "provider:" in the id when it differs from the name (tasks and packaged apps).</param>
/// <param name="ListedState">The state for entries that are only listed (the approval store does not apply to them).</param>
internal sealed record RawStartupEntry(
    StartupProvider Provider,
    string Name,
    string Command,
    string? IdName = null,
    StartupState? ListedState = null);

/// <summary>Where each place's entries are read from. Tests point this at a temporary key and folder.</summary>
internal interface IStartupSource
{
    IReadOnlyList<RawStartupEntry> Read(StartupProvider provider, CancellationToken ct);
}

/// <summary>
/// The registry keys and folders behind each <see cref="StartupProvider"/>. The default is the real machine; the tests
/// (which must never touch a real startup entry) give a temporary <c>HKCU\Software\PcMieruTest</c> subtree instead.
/// </summary>
internal sealed class StartupLocations
{
    /// <summary>The hive the Run key of that provider is below. The caller disposes it.</summary>
    public required Func<StartupProvider, RegistryKey?> OpenRunRoot { get; init; }

    public required Func<StartupProvider, string> RunPath { get; init; }

    /// <summary>The hive the StartupApproved store of that provider is below. The caller disposes it.</summary>
    public required Func<StartupProvider, RegistryKey?> OpenApprovedRoot { get; init; }

    public required Func<StartupProvider, string> ApprovedRoot { get; init; }

    public string? UserFolder { get; init; }

    public string? CommonFolder { get; init; }

    public static StartupLocations System() => new()
    {
        OpenRunRoot = provider => provider switch
        {
            StartupProvider.RunHkcu => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            StartupProvider.RunHklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            StartupProvider.RunHklm32 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
            _ => null,
        },
        RunPath = _ => StartupApprovedCodec.RunKeyPath,

        // Explorer (and Task Manager) keep every approval in the 64-bit view, "Run32" included.
        OpenApprovedRoot = provider => StartupManager.IsMachineSide(provider)
            ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        ApprovedRoot = _ => StartupApprovedCodec.ApprovedRoot,
        UserFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        CommonFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
    };
}

/// <summary>The real entry source: registry Run keys, Startup folders, scheduled tasks and packaged-app startup tasks. Read-only.</summary>
internal sealed class SystemStartupSource : IStartupSource
{
    private const string PackagedBase = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private readonly StartupLocations _locations;

    public SystemStartupSource(StartupLocations locations) => _locations = locations;

    public IReadOnlyList<RawStartupEntry> Read(StartupProvider provider, CancellationToken ct)
    {
        switch (provider)
        {
            case StartupProvider.RunHkcu:
            case StartupProvider.RunHklm:
            case StartupProvider.RunHklm32:
                using (var root = _locations.OpenRunRoot(provider))
                {
                    return StartupReaders.ReadRun(root, _locations.RunPath(provider), provider);
                }

            case StartupProvider.StartupFolderUser:
                return StartupReaders.ReadFolder(_locations.UserFolder, provider, StartupReaders.ShortcutCommand);
            case StartupProvider.StartupFolderCommon:
                return StartupReaders.ReadFolder(_locations.CommonFolder, provider, StartupReaders.ShortcutCommand);
            case StartupProvider.ScheduledTask:
                return StartupReaders.ReadScheduledTasks(ct);
            case StartupProvider.PackagedApp:
                using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                {
                    return StartupReaders.ReadPackaged(hkcu, PackagedBase);
                }

            default:
                return [];
        }
    }
}

/// <summary>The readers themselves, static so a test can hand each one a temporary key, folder or link resolver.</summary>
internal static class StartupReaders
{
    private static readonly string[] SkippedPackagedKeys = ["HAM", "PSR", "Schemas", "SplashScreen"];

    /// <summary>
    /// Run key values of a string kind (REG_SZ, REG_EXPAND_SZ); the default value and any other kind are not startup
    /// entries (Windows ignores them). The command is the registry text, environment variables NOT expanded.
    /// </summary>
    public static IReadOnlyList<RawStartupEntry> ReadRun(RegistryKey? root, string path, StartupProvider provider)
    {
        var entries = new List<RawStartupEntry>();
        try
        {
            using var key = root?.OpenSubKey(path, writable: false);
            if (key is null)
            {
                return entries;
            }

            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0)
                {
                    continue;
                }

                try
                {
                    var kind = key.GetValueKind(name);
                    if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    {
                        continue;
                    }

                    entries.Add(new RawStartupEntry(
                        provider, name, key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? ""));
                }
                catch (IOException)
                {
                    // The value went away between listing and reading: it is not an entry any more.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable key is an empty place, never a reason to fail the whole list.
        }

        return entries;
    }

    public static IReadOnlyList<RawStartupEntry> ReadFolder(string? folder, StartupProvider provider, Func<string, string?> shortcutCommand)
    {
        var entries = new List<RawStartupEntry>();
        if (string.IsNullOrEmpty(folder))
        {
            return entries;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var command = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? shortcutCommand(file) ?? file
                    : file;
                entries.Add(new RawStartupEntry(provider, name, command));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read has no entries we can show.
        }

        return entries;
    }

    /// <summary>The target of a .lnk (WScript.Shell, late-bound), or null when it cannot be read.</summary>
    public static string? ShortcutCommand(string linkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(linkPath);
                var target = (string?)link.TargetPath ?? "";
                var arguments = (string?)link.Arguments ?? "";
                var text = (target + " " + arguments).Trim();
                return text.Length == 0 ? null : text;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Tasks that start at boot or logon, outside <c>\Microsoft\Windows\</c> (Windows' own housekeeping would bury the
    /// user's entries). List-only: Task Scheduler is not changed from here. A task whose definition cannot be read is
    /// not listed, since its triggers are unknown.
    /// </summary>
    public static IReadOnlyList<RawStartupEntry> ReadScheduledTasks(CancellationToken ct)
    {
        var entries = new List<RawStartupEntry>();
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return entries;
            }

            dynamic service = Activator.CreateInstance(type)!;
            try
            {
                service.Connect();
                dynamic root = service.GetFolder("\\");
                WalkFolder(root, entries, ct, 0);
            }
            finally
            {
                Marshal.FinalReleaseComObject(service);
            }
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            // The scheduler is not reachable: no task entries, the rest of the list is unaffected.
        }

        return entries;
    }

    private static void WalkFolder(dynamic folder, List<RawStartupEntry> entries, CancellationToken ct, int depth)
    {
        string folderPath = folder.Path;
        if (depth > 8 || folderPath.StartsWith(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var task in folder.GetTasks(1))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (StartsAtLogon(task.Definition))
                {
                    string path = task.Path;
                    string name = task.Name;
                    bool enabled = task.Enabled;
                    entries.Add(new RawStartupEntry(
                        StartupProvider.ScheduledTask,
                        name,
                        FirstCommand(task.Definition),
                        path,
                        enabled ? StartupState.Enabled : StartupState.Disabled));
                }
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                // This one task cannot be read (a task of another account, for example): skip it.
            }
        }

        foreach (var sub in folder.GetFolders(0))
        {
            WalkFolder(sub, entries, ct, depth + 1);
        }
    }

    // TASK_TRIGGER_BOOT = 8, TASK_TRIGGER_LOGON = 9.
    private static bool StartsAtLogon(dynamic definition)
    {
        foreach (var trigger in definition.Triggers)
        {
            int type = trigger.Type;
            bool enabled = trigger.Enabled;
            if (enabled && type is 8 or 9)
            {
                return true;
            }
        }

        return false;
    }

    // TASK_ACTION_EXEC = 0.
    private static string FirstCommand(dynamic definition)
    {
        foreach (var action in definition.Actions)
        {
            int type = action.Type;
            if (type == 0)
            {
                string path = action.Path ?? "";
                string arguments = action.Arguments ?? "";
                return (path + " " + arguments).Trim();
            }
        }

        return "";
    }

    /// <summary>
    /// Packaged (Store) apps' startup tasks, as Task Manager lists them: <c>SystemAppData\&lt;family&gt;\&lt;task&gt;</c>
    /// with a REG_DWORD "State" (0 disabled, 1 disabled by user, 2 enabled, 3 disabled by policy, 4 enabled by policy).
    /// List-only. The id is "packagedApp:&lt;family&gt;/&lt;task&gt;"; the command column carries the package family name.
    /// </summary>
    public static IReadOnlyList<RawStartupEntry> ReadPackaged(RegistryKey? root, string basePath)
    {
        var entries = new List<RawStartupEntry>();
        try
        {
            using var baseKey = root?.OpenSubKey(basePath, writable: false);
            if (baseKey is null)
            {
                return entries;
            }

            foreach (var family in baseKey.GetSubKeyNames())
            {
                using var familyKey = baseKey.OpenSubKey(family, writable: false);
                if (familyKey is null)
                {
                    continue;
                }

                foreach (var taskId in familyKey.GetSubKeyNames())
                {
                    if (SkippedPackagedKeys.Contains(taskId, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var taskKey = familyKey.OpenSubKey(taskId, writable: false);
                    if (taskKey is null || !HasStateValue(taskKey, out var state))
                    {
                        continue;
                    }

                    entries.Add(new RawStartupEntry(StartupProvider.PackagedApp, taskId, family, family + "/" + taskId, MapPackagedState(state)));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable: nothing to list from here.
        }

        return entries;
    }

    internal static StartupState MapPackagedState(int state) => state switch
    {
        0 or 1 or 3 => StartupState.Disabled,
        2 or 4 => StartupState.Enabled,
        _ => StartupState.Unknown,
    };

    private static bool HasStateValue(RegistryKey key, out int state)
    {
        state = 0;
        try
        {
            if (key.GetValue("State") is int value && key.GetValueKind("State") == RegistryValueKind.DWord)
            {
                state = value;
                return true;
            }
        }
        catch (IOException)
        {
            // Treated as "not a startup task".
        }

        return false;
    }

    private static bool IsComFailure(Exception ex) =>
        ex is COMException or InvalidCastException or NotSupportedException or InvalidOperationException
            or FileNotFoundException or UnauthorizedAccessException or System.Reflection.TargetInvocationException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
}
