using PcMieru.Core;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;

// Owner C: replace FakeSensorClient with the NVML/ADLX readers. The protocol stays: one SensorFrame JSON line on
// stdout per tick, diagnostics on stderr, exit when stdin closes (the parent died) or --once was given.
var once = args.Contains("--once", StringComparer.Ordinal);
var intervalMs = 1000;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--interval-ms" && int.TryParse(args[i + 1], out var parsed) && parsed is >= 100 and <= 60000)
    {
        intervalMs = parsed;
    }
}

using var sensors = new FakeSensorClient();
using var cts = new CancellationTokenSource();
var parentGone = once ? Task.CompletedTask : Task.Run(
    async () =>
    {
        while (await Console.In.ReadLineAsync(cts.Token).ConfigureAwait(false) is not null)
        {
        }

        await cts.CancelAsync().ConfigureAwait(false);
    },
    cts.Token);
_ = parentGone;

var stdout = Console.Out;
try
{
    do
    {
        var gpus = await sensors.ReadGpuAsync(cts.Token).ConfigureAwait(false);
        var frame = new SensorFrame(SensorFrame.CurrentSchemaVersion, DateTime.UtcNow, gpus);
        await stdout.WriteLineAsync(PcMieruJson.Serialize(frame)).ConfigureAwait(false);
        await stdout.FlushAsync(cts.Token).ConfigureAwait(false);
        if (once)
        {
            break;
        }

        await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
    }
    while (true);
}
catch (OperationCanceledException)
{
}

return 0;
