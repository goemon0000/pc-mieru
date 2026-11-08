using System.Text.Json.Nodes;
using PcMieru.Core;
using PcMieru.Core.Hardware;

namespace PcMieru.Gui.Tests;

/// <summary>The pieces of the GUI that hold decisions but no window: classifier, session totals, heavy apps, report text, settings, pump.</summary>
public class DataAndPagesTests
{
    private static ProcessInfo Proc(uint pid, string name, double? cpu, long memory = 0, uint session = 1) =>
        new(
            new ProcessKey(pid, 1000 + pid),
            name,
            0,
            null,
            session,
            1,
            0,
            8,
            0,
            memory,
            0,
            0,
            0,
            cpu is { } c ? Value.Ok(c, "t") : Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.Ok(ProcessState.Running, "t"));

    // ---- ProcessClassifier ----

    [Fact]
    public void Classifier_reads_the_windows_on_the_very_first_refresh()
    {
        // Regression: the age counter started at int.MaxValue and overflowed, so the window list was never read.
        var reads = 0;
        var classifier = new ProcessClassifier(() =>
        {
            reads++;
            return [42u];
        });
        classifier.Refresh();
        Assert.Equal(1, reads);
        Assert.Equal(ProcessGroup.Apps, classifier.Classify(Proc(42, "app.exe", 1)));
    }

    [Fact]
    public void Classifier_then_reads_only_every_nth_refresh()
    {
        var reads = 0;
        var classifier = new ProcessClassifier(() =>
        {
            reads++;
            return [];
        });
        for (var i = 0; i < 10; i++)
        {
            classifier.Refresh(every: 5);
        }

        Assert.Equal(2, reads);
    }

    [Fact]
    public void Classifier_puts_a_window_owner_in_apps_session_zero_and_known_names_in_windows_and_the_rest_in_background()
    {
        var classifier = new ProcessClassifier(() => [10u, 11u]);
        classifier.Refresh();
        Assert.Equal(ProcessGroup.Apps, classifier.Classify(Proc(10, "notepad.exe", 1)));
        Assert.Equal(ProcessGroup.Windows, classifier.Classify(Proc(4, "System", 1, session: 0)));
        Assert.Equal(ProcessGroup.Windows, classifier.Classify(Proc(200, "svchost.exe", 1)));
        Assert.Equal(ProcessGroup.Background, classifier.Classify(Proc(300, "helper.exe", 1)));
        // A window in session 0 (a service with a dialog) is still a Windows part, not an app.
        Assert.Equal(ProcessGroup.Windows, classifier.Classify(Proc(11, "svc.exe", 1, session: 0)));
    }

    [Fact]
    public void Sample_data_classifier_gives_the_apps_group_a_member()
    {
        var classifier = ProcessClassifier.ForSampleData();
        classifier.Refresh();
        Assert.Equal(ProcessGroup.Apps, classifier.Classify(Proc(3, "chrome.exe", 1)));
        Assert.NotEqual(ProcessGroup.Apps, classifier.Classify(Proc(4, "chrome.exe", 1)));
    }

    // ---- UsersPage.BuildRows ----

    [Fact]
    public void Users_totals_leave_the_idle_process_out_but_still_count_it()
    {
        var sessions = new[] { new SessionInfo(1, "user", SessionConnectState.Active, "Console") };
        var rows = UsersPage.BuildRows(sessions, [Proc(0, "System Idle Process", 90, 8192), Proc(100, "a.exe", 5, 1000), Proc(101, "b.exe", 3, 2000)], new HashSet<uint>(), out _);
        var row = Assert.Single(rows);
        Assert.Equal(8, row.Cpu, 3);
        Assert.Equal(3000, row.MemoryBytes);
        Assert.Equal(3, row.ProcessCount);
    }

    [Fact]
    public void Users_totals_are_per_session_and_an_unknown_session_still_gets_a_row()
    {
        var sessions = new[] { new SessionInfo(1, "user", SessionConnectState.Active, "Console") };
        var rows = UsersPage.BuildRows(sessions, [Proc(100, "a.exe", 5, 10, session: 1), Proc(200, "s.exe", 2, 20, session: 0), Proc(300, "x.exe", 1, 30, session: 7)], new HashSet<uint>(), out _);
        Assert.Equal([0u, 1u, 7u], rows.Select(r => r.Session.SessionId).ToArray());
        Assert.Equal(SessionConnectState.Other, rows[0].Session.State);
        Assert.Equal(20, rows[0].MemoryBytes);
        Assert.Equal(10, rows[1].MemoryBytes);
        Assert.All(rows, r => Assert.True(r.IsSession));
    }

    [Fact]
    public void Users_partial_is_reported_only_when_a_signed_in_user_could_not_be_read()
    {
        var user = new[] { new SessionInfo(2, "other", SessionConnectState.Disconnected, null) };
        UsersPage.BuildRows(user, [Proc(1, "a.exe", 1, session: 2)], new HashSet<uint>(), out var readable);
        Assert.False(readable);

        UsersPage.BuildRows(user, [Proc(1, "a.exe", null, session: 2)], new HashSet<uint>(), out var unreadableCpu);
        Assert.True(unreadableCpu);

        UsersPage.BuildRows(user, [], new HashSet<uint>(), out var noProcesses);
        Assert.True(noProcesses);

        // A session without a user name (services) is not "someone we cannot see".
        var nobody = new[] { new SessionInfo(0, string.Empty, SessionConnectState.Other, "Services") };
        UsersPage.BuildRows(nobody, [], new HashSet<uint>(), out var service);
        Assert.False(service);
    }

    [Fact]
    public void Users_expanded_session_lists_its_processes_heaviest_first()
    {
        var sessions = new[] { new SessionInfo(1, "user", SessionConnectState.Active, "Console") };
        var rows = UsersPage.BuildRows(sessions, [Proc(1, "low.exe", 1), Proc(2, "high.exe", 50), Proc(3, "mid.exe", 10)], new HashSet<uint> { 1 }, out _);
        Assert.Equal(["high.exe", "mid.exe", "low.exe"], rows.Skip(1).Select(r => r.Process!.Name).ToArray());
        Assert.False(rows[1].IsSession);
    }

    // ---- TopApps ----

    [Fact]
    public void Top_apps_leave_out_the_idle_process_and_kernel_parts()
    {
        var processes = new[]
        {
            Proc(0, "System Idle Process", 95, 8192),
            Proc(4, "System", 1, 5L << 30, 0),
            Proc(90, "Memory Compression", 0, 4L << 30, 0),
            Proc(100, "chrome.exe", 5, 1L << 30),
        };
        var apps = TopApps.Compute(processes, 16L << 30, _ => null);
        Assert.Equal(["chrome"], apps.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void Top_apps_merge_processes_of_one_product_into_one_line()
    {
        var processes = new[] { Proc(1, "chrome.exe", 4, 100), Proc(2, "chrome.exe", 1, 100), Proc(3, "chromium-helper.exe", 1, 100), Proc(4, "other.exe", 0, 10) };
        var apps = TopApps.Compute(processes, 1000, key => key.Pid <= 3 ? "Chrome" : null);
        Assert.Equal(["Chrome", "other"], apps.Select(a => a.Name).ToArray());
        Assert.Equal(3, apps[0].ProcessCount);
        Assert.Equal(300, apps[0].MemoryBytes);
        Assert.Equal(6, apps[0].Cpu, 3);
    }

    [Fact]
    public void Top_apps_do_not_merge_everything_that_carries_the_windows_product_name()
    {
        var processes = new[] { Proc(1, "svchost.exe", 2, 100), Proc(2, "explorer.exe", 1, 100) };
        var apps = TopApps.Compute(processes, 1000, _ => "Microsoft® Windows® Operating System");
        Assert.Equal(["explorer", "svchost"], apps.Select(a => a.Name).Order().ToArray());
    }

    [Fact]
    public void Top_apps_take_the_count_asked_for_ranked_by_cpu_share_plus_memory_share()
    {
        var processes = Enumerable.Range(1, 8).Select(i => Proc((uint)i, "app" + i + ".exe", i, 0)).ToArray();
        var apps = TopApps.Compute(processes, 1000, _ => null, count: 3);
        Assert.Equal(["app8", "app7", "app6"], apps.Select(a => a.Name).ToArray());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Microsoft® Windows® Operating System", true)]
    [InlineData("Microsoft Windows Operating System", true)]
    [InlineData("Google Chrome", false)]
    [InlineData("Windows Terminal", false)]
    public void Generic_product_names_are_recognised(string? product, bool generic) =>
        Assert.Equal(generic, TopApps.IsGenericProduct(product));

    // ---- Report text ----

    private sealed class SpyRedactor(bool enabled) : IRedactor
    {
        public bool Enabled { get; } = enabled;

        public int TextCalls { get; private set; }

        public int JsonCalls { get; private set; }

        public string RedactText(string text)
        {
            TextCalls++;
            return text.Replace("PC", "XX", StringComparison.Ordinal);
        }

        public JsonNode? RedactJson(JsonNode? node)
        {
            JsonCalls++;
            return node;
        }
    }

    private static async Task<ReportDocument> ReportAsync()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        return await services.Reports.BuildAsync(new ReportOptions(0));
    }

    [Fact]
    public async Task Report_marks_the_document_with_what_was_really_done()
    {
        var document = await ReportAsync();
        var onAndEnabled = JsonNode.Parse(DiagnosePage.BuildText(document, true, true, new SpyRedactor(true), Language.En))!;
        Assert.True(onAndEnabled["redacted"]!.GetValue<bool>());

        var switchedOff = JsonNode.Parse(DiagnosePage.BuildText(document, true, false, new SpyRedactor(true), Language.En))!;
        Assert.False(switchedOff["redacted"]!.GetValue<bool>());

        // The process was started with --no-redact: ticking the box cannot make the text "redacted".
        var forcedOff = JsonNode.Parse(DiagnosePage.BuildText(document, true, true, new SpyRedactor(false), Language.En))!;
        Assert.False(forcedOff["redacted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Report_runs_the_redactor_only_when_asked()
    {
        var document = await ReportAsync();
        var asked = new SpyRedactor(true);
        DiagnosePage.BuildText(document, false, true, asked, Language.En);
        DiagnosePage.BuildText(document, true, true, asked, Language.En);
        Assert.Equal(1, asked.TextCalls);
        Assert.Equal(1, asked.JsonCalls);

        var notAsked = new SpyRedactor(true);
        DiagnosePage.BuildText(document, false, false, notAsked, Language.En);
        DiagnosePage.BuildText(document, true, false, notAsked, Language.En);
        Assert.Equal(0, notAsked.TextCalls);
        Assert.Equal(0, notAsked.JsonCalls);
    }

    [Fact]
    public async Task Report_markdown_follows_the_language_and_json_is_the_same_in_both()
    {
        var document = await ReportAsync();
        var spy = new SpyRedactor(true);
        var ja = DiagnosePage.BuildText(document, false, false, spy, Language.Ja);
        var en = DiagnosePage.BuildText(document, false, false, spy, Language.En);
        Assert.NotEqual(ja, en);
        Assert.StartsWith("# ", ja, StringComparison.Ordinal);
        Assert.Equal(DiagnosePage.BuildText(document, true, false, spy, Language.Ja), DiagnosePage.BuildText(document, true, false, spy, Language.En));
    }

    // ---- Hardware text ----

    [Fact]
    public void Parts_tree_and_copy_text_are_built_from_the_json_of_the_model()
    {
        var info = FakeHardwareData.Create(new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));
        var root = (JsonObject)PcMieruJson.ToNode(info)!;
        var nodes = PartsModel.Build(root);
        Assert.NotEmpty(nodes);
        var text = PartsModel.ToText(nodes);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal(text, PartsModel.ToText(PartsModel.Build((JsonObject)PcMieruJson.ToNode(info)!)));
    }

    [Fact]
    public void Parts_open_a_details_record_into_folded_rows_instead_of_printing_its_json()
    {
        var root = JsonNode.Parse(
            """
            {"cpu":{"status":"ok","source":"cpuid","value":{
              "name":{"status":"ok","value":"Test CPU","source":"cpuid"},
              "details":{"maxClockMhz":{"status":"ok","value":5850,"source":"smbios:type4"},
                         "socket":{"status":"unsupported","source":"smbios:type4"}}}}}
            """)!.AsObject();
        var cpu = PartsModel.Find(PartsModel.Build(root), "cpu")!;
        var rows = PartsModel.Rows(cpu);
        Assert.DoesNotContain(rows, r => r.Text.StartsWith('{'));
        Assert.DoesNotContain(rows, r => r.Key == "details");
        var clock = Assert.Single(rows, r => r.Key == "maxClockMhz");
        Assert.True(clock.Expert);
        Assert.Equal(ValueStatus.Ok, clock.Status);
        Assert.Equal(ValueStatus.Unsupported, Assert.Single(rows, r => r.Key == "socket").Status);
        Assert.False(Assert.Single(rows, r => r.Key == "name").Expert);
    }

    // ---- Services / settings / pump ----

    private static ServiceInfo Service(ServiceState state, ServiceStartType start) =>
        new("svc", "Service", state, start, null, Value.NotCollected<string>("t"), [], []);

    [Theory]
    [InlineData(ServiceState.Stopped, ServiceStartType.Manual, true, false)]
    [InlineData(ServiceState.Stopped, ServiceStartType.Disabled, false, false)]
    [InlineData(ServiceState.Running, ServiceStartType.Automatic, false, true)]
    [InlineData(ServiceState.Paused, ServiceStartType.Manual, false, true)]
    [InlineData(ServiceState.StartPending, ServiceStartType.Manual, false, false)]
    public void Start_and_stop_are_offered_only_where_they_can_work(ServiceState state, ServiceStartType start, bool canStart, bool canStop)
    {
        var service = Service(state, start);
        Assert.Equal(canStart, ServicesPage.CanStart(service));
        Assert.Equal(canStop, ServicesPage.CanStop(service));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(600, 500)]
    [InlineData(1400, 1000)]
    [InlineData(1600, 2000)]
    [InlineData(3000, 2000)]
    [InlineData(4000, 5000)]
    [InlineData(60000, 5000)]
    public void Settings_snap_any_interval_to_one_of_the_four_choices(int requested, int expected)
    {
        Assert.Equal(expected, GuiSettings.Snap(requested));
        Assert.Equal(expected, new GuiSettings { IntervalMs = requested }.IntervalMs);
    }

    [Fact]
    public void Settings_keep_page_values_and_do_not_remember_the_pause()
    {
        var settings = new GuiSettings { Paused = true };
        settings.SetInt("win.w", 1234);
        settings.SetBool("proc.tree", true);
        var again = new GuiSettings();
        foreach (var line in settings.ToLines())
        {
            again.Apply(line);
        }

        Assert.Equal(1234, again.GetInt("win.w", 0));
        Assert.True(again.GetBool("proc.tree", false));
        Assert.False(again.Paused);
        Assert.Equal(7, again.GetInt("missing", 7));
    }

    [Fact]
    public void Pump_slow_mode_never_makes_a_slow_choice_faster_and_never_a_fast_one_slower_than_five_seconds()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services.Snapshots, 500);
        Assert.Equal(500, pump.EffectiveIntervalMs);
        pump.SetSlowMode(true);
        Assert.Equal(SnapshotPump.SlowIntervalMs, pump.EffectiveIntervalMs);
        pump.SetSlowMode(false);
        Assert.Equal(500, pump.EffectiveIntervalMs);
        pump.SetInterval(5000);
        pump.SetSlowMode(true);
        Assert.Equal(5000, pump.EffectiveIntervalMs);
    }

    [Fact]
    public void Pump_reports_a_change_of_interval_pause_or_slow_mode_once_each_and_nothing_for_a_repeat()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services.Snapshots, 1000);
        var changes = 0;
        pump.StateChanged += () => changes++;
        pump.SetInterval(1000);
        pump.SetPaused(false);
        pump.SetSlowMode(false);
        Assert.Equal(0, changes);
        pump.SetInterval(2000);
        pump.SetPaused(true);
        pump.SetSlowMode(true);
        Assert.Equal(3, changes);
        Assert.True(pump.Paused);
        Assert.False(pump.Running);
    }
}
