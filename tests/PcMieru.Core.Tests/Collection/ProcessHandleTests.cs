using System.Diagnostics;
using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

/// <summary>A child process this test owns (a hidden ping), so nothing else on the machine is ever touched.</summary>
internal sealed class SpawnedPing : IDisposable
{
    private readonly Process _process;

    public SpawnedPing(int count = 60)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), $"-n {count} 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        _process = Process.Start(start) ?? throw new InvalidOperationException("ping did not start");
        Pid = (uint)_process.Id;
        if (!Win32.GetProcessTimes(_process.Handle, out var created, out _, out _, out _))
        {
            throw new InvalidOperationException("GetProcessTimes failed for the child");
        }

        Key = new ProcessKey(Pid, created);
    }

    public uint Pid { get; }

    public ProcessKey Key { get; }

    public void Kill()
    {
        try
        {
            _process.Kill();
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

public sealed class ProcessHandleTests(ITestOutputHelper output)
{
    [Fact]
    public void The_creation_time_of_the_key_identifies_the_process()
    {
        using var ping = new SpawnedPing();

        var outcome = ProcessHandle.Open(ping.Key, out var handle, out var detail);

        using (handle)
        {
            Assert.Equal(OpenOutcome.Opened, outcome);
            Assert.Null(detail);
            Assert.Equal(ping.Key, handle!.Key);
        }
    }

    [Fact]
    public void The_same_pid_with_another_creation_time_is_a_different_process_and_is_gone()
    {
        using var ping = new SpawnedPing();

        var outcome = ProcessHandle.Open(new ProcessKey(ping.Pid, ping.Key.CreateTime + 10_000_000), out var handle, out var detail);

        Assert.Equal(OpenOutcome.Gone, outcome);
        Assert.Null(handle);
        Assert.Contains("different process", detail);
    }

    [Fact]
    public void A_process_that_has_exited_is_gone()
    {
        var ping = new SpawnedPing();
        var key = ping.Key;
        ping.Dispose();

        var outcome = ProcessHandle.Open(key, out var handle, out _);

        handle?.Dispose();
        Assert.Equal(OpenOutcome.Gone, outcome);
    }

    [Fact]
    public void The_idle_process_is_denied_not_probed()
    {
        Assert.Equal(OpenOutcome.Denied, ProcessHandle.Open(new ProcessKey(0, 0), out var handle, out var detail));
        Assert.Null(handle);
        Assert.False(string.IsNullOrEmpty(detail));
    }

    [Fact]
    public void The_facts_of_a_running_child_are_read_from_the_process_itself()
    {
        using var ping = new SpawnedPing();
        Assert.Equal(OpenOutcome.Opened, ProcessHandle.Open(ping.Key, out var opened, out _));
        using var handle = opened!;

        var path = handle.ReadPath();
        var architecture = handle.ReadArchitecture();
        var owner = handle.ReadOwner();
        var commandLine = handle.ReadCommandLine();
        var efficiency = handle.ReadEfficiencyMode();
        output.WriteLine($"path={path.Data} arch={architecture.Data} owner={owner.Data} cmd={commandLine.Data} eff={efficiency.Status}/{efficiency.Data}/{efficiency.Detail}");

        Assert.True(path.IsOk, path.Detail);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "ping.exe"), path.Data, ignoreCase: true);
        Assert.True(architecture.IsOk, architecture.Detail);
        Assert.Equal("x64", architecture.Data); // ping.exe in System32 is the native one on this x64 machine
        Assert.True(owner.IsOk, owner.Detail);
        Assert.EndsWith("\\" + Environment.UserName, owner.Data, StringComparison.OrdinalIgnoreCase);
        Assert.True(commandLine.IsOk, commandLine.Detail);
        Assert.Contains("127.0.0.1", commandLine.Data);
        Assert.Contains("-n 60", commandLine.Data);
        Assert.True(efficiency.IsOk, efficiency.Detail);
    }

    [Fact]
    public void Facts_of_a_process_that_exits_while_they_are_read_come_back_as_Gone_not_as_an_error()
    {
        var ping = new SpawnedPing();
        Assert.Equal(OpenOutcome.Opened, ProcessHandle.Open(ping.Key, out var opened, out _));
        using var handle = opened!;
        ping.Dispose();

        Assert.True(handle.HasExited());
        var commandLine = handle.ReadCommandLine();
        var owner = handle.ReadOwner();
        var efficiency = handle.ReadEfficiencyMode();
        output.WriteLine($"after exit: cmd={commandLine.Status} owner={owner.Status} eff={efficiency.Status}");

        // Whatever the OS still answers for a dead process, it must never be reported as a generic failure.
        foreach (var status in new[] { commandLine.Status, owner.Status, efficiency.Status })
        {
            Assert.True(status is ValueStatus.Gone or ValueStatus.Ok, $"unexpected status {status}");
        }
    }

    [Fact]
    public void The_System_process_has_a_path_from_the_kernel_or_says_it_has_none()
    {
        var outcome = ProcessHandle.Open(ProcessKeyOf(4), out var opened, out var detail);
        output.WriteLine($"System: {outcome} {detail}");
        if (outcome != OpenOutcome.Opened)
        {
            Assert.Equal(OpenOutcome.Denied, outcome);
            return;
        }

        Assert.NotNull(opened);

        using var handle = opened!;
        var path = handle.ReadPath();
        output.WriteLine($"System path: {path.Status} {path.Data} {path.Detail}");
        Assert.True(path.Status is ValueStatus.Ok or ValueStatus.Unsupported or ValueStatus.Denied, path.Status.ToString());
    }

    [Fact]
    public void The_System_process_has_no_command_line_and_that_is_not_reported_as_a_failure()
    {
        var outcome = ProcessHandle.Open(ProcessKeyOf(4), out var opened, out _);
        if (outcome != OpenOutcome.Opened)
        {
            Assert.Equal(OpenOutcome.Denied, outcome); // a non-elevated run cannot open it; the denied path is covered elsewhere
            return;
        }

        using var handle = opened!;
        var commandLine = handle.ReadCommandLine();
        output.WriteLine($"System command line: {commandLine.Status} {commandLine.Data} {commandLine.Detail}");

        // It has no user-mode environment block: "does not exist" (Unsupported) or "cannot be read" (Denied), never a fault.
        Assert.True(commandLine.Status is ValueStatus.Unsupported or ValueStatus.Denied, $"{commandLine.Status}: {commandLine.Detail}");
    }

    [Fact]
    public void A_process_by_pid_alone_still_has_an_image_path()
    {
        var path = ProcessImagePath.Resolve((uint)Environment.ProcessId);

        Assert.NotNull(path);
        Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
    }

    [Fact]
    public void A_pid_that_does_not_exist_has_no_image_path()
    {
        Assert.Null(ProcessImagePath.Resolve(0x7FFFFFF0));
    }

    internal static ProcessKey ProcessKeyOf(uint pid)
    {
        var handle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            return new ProcessKey(pid, 0); // not even queryable: Open must then say Denied or Gone
        }

        try
        {
            Assert.True(Win32.GetProcessTimes(handle, out var created, out _, out _, out _));
            return new ProcessKey(pid, created);
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }
}
