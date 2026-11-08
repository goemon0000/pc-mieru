using System.Diagnostics;
using System.Security.Principal;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>Whether this process runs with an elevated administrator token. Never prompts.</summary>
internal static class ElevationCheck
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // If it cannot be told, behave as if elevated: the safe side for what refuses when elevated (Explorer),
            // and anything else that tries in-process simply ends as "denied".
            return true;
        }
    }
}

/// <summary>What the Explorer restart needs from the system, so the sequence can be tested with a fake that touches nothing.</summary>
internal interface IShellHost
{
    bool IsElevated { get; }

    /// <summary>The explorer.exe processes of THIS session whose image is the one in the Windows folder.</summary>
    IReadOnlyList<ProcessKey> FindOwnExplorers();

    ActionResult Terminate(ProcessKey key);

    /// <summary>True when a new explorer.exe (not one of <paramref name="oldPids"/>) of this session is running.</summary>
    bool HasNewExplorer(IReadOnlyCollection<uint> oldPids);

    bool StartExplorer();

    void Delay(int milliseconds, CancellationToken ct);
}

/// <summary>
/// Restarts Explorer the way Task Manager does: end it, let Windows (Winlogon) start the shell again, and only if that
/// does not happen start explorer.exe ourselves, as the current user. It is refused when PC Mieru is elevated, because
/// a shell started from an elevated process would be an elevated shell for the whole desktop.
/// </summary>
public sealed class ShellActions : IShellActions
{
    private const int WaitForWindowsMs = 8000;
    private const int WaitForOurStartMs = 4000;
    private const int PollMs = 250;

    private readonly IShellHost _host;

    public ShellActions()
        : this(new SystemShellHost())
    {
    }

    internal ShellActions(IShellHost host) => _host = host;

    public Task<OperationResult> RestartExplorerAsync(CancellationToken ct = default) =>
        Task.Run(() => Restart(ct), ct);

    private OperationResult Restart(CancellationToken ct)
    {
        if (_host.IsElevated)
        {
            return OperationMapper.ToResult(OperationCode.Unsupported, MessageKeys.ShellElevatedRefused);
        }

        var explorers = _host.FindOwnExplorers();
        var oldPids = explorers.Select(e => e.Pid).ToHashSet();
        var ended = 0;
        var failure = OperationCode.Ok;
        foreach (var key in explorers)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var result = _host.Terminate(key);
            if (result.Code is OperationCode.Ok or OperationCode.Gone)
            {
                ended++;
            }
            else
            {
                failure = result.Code;
            }
        }

        if (explorers.Count > 0 && ended == 0)
        {
            // Nothing could be ended (refused or denied): nothing changed, so say that and do not start anything.
            return OperationMapper.ToResult(failure == OperationCode.Ok ? OperationCode.Error : failure, MessageKeys.ShellFailed);
        }

        if (WaitFor(() => _host.HasNewExplorer(oldPids), WaitForWindowsMs, ct))
        {
            return OperationMapper.ToResult(OperationCode.Ok, MessageKeys.ShellRestarted);
        }

        if (ct.IsCancellationRequested)
        {
            return new OperationResult(OperationCode.Cancelled, "");
        }

        // Windows did not bring the shell back (AutoRestartShell off, or it is slow): start it ourselves, once.
        if (!_host.StartExplorer())
        {
            return OperationMapper.ToResult(OperationCode.Error, MessageKeys.ShellFailed);
        }

        return WaitFor(() => _host.HasNewExplorer(oldPids), WaitForOurStartMs, ct)
            ? OperationMapper.ToResult(OperationCode.Ok, MessageKeys.ShellRestarted)
            : OperationMapper.ToResult(OperationCode.Error, MessageKeys.ShellFailed);
    }

    private bool WaitFor(Func<bool> condition, int timeoutMs, CancellationToken ct)
    {
        for (var waited = 0; ; waited += PollMs)
        {
            if (condition())
            {
                return true;
            }

            if (waited >= timeoutMs || ct.IsCancellationRequested)
            {
                return false;
            }

            _host.Delay(PollMs, ct);
        }
    }
}

internal sealed unsafe class SystemShellHost : IShellHost
{
    private readonly IProcessTable _table = new ToolhelpProcessTable();
    private readonly string _explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private readonly SelfIdentity _self = ProcessOperations.CurrentSelf();
    private readonly string _systemDirectory = Environment.SystemDirectory;

    public bool IsElevated => ElevationCheck.IsElevated();

    public IReadOnlyList<ProcessKey> FindOwnExplorers()
    {
        var result = new List<ProcessKey>();
        if (!ManagementNative.ProcessIdToSessionId((uint)Environment.ProcessId, out var ourSession))
        {
            return result;
        }

        foreach (var row in _table.Rows())
        {
            if (!row.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_table.CreateTime(row.Pid) is not { } created
                || !ManagementNative.ProcessIdToSessionId(row.Pid, out var session)
                || session != ourSession
                || !string.Equals(ImagePath(row.Pid), _explorerPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new ProcessKey(row.Pid, created));
        }

        return result;
    }

    public ActionResult Terminate(ProcessKey key) => ProcessOperations.Terminate(key.Pid, key.CreateTime, _self, _systemDirectory);

    public bool HasNewExplorer(IReadOnlyCollection<uint> oldPids) =>
        FindOwnExplorers().Any(k => !oldPids.Contains(k.Pid));

    public bool StartExplorer()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_explorerPath) { UseShellExecute = false });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public void Delay(int milliseconds, CancellationToken ct) => ct.WaitHandle.WaitOne(milliseconds);

    private static string? ImagePath(uint pid)
    {
        using var handle = ManagementNative.OpenProcess(ManagementNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = stackalloc char[1024];
        var size = 1024u;
        return ManagementNative.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }
}
