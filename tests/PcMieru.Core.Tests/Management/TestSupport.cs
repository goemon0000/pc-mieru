using System.Diagnostics;
using Microsoft.Win32;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

/// <summary>
/// The shared parent <c>HKCU\Software\PcMieruTest</c> that every throw-away registry world lives under. xUnit runs test
/// classes in parallel, so one world used to delete the parent ("when it looks empty") while another was creating its
/// own key under it: the delete threw "key has subkeys", or the create failed under the other's feet. Worlds now take
/// the parent before they create anything and give it back when they are gone, and the parent is removed only by the
/// last one, under the same lock the takers use, so it can never be removed while somebody is about to use it.
/// </summary>
internal static class TestRegistryRoot
{
    public const string Key = @"Software\PcMieruTest";

    private static readonly object Gate = new();
    private static int _users;

    public static void Take()
    {
        lock (Gate)
        {
            _users++;
        }
    }

    public static void Release(string worldKey)
    {
        lock (Gate)
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            hkcu.DeleteSubKeyTree(worldKey, throwOnMissingSubKey: false);
            if (--_users == 0)
            {
                // Only an empty parent goes. If something else (an earlier crashed run) left a key in it, it stays.
                using var parent = hkcu.OpenSubKey(Key);
                if (parent is { SubKeyCount: 0 })
                {
                    hkcu.DeleteSubKey(Key, throwOnMissingSubKey: false);
                }
            }
        }
    }
}

/// <summary>
/// A <c>ping -n 120 127.0.0.1</c> that this test started itself. It is the only process the tests ever act on:
/// terminate, priority and efficiency tests all run against it, and it is killed on dispose whatever happened.
/// </summary>
internal sealed class OwnPing : IDisposable
{
    private readonly Process _process;

    public OwnPing()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        _process = Process.Start(info) ?? throw new InvalidOperationException("ping did not start");
        Pid = (uint)_process.Id;
        CreateTime = _process.StartTime.ToFileTimeUtc();
    }

    public uint Pid { get; }

    public long CreateTime { get; }

    public bool HasExited
    {
        get
        {
            _process.Refresh();
            return _process.HasExited;
        }
    }

    public System.Diagnostics.ProcessPriorityClass Priority
    {
        get
        {
            _process.Refresh();
            return _process.PriorityClass;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}

internal sealed class FakeClock : ITransitionClock
{
    public long NowMs { get; private set; } = 1_000;

    public int Delays { get; private set; }

    public void Delay(int milliseconds)
    {
        NowMs += milliseconds;
        Delays++;
    }

    public void Advance(long milliseconds) => NowMs += milliseconds;
}

/// <summary>
/// A scripted service. Before the first Start/Stop the service looks like <see cref="Initial"/>; after it, the
/// snapshot of the last script entry whose offset (ms since the action) has passed. Nothing real is touched.
/// </summary>
internal sealed class FakeServiceApi(FakeClock clock) : IServiceControlApi
{
    private long? _actionAt;

    public ServiceSnapshot Initial { get; set; }

    public List<(long AfterMs, ServiceSnapshot Snapshot)> Script { get; } = [];

    public int QueryError { get; set; }

    public int StartError { get; set; }

    public int StopError { get; set; }

    public IReadOnlyList<string> Dependents { get; set; } = [];

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public int Query(string name, out ServiceSnapshot snapshot)
    {
        snapshot = Current();
        return QueryError;
    }

    public int Start(string name)
    {
        StartCalls++;
        if (StartError == 0)
        {
            _actionAt = clock.NowMs;
        }

        return StartError;
    }

    public int Stop(string name)
    {
        StopCalls++;
        if (StopError == 0)
        {
            _actionAt = clock.NowMs;
        }

        return StopError;
    }

    public int RunningDependents(string name, out IReadOnlyList<string> dependents)
    {
        dependents = Dependents;
        return 0;
    }

    private ServiceSnapshot Current()
    {
        if (_actionAt is not { } at)
        {
            return Initial;
        }

        var snapshot = Initial;
        foreach (var (afterMs, entry) in Script)
        {
            if (clock.NowMs - at >= afterMs)
            {
                snapshot = entry;
            }
        }

        return snapshot;
    }

    public static ServiceSnapshot Stopped(uint exitCode = 0) => new(ServiceSnapshot.Stopped, 0, 0, 0, 0, exitCode);

    public static ServiceSnapshot Running() => new(ServiceSnapshot.Running, 0, 0, 1234, ServiceSnapshot.AcceptStop, 0);

    public static ServiceSnapshot StartPending(uint checkpoint, uint waitHint = 3000) => new(ServiceSnapshot.StartPending, checkpoint, waitHint, 0, 0, 0);

    public static ServiceSnapshot StopPending(uint checkpoint, uint waitHint = 3000) => new(ServiceSnapshot.StopPending, checkpoint, waitHint, 1234, 0, 0);
}
