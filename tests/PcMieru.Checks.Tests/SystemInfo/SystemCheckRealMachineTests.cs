using System.Globalization;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>
/// The system check on this machine: the real Core hardware provider, the real WMI and the real System log, a temp root, no legacy folder,
/// no elevation. It only reads. Run on purpose, not by default (<c>--filter Category=RealMachine</c>).
/// What it prints is masked: counts per verdict, and for a row that could not be read its id and the reason, never a model, a serial,
/// a volume label, a drive letter or a path.
/// </summary>
public sealed class SystemCheckRealMachineTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task The_real_system_check_passes_the_strict_validator_and_every_row_that_is_not_read_says_why()
    {
        using var temp = new TempRoot();
        var services = CheckServices.CreateLive(temp.Path, legacyDirectory: null, app: Samples.App);
        var runner = new CheckRunner(services);

        var started = TimeProvider.System.GetTimestamp();
        var run = await runner.RunAsync(new CheckRequest(CheckKind.System), null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        var took = TimeProvider.System.GetElapsedTime(started);

        var items = run.Result.Items;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"system check: outcome={run.Result.Outcome} verdict={run.Result.Verdict} saved={run.IsSaved} items={items.Count} in {took.TotalMilliseconds:0} ms"));
        foreach (var verdict in Enum.GetValues<ItemVerdict>())
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {verdict}: {items.Count(i => i.Verdict == verdict)}"));
        }

        // Every row that could not be read, by id and reason alone (the subject's number, never its model).
        foreach (var item in items.Where(static i => i.Reason is not null))
        {
            output.WriteLine($"  not read: {item.Id}{(item.Subject is null ? string.Empty : " " + item.Subject.Id)} verdict={item.Verdict} reason={item.Reason}");
        }

        // The disks, as the check sees them: Windows' own status of each, as a number of the slot and the word only.
        foreach (var health in items.Where(static i => i.Id == "system.storage.health"))
        {
            var text = health.Value is { Type: CheckValueType.Text } value ? value.AsText + "/" + health.Verdict : "missing:" + health.Reason;
            output.WriteLine($"  {health.Subject!.Id}: windows_health={text}");
        }

        Assert.Null(run.FailureCode);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(CheckKind.System, run.Result.Kind);
        Assert.True(run.IsSaved, "the run was not saved: " + run.Save.FailureCode);
        var problems = CheckResultValidator.Validate(run.Result);
        Assert.True(problems.Count == 0, string.Join(" | ", problems));

        // The counts say what the rows say.
        Assert.Equal(items.Count, run.Result.Counts.Total);
        Assert.Equal(items.Count(static i => i.Verdict == ItemVerdict.Unavailable), run.Result.Counts.Unavailable);

        // All eleven event counts are there, whatever they say.
        Assert.Equal(11, items.Count(static i => i.Id.StartsWith("system.eventlog.", StringComparison.Ordinal)));

        // No unavailable row is without a reason, and nothing that names the machine reaches the file.
        Assert.All(items.Where(static i => i.Verdict == ItemVerdict.Unavailable), static i => Assert.NotNull(i.Reason));
        var text2 = System.Text.Encoding.UTF8.GetString(Kit.Bytes(run.Result));
        Assert.DoesNotContain("PhysicalDrive", text2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pagefile.sys", text2, StringComparison.OrdinalIgnoreCase);

        // The file read back by the read-only history is the file that was written.
        var back = await CheckHistory.OpenReadOnly(Path.Combine(temp.Path, "checks")).GetAsync(run.Result.RunId, CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Found, back.Status);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back.Result!));
    }
}
