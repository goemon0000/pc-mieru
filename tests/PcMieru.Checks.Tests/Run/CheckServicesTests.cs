using System.Text.RegularExpressions;
using PcMieru.Checks.Tests.Storage;
using PcMieru.Core.Hardware;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// How the services are put together: every folder is given, nothing is created before a run, and the live wiring is the real Core source over
/// <c>&lt;root&gt;\checks</c> (the results) and <c>&lt;root&gt;\checks\baseline</c> (DESIGN-A §4). All of it on fresh temp folders; the
/// user's own folders are never named.
/// </summary>
public sealed partial class CheckServicesTests
{
    [GeneratedRegex(@"^PCMieru \d+\.\d+\.\d+")]
    private static partial Regex AppFormat();

    [Fact]
    public void The_default_app_string_is_the_product_and_the_version_without_a_build_suffix()
    {
        Assert.Matches(AppFormat(), CheckServices.DefaultApp);
        Assert.DoesNotContain('+', CheckServices.DefaultApp);
    }

    [Fact]
    public void The_default_app_string_is_a_string_the_validator_accepts()
    {
        var result = CheckResult.Create(CheckServices.DefaultApp, Samples.StorageRun, CheckKind.Storage, Samples.Start, Samples.Start, CheckOutcome.Stopped, []);

        Assert.Empty(CheckResultValidator.Validate(result));
    }

    [Fact]
    public void The_public_constructor_takes_every_folder_and_creates_nothing()
    {
        using var temp = new TempRoot();
        var root = Path.Combine(temp.Path, "app-data");
        var legacy = Path.Combine(temp.Path, "old-install");

        var services = new CheckServices(TimeProvider.System, root, new FakeStorageSource(), legacy, new CheckHistoryStore(Path.Combine(root, "checks")), Samples.App);

        Assert.Equal(Samples.App, services.App);
        Assert.Same(TimeProvider.System, services.Time);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path)); // not the root, not the legacy folder, not the lock folder
    }

    [Fact]
    public void An_app_that_is_empty_or_blank_is_the_default()
    {
        using var temp = new TempRoot();

        Assert.Equal(CheckServices.DefaultApp, Make(temp, null).App);
        Assert.Equal(CheckServices.DefaultApp, Make(temp, "").App);
        Assert.Equal(CheckServices.DefaultApp, Make(temp, "   ").App);
        Assert.Equal("PCMieru 9.9.9", Make(temp, "PCMieru 9.9.9").App);
    }

    [Fact]
    public void The_constructors_refuse_what_is_missing()
    {
        using var temp = new TempRoot();
        var history = new CheckHistoryStore(Path.Combine(temp.Path, "checks"));

        Assert.Throws<ArgumentNullException>(() => new CheckServices(null!, temp.Path, new FakeStorageSource(), null, history));
        Assert.Throws<ArgumentNullException>(() => new CheckServices(TimeProvider.System, temp.Path, null!, null, history));
        Assert.Throws<ArgumentNullException>(() => new CheckServices(TimeProvider.System, temp.Path, new FakeStorageSource(), null, null!));
    }

    [Fact]
    public void Live_services_use_the_real_storage_source_and_the_history_store_and_create_nothing_yet()
    {
        using var temp = new TempRoot();

        var services = CheckServices.CreateLive(temp.Path, legacyDirectory: null, app: Samples.App);

        Assert.IsType<RealStorageInspectionSource>(services.Storage);
        Assert.IsType<CheckHistoryStore>(services.History);
        Assert.Same(TimeProvider.System, services.Time);
        Assert.Equal(Samples.App, services.App);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Live_services_keep_the_history_in_a_checks_folder_under_the_given_root()
    {
        using var temp = new TempRoot();
        var services = CheckServices.CreateLive(temp.Path, legacyDirectory: null, app: Samples.App);

        var save = await services.History.SaveAsync(Samples.StorageCheck());

        Assert.Equal(CheckSaveStatus.Saved, save.Status);
        var files = Directory.GetFiles(Path.Combine(temp.Path, "checks"), "*.json", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.DoesNotContain(Directory.GetDirectories(temp.Path), static d => !Path.GetFileName(d).Equals("checks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_baseline_lives_in_checks_baseline_and_nowhere_else_under_the_root()
    {
        using var temp = new TempRoot();
        var root = Path.Combine(temp.Path, "app-data");
        var checks = Path.Combine(root, "checks");
        var baseline = Path.Combine(checks, "baseline");
        var source = new FakeStorageSource();
        var services = new CheckServices(TimeProvider.System, root, source, null, new CheckHistoryStore(CheckServices.ChecksFolder(root)), Samples.App);
        var runner = new CheckRunner(services);
        var before = DateTimeOffset.UtcNow.AddHours(-2);
        source.Returns(Drives.Inspect(before, [Drives.Nvme(at: before)]));
        source.Returns(Drives.Inspect(before.AddHours(1), [Drives.Nvme(at: before.AddHours(1), poh: 101)]));

        var first = await runner.RunAsync(new CheckRequest(CheckKind.Storage), null, CancellationToken.None);
        var second = await runner.RunAsync(new CheckRequest(CheckKind.Storage), null, CancellationToken.None);

        // The root holds one folder, "checks", and that holds the results and one folder, "baseline", with the disks' files in it.
        Assert.Equal(["checks"], Directory.GetDirectories(root).Select(static d => Path.GetFileName(d)));
        Assert.Equal(["baseline"], Directory.GetDirectories(checks).Select(static d => Path.GetFileName(d)));
        Assert.Empty(Directory.GetDirectories(baseline)); // no baseline\baseline
        Assert.False(Directory.Exists(Path.Combine(root, "baseline")));
        Assert.NotEmpty(Directory.GetFiles(baseline, "disk-*.json"));
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(2, Directory.GetFiles(checks).Length); // the two saved runs, nothing of the baseline
        Assert.All(Directory.GetFiles(checks), static f => Assert.EndsWith(".json", f, StringComparison.Ordinal));

        // The second run found the first one's record where it was written, and the history still reads both runs beside the folder.
        Assert.Equal(CheckValue.SignedDec(1), second.Result.Items.Single(static i => i.Id == "storage.nvme.power_on_hours.delta").Value);
        var history = CheckHistory.OpenReadOnly(checks);
        Assert.Equal(CheckHistoryGetStatus.Found, (await history.GetAsync(first.Result.RunId, CancellationToken.None)).Status);
        Assert.Equal(CheckHistoryGetStatus.Found, (await history.GetAsync(second.Result.RunId, CancellationToken.None)).Status);
    }

    [Fact]
    public void Live_services_need_a_root()
    {
        Assert.ThrowsAny<ArgumentException>(() => CheckServices.CreateLive("", null));
        Assert.ThrowsAny<ArgumentException>(() => CheckServices.CreateLive("  ", null));
        Assert.Throws<ArgumentNullException>(() => CheckServices.CreateLive(null!, null));
    }

    [Fact]
    public void Live_services_give_a_runner_that_knows_the_storage_check()
    {
        using var temp = new TempRoot();

        var runner = new CheckRunner(CheckServices.CreateLive(temp.Path, null));

        Assert.Equal(CheckKind.Storage, Assert.Single(runner.SupportedKinds));
    }

    private static CheckServices Make(TempRoot temp, string? app) =>
        new(TimeProvider.System, temp.Path, new FakeStorageSource(), null, new CheckHistoryStore(Path.Combine(temp.Path, "checks")), app);
}
