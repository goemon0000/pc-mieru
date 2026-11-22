using System.Diagnostics;
using System.Globalization;
using System.Text;
using PcMieru.Core;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Sensors;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The recorder on this PC with the real sampler, the real Windows counters and (when its build output is found) the real SensorHost: about ten
/// seconds, one bench-start / bench-end pair, then the run is read back through the store. It prints the JSON header, the first CSV lines and the
/// summary. The folder is a scratch folder (the <c>PCMIERU_TRIAL_DIR</c> variable, else one under the system temp), never the Documents folder.
/// </summary>
[Trait("Category", "RealMachine")]
public sealed class RecordingRealMachineTests(ITestOutputHelper output)
{
    private readonly StringBuilder _report = new();

    private void Say(string line)
    {
        output.WriteLine(line);
        _report.AppendLine(line);
    }

    private static string? FindSensorHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(dir.FullName, "src", "PcMieru.SensorHost", "bin", configuration, "net10.0-windows", SystemSensorProcessFactory.ExeName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        // Wait for a CONDITION (a number of samples seen), never for a duration; the limit only keeps a broken sampler from hanging the run.
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), "Waited too long for " + what);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task About_ten_seconds_from_the_real_sampler_are_recorded_with_a_bench_pair_and_read_back()
    {
        Strings.Register(RecordStrings.All);
        var scratch = Environment.GetEnvironmentVariable("PCMIERU_TRIAL_DIR");
        var baseDir = string.IsNullOrWhiteSpace(scratch)
            ? Path.Combine(Path.GetTempPath(), "pcmieru-trial-" + Guid.NewGuid().ToString("N"))
            : scratch;
        var root = Path.Combine(baseDir, "runs");
        Say("Trial folder: " + root);
        Assert.DoesNotContain("PCMieru\\runs", root, StringComparison.OrdinalIgnoreCase);

        var exe = FindSensorHost();
        Say("SensorHost: " + (exe ?? "NOT FOUND (vendor GPU values are not covered by this run)"));
        using var client = exe is null ? null : new SensorHostClient(exe);
        var control = new SamplerSettings();
        using var source = new WindowsSnapshotSource(static () => DateTime.UtcNow, control, () => client);
        using var loop = new SamplerLoop(source, control, () => client);
        var seen = 0;
        loop.RecordingSampled += _ => Interlocked.Increment(ref seen);

        var dxgi = GpuAdapters.Enumerate();
        var listedByDxgi = dxgi.Any(a => !a.IsSoftware && a.Name.Contains("RTX 5070", StringComparison.OrdinalIgnoreCase));
        loop.Start();

        var recorder = new RunRecorder(loop, root);
        Assert.Equal(RunActivityState.Idle, recorder.State);
        var run = await recorder.StartAsync(CancellationToken.None);
        Assert.Equal(RunActivityState.Recording, recorder.State);

        await WaitFor(() => Volatile.Read(ref seen) >= 4, "four samples");
        var start = await recorder.MarkAsync(run, MarkerKind.BenchStart, CancellationToken.None);
        await WaitFor(() => Volatile.Read(ref seen) >= 8, "eight samples");
        var end = await recorder.MarkAsync(run, MarkerKind.BenchEnd, CancellationToken.None);
        await WaitFor(() => Volatile.Read(ref seen) >= 11, "eleven samples");
        var result = await recorder.StopAsync(run, RunStopReason.User);
        await loop.StopAsync();

        Say($"Result: status={result.Status} stop={result.StopReason} rows={result.Rows} droppedTicks={result.DroppedTicks} duration={result.DurationUs / 1_000_000.0:0.000} s");
        Say($"Samples the test saw: {Volatile.Read(ref seen)} (rows written: {result.Rows}; a sample acquired before the start is dropped uncounted by the backlog rule); bench-start at {start.OffsetUs / 1_000_000.0:0.000} s, bench-end at {end.OffsetUs / 1_000_000.0:0.000} s");
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunActivityState.Idle, recorder.State);
        Assert.False(loop.IsRecording);

        Say("---- JSON header ----");
        Say(File.ReadAllText(run.JsonPath));

        var csvLines = File.ReadAllLines(run.CsvPath);
        Say("---- first 3 CSV lines ----");
        foreach (var line in csvLines.Take(3))
        {
            Say(line);
        }

        var store = new RunStore(root);
        var info = Assert.Single((await store.ListAsync(CancellationToken.None)).Runs);
        var detail = await store.ReadAsync(run.RunId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(run.RunId, info.RunId);
        Assert.Equal(result.Rows, detail.Info.Rows);
        Assert.True(detail.Info.Rows >= 8, $"only {detail.Info.Rows} rows");
        Assert.Equal(csvLines.Length - 1, detail.Info.Rows);

        Say("---- summary ----");
        var summary = detail.Summary;
        Say($"duration={summary.DurationSec:0.000} s rows={summary.Rows} droppedTicks={summary.DroppedTicks} freshness={summary.Freshness} sustainedWindow={summary.SustainedWindowSec:0} s");
        foreach (var m in summary.Metrics)
        {
            Say(string.Create(
                CultureInfo.InvariantCulture,
                $"  {m.Key,-34} {m.Unit,-6} new={m.NewPoints,3} observed={m.ObservedSeconds,7:0.000} missing={m.MissingSeconds,7:0.000} min={m.Min:0.##} avg={m.Avg:0.##} max={m.Max:0.##}"));
        }

        foreach (var note in summary.Notes)
        {
            Say($"  note {note.Code} [{note.Subject}] [{note.Detail}] -> {RecordStrings.NoteText(note, Language.En)}");
        }

        foreach (var limit in summary.Limits)
        {
            Say($"  limit {limit.Device}: observed={limit.ObservedSeconds:0.0} s reasons=" + string.Join(",", limit.Reasons.Select(r => $"{r.Name}:{r.Seconds:0.0}")));
        }

        var section = Assert.Single(summary.Sections);
        Say($"  section {section.Kind} {section.StartSec:0.000}-{section.EndSec:0.000} s open={section.Open} metrics={section.Metrics.Count}");
        Assert.Equal("bench", section.Kind);
        Assert.False(section.Open);
        Assert.Equal(new[] { MarkerKind.BenchStart, MarkerKind.BenchEnd }, detail.Info.Markers.Select(m => m.Kind).ToArray());

        Say("---- fresh / repeat / missing per source ----");
        var data = RunCsvReader.Read(File.ReadAllBytes(run.CsvPath), detail.Info.Metrics);
        foreach (var (name, counts) in data.Sources.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            Say($"  {name}: fresh={counts.Fresh} repeat={counts.Repeat} missing={counts.Missing}" + (counts.Reasons.Count > 0 ? " reasons=" + string.Join(",", counts.Reasons) : ""));
        }

        var rtx = detail.Info.Sources.Count(s => s.Name.Contains("RTX 5070", StringComparison.OrdinalIgnoreCase));
        Say($"RTX 5070 in the run's sources: {rtx} (DXGI lists one: {listedByDxgi})");
        Assert.Equal(listedByDxgi ? 1 : 0, rtx);
        Assert.StartsWith("offsetUs,utc,core.frame,", csvLines[0], StringComparison.Ordinal);

        Say($"Lease back: subscribers and lease are released (IsRecording={loop.IsRecording}).");
        Assert.Equal(0, detail.Summary.DroppedTicks);

        try
        {
            File.WriteAllText(Path.Combine(baseDir, "trial-report.txt"), _report.ToString());
        }
        catch (IOException ex)
        {
            output.WriteLine("The report file could not be written: " + ex.Message);
        }
    }
}
