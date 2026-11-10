using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace PcMieru.Gui;

internal static partial class SmokeRun
{
    private sealed partial class Runner
    {
        /// <summary>Bytes per second the loopback pair moves while the meter is checked (the sender paces itself to it).</summary>
        private const int LoopbackRate = 4 * 1000 * 1000;

        /// <summary>
        /// `--netmeter`: the end-to-end path the unit tests cannot walk. The meter button starts the real PCMieru.NetMeter.exe,
        /// this process moves a known amount of loopback traffic, and the process list must show it on this process's own row
        /// (loopback counts on both sides: about the rate sent and the rate received). Then the button stops the meter and the
        /// meter process must be gone. Real data only, and only from an elevated shell: an unattended run never raises a UAC
        /// prompt, so without elevation this fails instead of asking.
        /// </summary>
        private async Task RealNetMeter()
        {
            if (context.Services.UsesFakeData)
            {
                Check("netmeter: needs real data", false, "--netmeter was given with --fakes");
                return;
            }

            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    Check("netmeter: the shell is elevated", false, "--netmeter needs an elevated shell; the smoke run does not bring up a UAC prompt");
                    return;
                }
            }

            if (await Open("processes").ConfigureAwait(true) is not ProcessesPage processes)
            {
                Check("netmeter: the processes page opens", false, LastOpen);
                return;
            }

            context.SetNetworkUnit(NetworkUnit.AutoBits);
            Check("netmeter: the meter is off before the button is pressed", context.NetMeter.State.Status == NetMeterStatus.Off, context.NetMeter.State.Status.ToString());
            processes.MeterButton.PerformClick();
            await Until(() => context.NetMeter.State.Status is not (NetMeterStatus.Off or NetMeterStatus.Starting), 20000).ConfigureAwait(true);
            var started = context.NetMeter.State;
            Check("netmeter: the button starts the meter", started.Status == NetMeterStatus.Running, started.Status + " " + started.Detail);
            if (started.Status != NetMeterStatus.Running)
            {
                return;
            }

            using var traffic = new CancellationTokenSource();
            var loopback = Task.Run(() => Loopback(traffic.Token));
            try
            {
                var me = (uint)Environment.ProcessId;
                ProcessInfo? Own() => context.Pump.Latest?.Processes.FirstOrDefault(p => p.Key.Pid == me);

                // Every sample while the traffic runs (the first one after the start is only a baseline). A steady stream must read
                // as a steady rate: each sample's send and receive are checked, not only the best one, so a rate that swings
                // between half and double (events counted in the wrong second) fails here.
                var watch = Stopwatch.StartNew();
                var seen = new HashSet<DateTime>();
                var sends = new List<double>();
                var receives = new List<double>();
                ProcessInfo? own = null;
                while (watch.ElapsedMilliseconds < 9000 && sends.Count < 6)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                    if (context.Pump.Latest is not { } latest || !seen.Add(latest.TakenAtUtc))
                    {
                        continue;
                    }

                    own = Own();
                    if (own is not null && own.NetSendBytesPerSec.TryGet(out var s) && own.NetReceiveBytesPerSec.TryGet(out var r) && watch.ElapsedMilliseconds > 1500)
                    {
                        sends.Add(s);
                        receives.Add(r);
                    }
                }

                static bool Near(double v) => v is >= LoopbackRate * 0.7 and <= LoopbackRate * 1.3;
                static string Mb(IEnumerable<double> values) => string.Join(" ", values.Select(v => (v / 1e6).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
                Check(
                    "netmeter: a steady loopback stream reads as a steady rate, sent and received, in every sample",
                    sends.Count >= 4 && sends.All(Near) && receives.All(Near),
                    $"sent {LoopbackRate / 1e6:0.00} MB/s; samples send [{Mb(sends)}] receive [{Mb(receives)}] MB/s");

                if (own is not null)
                {
                    processes.OnNavigatedTo(own.Key);
                    await Task.Delay(400).ConfigureAwait(true);
                    var row = processes.Model.IndexOf(own.Key);
                    var text = row >= 0 ? processes.CellAt(row, ProcessColumn.Network).Text : "(row not shown)";
                    Check(
                        "netmeter: this process's row shows its rate in the Network column",
                        row >= 0 && text != Fmt.Dash && text.EndsWith("bps", StringComparison.Ordinal) && !text.StartsWith("0 ", StringComparison.Ordinal),
                        text);
                    if (shotsDirectory is not null)
                    {
                        // Real data: this picture is for looking at once, never for docs/screenshots.
                        _shots.Add(await Capture(shotsDirectory, "real-netmeter-processes.png").ConfigureAwait(true));
                    }
                }
            }
            finally
            {
                await traffic.CancelAsync().ConfigureAwait(true);
                try
                {
                    await loopback.ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
                {
                }
            }

            processes.MeterButton.PerformClick();
            await Until(() => context.NetMeter.State.Status == NetMeterStatus.Off, 10000).ConfigureAwait(true);
            Check("netmeter: the button stops the meter", context.NetMeter.State.Status == NetMeterStatus.Off, context.NetMeter.State.Status.ToString());
            await Until(() => Process.GetProcessesByName("PCMieru.NetMeter").Length == 0, 10000).ConfigureAwait(true);
            var left = Process.GetProcessesByName("PCMieru.NetMeter");
            Check("netmeter: no meter process is left after stop", left.Length == 0, string.Join(", ", left.Select(p => p.Id)));
            foreach (var p in left)
            {
                p.Dispose();
            }
        }

        /// <summary>A TCP pair on 127.0.0.1 inside this process; the sender keeps to <see cref="LoopbackRate"/>.</summary>
        private static async Task Loopback(CancellationToken ct)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct).ConfigureAwait(false);
            using var server = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);

            var drain = Task.Run(
                async () =>
                {
                    var sink = new byte[64 * 1024];
                    var input = server.GetStream();
                    while (await input.ReadAsync(sink, ct).ConfigureAwait(false) > 0)
                    {
                    }
                },
                ct);

            var chunk = new byte[LoopbackRate / 20];
            var output = client.GetStream();
            var clock = Stopwatch.StartNew();
            long sent = 0;
            while (!ct.IsCancellationRequested)
            {
                await output.WriteAsync(chunk, ct).ConfigureAwait(false);
                sent += chunk.Length;
                var due = TimeSpan.FromSeconds((double)sent / LoopbackRate) - clock.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, ct).ConfigureAwait(false);
                }
            }

            client.Close();
            await drain.ConfigureAwait(false);
        }
    }
}
