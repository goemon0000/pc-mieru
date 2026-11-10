using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PcMieru.Core.Collection;
using PcMieru.Core.Management;
using PcMieru.Wire;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.NetMeter;

/// <summary>
/// The network meter for real: the exe is started, it opens its ETW session, and the counters are read over the pipe. Nothing
/// here is faked. Run from an ELEVATED shell: the meter needs administrator rights, and these tests deliberately do not start
/// it through a UAC prompt (a prompt cannot be answered by a test run), so they fail, with the reason, when the shell is not.
///
/// One class and one collection: the ETW session has one name per user (<c>PCMieru.NetMeter.&lt;sid&gt;</c>), a second meter
/// would take the first one's session, and the tests assert about that very session. Every test leaves the machine as it found
/// it: no meter process, no session (checked, and cleaned up even when a test fails half way).
/// </summary>
[Trait("Category", "RealMachine")]
[Collection(NetMeterRealMachineCollection.Name)]
public sealed class NetMeterRealMachineTests : IDisposable
{
    private const int FourMiB = 4 * 1024 * 1024;
    private const int SteadySamples = 8;
    private const string MeterProcessName = "PCMieru.NetMeter";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly ITestOutputHelper _output;
    private readonly List<NetMeterControl> _controls = [];
    private readonly List<Process> _children = [];
    private readonly string _sessionName = NetMeterArguments.SessionNameFor(NetMeterIdentity.Current().UserSid);

    public NetMeterRealMachineTests(ITestOutputHelper output)
    {
        _output = output;

        using (var identity = WindowsIdentity.GetCurrent())
        {
            Assert.True(
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
                "These tests start the real network meter and read its ETW session, which needs administrator rights. "
                + "Run them from an elevated shell (Run as administrator); a prompt cannot be answered by a test run.");
        }

        Assert.True(File.Exists(MeterExe), $"{MeterExe} is missing. A dev build puts it next to the test binaries (build/NetMeterNextToApp.targets); build the solution first.");
        Assert.True(
            Process.GetProcessesByName(MeterProcessName).Length == 0,
            "A network meter is already running. Starting another takes over its ETW session, so these tests will not run next to a live one.");
    }

    private static string MeterExe => Path.Combine(AppContext.BaseDirectory, NetMeterControl.ExeName);

    public void Dispose()
    {
        foreach (var control in _controls)
        {
            control.Dispose();
        }

        // Whatever a failed test left. Only this test's own children and meters, and only the one session named after the user.
        foreach (var child in _children)
        {
            Kill(child);
            child.Dispose();
        }

        foreach (var meter in Process.GetProcessesByName(MeterProcessName))
        {
            Kill(meter);
            meter.Dispose();
        }

        if (EtwProbe.Exists(_sessionName))
        {
            _ = EtwProbe.Stop(_sessionName);
        }
    }

    [Fact]
    public async Task The_meter_counts_this_process_traffic_and_Stop_removes_the_ETW_session()
    {
        var control = NewControl();

        var state = await control.StartAsync();
        Assert.True(state.Status == NetMeterStatus.Running, "the meter did not come up: " + Describe(state));
        var meter = AliveMeter();
        Assert.True(EtwProbe.Exists(_sessionName), "the meter is Running but its ETW session " + _sessionName + " does not exist");

        var reader = (INetMeterReader)control;
        var me = NetMeterIdentity.Current();
        var before = ReadOwn(reader, me);
        await LoopbackAsync(FourMiB);
        var after = await WaitForOwnAsync(reader, me, before, FourMiB);

        var sent = after.Send - before.Send;
        var received = after.Receive - before.Receive;
        _output.WriteLine($"meter pid {meter.Id}: 4 MiB over a loopback socket pair = {FourMiB} bytes; own send counter grew by {sent}, receive by {received}");
        Assert.True(sent >= FourMiB, $"send counter grew by {sent} bytes, expected at least {FourMiB}");
        Assert.True(received >= FourMiB, $"receive counter grew by {received} bytes, expected at least {FourMiB}");
        // Both ends are in this process, so each direction is counted once. Twice would be the duplicate TCP copy event.
        Assert.True(sent <= FourMiB * 3 / 2, $"send counter grew by {sent} bytes for {FourMiB}: counted more than once?");
        Assert.True(received <= FourMiB * 3 / 2, $"receive counter grew by {received} bytes for {FourMiB}: counted more than once?");

        control.Stop();

        Assert.Equal(NetMeterStatus.Off, control.State.Status);
        Assert.True(meter.WaitForExit((int)Patience.TotalMilliseconds), "the meter did not end after Stop");
        Assert.Equal(0u, ExitCodeOf(meter)); // MeterExit.Normal
        Assert.False(EtwProbe.Exists(_sessionName), "the ETW session " + _sessionName + " is still there after Stop");
    }

    [Fact]
    public async Task A_killed_meter_leaves_its_session_and_the_next_start_takes_it_over()
    {
        var first = NewControl();
        var firstState = await first.StartAsync();
        Assert.True(firstState.Status == NetMeterStatus.Running, "the first meter did not come up: " + Describe(firstState));
        var killed = AliveMeter();
        var killedPid = killed.Id;

        killed.Kill();
        Assert.True(killed.WaitForExit(10_000), "the killed meter is still running");

        // The premise of the whole take-over: a session belongs to the system, not to the process that opened it.
        Assert.True(
            EtwProbe.Exists(_sessionName),
            "the ETW session is gone together with the killed meter, so there is no orphan to take over (and nothing to test)");
        first.Stop();

        var second = NewControl();
        var secondState = await second.StartAsync();
        Assert.True(secondState.Status == NetMeterStatus.Running, "the next start did not take over the orphaned session: " + Describe(secondState));
        var replacement = AliveMeter();
        Assert.NotEqual(killedPid, replacement.Id);
        Assert.True(EtwProbe.Exists(_sessionName));

        // The taken-over session really delivers: 1 MiB over loopback is counted.
        var reader = (INetMeterReader)second;
        var me = NetMeterIdentity.Current();
        var before = ReadOwn(reader, me);
        await LoopbackAsync(1024 * 1024);
        var after = await WaitForOwnAsync(reader, me, before, 1024 * 1024);
        _output.WriteLine($"replacement meter pid {replacement.Id} (killed was {killedPid}): own send grew by {after.Send - before.Send}, receive by {after.Receive - before.Receive}");

        second.Stop();
        Assert.True(replacement.WaitForExit((int)Patience.TotalMilliseconds), "the replacement meter did not end after Stop");
        Assert.False(EtwProbe.Exists(_sessionName), "an orphan session is left after the replacement stopped");
    }

    [Fact]
    public void A_meter_whose_parent_exits_ends_and_takes_its_session_with_it()
    {
        var parent = StartParent();
        var meter = StartMeterByHand(parent, createdOffset: 0);
        Assert.True(WaitFor(() => EtwProbe.Exists(_sessionName), TimeSpan.FromSeconds(10)), "the meter did not open its ETW session (exit code " + ExitCodeText(meter) + ")");
        Assert.False(meter.HasExited);

        Kill(parent);

        Assert.True(meter.WaitForExit((int)Patience.TotalMilliseconds), "the meter is still running after its parent exited");
        Assert.Equal(0, meter.ExitCode);
        Assert.False(EtwProbe.Exists(_sessionName), "the ETW session is still there after the meter ended");
    }

    [Fact]
    public void A_meter_that_is_not_started_by_the_process_it_names_refuses_and_opens_no_session()
    {
        var parent = StartParent();

        // The right pid, a creation time that is off by one: a recycled pid must not be taken for the parent.
        var meter = StartMeterByHand(parent, createdOffset: 1);

        Assert.True(meter.WaitForExit((int)Patience.TotalMilliseconds), "the meter went on although its parent's creation time did not match");
        Assert.Equal(3, meter.ExitCode); // MeterExit.NotOurParent
        Assert.False(EtwProbe.Exists(_sessionName), "a refused meter left an ETW session");
    }

    [Fact]
    public async Task The_sampling_stage_with_the_real_control_gives_this_process_its_rates_and_the_keys_agree()
    {
        // The public constructor: the exe next to the app, the real elevation check (elevated here, so a plain start).
        var control = new NetMeterControl();
        _controls.Add(control);
        var state = await control.StartAsync();
        Assert.True(state.Status == NetMeterStatus.Running, "the meter did not come up: " + Describe(state));

        using var source = new WindowsSnapshotSource(
            static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = false }, null, new SignatureVerifier(), () => control);
        var me = (uint)Environment.ProcessId;

        // A steady stream: 4 MiB/s over loopback, paced, running for a while before the baseline so that every window after
        // the baseline lies entirely inside it. Every sample must then read about 4 MiB/s. Not the best of them and not their
        // mean: a mean hides a rate that swings (events handed over in batches at a different time than they happened gave
        // 0, 2, 4.6, 4, 1.8, 6.4 MB/s while the totals were right). A key that does not match gives 0 for ever (the process
        // is "alive and absent from the meter"), so a rate near 4 MiB/s also proves that the meter's (pid, creation time)
        // is the stage's key.
        using var stop = new CancellationTokenSource();
        var stream = Task.Run(() => PacedLoopbackAsync(FourMiB, stop.Token));
        await Task.Delay(2500);

        var first = await source.SampleAsync();
        var firstOwn = first.Processes.Single(p => p.Key.Pid == me);
        Assert.Equal(ValueStatus.NotCollected, firstOwn.NetSendBytesPerSec.Status); // the first sample is only a baseline

        ProcessInfo own = firstOwn;
        var sends = new List<double>();
        var receives = new List<double>();
        for (var i = 0; i < SteadySamples; i++)
        {
            await Task.Delay(1000); // about as often as the app samples
            var snapshot = await source.SampleAsync();
            own = snapshot.Processes.Single(p => p.Key.Pid == me);
            sends.Add(own.NetSendBytesPerSec.IsOk ? own.NetSendBytesPerSec.Data : double.NaN);
            receives.Add(own.NetReceiveBytesPerSec.IsOk ? own.NetReceiveBytesPerSec.Data : double.NaN);
        }

        stop.Cancel();
        await stream.WaitAsync(Patience);

        var sendText = string.Join(' ', sends.Select(v => (v / (1024 * 1024)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        var receiveText = string.Join(' ', receives.Select(v => (v / (1024 * 1024)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        _output.WriteLine($"a paced 4.00 MiB/s loopback stream, own rate per sample: send [{sendText}] receive [{receiveText}] MiB/s");
        var low = FourMiB * 0.7;
        var high = FourMiB * 1.3;
        Assert.True(
            sends.All(v => v >= low && v <= high),
            $"send rates are not steady (every sample must be within 30% of 4 MiB/s): [{sendText}] MiB/s; last status {own.NetSendBytesPerSec.Status}: {own.NetSendBytesPerSec.Detail}");
        Assert.True(
            receives.All(v => v >= low && v <= high),
            $"receive rates are not steady (every sample must be within 30% of 4 MiB/s): [{receiveText}] MiB/s; last status {own.NetReceiveBytesPerSec.Status}: {own.NetReceiveBytesPerSec.Detail}");

        // The meter's key for this process is the stage's key, byte for byte.
        var reader = (INetMeterReader)control;
        Assert.True(reader.TryRead(TimeSpan.FromSeconds(3), out var reading, out _));
        var entry = Assert.Single(reading!.Entries, e => e.Pid == me);
        Assert.Equal(own.Key.CreateTime, entry.CreationFileTime);
        Assert.Equal(NetMeterIdentity.Current().CreatedFileTime, entry.CreationFileTime);

        control.Stop();
        Assert.True(WaitFor(() => !EtwProbe.Exists(_sessionName), Patience), "the ETW session is still there after Stop");
        Assert.True(WaitFor(() => Process.GetProcessesByName(MeterProcessName).Length == 0, Patience), "the meter process is still there after Stop");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private NetMeterControl NewControl()
    {
        var control = new NetMeterControl(
            new SystemNetMeterLauncher(),
            new NamedPipeNetMeterChannelFactory(),
            MeterExe,
            ElevationCheck.IsElevated,
            NetMeterIdentity.Current,
            File.Exists,
            NetMeterTimings.Default);
        _controls.Add(control);
        return control;
    }

    private static string Describe(NetMeterState state) => state.Status + (state.Detail is null ? string.Empty : ": " + state.Detail);

    private static Process AliveMeter()
    {
        Process[] meters = [];
        Assert.True(WaitFor(() => (meters = Process.GetProcessesByName(MeterProcessName)).Length > 0, TimeSpan.FromSeconds(10)), "no meter process is running");
        var meter = Assert.Single(meters);
        _ = meter.SafeHandle; // opened now, while it is alive, so its exit code can still be read after it ended
        return meter;
    }

    // Process.ExitCode refuses a process this object did not start (the control started it, through ShellExecute).
    [DllImport("kernel32.dll", ExactSpelling = true)]
#pragma warning disable SYSLIB1054 // a test-only call; no unsafe code in this project
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
#pragma warning restore SYSLIB1054

    private static uint ExitCodeOf(Process process)
    {
        Assert.True(GetExitCodeProcess(process.SafeHandle, out var code), "cannot read the exit code of the meter");
        return code;
    }

    /// <summary>A throwaway process that sleeps, to be a parent that can be made to exit.</summary>
    private Process StartParent()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException("could not start the stand-in parent");
        _children.Add(process);
        return process;
    }

    /// <summary>Starts the meter exe directly (we are elevated), naming <paramref name="parent"/> as the front end.</summary>
    private Process StartMeterByHand(Process parent, long createdOffset)
    {
        Assert.True(NetMeterNativeTimes(parent, out var created), "cannot read the stand-in parent's creation time");
        var arguments = NetMeterArguments.BuildCommandLine(
            (uint)parent.Id, created + createdOffset, Guid.NewGuid().ToString("N"), NetMeterIdentity.Current().UserSid);
        var info = new ProcessStartInfo(MeterExe, arguments) { UseShellExecute = false, CreateNoWindow = true };
        var meter = Process.Start(info) ?? throw new InvalidOperationException("could not start the meter");
        _children.Add(meter);
        return meter;
    }

    private static bool NetMeterNativeTimes(Process process, out long created) =>
        NetMeterNative.GetProcessTimes(process.Handle, out created, out _, out _, out _);

    private static string ExitCodeText(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "still running";
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                _ = process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (clock.Elapsed >= limit)
            {
                return false;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>The counters of this process as the meter has them; 0/0 for a process it has not seen (yet).</summary>
    private static (ulong Send, ulong Receive) ReadOwn(INetMeterReader reader, NetMeterIdentity me)
    {
        Assert.True(reader.TryRead(TimeSpan.FromSeconds(3), out var reading, out _), "the meter did not answer a read");
        Assert.True(reading!.SessionHealthy, "the meter reports its ETW session unhealthy (error " + reading.ErrorCode + ")");
        Assert.Equal(0UL, reading.Undecodable);
        var entries = reading.Entries.Where(e => e.Pid == me.ProcessId).ToArray();
        if (entries.Length == 0)
        {
            return (0, 0);
        }

        var entry = Assert.Single(entries);
        Assert.Equal(me.CreatedFileTime, entry.CreationFileTime);
        return (entry.SendBytes, entry.ReceiveBytes);
    }

    /// <summary>Reads until both of this process's counters have grown by at least <paramref name="bytes"/> since <paramref name="before"/>, or the time is up.</summary>
    private static async Task<(ulong Send, ulong Receive)> WaitForOwnAsync(INetMeterReader reader, NetMeterIdentity me, (ulong Send, ulong Receive) before, int bytes)
    {
        var clock = Stopwatch.StartNew();
        var last = before;
        while (clock.Elapsed < Patience)
        {
            // The meter reports counters up to about 0.75 s ago (it waits for events still in flight).
            await Task.Delay(500);
            last = ReadOwn(reader, me);
            if (last.Send - before.Send >= (ulong)bytes && last.Receive - before.Receive >= (ulong)bytes)
            {
                break;
            }
        }

        return last;
    }

    /// <summary>
    /// Sends over a TCP connection to a listener in this very process at <paramref name="bytesPerSecond"/> (paced against a
    /// stopwatch, so the pacing does not drift) until <paramref name="stop"/> is cancelled.
    /// </summary>
    private static async Task PacedLoopbackAsync(int bytesPerSecond, CancellationToken stop)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            var inbound = accepted.GetStream();
            var buffer = new byte[64 * 1024];
            while (await inbound.ReadAsync(buffer) > 0)
            {
            }
        });

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var outbound = client.GetStream();
            var chunk = new byte[16 * 1024];
            var clock = Stopwatch.StartNew();
            long sent = 0;
            while (!stop.IsCancellationRequested)
            {
                var due = (long)(clock.Elapsed.TotalSeconds * bytesPerSecond);
                while (sent + chunk.Length <= due)
                {
                    await outbound.WriteAsync(chunk);
                    sent += chunk.Length;
                }

                await Task.Delay(5);
            }
        }

        await receiver.WaitAsync(Patience);
    }

    /// <summary>Sends <paramref name="bytes"/> over a TCP connection to a listener in this very process, and waits until all of it has arrived.</summary>
    private static async Task LoopbackAsync(int bytes)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            var inbound = accepted.GetStream();
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (total < bytes)
            {
                var read = await inbound.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var outbound = client.GetStream();
        var chunk = new byte[64 * 1024];
        for (var sent = 0; sent < bytes; sent += chunk.Length)
        {
            await outbound.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, bytes - sent)));
        }

        await outbound.FlushAsync();
        Assert.Equal(bytes, await receiver.WaitAsync(Patience));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NetMeterRealMachineCollection
{
    public const string Name = "NetMeter real machine";
}

/// <summary>Asks Windows about an ETW session by its exact name. Only ever used with the meter's own session name.</summary>
internal static class EtwProbe
{
    private const int PropertiesSize = 120; // EVENT_TRACE_PROPERTIES on x64
    private const int NameBytes = 2048;
    private const uint ControlQuery = 0;
    private const uint ControlStop = 1;
    private const uint ErrorWmiInstanceNotFound = 4201;
    private const uint ErrorSuccess = 0;

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
#pragma warning disable SYSLIB1054 // a test-only probe: DllImport is simpler than LibraryImport here (no unsafe code in this project)
    private static extern uint ControlTrace(ulong sessionHandle, string sessionName, byte[] properties, uint controlCode);
#pragma warning restore SYSLIB1054

    /// <summary>True when the session exists, false when Windows says there is none; anything else (say, access denied) is an error.</summary>
    public static bool Exists(string name)
    {
        RequireOwnName(name);
        var status = ControlTrace(0, name, NewBlock(), ControlQuery);
        return status switch
        {
            ErrorSuccess => true,
            ErrorWmiInstanceNotFound => false,
            _ => throw new InvalidOperationException($"ControlTrace(QUERY, {name}) failed with {status}"),
        };
    }

    /// <summary>The safety net for a test that failed half way: stops the meter's own session by name.</summary>
    public static uint Stop(string name)
    {
        RequireOwnName(name);
        return ControlTrace(0, name, NewBlock(), ControlStop);
    }

    private static void RequireOwnName(string name)
    {
        if (!name.StartsWith(NetMeterArguments.SessionPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("only the network meter's own session may be asked about or stopped", nameof(name));
        }
    }

    private static byte[] NewBlock()
    {
        var block = new byte[PropertiesSize + (2 * NameBytes)];
        BitConverter.TryWriteBytes(block.AsSpan(0, 4), (uint)block.Length); // Wnode.BufferSize
        BitConverter.TryWriteBytes(block.AsSpan(44, 4), 0x0002_0000u); // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        BitConverter.TryWriteBytes(block.AsSpan(112, 4), (uint)(PropertiesSize + NameBytes)); // LogFileNameOffset
        BitConverter.TryWriteBytes(block.AsSpan(116, 4), (uint)PropertiesSize); // LoggerNameOffset
        return block;
    }
}
