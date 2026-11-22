using System.Text;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;
using PcMieru.SensorHost;

// PCMieru.SensorHost: reads GPU sensors through the vendors' own libraries (NVML, ADLX) in a process of its own, because a vendor driver
// call can hang and must never freeze the app. Protocol: one SensorFrame JSON line on stdout every two seconds (or every second while the
// parent asks for it; the first one as soon as the libraries are open), diagnostics on stderr, exit when stdin closes (the app is gone) or
// after one frame with --once. The parent starts the child with --interval-ms (its current request) and --generation (its own number for
// this child, copied into every frame), and changes the period later by writing a line, "1000" or "2000", to stdin; the new period starts
// at the next sampling boundary (SensorCadence). Runs as the normal user. Reads only; nothing here changes a GPU setting.
const int DefaultIntervalMs = 2000;

var once = args.Contains("--once", StringComparer.Ordinal);
var intervalMs = DefaultIntervalMs;
long generation = 0;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--interval-ms" && int.TryParse(args[i + 1], out var parsed) && parsed is >= 100 and <= 60_000)
    {
        intervalMs = parsed;
    }
    else if (args[i] == "--generation" && long.TryParse(args[i + 1], out var gen) && gen > 0)
    {
        generation = gen;
    }
}

// An explicit UTF-8 writer: the console's default encoding would mangle non-ASCII text (a GPU name) on its way to the parent.
using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
using var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

var cadence = new SensorCadence(intervalMs);
if (!once)
{
    // Lines from the parent are period requests; the parent closing our input is the "you may stop" signal. Exit the whole process: the
    // main thread may be stuck inside a vendor driver call, and waiting for it would keep this process (and the driver) alive.
    var watcher = new Thread(() =>
    {
        try
        {
            while (Console.In.ReadLine() is { } line)
            {
                _ = cadence.TryRequest(line);
            }
        }
        catch (IOException)
        {
        }

        Environment.Exit(0);
    })
    {
        IsBackground = true,
        Name = "parent-watch",
    };
    watcher.Start();
}

var sampler = new GpuSampler(line => stderr.WriteLine("sensorhost " + line));
var next = Environment.TickCount64;
long sequence = 0;
do
{
    // The sampling boundary. The frame says the period that led up to it; a request that came in while we waited starts to count now.
    var framePeriod = cadence.PeriodMs;
    var period = cadence.ApplyPending();

    Value<IReadOnlyList<GpuSensorReading>> gpus;
    try
    {
        gpus = sampler.Sample();
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        stderr.WriteLine("sensorhost reading failed: " + ex.GetType().Name);
        gpus = Value.Failed<IReadOnlyList<GpuSensorReading>>("sensorhost", "reading the GPU sensors failed (" + ex.GetType().Name + ")");
    }

    sequence++;
    var frame = new SensorFrame(
        SensorFrame.CurrentSchemaVersion,
        DateTime.UtcNow,
        gpus,
        generation,
        sequence,
        framePeriod,
        MonotonicClock.NowMicroseconds());
    stdout.WriteLine(PcMieruJson.Serialize(frame));
    if (once)
    {
        break;
    }

    // A fixed cadence: a slow reading does not push every later frame back.
    next += period;
    var wait = next - Environment.TickCount64;
    if (wait > 0)
    {
        Thread.Sleep((int)wait);
    }
    else
    {
        next = Environment.TickCount64;
    }
}
while (true);

return 0;
