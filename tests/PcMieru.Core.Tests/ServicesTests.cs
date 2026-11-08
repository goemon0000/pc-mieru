using System.Text.Json.Nodes;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Reporting;
using PcMieru.Wire;

namespace PcMieru.Core.Tests;

public class ServicesTests
{
    private static PcMieruServices Create(bool redact = true) =>
        PcMieruServices.Create(new PcMieruOptions { Redact = redact, Clock = static () => Fixture.FixedTime, ForceFakes = true });

    /// <summary>
    /// Management has real implementations: a test that expects the Fake's behaviour (and must not act on this PC: end a
    /// process, switch a startup entry, start the Helper) has to ask for the Fakes.
    /// </summary>
    private static PcMieruServices CreateFakes() =>
        PcMieruServices.Create(new PcMieruOptions { ForceFakes = true, Clock = static () => Fixture.FixedTime });

    [Fact]
    public void Every_boundary_interface_resolves_after_create()
    {
        using var s = Create();
        Assert.NotNull(s.Snapshots);
        Assert.NotNull(s.ProcessDetails);
        Assert.NotNull(s.Hardware);
        Assert.NotNull(s.StorageHealth);
        Assert.NotNull(s.Sensors);
        Assert.NotNull(s.ProcessControl);
        Assert.NotNull(s.Protection);
        Assert.NotNull(s.Services);
        Assert.NotNull(s.Startup);
        Assert.NotNull(s.Sessions);
        Assert.NotNull(s.Helper);
        Assert.NotNull(s.Tasks);
        Assert.NotNull(s.Observations);
        Assert.NotNull(s.KnownProcesses);
        Assert.NotNull(s.Redactor);
        Assert.NotNull(s.Reports);
        Assert.NotNull(s.Insights);
    }

    [Fact]
    public void Skeleton_reports_that_it_is_still_on_fakes()
    {
        using var s = Create();
        Assert.True(s.UsesFakeData);
        Assert.Contains("FakeSnapshotSource", s.FakeAreas);
        Assert.Contains("FakeHardwareProvider", s.FakeAreas);
    }

    [Fact]
    public void Configure_replaces_a_registration_after_the_modules_ran()
    {
        var replaced = new FakeHardwareProvider();
        using var s = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true }, r => r.Set<IHardwareProvider>(replaced));
        Assert.Same(replaced, s.Hardware);
    }

    [Fact]
    public async Task The_first_sample_has_no_baseline_and_the_second_does()
    {
        using var s = Create();
        var first = await s.Snapshots.SampleAsync();
        var second = await s.Snapshots.SampleAsync();
        Assert.All(first.Processes, p => Assert.Equal(ValueStatus.NotCollected, p.CpuPercent.Status));
        Assert.Contains(second.Processes, p => p.CpuPercent.IsOk);

        s.Snapshots.ResetBaseline();
        var again = await s.Snapshots.SampleAsync();
        Assert.All(again.Processes, p => Assert.Equal(ValueStatus.NotCollected, p.CpuPercent.Status));
    }

    [Fact]
    public async Task List_processes_sorts_filters_and_limits()
    {
        using var s = Create();
        var byCpu = await s.Insights.ListProcessesAsync(new ProcessQuery(ProcessSort.Cpu, 3, null, TimeSpan.Zero));
        Assert.Equal(3, byCpu.Processes.Count);
        Assert.Equal("SkyrimVR.exe", byCpu.Processes[0].Name);
        Assert.True(byCpu.TotalProcesses > 20);

        var chrome = await s.Insights.ListProcessesAsync(new ProcessQuery(ProcessSort.Name, null, "CHROME", TimeSpan.Zero));
        Assert.Equal(4, chrome.Processes.Count);
        Assert.All(chrome.Processes, p => Assert.Equal("chrome.exe", p.Name));

        var byMem = await s.Insights.ListProcessesAsync(new ProcessQuery(ProcessSort.Mem, 1, null, TimeSpan.Zero));
        Assert.Equal("SkyrimVR.exe", byMem.Processes[0].Name);
    }

    [Fact]
    public async Task Get_process_returns_details_for_a_known_pid_and_null_for_an_unknown_one()
    {
        using var s = Create();
        var found = await s.Insights.GetProcessAsync(8800);
        Assert.NotNull(found);
        Assert.Equal("SkyrimVR.exe", found.Process.Name);
        Assert.NotNull(found.Details);
        Assert.Null(await s.Insights.GetProcessAsync(999_999));
    }

    [Fact]
    public async Task System_summary_carries_observations_with_evidence_and_candidate_wording()
    {
        using var s = Create();
        var summary = await s.Insights.GetSystemSummaryAsync();
        var single = Assert.Single(summary.Observations, o => o.Id == "cpu.singleCoreSaturated");
        Assert.Equal(ObservationCertainty.Candidate, single.Certainty);
        Assert.NotEmpty(single.Evidence);
        Assert.Contains("%", Strings.Render(single, Language.En), StringComparison.Ordinal);
        Assert.NotEqual(Strings.Render(single, Language.Ja), Strings.Render(single, Language.En));
        Assert.True(summary.TopProcesses.Count > 0);
    }

    [Fact]
    public async Task Diagnose_observes_and_returns_the_summary()
    {
        using var s = Create();
        var result = await s.Insights.DiagnoseAsync(TimeSpan.Zero);
        Assert.Equal(DiagnoseResult.CurrentSchemaVersion, result.SchemaVersion);
        Assert.NotEmpty(result.Summary.Observations);
    }

    [Fact]
    public async Task Hardware_requested_by_category_marks_the_others_not_collected()
    {
        using var s = Create();
        var cpuOnly = await s.Insights.GetHardwareAsync(HardwareCategory.Cpu);
        Assert.True(cpuOnly.Cpu.IsOk);
        Assert.Equal(ValueStatus.NotCollected, cpuOnly.Memory.Status);
    }

    [Fact]
    public async Task Storage_health_without_elevation_is_denied_not_guessed()
    {
        using var s = Create();
        var report = await s.Insights.GetStorageHealthAsync();
        Assert.Contains(report.Drives, d => d.Health.Status == ValueStatus.Denied);
        Assert.Contains(report.Drives, d => d.Health.IsOk);
    }

    [Fact]
    public async Task Report_is_redacted_by_default_and_carries_hardware_and_storage()
    {
        using var s = Create();
        var report = await s.Insights.GetReportAsync(new ReportOptions(DurationSec: 0));
        Assert.True(report.Redacted);
        Assert.NotNull(report.Hardware);
        Assert.NotNull(report.Storage);
        var markdownJa = ReportMarkdown.Render(report, Language.Ja);
        var markdownEn = ReportMarkdown.Render(report, Language.En);
        Assert.Contains("#", markdownJa, StringComparison.Ordinal);
        Assert.NotEqual(markdownJa, markdownEn);
    }

    [Fact]
    public void Redactor_hides_user_pc_and_profile_in_text_and_blanks_sensitive_keys()
    {
        var redactor = new SimpleRedactor(new RedactionContext("tatsuki", "DESKTOP-ABC", @"C:\Users\tatsuki"), enabled: true);
        Assert.Equal(@"%USERPROFILE%\Documents", redactor.RedactText(@"C:\Users\tatsuki\Documents"));
        Assert.Equal(@"\\<host>\share", redactor.RedactText(@"\\nas01\share"));
        Assert.Equal("owner <user> on <host>", redactor.RedactText("owner tatsuki on desktop-abc"));
        Assert.Equal("<mac>", redactor.RedactText("00-1A-2B-3C-4D-5E"));

        var node = JsonNode.Parse("""
            {"name":"app.exe","commandLine":"app.exe --token abc","nested":{"macAddress":{"value":"00-1A-2B-3C-4D-5E","status":"ok","source":"x"}},"list":["C:\\Users\\tatsuki\\x"]}
            """);
        redactor.RedactJson(node);
        Assert.Equal("app.exe", node!["name"]!.GetValue<string>());
        Assert.Equal(SimpleRedactor.Placeholder, node["commandLine"]!.GetValue<string>());
        Assert.Equal(SimpleRedactor.Placeholder, node["nested"]!["macAddress"]!["value"]!.GetValue<string>());
        Assert.Equal(@"%USERPROFILE%\x", node["list"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Disabled_redactor_leaves_everything_alone()
    {
        var redactor = new SimpleRedactor(new RedactionContext("tatsuki", "PC", @"C:\Users\tatsuki"), enabled: false);
        Assert.Equal(@"C:\Users\tatsuki", redactor.RedactText(@"C:\Users\tatsuki"));
    }

    [Fact]
    public void Protection_policy_treats_system_and_unknown_processes_as_not_operable()
    {
        using var s = CreateFakes();
        Assert.False(s.Protection.Evaluate(new ProcessKey(4, 1)).MayOperate);
        Assert.False(s.Protection.Evaluate(new ProcessKey(123_456, 1)).MayOperate);
        Assert.Equal(ProtectionKind.Unknown, s.Protection.Evaluate(new ProcessKey(123_456, 1)).Kind);
    }

    [Fact]
    public async Task Process_control_refuses_a_protected_process()
    {
        using var s = CreateFakes();
        var snapshot = await s.Snapshots.SampleAsync();
        var lsass = snapshot.Processes.First(p => p.Name == "lsass.exe").Key;
        var result = await s.ProcessControl.TerminateAsync(lsass);
        Assert.Equal(OperationCode.Protected, result.Code);
        var notepad = snapshot.Processes.First(p => p.Name == "notepad.exe").Key;
        Assert.True((await s.ProcessControl.TerminateAsync(notepad)).IsSuccess);
    }

    [Fact]
    public async Task Terminate_tree_plan_skips_protected_children()
    {
        using var s = CreateFakes();
        var snapshot = await s.Snapshots.SampleAsync();
        var services = snapshot.Processes.First(p => p.Name == "services.exe").Key;
        var plan = await s.ProcessControl.PlanTerminateTreeAsync(services);
        Assert.Contains(plan.Targets, k => k.Pid == 1100);
        Assert.DoesNotContain(plan.Targets, k => k.Pid == 960);
    }

    [Fact]
    public async Task Startup_toggle_works_only_on_toggleable_entries()
    {
        using var s = CreateFakes();
        Assert.True((await s.Startup.SetEnabledAsync("runHkcu:Steam", true)).IsSuccess);
        Assert.Equal(OperationCode.Unsupported, (await s.Startup.SetEnabledAsync("scheduledTask:OneDrive Standalone Update", true)).Code);
        Assert.Equal(OperationCode.NotFound, (await s.Startup.SetEnabledAsync("nope", true)).Code);
    }

    [Fact]
    public async Task Fake_helper_never_runs_anything()
    {
        using var s = CreateFakes();
        var request = new HelperRequest(HelperOperation.ServiceStart, "0123456789abcdef0123", @"C:\Temp\0123456789abcdef0123.json") { ServiceName = "Spooler" };
        var outcome = await s.Helper.RunAsync(request);
        Assert.Equal(OperationCode.Ok, outcome.Code);
        Assert.False(outcome.UacDeclined);
    }

    [Fact]
    public async Task Sensor_fake_returns_a_value_not_a_bare_list()
    {
        using var s = Create();
        var gpu = await s.Sensors.ReadGpuAsync();
        Assert.True(gpu.IsOk);
    }
}
