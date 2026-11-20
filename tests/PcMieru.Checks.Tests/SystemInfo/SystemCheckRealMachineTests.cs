using System.Globalization;
using PcMieru.Checks.SystemInfo;
using PcMieru.Core;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;
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
    public async Task The_real_system_check_passes_the_strict_validator_pairs_or_declines_every_gpu_and_reads_every_fixed_disk()
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

        // The GPUs, as the check sees them: the device status of each (its number in the list, the verdict, and the code or the reason).
        foreach (var status in items.Where(static i => i.Id == "system.gpu.device_status"))
        {
            var shown = status.Value is null ? "missing:" + status.Reason : "code=" + status.Value;
            output.WriteLine($"  {status.Subject!.Id}: device_status verdict={status.Verdict} {shown}");
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

        // A GPU Windows can pair gives its code; one it cannot pair (software, virtual) is unsupported, never an error of ours.
        Assert.All(
            items.Where(static i => i.Id == "system.gpu.device_status"),
            static i => Assert.True(i.Value is not null || i.Reason == CheckReason.Unsupported, $"{i.Subject!.Id}: device_status is missing with {i.Reason}"));

        // Every disk that Core says is fixed has its health read: the disks are numbered in the order of the drives that are counted
        // (the slots with no media are left out), the same way the check does.
        var fixedDisks = await FixedDiskIdsAsync();
        foreach (var id in fixedDisks)
        {
            var health = Assert.Single(items, i => i.Id == "system.storage.health" && i.Subject?.Id == id);
            Assert.True(health.Value is not null, $"{id}: the health of a fixed disk is missing with {health.Reason}");
        }

        // Nothing that names the machine reaches the file. (That an unavailable row has a reason is the validator's rule, so it is not asserted again.)
        var text2 = System.Text.Encoding.UTF8.GetString(Kit.Bytes(run.Result));
        Assert.DoesNotContain("PhysicalDrive", text2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pagefile.sys", text2, StringComparison.OrdinalIgnoreCase);

        // The file read back by the read-only history is the file that was written.
        var back = await CheckHistory.OpenReadOnly(Path.Combine(temp.Path, "checks")).GetAsync(run.Result.RunId, CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Found, back.Status);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back.Result!));
    }

    // "disk-N" of the fixed (not removable) drives, as the check numbers them: in the order of the drive numbers, the empty slots left out.
    private static async Task<List<string>> FixedDiskIdsAsync()
    {
        var info = await new RealHardwareProvider().GetAsync(HardwareCategory.Storage, CancellationToken.None);
        var drives = info.Storage.Data!
            .Where(static d => !SystemInputsBuilder.HasNoMedia(d))
            .OrderBy(static d => d.PhysicalDriveNumber)
            .ToList();
        var ids = new List<string>();
        for (var i = 0; i < drives.Count; i++)
        {
            if (drives[i].Details is { Removable: { Status: ValueStatus.Ok, Data: false } })
            {
                ids.Add("disk-" + (i + 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }
}
