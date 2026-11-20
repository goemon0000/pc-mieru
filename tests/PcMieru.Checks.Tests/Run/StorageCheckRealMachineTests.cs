using System.Globalization;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// The storage check on this machine: the real Core source, no elevation (the Helper is never started), a temp root, no legacy folder.
/// It only reads; nothing is written to a drive. Run on purpose, not by default (<c>--filter Category=RealMachine</c>).
/// What it prints is masked: disk numbers, protocols and verdicts, never a serial, a unique ID or a model.
/// </summary>
public sealed class StorageCheckRealMachineTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task The_real_storage_check_twice_passes_the_strict_validator_and_the_second_run_compares_or_says_why_not()
    {
        using var temp = new TempRoot();
        var services = CheckServices.CreateLive(temp.Path, legacyDirectory: null, app: Samples.App);
        var runner = new CheckRunner(services);
        var request = new CheckRequest(CheckKind.Storage); // AllowElevation is false

        var first = await runner.RunAsync(request, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        var second = await runner.RunAsync(request, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));

        Report("run 1", first);
        Report("run 2", second);

        foreach (var run in new[] { first, second })
        {
            Assert.Null(run.FailureCode);
            Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
            Assert.True(run.IsSaved, "the run was not saved: " + run.Save.FailureCode);
            var problems = CheckResultValidator.Validate(run.Result);
            Assert.True(problems.Count == 0, string.Join(" | ", problems));

            var devices = Assert.Single(run.Result.Items, static i => i.Id == "storage.devices");
            var disks = run.Result.Items.Where(static i => i.Id == "storage.health").ToList();
            Assert.Equal(CheckValue.Int(disks.Count), devices.Value);
        }

        // The file read back by the read-only history is the file that was written.
        var back = await CheckHistory.OpenReadOnly(Path.Combine(temp.Path, "checks")).GetAsync(second.Result.RunId, CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Found, back.Status);
        Assert.Equal(Kit.Bytes(second.Result), Kit.Bytes(back.Result!));

        // The first run had nothing to compare with: every .delta says why. The second run compares, or says why it could not.
        Assert.All(Deltas(first), static d => AssertNotCompared(d));
        foreach (var delta in Deltas(second))
        {
            if (delta.Evidence is { Code: { } code })
            {
                Assert.True(CheckEvidenceCodes.IsBaseline(code), $"{delta.Id}: {code} is not a baseline code");
                Assert.Null(delta.Value);
            }
            else
            {
                Assert.NotNull(delta.Value);
                Assert.NotNull(delta.Evidence!.Before);
                Assert.NotNull(delta.Evidence.After);
                Assert.NotNull(delta.Evidence.SinceUtc);
            }
        }

        // Nothing but the two folders of the check was made under the temp root.
        Assert.All(Directory.GetDirectories(temp.Path), static d => Assert.Contains(Path.GetFileName(d), new[] { "checks", "baseline" }));
    }

    private static IEnumerable<CheckItem> Deltas(CheckRun run) => run.Result.Items.Where(static i => i.Id.EndsWith(".delta", StringComparison.Ordinal));

    private static void AssertNotCompared(CheckItem delta)
    {
        Assert.Equal(ItemVerdict.Info, delta.Verdict);
        Assert.Null(delta.Value);
        var code = Assert.NotNull(delta.Evidence?.Code);
        Assert.True(CheckEvidenceCodes.IsBaseline(code), $"{delta.Id}: {code} is not a baseline code");
    }

    private void Report(string label, CheckRun run)
    {
        var result = run.Result;
        var counts = result.Counts;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: outcome={result.Outcome} verdict={result.Verdict} ok={counts.Ok} caution={counts.Caution} check={counts.Check} unavailable={counts.Unavailable} info={counts.Info} items={result.Items.Count} saved={run.IsSaved}"));
        output.WriteLine("  notes: " + (result.Notes is { Count: > 0 } notes ? string.Join(",", notes) : "(none)"));

        foreach (var health in result.Items.Where(static i => i.Id == "storage.health"))
        {
            var id = health.Subject!.Id;
            var rows = result.Items.Where(i => i.Subject?.Id == id).ToList();
            var protocol = rows.Any(static r => r.Id.StartsWith("storage.nvme.", StringComparison.Ordinal))
                ? "nvme"
                : rows.Any(static r => r.Id.StartsWith("storage.ata.", StringComparison.Ordinal)) ? "ata" : "no-health";
            var deltas = rows.Where(static r => r.Id.EndsWith(".delta", StringComparison.Ordinal)).ToList();
            var compared = deltas.Count(static d => d.Value is not null);
            var codes = deltas.Where(static d => d.Evidence?.Code is not null).Select(static d => d.Evidence!.Code!.Value.ToString()).Distinct().Order();
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {id}: protocol={protocol} health={health.Verdict} rows={rows.Count} deltas={deltas.Count} compared={compared} codes=[{string.Join(",", codes)}]"));
        }
    }
}
