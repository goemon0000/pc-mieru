using System.Collections.Concurrent;
using System.Globalization;
using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// The checks page (DESIGN-A §7): where it sits in the navigation, its four tabs, what each tab draws for each state of the one run the
/// window owns, the history in each of its states, and that looking at any of it touches no disk. The tests are synchronous bodies inside
/// <see cref="Ui.Run"/> (the queue context stands in for the UI thread), build the page in a window that has a handle only where a list needs
/// one and is never shown, use scripted runners and temporary folders, and assert on what ends up on screen, never on how long anything took.
/// </summary>
public class ChecksPageTests
{
    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.InvariantCulture);

    private static IEnumerable<int> Rows(CheckResultGrid grid) => Enumerable.Range(0, grid.RowCount);

    private static int RowOf(CheckResultGrid grid, string id, string? subject = null) =>
        Rows(grid).First(row => grid.ItemAt(row) is { } item && item.Id == id && (subject is null || item.Subject?.Id == subject));

    private static CheckHistoryEntry[] EntriesOf(params CheckResult[] results) => [.. results.Select(CheckHistoryEntry.From)];

    // ---- the window the page is built in ----

    /// <summary>A page in a window that is never shown, on fake services and the controller a test hands in (which the rig disposes).</summary>
    private sealed class PageRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public PageRig(CheckController checks, Language language = Language.En, bool handle = false)
            : this(_ => checks, language, handle)
        {
        }

        public PageRig(Func<PcMieruServices, CheckController> checks, Language language = Language.En, bool handle = false)
        {
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Checks = checks(Services);
            Context = new GuiContext(Services, Pump, _theme, new GuiSettings(), checks: Checks);
            Page = new ChecksPage(Context);
            Host = new Form { ShowInTaskbar = false };
            Host.Controls.Add(Page);
            if (handle)
            {
                _ = Host.Handle;
                _ = HistoryTab.List.Handle;
            }
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public CheckController Checks { get; }

        public GuiContext Context { get; }

        public ChecksPage Page { get; }

        public Form Host { get; }

        public SystemCheckTab SystemTab => (SystemCheckTab)Page.TabOf("system");

        public LoadCheckTab LoadTab => (LoadCheckTab)Page.TabOf("load");

        public StorageCheckTab StorageTab => (StorageCheckTab)Page.TabOf("storage");

        public CheckHistoryTab HistoryTab => (CheckHistoryTab)Page.TabOf("history");

        /// <summary>Brings the page and its history tab to the front, and waits (pumping) until the list reached the given view.</summary>
        public CheckHistoryTab ShowHistory(QueueContext ui, HistoryView until)
        {
            Page.OnActivated();
            Page.SelectTab("history");
            Assert.True(ui.Until(() => HistoryTab.View == until), $"The history stayed {HistoryTab.View}, expected {until}.");
            return HistoryTab;
        }

        public void Dispose()
        {
            Host.Dispose();
            Checks.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
        }
    }

    /// <summary>A history that answers what a test scripts: how a list read goes (it is told which call this is) and how an open of one run goes.</summary>
    private sealed class StubHistory : IReadOnlyCheckHistory
    {
        private int _lists;
        private int _gets;

        public Func<int, CancellationToken, Task<CheckHistoryPage>> OnList { get; init; } = static (_, _) => Task.FromResult(new CheckHistoryPage([], []));

        public Func<Guid, CancellationToken, Task<CheckHistoryGetResult>> OnGet { get; init; } = static (_, _) => Task.FromResult(CheckHistoryGetResult.NotFound);

        public int Lists => Volatile.Read(ref _lists);

        public int Gets => Volatile.Read(ref _gets);

        public Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct) => OnList(Interlocked.Increment(ref _lists), ct);

        public Task<CheckHistoryGetResult> GetAsync(Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref _gets);
            return OnGet(runId, ct);
        }
    }

    /// <summary>Hands one fixed answer back, whatever it is asked (a run whose result the check refused, for example).</summary>
    private sealed class FixedRunner(CheckRun run) : ICheckRunner
    {
        public Task<CheckRun> RunAsync(CheckRequest request, IProgress<CheckProgress>? progress, CancellationToken ct) => Task.FromResult(run);
    }

    /// <summary>Remembers each request it was given and lets another runner do the run.</summary>
    private sealed class RecordingRunner(ICheckRunner inner) : ICheckRunner
    {
        private readonly ConcurrentQueue<CheckRequest> _requests = new();

        public IReadOnlyCollection<CheckRequest> Requests => _requests;

        public Task<CheckRun> RunAsync(CheckRequest request, IProgress<CheckProgress>? progress, CancellationToken ct)
        {
            _requests.Enqueue(request);
            return inner.RunAsync(request, progress, ct);
        }
    }

    // ---- the catalog and the tabs ----

    [Fact]
    public void The_checks_page_is_in_the_catalog_right_before_diagnose()
    {
        var ids = PageCatalog.All.Select(static p => p.Id).ToArray();
        var at = Array.IndexOf(ids, "checks");

        Assert.True(at > 0, "The checks page is not in the catalog.");
        Assert.Equal(1, ids.Count(static id => id == "checks"));
        Assert.Equal("diagnose", ids[at + 1]);
        Assert.Equal("gui.nav.checks", PageCatalog.All[at].TitleKey);
    }

    [Fact]
    public void The_tabs_are_system_load_storage_history_in_that_order_with_their_names_in_both_languages()
    {
        Ui.Run(_ =>
        {
            using var rig = new PageRig(new RunnerQueue().Controller());

            Assert.Equal(new[] { "system", "load", "storage", "history" }, rig.Page.TabIds);
            Assert.Equal("system", rig.Page.CurrentId);
            Assert.True(rig.Page.ButtonOf("system").Checked);
            Assert.All(new[] { "load", "storage", "history" }, id => Assert.False(rig.Page.ButtonOf(id).Checked));
            Assert.Equal(new[] { "System", "Load", "Storage", "History" }, rig.Page.TabIds.Select(id => rig.Page.ButtonOf(id).Text));

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.Equal(new[] { "システム", "負荷", "記憶媒体", "履歴" }, rig.Page.TabIds.Select(id => rig.Page.ButtonOf(id).Text));
        });
    }

    [Fact]
    public void A_tab_is_added_between_storage_and_history_with_one_line_of_the_page()
    {
        // Stage B puts the record of tests between Storage and History. That is one line in ChecksPage, because each tab is one line there.
        var lines = File.ReadAllLines(Path.Combine(Harness.GuiSource, "Checks", "ChecksPage.cs"))
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("_tabs.Add(new TabEntry(", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, lines.Length);
        Assert.Contains("\"system\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"load\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"storage\"", lines[2], StringComparison.Ordinal);
        Assert.Contains("\"history\"", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_remembers_the_tab_it_was_left_on()
    {
        Ui.Run(_ =>
        {
            using var rig = new PageRig(new RunnerQueue().Controller());
            rig.Page.SelectTab("storage");
            rig.Page.SaveState();

            using var again = new ChecksPage(rig.Context);
            Assert.Equal("storage", again.CurrentId);
            Assert.True(again.ButtonOf("storage").Checked);
        });
    }

    // ---- the load tab ----

    [Fact]
    public void The_load_tab_offers_the_cpu_and_the_memory_only_for_30_seconds_2_minutes_or_5_minutes()
    {
        Ui.Run(_ =>
        {
            using var rig = new PageRig(new RunnerQueue().Controller());
            var load = rig.LoadTab;

            Assert.Equal(new[] { "CPU", "Memory" }, load.CpuButton.Parent!.Controls.Cast<Control>().Select(static c => c.Text));
            Assert.True(load.CpuButton.Checked);
            Assert.False(load.MemoryButton.Checked);
            Assert.Equal(CheckKind.Cpu, load.SelectedKind);

            Assert.Equal(new[] { "30 s", "2 min", "5 min" }, load.Duration.Items.Cast<string>());
            Assert.Equal(CheckRequest.AllowedDurations.Select(static d => (int)d.TotalSeconds), LoadCheckTab.DurationSeconds);
            Assert.Equal(30, load.Seconds);
            load.Duration.SelectedIndex = 2;
            Assert.Equal(300, load.Seconds);

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.Equal(new[] { "30 秒", "2 分", "5 分" }, load.Duration.Items.Cast<string>());
            Assert.Equal(300, load.Seconds);
            Assert.Equal(new[] { "CPU", "メモリ" }, new[] { load.CpuButton.Text, load.MemoryButton.Text });
        });
    }

    [Fact]
    public void The_start_button_sends_the_target_and_the_length_that_are_chosen()
    {
        Ui.Run(ui =>
        {
            var first = new ScriptedRunner { Hold = false };
            var second = new ScriptedRunner { Hold = false };
            using var rig = new PageRig(new RunnerQueue(first, second).Controller());

            rig.LoadTab.PressStart();
            Assert.True(ui.Until(() => first.Started.IsCompleted && !rig.Checks.IsBusy));
            var one = Assert.Single(first.Requests);
            Assert.Equal(CheckKind.Cpu, one.Kind);
            Assert.Equal(TimeSpan.FromSeconds(30), one.Duration);
            Assert.False(one.AllowElevation);

            rig.LoadTab.PressKind(CheckKind.Memory);
            rig.LoadTab.Duration.SelectedIndex = 2;
            rig.LoadTab.PressStart();
            Assert.True(ui.Until(() => second.Started.IsCompleted && !rig.Checks.IsBusy));
            var two = Assert.Single(second.Requests);
            Assert.Equal(CheckKind.Memory, two.Kind);
            Assert.Equal(TimeSpan.FromSeconds(300), two.Duration);
        });
    }

    [Fact]
    public void A_load_shows_its_progress_and_rate_and_every_start_button_waits_while_it_runs()
    {
        Ui.Run(ui =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new ScriptedRunner { SaveGate = gate };
            var queue = new RunnerQueue(runner);
            using var rig = new PageRig(queue.Controller());
            rig.Page.OnActivated();
            rig.Page.SelectTab("load");
            var load = rig.LoadTab;

            Assert.False(load.ProgressShown);
            Assert.True(load.StartButton.Enabled);
            Assert.False(load.StopButton.Enabled);
            Assert.Equal("Not run yet", load.StateLabel.Text);

            load.PressKind(CheckKind.Memory);
            load.PressStart();
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            ui.Pump();
            Assert.Equal("Running", load.StateLabel.Text);
            Assert.True(load.ProgressShown);
            Assert.Equal("Preparing", load.ElapsedLabel.Text);

            runner.Reporter!.Report(new CheckProgress(runner.RunId, CheckStage.Loading, 12, 30, 5300, "MiB/s"));
            ui.Pump();
            Assert.Equal("0:12 / 0:30", load.ElapsedLabel.Text);
            Assert.Equal("Current rate: 5,300 MiB/s", load.RateLabel.Text);
            Assert.Equal(0.4, load.Bar.Fraction!.Value, 6);

            // One run at a time: no tab can start another, and the chosen target cannot change under the run.
            foreach (var tab in new RunCheckTab[] { rig.SystemTab, rig.LoadTab, rig.StorageTab })
            {
                Assert.False(tab.StartButton.Enabled);
            }

            Assert.True(load.StopButton.Enabled);
            Assert.False(rig.SystemTab.StopButton.Enabled);
            Assert.False(load.CpuButton.Enabled);
            Assert.False(load.MemoryButton.Enabled);
            Assert.False(load.Duration.Enabled);
            Assert.Contains("Memory", rig.SystemTab.HintLabel.Text, StringComparison.Ordinal);
            rig.SystemTab.PressStart();
            load.PressKind(CheckKind.Cpu);
            Assert.Equal(1, queue.Made);
            Assert.Equal(CheckKind.Memory, load.SelectedKind);
            Assert.Equal(CheckRunState.NotRun, rig.Checks.StateOf(CheckKind.System).State);

            // The tab and the page can change while it runs; the run and what it last said stay with the window.
            rig.Page.SelectTab("history");
            Assert.True(ui.Until(() => rig.HistoryTab.IsReady));
            rig.Page.SelectTab("load");
            Assert.Equal("0:12 / 0:30", load.ElapsedLabel.Text);
            using (var rebuilt = new ChecksPage(rig.Context))
            {
                var again = (LoadCheckTab)rebuilt.TabOf("load");
                Assert.Equal("Running", again.StateLabel.Text);
                Assert.True(again.StopButton.Enabled);
                Assert.True(again.ProgressShown);
                Assert.Equal(CheckKind.Memory, again.SelectedKind);
                Assert.Equal("0:12 / 0:30", again.ElapsedLabel.Text);
            }

            // Stop: "stopping" while the result is saved, then "stopped".
            load.PressStop();
            ui.Pump();
            Assert.Equal("Stopping (saving the result)", load.StateLabel.Text);
            Assert.False(load.StopButton.Enabled);
            Assert.False(load.StartButton.Enabled);
            Assert.True(runner.WasStopped);

            gate.SetResult();
            Assert.True(ui.Until(() => !rig.Checks.IsBusy));
            ui.Pump();
            Assert.Equal("Stopped", load.StateLabel.Text);
            Assert.False(load.ProgressShown);
            Assert.True(load.StartButton.Enabled);
            Assert.True(load.CpuButton.Enabled);
            Assert.Equal(CheckOutcome.Stopped, load.Grid.Result!.Outcome);
            Assert.True(load.Grid.RowCount > 0);
        });
    }

    // ---- the state line ----

    [Fact]
    public void The_state_line_follows_one_check_through_every_state_in_both_languages()
    {
        Ui.Run(ui =>
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new ScriptedRunner { SaveGate = hold };
            var completed = new ScriptedRunner { Hold = false };
            var thrown = new ScriptedRunner { Hold = false, Failure = new InvalidOperationException("boom") };
            var failed = new ScriptedRunner { Hold = false, Outcome = CheckOutcome.Failed };
            var unsaved = new ScriptedRunner { Hold = false, Save = CheckSaveState.Failed(CheckSaveFailureCodes.DiskFull) };
            using var rig = new PageRig(new RunnerQueue(stopped, completed, thrown, failed, unsaved).Controller());
            rig.Page.OnActivated();
            var system = rig.SystemTab;

            void Finish(ScriptedRunner runner)
            {
                Assert.True(ui.Until(() => runner.Started.IsCompleted));
                Assert.True(ui.Until(() => !rig.Checks.IsBusy));
                ui.Pump();
            }

            Assert.Equal("Not run yet", system.StateLabel.Text);

            system.PressStart();
            Assert.True(ui.Until(() => stopped.Started.IsCompleted));
            ui.Pump();
            Assert.Equal("Running", system.StateLabel.Text);
            Assert.True(system.StopButton.Enabled);

            system.PressStop();
            ui.Pump();
            Assert.Equal("Stopping (saving the result)", system.StateLabel.Text);
            hold.SetResult();
            Finish(stopped);
            Assert.Equal("Stopped", system.StateLabel.Text);

            system.PressStart();
            Finish(completed);
            Assert.Equal("Completed", system.StateLabel.Text);
            Assert.Contains("Overall:", system.HintLabel.Text, StringComparison.Ordinal);

            system.PressStart();
            Finish(thrown);
            Assert.Equal("Failed: An internal error occurred (InvalidOperationException 0x80131509)", system.StateLabel.Text);
            Assert.DoesNotContain("boom", system.StateLabel.Text, StringComparison.Ordinal);

            system.PressStart();
            Finish(failed);
            Assert.Equal("Failed: An internal error occurred", system.StateLabel.Text);

            system.PressStart();
            Finish(unsaved);
            Assert.Equal("Completed · The result could not be saved: The disk is full", system.StateLabel.Text);
            Assert.True(system.Grid.RowCount > 0);

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.Equal("完了しました ・ 結果を保存できませんでした: 保存先の空きが足りません", system.StateLabel.Text);
            Assert.True(system.StartButton.Enabled);
        });
    }

    [Fact]
    public void A_result_the_check_refused_to_save_is_still_shown_with_its_rows()
    {
        Ui.Run(ui =>
        {
            var runId = Guid.NewGuid();
            var built = FakeCheckData.SystemCheck(runId, FakeCheckData.Start);
            var empty = CheckResult.Create(FakeCheckData.App, runId, CheckKind.System, FakeCheckData.Start, FakeCheckData.Start.AddSeconds(1), CheckOutcome.Failed, []);
            var run = new CheckRun(empty, CheckSaveState.Saved("empty.json"), CheckRunFailureCodes.ResultInvalid, "invalid", built, null, "first problem");
            using var rig = new PageRig(new CheckController(() => new FixedRunner(run), new InMemoryCheckHistory([])));
            rig.Page.OnActivated();

            rig.SystemTab.PressStart();
            Assert.True(ui.Until(() => rig.Checks.StateOf(CheckKind.System).State == CheckRunState.Failed));
            ui.Pump();

            Assert.Same(built, rig.SystemTab.Grid.Result);
            Assert.True(rig.SystemTab.Grid.RowCount > 0);
            Assert.Equal("Failed: The result did not pass validation and was replaced by an empty one", rig.SystemTab.StateLabel.Text);
        });
    }

    // ---- the result table ----

    [Fact]
    public void Each_verdict_has_its_word_and_the_observation_is_plain_text_that_never_looks_like_a_verdict()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner { Hold = false };
            using var rig = new PageRig(new RunnerQueue(runner).Controller());
            rig.Page.OnActivated();
            rig.SystemTab.PressStart();
            Assert.True(ui.Until(() => runner.Started.IsCompleted && !rig.Checks.IsBusy));
            ui.Pump();

            var grid = rig.SystemTab.Grid;
            var words = new Dictionary<ItemVerdict, string>
            {
                [ItemVerdict.Ok] = "OK",
                [ItemVerdict.Caution] = "Caution",
                [ItemVerdict.Check] = "Needs review",
                [ItemVerdict.Unavailable] = "Not measured",
                [ItemVerdict.Info] = "Info",
            };
            var seen = new HashSet<ItemVerdict>();
            foreach (var row in Rows(grid).Where(r => grid.ItemAt(r) is not null))
            {
                var item = grid.ItemAt(row)!;
                seen.Add(item.Verdict);
                Assert.Equal(words[item.Verdict], grid.TextAt(row, 1));
                var tone = grid.ToneAt(row, 1);
                switch (item.Verdict)
                {
                    case ItemVerdict.Check or ItemVerdict.Caution:
                        Assert.Equal(CellTone.Accent, tone);
                        break;
                    case ItemVerdict.Unavailable:
                        Assert.Equal(CellTone.Dim, tone);
                        break;
                    default:
                        Assert.Equal(CellTone.Normal, tone);
                        break;
                }

                // The label is the words of the table, never the raw ID or the "[key]" of a missing label.
                Assert.NotEqual(item.Id, grid.TextAt(row, 0));
                Assert.False(grid.TextAt(row, 0).StartsWith('['));
            }

            Assert.Equal(5, seen.Count);
        });
    }

    [Fact]
    public void A_storage_counter_that_grew_shows_before_after_and_the_difference_under_its_own_row_in_both_languages()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner { Hold = false };
            using var rig = new PageRig(new RunnerQueue(runner).Controller());
            rig.Page.OnActivated();
            rig.Page.SelectTab("storage");
            rig.StorageTab.PressStart();
            Assert.True(ui.Until(() => runner.Started.IsCompleted && !rig.Checks.IsBusy));
            ui.Pump();

            var grid = rig.StorageTab.Grid;
            var delta = RowOf(grid, "storage.nvme.power_on_hours.delta", "disk-1");
            Assert.Equal("storage.nvme.power_on_hours", grid.ItemAt(delta - 1)!.Id);
            Assert.Equal("Info", grid.TextAt(delta, 1));
            Assert.Equal(CellTone.Normal, grid.ToneAt(delta, 1));
            Assert.Contains("23", grid.TextAt(delta, 2), StringComparison.Ordinal);
            Assert.Contains("1,204 → 1,227 (+23)", grid.TextAt(delta, 3), StringComparison.Ordinal);

            var errors = RowOf(grid, "storage.nvme.error_log_entries.delta", "disk-1");
            Assert.Contains("12 → 15 (+3)", grid.TextAt(errors, 3), StringComparison.Ordinal);
            Assert.Equal(CellTone.Accent, grid.ToneAt(errors, 1));

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            delta = RowOf(grid, "storage.nvme.power_on_hours.delta", "disk-1");
            Assert.Contains("1,204 → 1,227（+23）", grid.TextAt(delta, 3), StringComparison.Ordinal);

            // One header row for each disk (the two samples that look the same come after the first two), with that disk's own verdict; the disk that Windows refused says "not measured".
            var headers = Rows(grid).Where(row => grid.ItemAt(row) is null).ToArray();
            Assert.Equal(4, headers.Length);
            Assert.Equal("測れず", grid.TextAt(headers[1], 1));

            // The two disks that look the same: every change row says why it was not compared, and none of them shows a number.
            var twin = RowOf(grid, "storage.nvme.power_on_hours.delta", "disk-3");
            Assert.Equal("storage.nvme.power_on_hours", grid.ItemAt(twin - 1)!.Id);
            Assert.DoesNotContain("→", grid.TextAt(twin, 3), StringComparison.Ordinal);
            Assert.Contains("同じディスク", grid.TextAt(twin, 3), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_change_of_a_counter_is_written_on_its_change_row_only_and_not_twice()
    {
        Ui.Run(ui =>
        {
            var runId = Guid.NewGuid();
            var disk = new CheckSubject("disk-1", "Test disk");
            CheckEvidence Change() => new() { Before = "1204", After = "1227" };
            var items = new[]
            {
                // The counter row itself carries the same before and after (a writer may put them on both rows): they show on the change row, once.
                CheckItem.Create("storage.nvme.power_on_hours", CheckValue.Dec("1227"), ItemVerdict.Info, CheckSource.Nvme, subject: disk, evidence: Change()),
                CheckItem.Create("storage.nvme.power_on_hours.delta", CheckValue.SignedDec("23"), ItemVerdict.Info, CheckSource.Baseline, subject: disk, evidence: Change()),
                // A change row whose counter is not in the result stands alone and keeps its own change text.
                CheckItem.Create("storage.nvme.unsafe_shutdowns.delta", CheckValue.SignedDec("1"), ItemVerdict.Info, CheckSource.Baseline, subject: disk, evidence: new CheckEvidence { Before = "44", After = "45" }),
            };
            var result = CheckResult.Create(FakeCheckData.App, runId, CheckKind.Storage, FakeCheckData.Start, FakeCheckData.Start.AddSeconds(1), CheckOutcome.Completed, items);
            using var rig = new PageRig(new CheckController(() => new FixedRunner(new CheckRun(result, CheckSaveState.Saved("x.json"))), new InMemoryCheckHistory([])));
            rig.Page.OnActivated();
            rig.Page.SelectTab("storage");

            rig.StorageTab.PressStart();
            Assert.True(ui.Until(() => rig.Checks.StateOf(CheckKind.Storage).State == CheckRunState.Completed));
            ui.Pump();

            var grid = rig.StorageTab.Grid;
            var counter = RowOf(grid, "storage.nvme.power_on_hours", "disk-1");
            var delta = RowOf(grid, "storage.nvme.power_on_hours.delta", "disk-1");
            Assert.Equal(counter + 1, delta);
            Assert.DoesNotContain("→", grid.TextAt(counter, 3), StringComparison.Ordinal);
            Assert.Contains("1,204 → 1,227 (+23)", grid.TextAt(delta, 3), StringComparison.Ordinal);

            var alone = RowOf(grid, "storage.nvme.unsafe_shutdowns.delta", "disk-1");
            Assert.Contains("44 → 45 (+1)", grid.TextAt(alone, 3), StringComparison.Ordinal);

            // The same in Japanese: the language change draws the rows again with the same rule.
            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.DoesNotContain("→", grid.TextAt(RowOf(grid, "storage.nvme.power_on_hours", "disk-1"), 3), StringComparison.Ordinal);
            Assert.Contains("1,204 → 1,227（+23）", grid.TextAt(RowOf(grid, "storage.nvme.power_on_hours.delta", "disk-1"), 3), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_result_that_did_not_validate_is_drawn_row_by_row_even_with_a_value_no_cell_can_be_written_from()
    {
        Ui.Run(ui =>
        {
            var runId = Guid.NewGuid();
            var disk = new CheckSubject("disk-1", "Test disk");
            var items = new[]
            {
                CheckItem.Create("system.os.uptime", CheckValue.Number(1e300), ItemVerdict.Info, CheckSource.Win32),
                CheckItem.Create("storage.nvme.critical_warning", CheckValue.Int(1L << 40), ItemVerdict.Ok, CheckSource.Nvme, subject: disk),
                CheckItem.Create("storage.capacity", CheckValue.Dec("340282366920938463463374607431768211455"), ItemVerdict.Info, CheckSource.StorageQuery, subject: disk),
                CheckItem.Create("storage.temperature", CheckValue.Number(41), ItemVerdict.Ok, CheckSource.Nvme, subject: disk, evidence: new CheckEvidence { Cv = double.NaN, ClockSteadiness = double.PositiveInfinity }),
                CheckItem.Create("storage.nvme.power_on_hours", CheckValue.Dec("5"), ItemVerdict.Info, CheckSource.Nvme, subject: disk),
            };
            var built = CheckResult.Create(FakeCheckData.App, runId, CheckKind.System, FakeCheckData.Start, FakeCheckData.Start.AddSeconds(1), CheckOutcome.Completed, items);
            var empty = CheckResult.Create(FakeCheckData.App, runId, CheckKind.System, FakeCheckData.Start, FakeCheckData.Start.AddSeconds(1), CheckOutcome.Failed, []);
            var run = new CheckRun(empty, CheckSaveState.Saved("empty.json"), CheckRunFailureCodes.ResultInvalid, "invalid", built, null, "first problem");
            using var rig = new PageRig(new CheckController(() => new FixedRunner(run), new InMemoryCheckHistory([])));
            rig.Page.OnActivated();

            rig.SystemTab.PressStart();
            Assert.True(ui.Until(() => rig.Checks.StateOf(CheckKind.System).State == CheckRunState.Failed));
            ui.Pump();

            var grid = rig.SystemTab.Grid;
            Assert.Same(built, grid.Result);
            foreach (var item in items)
            {
                var row = RowOf(grid, item.Id, item.Subject?.Id);
                Assert.False(string.IsNullOrEmpty(grid.TextAt(row, 0)));
                Assert.False(string.IsNullOrEmpty(grid.TextAt(row, 2)), $"No value text for {item.Id}.");
            }

            // 1 << 40 is more than a byte, and the mask is still written from the number itself.
            Assert.Equal("0x10000000000", grid.TextAt(RowOf(grid, "storage.nvme.critical_warning", "disk-1"), 2));
            var temperature = grid.TextAt(RowOf(grid, "storage.temperature", "disk-1"), 3);
            Assert.False(string.IsNullOrEmpty(temperature));
            Assert.Equal("Failed: The result did not pass validation and was replaced by an empty one", rig.SystemTab.StateLabel.Text);
        });
    }

    [Fact]
    public void A_cell_that_cannot_be_written_falls_back_to_its_raw_text_and_never_throws()
    {
        Assert.Equal("fine", CheckFormat.Safe(static () => "fine", static () => "raw"));
        Assert.Equal("raw", CheckFormat.Safe(static () => throw new InvalidOperationException("no"), static () => "raw"));

        // The mask of a whole number of any size, written from the number itself: 0x05, 0xFF, and 128 bits (a valid dec) with no cast in between.
        Assert.Equal("0x05", CheckFormat.HexText(5));
        Assert.Equal("0xFF", CheckFormat.HexText(255));
        Assert.Equal("0x00", CheckFormat.HexText(0));
        Assert.Equal("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", CheckFormat.HexText(System.Numerics.BigInteger.Parse("340282366920938463463374607431768211455", CultureInfo.InvariantCulture)));
        Assert.Null(CheckFormat.HexText(-1));
    }

    [Fact]
    public void While_a_kind_runs_again_the_table_and_the_line_under_it_say_they_are_the_previous_result()
    {
        Ui.Run(ui =>
        {
            var first = new ScriptedRunner { Hold = false };
            var second = new ScriptedRunner();
            using var rig = new PageRig(new RunnerQueue(first, second).Controller());
            rig.Page.OnActivated();
            var system = rig.SystemTab;

            system.PressStart();
            Assert.True(ui.Until(() => first.Started.IsCompleted && !rig.Checks.IsBusy));
            ui.Pump();
            Assert.DoesNotContain("Previous result", system.HintLabel.Text, StringComparison.Ordinal);
            var shown = system.Grid.Result;
            Assert.NotNull(shown);

            system.PressStart();
            Assert.True(ui.Until(() => second.Started.IsCompleted));
            ui.Pump();
            Assert.Equal("Running", system.StateLabel.Text);
            Assert.Same(shown, system.Grid.Result);
            Assert.StartsWith("Previous result", system.HintLabel.Text, StringComparison.Ordinal);
            Assert.Contains("Overall:", system.HintLabel.Text, StringComparison.Ordinal);

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.StartsWith("前回の結果", system.HintLabel.Text, StringComparison.Ordinal);
            Strings.Current = Language.En;
            rig.Page.ApplyLanguage();

            second.Release();
            Assert.True(ui.Until(() => !rig.Checks.IsBusy));
            ui.Pump();
            Assert.DoesNotContain("Previous result", system.HintLabel.Text, StringComparison.Ordinal);
            Assert.NotSame(shown, system.Grid.Result);
        });
    }

    [Fact]
    public void Read_again_as_administrator_is_offered_for_a_denied_disk_and_runs_a_new_elevated_check_saved_as_a_new_run()
    {
        Ui.Run(ui =>
        {
            var history = new InMemoryCheckHistory([]);
            var recorder = new RecordingRunner(new FakeCheckRunner(TimeSpan.Zero, history.Add));
            using var rig = new PageRig(new CheckController(() => recorder, history));
            rig.Page.OnActivated();
            rig.Page.SelectTab("storage");
            var storage = rig.StorageTab;
            Assert.False(storage.ElevateOffered);
            Assert.Equal("Read again as administrator", storage.ElevateButton.Text);

            storage.PressStart();
            Assert.True(ui.Until(() => recorder.Requests.Count == 1 && !rig.Checks.IsBusy));
            ui.Pump();
            Assert.True(storage.ElevateOffered);
            Assert.Equal("Some disks could not be read with normal rights", storage.Notice.Text);
            var denied = Rows(storage.Grid).Where(row => storage.Grid.ItemAt(row)?.Reason == CheckReason.Denied).ToArray();
            Assert.NotEmpty(denied);
            Assert.All(denied, row => Assert.Equal("Not measured", storage.Grid.TextAt(row, 1)));
            Assert.All(denied, row => Assert.Equal(Fmt.Dash, storage.Grid.TextAt(row, 2)));
            var firstResult = storage.Grid.Result!;

            storage.PressElevate();
            Assert.True(ui.Until(() => recorder.Requests.Count == 2 && !rig.Checks.IsBusy));
            ui.Pump();

            Assert.Equal(new[] { false, true }, recorder.Requests.Select(static r => r.AllowElevation));
            Assert.All(recorder.Requests, static r => Assert.Equal(CheckKind.Storage, r.Kind));
            Assert.False(storage.ElevateOffered);
            var secondResult = storage.Grid.Result!;
            Assert.NotEqual(firstResult.RunId, secondResult.RunId);
            Assert.DoesNotContain(secondResult.Items, static i => i.Reason == CheckReason.Denied);

            // Two runs in the history; the first is as it was (the disk is still denied there), the second is the new one.
            var page = history.ListAsync(new CheckHistoryQuery(null, null), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(2, page.Entries.Count);
            Assert.Equal(new[] { firstResult.RunId, secondResult.RunId }.Order(), page.Entries.Select(static e => e.RunId).Order());
            var kept = history.GetAsync(firstResult.RunId, CancellationToken.None).GetAwaiter().GetResult().Result!;
            Assert.Contains(kept.Items, static i => i.Reason == CheckReason.Denied);
        });
    }

    [Fact]
    public void The_administrator_offer_is_made_only_to_an_idle_tab_and_not_for_a_run_that_was_already_elevated()
    {
        Ui.Run(ui =>
        {
            var normal = new ScriptedRunner { Hold = false };
            var elevated = new ScriptedRunner();
            using var rig = new PageRig(new RunnerQueue(normal, elevated).Controller());
            rig.Page.OnActivated();
            rig.Page.SelectTab("storage");
            var storage = rig.StorageTab;

            storage.PressStart();
            Assert.True(ui.Until(() => normal.Started.IsCompleted && !rig.Checks.IsBusy));
            ui.Pump();
            Assert.True(storage.ElevateOffered);
            Assert.True(storage.ElevateButton.Enabled);

            storage.PressElevate();
            Assert.True(ui.Until(() => elevated.Started.IsCompleted));
            ui.Pump();
            Assert.True(elevated.Requests.Single().AllowElevation);
            Assert.False(storage.ElevateOffered);
            Assert.False(storage.StartButton.Enabled);
            Assert.Equal("Running", storage.StateLabel.Text);

            // A second press while it runs starts nothing.
            storage.PressElevate();
            Assert.Single(elevated.Requests);

            elevated.Release();
            Assert.True(ui.Until(() => !rig.Checks.IsBusy));
            ui.Pump();
            Assert.False(storage.ElevateOffered);
            Assert.Equal("Completed", storage.StateLabel.Text);
            Assert.True(storage.StartButton.Enabled);
        });
    }

    // ---- the history ----

    [Fact]
    public void The_history_says_it_is_loading_until_the_list_arrives_and_the_page_is_not_ready_meanwhile()
    {
        Ui.Run(ui =>
        {
            var gate = new TaskCompletionSource<CheckHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var history = new StubHistory { OnList = (_, _) => gate.Task };
            using var rig = new PageRig(new RunnerQueue().Controller(history));
            var tab = rig.HistoryTab;
            Assert.Equal(HistoryView.NotLoaded, tab.View);

            rig.Page.OnActivated();
            rig.Page.SelectTab("history");
            Assert.Equal(HistoryView.Loading, tab.View);
            Assert.False(tab.IsReady);
            Assert.False(rig.Page.IsReady);
            Assert.Equal("Loading the history…", tab.CountLabel.Text);

            Assert.True(ui.Until(() => history.Lists == 1));
            ui.Pump();
            Assert.Equal(HistoryView.Loading, tab.View);

            gate.SetResult(new CheckHistoryPage(EntriesOf(FakeCheckData.Seed[0], FakeCheckData.Seed[1]), []));
            Assert.True(ui.Until(() => tab.View == HistoryView.List));
            Assert.True(tab.IsReady);
            Assert.True(rig.Page.IsReady);
            Assert.Equal("2 entries", tab.CountLabel.Text);
        });
    }

    [Fact]
    public void An_empty_history_says_so_in_both_languages()
    {
        Ui.Run(ui =>
        {
            using var rig = new PageRig(new RunnerQueue().Controller(new InMemoryCheckHistory([])));
            var tab = rig.ShowHistory(ui, HistoryView.Empty);

            Assert.Empty(tab.Entries);
            Assert.Equal(0, tab.List.VirtualListSize);
            Assert.Equal("There is no check history yet", tab.CountLabel.Text);
            Assert.Equal(string.Empty, tab.Notice.Text);
            Assert.Equal(string.Empty, tab.DetailHead.Text);

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.Equal("点検の履歴はまだありません", tab.CountLabel.Text);
        });
    }

    [Fact]
    public void The_history_lists_the_saved_runs_newest_first_with_date_kind_verdict_and_outcome()
    {
        Ui.Run(ui =>
        {
            using var rig = new PageRig(new RunnerQueue().Controller(new InMemoryCheckHistory()));
            var tab = rig.ShowHistory(ui, HistoryView.List);

            Assert.Equal(4, tab.Entries.Count);
            Assert.Equal(4, tab.List.VirtualListSize);
            Assert.Equal("4 entries", tab.CountLabel.Text);
            Assert.Equal(string.Empty, tab.Notice.Text);
            Assert.Equal(new[] { "Storage", "System", "Memory", "CPU" }, Enumerable.Range(0, 4).Select(row => tab.ListText(row, 1)));
            Assert.Equal(Local(FakeCheckData.Start.AddMilliseconds(820)), tab.ListText(0, 0));
            Assert.Equal(new[] { "Needs review", "Caution" }, new[] { tab.ListText(2, 2), tab.ListText(3, 2) });
            Assert.Equal(new[] { "Stopped", "Completed" }, new[] { tab.ListText(2, 3), tab.ListText(3, 3) });
            Assert.Equal("Select an entry to see the values that were saved", tab.DetailHead.Text);

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.Equal("4 件", tab.CountLabel.Text);
            Assert.Equal(new[] { "記憶媒体", "システム", "メモリ", "CPU" }, Enumerable.Range(0, 4).Select(row => tab.ListText(row, 1)));
            Assert.Equal("要確認", tab.ListText(2, 2));
            Assert.Equal("停止", tab.ListText(2, 3));
        });
    }

    [Fact]
    public void Files_that_could_not_be_read_are_named_above_the_list_and_do_not_hide_the_ones_that_could()
    {
        Ui.Run(ui =>
        {
            var history = new StubHistory
            {
                OnList = (_, _) => Task.FromResult(new CheckHistoryPage(
                    EntriesOf(FakeCheckData.Seed[0], FakeCheckData.Seed[1]),
                    [new CheckHistoryWarning(CheckHistoryWarningCodes.Unreadable, 2), new CheckHistoryWarning("history_from_a_later_version", 1)])),
            };
            using var rig = new PageRig(new RunnerQueue().Controller(history));
            var tab = rig.ShowHistory(ui, HistoryView.List);

            Assert.Equal(2, tab.Entries.Count);
            Assert.Equal("2 entries", tab.CountLabel.Text);
            Assert.StartsWith("2 history file(s) could not be read", tab.Notice.Text, StringComparison.Ordinal);
            Assert.Contains("history_from_a_later_version", tab.Notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain('[', tab.Notice.Text);

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            Assert.StartsWith("読めない履歴が 2 件あります", tab.Notice.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_warning_on_an_otherwise_empty_listing_still_shows_the_empty_state_with_the_notice()
    {
        Ui.Run(ui =>
        {
            var history = new StubHistory
            {
                OnList = (_, _) => Task.FromResult(new CheckHistoryPage([], [new CheckHistoryWarning(CheckHistoryWarningCodes.Locked, 3)])),
            };
            using var rig = new PageRig(new RunnerQueue().Controller(history));
            var tab = rig.ShowHistory(ui, HistoryView.Empty);

            Assert.Equal("There is no check history yet", tab.CountLabel.Text);
            Assert.Equal("3 file(s) are in use by another process and could not be read", tab.Notice.Text);
        });
    }

    [Fact]
    public void A_history_folder_that_cannot_be_opened_is_a_notice_with_the_kind_of_error_and_never_a_path_and_reading_again_recovers()
    {
        Ui.Run(ui =>
        {
            var history = new StubHistory
            {
                OnList = (call, _) => call switch
                {
                    1 => Task.FromException<CheckHistoryPage>(new CheckHistoryFolderException(new UnauthorizedAccessException(@"C:\Users\someone\secret-folder"))),
                    2 => Task.FromException<CheckHistoryPage>(new InvalidOperationException(@"C:\Users\someone\secret-folder")),
                    _ => Task.FromResult(new CheckHistoryPage(EntriesOf(FakeCheckData.Seed[0]), [])),
                },
            };
            using var rig = new PageRig(new RunnerQueue().Controller(history));
            var tab = rig.ShowHistory(ui, HistoryView.Failed);

            Assert.Equal("The history folder could not be opened: UnauthorizedAccessException 0x80070005", tab.Notice.Text);
            Assert.Equal(string.Empty, tab.CountLabel.Text);
            Assert.Empty(tab.Entries);

            // F5 reads again: this time with a failure of another kind (still no path on the screen), then a good list.
            rig.Page.RefreshNow();
            Assert.True(ui.Until(() => tab.View == HistoryView.Failed && history.Lists == 2));
            Assert.StartsWith("The history folder could not be opened: InvalidOperationException 0x", tab.Notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", tab.Notice.Text, StringComparison.Ordinal);

            rig.Page.RefreshNow();
            Assert.True(ui.Until(() => tab.View == HistoryView.List));
            Assert.Equal(string.Empty, tab.Notice.Text);
            Assert.Equal("1 entries", tab.CountLabel.Text);
        });
    }

    [Fact]
    public void Selecting_a_run_shows_the_values_that_were_saved_and_another_run_replaces_them()
    {
        Ui.Run(ui =>
        {
            using var rig = new PageRig(new RunnerQueue().Controller(new InMemoryCheckHistory()), handle: true);
            var tab = rig.ShowHistory(ui, HistoryView.List);
            Assert.Null(tab.SelectedRunId);
            Assert.Equal(0, tab.Detail.RowCount);

            tab.SelectEntry(0);
            Assert.True(ui.Until(() => tab.SelectedRunId == FakeCheckData.StorageRun), "The selection did not reach the tab.");
            var opening = tab.DetailTask;
            Assert.True(ui.Until(() => opening.IsCompleted));
            Assert.Equal(FakeCheckData.StorageRun, tab.Detail.Result!.RunId);
            Assert.True(tab.Detail.RowCount > 0);
            Assert.StartsWith("Storage", tab.DetailHead.Text, StringComparison.Ordinal);
            var delta = RowOf(tab.Detail, "storage.nvme.power_on_hours.delta", "disk-1");
            Assert.Contains("1,204 → 1,227 (+23)", tab.Detail.TextAt(delta, 3), StringComparison.Ordinal);

            tab.SelectEntry(2);
            Assert.True(ui.Until(() => tab.SelectedRunId == FakeCheckData.MemoryRun));
            var next = tab.DetailTask;
            Assert.True(ui.Until(() => next.IsCompleted));
            Assert.Equal(FakeCheckData.MemoryRun, tab.Detail.Result!.RunId);
            Assert.Equal(CheckOutcome.Stopped, tab.Detail.Result.Outcome);
            Assert.StartsWith("Memory", tab.DetailHead.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_run_that_is_gone_or_cannot_be_opened_says_so_where_the_values_would_be()
    {
        Ui.Run(ui =>
        {
            var answers = new Dictionary<Guid, Func<Task<CheckHistoryGetResult>>>
            {
                [FakeCheckData.StorageRun] = static () => Task.FromResult(CheckHistoryGetResult.NotFound),
                [FakeCheckData.SystemRun] = static () => Task.FromResult(CheckHistoryGetResult.Unreadable("history_future_format")),
                [FakeCheckData.MemoryRun] = static () => Task.FromException<CheckHistoryGetResult>(new IOException(@"C:\Users\someone\secret-folder")),
            };
            var history = new StubHistory
            {
                OnList = (_, _) => Task.FromResult(new CheckHistoryPage(EntriesOf(FakeCheckData.Seed[0], FakeCheckData.Seed[1], FakeCheckData.Seed[2]), [])),
                OnGet = (id, _) => answers[id](),
            };
            using var rig = new PageRig(new RunnerQueue().Controller(history), handle: true);
            var tab = rig.ShowHistory(ui, HistoryView.List);

            tab.SelectEntry(0);
            Assert.True(ui.Until(() => tab.SelectedRunId == FakeCheckData.StorageRun));
            var one = tab.DetailTask;
            Assert.True(ui.Until(() => one.IsCompleted));
            Assert.Equal("This entry cannot be found (it was removed or moved)", tab.DetailHead.Text);
            Assert.Equal(0, tab.Detail.RowCount);

            tab.SelectEntry(1);
            Assert.True(ui.Until(() => tab.SelectedRunId == FakeCheckData.SystemRun));
            var two = tab.DetailTask;
            Assert.True(ui.Until(() => two.IsCompleted));
            Assert.StartsWith("This entry cannot be opened: ", tab.DetailHead.Text, StringComparison.Ordinal);
            Assert.Contains(Strings.Get("gui.chk.warnone.history_future_format"), tab.DetailHead.Text, StringComparison.Ordinal);
            Assert.Equal(0, tab.Detail.RowCount);

            tab.SelectEntry(2);
            Assert.True(ui.Until(() => tab.SelectedRunId == FakeCheckData.MemoryRun));
            var three = tab.DetailTask;
            Assert.True(ui.Until(() => three.IsCompleted));
            Assert.StartsWith("The history folder could not be opened: IOException 0x", tab.DetailHead.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", tab.DetailHead.Text, StringComparison.Ordinal);
            Assert.Equal(0, tab.Detail.RowCount);
        });
    }

    [Fact]
    public void A_slow_open_that_a_later_selection_overtook_never_paints_over_the_newer_one()
    {
        Ui.Run(ui =>
        {
            var memory = new InMemoryCheckHistory();
            var slow = new TaskCompletionSource<CheckHistoryGetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var history = new StubHistory
            {
                OnList = (_, ct) => memory.ListAsync(new CheckHistoryQuery(null, null), ct),
                OnGet = (id, ct) => id == FakeCheckData.StorageRun ? slow.Task : memory.GetAsync(id, ct),
            };
            using var rig = new PageRig(new RunnerQueue().Controller(history), handle: true);
            var tab = rig.ShowHistory(ui, HistoryView.List);

            tab.SelectEntry(0);
            Assert.True(ui.Until(() => tab.SelectedRunId == FakeCheckData.StorageRun));
            var first = tab.DetailTask;
            Assert.Equal("Opening the saved values…", tab.DetailHead.Text);

            tab.SelectEntry(1);
            Assert.True(ui.Until(() => tab.SelectedRunId == FakeCheckData.SystemRun));
            var second = tab.DetailTask;
            Assert.True(ui.Until(() => second.IsCompleted));
            Assert.Equal(FakeCheckData.SystemRun, tab.Detail.Result!.RunId);

            slow.SetResult(CheckHistoryGetResult.Found(FakeCheckData.Seed[0]));
            Assert.True(ui.Until(() => first.IsCompleted));
            Assert.Equal(FakeCheckData.SystemRun, tab.Detail.Result!.RunId);
            Assert.Equal(FakeCheckData.SystemRun, tab.SelectedRunId);
        });
    }

    [Fact]
    public void A_run_that_ends_shows_up_in_the_history_when_it_is_on_screen_and_the_next_time_it_is_shown_when_it_is_not()
    {
        Ui.Run(ui =>
        {
            var history = new InMemoryCheckHistory();
            using var rig = new PageRig(new CheckController(() => new FakeCheckRunner(TimeSpan.Zero, history.Add), history));
            rig.Page.OnActivated();

            // Not on screen: the run is saved, nothing is read, and the list is read when the tab is shown.
            rig.SystemTab.PressStart();
            Assert.True(ui.Until(() => rig.Checks.StateOf(CheckKind.System).State == CheckRunState.Completed));
            ui.Pump();
            Assert.Equal(HistoryView.NotLoaded, rig.HistoryTab.View);
            rig.Page.SelectTab("history");
            Assert.True(ui.Until(() => rig.HistoryTab.View == HistoryView.List && rig.HistoryTab.Entries.Count == 5));
            Assert.Equal(CheckKind.System, rig.HistoryTab.Entries[0].Kind);

            // On screen: the list is read again by itself when the run ends.
            rig.SystemTab.PressStart();
            Assert.True(ui.Until(() => rig.HistoryTab.Entries.Count == 6 && rig.HistoryTab.View == HistoryView.List && !rig.Checks.IsBusy));
        });
    }

    // ---- nothing is created by looking ----

    [Fact]
    public void Opening_the_page_and_every_tab_creates_nothing_under_the_data_folder_and_starts_nothing()
    {
        Ui.Run(ui =>
        {
            using var temp = new Ui.TempRoot();
            var root = Path.Combine(temp.Path, "PCMieru");

            // Not covered here: the default legacy folder (CheckHistoryStore.DefaultLegacyDirectory, in the roaming profile) is never read before a start, and this test only watches the data root it was given.
            using var rig = new PageRig(services => CheckComposition.Create(services, dataRoot: root, fake: false));
            Assert.Empty(temp.Entries());

            var tab = rig.ShowHistory(ui, HistoryView.Empty);
            Assert.Equal("There is no check history yet", tab.CountLabel.Text);
            foreach (var id in rig.Page.TabIds)
            {
                rig.Page.SelectTab(id);
                Assert.True(ui.Until(() => rig.Page.IsReady));
            }

            Strings.Current = Language.Ja;
            rig.Page.ApplyLanguage();
            rig.Page.SelectTab("history");
            rig.Page.RefreshNow();
            Assert.True(ui.Until(() => tab.View == HistoryView.Empty && tab.IsReady));
            rig.Page.OnDeactivated();

            Assert.False(Directory.Exists(Path.Combine(root, "checks")));
            Assert.False(Directory.Exists(root));
            Assert.Empty(temp.Entries());
            Assert.False(rig.Checks.IsBusy);
            Assert.All(Enum.GetValues<CheckKind>(), kind => Assert.Equal(CheckRunState.NotRun, rig.Checks.StateOf(kind).State));
        });
    }

    [Fact]
    public void Building_the_window_s_check_services_reads_and_creates_nothing_until_a_check_starts()
    {
        using var temp = new Ui.TempRoot();
        var root = Path.Combine(temp.Path, "PCMieru");
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });

        using var controller = CheckComposition.Create(services, dataRoot: root, fake: false);
        Assert.False(controller.IsBusy);
        Assert.Empty(temp.Entries());

        // The fake set, which is what --fakes builds, never touches a disk at all.
        using var fake = CheckComposition.Create(services, dataRoot: root, fake: true);
        Assert.Empty(temp.Entries());
    }

    [Fact]
    public void Every_sample_result_of_the_fake_data_passes_the_strict_validation()
    {
        var start = FakeCheckData.Start;
        var results = new List<CheckResult>(FakeCheckData.Seed) { FakeCheckData.Storage(Guid.NewGuid(), start, elevated: true) };
        foreach (var kind in Enum.GetValues<CheckKind>())
        {
            var load = kind is CheckKind.Cpu or CheckKind.Memory;
            results.Add(FakeCheckData.Stopped(kind, Guid.NewGuid(), start, 2));
            results.Add(FakeCheckData.For(new CheckRequest(kind, load ? TimeSpan.FromSeconds(30) : null), Guid.NewGuid(), start, 30));
        }

        foreach (var result in results)
        {
            var problems = CheckResultValidator.Validate(result);
            Assert.True(problems.Count == 0, $"{result.Kind}/{result.Outcome}: {string.Join("; ", problems)}");
        }
    }
}
