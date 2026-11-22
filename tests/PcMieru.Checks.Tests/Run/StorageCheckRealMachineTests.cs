using System.Globalization;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// The storage check on this machine: the real Core source, no elevation (the Helper is never started), a temp root, no legacy folder.
/// It only reads; nothing is written to a drive. Run on purpose, not by default (<c>--filter Category=RealMachine</c>).
/// What it prints is masked: disk numbers, protocols, verdicts and the reason a reading is missing, never a serial, a unique ID or a model.
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

            // Every slot has at least one row with its subject, so the slots are the subjects; and every one of them has exactly ONE
            // "storage.health" row (Windows' own status, step 5b), the first of its rows: also the disks the SMART read was denied for.
            var devices = Assert.Single(run.Result.Items, static i => i.Id == "storage.devices");
            Assert.Equal(CheckValue.Int(DiskIds(run).Count), devices.Value);
            foreach (var id in DiskIds(run))
            {
                var rows = run.Result.Items.Where(i => i.Subject?.Id == id).ToList();
                Assert.Equal("storage.health", rows[0].Id);
                Assert.Single(rows, static r => r.Id == "storage.health");
            }

            // Windows answers for at least one disk of this machine (it needs no elevation).
            Assert.Contains(run.Result.Items, static i => i.Id == "storage.health" && i.Value is not null);
        }

        // The file read back by the read-only history is the file that was written.
        var back = await CheckHistory.OpenReadOnly(Path.Combine(temp.Path, "checks")).GetAsync(second.Result.RunId, CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Found, back.Status);
        Assert.Equal(Kit.Bytes(second.Result), Kit.Bytes(back.Result!));

        // A disk that gave its health (it has rows of its protocol) has deltas in both runs, so the comparison is really looked at.
        if (first.Result.Items.Any(static i => IsProtocolRow(i.Id)))
        {
            Assert.NotEmpty(Deltas(first));
            Assert.NotEmpty(Deltas(second));
        }

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

        // Nothing but the one folder of the check was made under the temp root (DESIGN-A §4): the results, and the baseline inside them.
        Assert.Equal(["checks"], Directory.GetDirectories(temp.Path).Select(static d => Path.GetFileName(d)));
        Assert.Empty(Directory.GetFiles(temp.Path));
        var checks = Path.Combine(temp.Path, "checks");
        Assert.All(Directory.GetDirectories(checks), static d => Assert.Equal("baseline", Path.GetFileName(d)));
        var baseline = Path.Combine(checks, "baseline");
        if (Directory.Exists(baseline))
        {
            Assert.Empty(Directory.GetDirectories(baseline)); // no baseline\baseline
        }
    }

    private static IEnumerable<CheckItem> Deltas(CheckRun run) => run.Result.Items.Where(static i => i.Id.EndsWith(".delta", StringComparison.Ordinal));

    private static bool IsProtocolRow(string id) =>
        id.StartsWith("storage.nvme.", StringComparison.Ordinal) || id.StartsWith("storage.ata.", StringComparison.Ordinal);

    private static List<string> DiskIds(CheckRun run) =>
        [.. run.Result.Items.Where(static i => i.Subject is not null).Select(static i => i.Subject!.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

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

        foreach (var id in DiskIds(run))
        {
            var rows = result.Items.Where(i => i.Subject?.Id == id).ToList();
            var protocol = rows.Any(static r => r.Id.StartsWith("storage.nvme.", StringComparison.Ordinal))
                ? "nvme"
                : rows.Any(static r => r.Id.StartsWith("storage.ata.", StringComparison.Ordinal)) ? "ata" : "none";

            // What the SMART reading gave: the reason it is missing (denied, error, unsupported, timeout ...) or "readable". The temperature row is
            // the one every slot has, and it carries the reason of the health reading the disk's protocol rows come from.
            var temperature = rows.FirstOrDefault(static r => r.Id == "storage.temperature");
            var smart = temperature is { Value: null } ? "missing:" + (temperature.Reason?.ToString() ?? "no-reason") : "readable";

            // Windows' own status of the disk (the first row): its value, or the reason it is missing. Nothing else about the disk is printed.
            var windows = rows.FirstOrDefault(static r => r.Id == "storage.health");
            var windowsText = windows is null
                ? "no-row"
                : windows.Value is { Type: CheckValueType.Text } text ? text.AsText + "/" + windows.Verdict : "missing:" + (windows.Reason?.ToString() ?? "no-reason");
            var check = rows.Count(static r => r.Verdict == ItemVerdict.Check);
            var caution = rows.Count(static r => r.Verdict == ItemVerdict.Caution);
            var unavailable = rows.Count(static r => r.Verdict == ItemVerdict.Unavailable);
            var deltas = rows.Where(static r => r.Id.EndsWith(".delta", StringComparison.Ordinal)).ToList();
            var compared = deltas.Count(static d => d.Value is not null);
            var codes = deltas.Where(static d => d.Evidence?.Code is not null).Select(static d => d.Evidence!.Code!.Value.ToString()).Distinct().Order();
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {id}: protocol={protocol} smart={smart} windows_health={windowsText} check={check} caution={caution} unavailable={unavailable} rows={rows.Count} deltas={deltas.Count} compared={compared} codes=[{string.Join(",", codes)}]"));

            // The health of the NVMe log, row by row: the value and its verdict, or the reason it is missing (numbers of the log only, no name of the disk).
            foreach (var row in rows.Where(static r => r.Id.StartsWith("storage.nvme.", StringComparison.Ordinal) && !r.Id.EndsWith(".delta", StringComparison.Ordinal)))
            {
                var shown = row.Value is null ? "missing:" + (row.Reason?.ToString() ?? "no-reason") : row.Value.ToString() + "/" + row.Verdict;
                output.WriteLine($"    {row.Id}={shown}");
            }
        }
    }
}
