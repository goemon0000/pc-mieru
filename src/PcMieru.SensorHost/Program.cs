using System.Text;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;
using PcMieru.SensorHost;

// PCMieru.SensorHost: reads GPU sensors through the vendors' own libraries (NVML, ADLX) in a process of its own, because a vendor driver
// call can hang and must never freeze the app. Protocol: one SensorFrame JSON line on stdout every two seconds (the first one as soon as the
// libraries are open), diagnostics on stderr, exit when stdin closes (the app is gone) or after one frame with --once.
// Runs as the normal user. Reads only; nothing here changes a GPU setting.
const int DefaultIntervalMs = 2000;

var once = args.Contains("--once", StringComparer.Ordinal);
var intervalMs = DefaultIntervalMs;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--interval-ms" && int.TryParse(args[i + 1], out var parsed) && parsed is >= 100 and <= 60_000)
    {
        intervalMs = parsed;
    }
}

// An explicit UTF-8 writer: the console's default encoding would mangle non-ASCII text (a GPU name) on its way to the parent.
using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
using var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

if (!once)
{
    // The parent closing our input is the "you may stop" signal. Exit the whole process: the main thread may be stuck inside a vendor driver
    // call, and waiting for it would keep this process (and the driver) alive.
    var watcher = new Thread(() =>
    {
        try
        {
            while (Console.In.ReadLine() is not null)
            {
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
do
{
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

    var frame = new SensorFrame(SensorFrame.CurrentSchemaVersion, DateTime.UtcNow, gpus);
    stdout.WriteLine(PcMieruJson.Serialize(frame));
    if (once)
    {
        break;
    }

    // A fixed cadence: a slow reading does not push every later frame back.
    next += intervalMs;
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
