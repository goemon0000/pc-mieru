using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PcMieru.Checks;
using Xunit.Abstractions;

namespace PcMieru.Gui.Tests;

/// <summary>
/// The recording tab of the checks page and the window's part in it (DESIGN-B §2, §6, §7, §8, §10, §13): where the tab sits, what it draws in each
/// state of the recording and of the list, that nothing of the recorder exists until the tab is shown or a recording starts, that the recording goes
/// on when the tab, the page or the window goes out of sight, and that closing the window or ending the Windows session stops the recording within
/// the bound. The recorder and the store are the fakes of <c>--fakes</c> (in memory, with a clock the test moves by hand); the real store is used only
/// on a folder that does not exist, to show that looking at it makes nothing. The tests are synchronous bodies inside <see cref="Ui.Run"/>.
/// </summary>
public class RecordTabTests(ITestOutputHelper output)
{
    private const int WmQueryEndSession = 0x11;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static void Wait(QueueContext ui, Task task)
    {
        Assert.True(ui.Until(() => task.IsCompleted), "The action never finished.");
        task.GetAwaiter().GetResult();
    }

    private static string Cells(RunSummaryGrid grid) =>
        string.Join("\n", Enumerable.Range(0, grid.RowCount).Select(row => string.Join(" | ", Enumerable.Range(0, 6).Select(col => grid.TextAt(row, col)))));

    private static IEnumerable<SummaryRow> RowsOf(RunSummaryGrid grid, SummaryRowKind kind) => grid.Rows.Where(row => row.Kind == kind);

    // ---- the page and tab in a window that is never shown ----

    private sealed class TabRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public TabRig(Language language = Language.En, bool seed = false, string? savedTab = null, bool real = false, bool shown = false)
        {
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Temp = new Ui.TempRoot();
            Folder = Path.Combine(Temp.Path, "PCMieru", "runs");
            Now = DateTime.UtcNow;
            Kit = new FakeRecordingKit(Folder, () => Now);
            if (seed)
            {
                FakeRunData.Seed(Kit.Store, Folder, Now);
            }

            Hub = real ? RecordingComposition.Create(Services, root: Folder, fake: false) : Kit.CreateHub();
            Settings = new GuiSettings();
            if (savedTab is not null)
            {
                Settings.SetValue("checks.tab", savedTab);
            }

            Checks = new RunnerQueue().Controller();
            Context = new GuiContext(Services, Pump, _theme, Settings, checks: Checks, recording: Hub);
            Page = new ChecksPage(Context);
            Host = new Form { ShowInTaskbar = false };
            Host.Controls.Add(Page);
            _ = Host.Handle;
            _ = Tab.List.Handle;
            if (shown)
            {
                // The native list lays its rows out only in a window that has been shown: off the screen, small enough for the list to scroll.
                Host.StartPosition = FormStartPosition.Manual;
                Host.Location = new Point(-24000, -24000);
                Host.Size = new Size(900, 600);
                Host.Show();
            }
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public Ui.TempRoot Temp { get; }

        public string Folder { get; }

        /// <summary>The clock of the recorder and the hub, moved by hand.</summary>
        public DateTime Now { get; set; }

        public FakeRecordingKit Kit { get; }

        public RecordingHub Hub { get; }

        public GuiSettings Settings { get; }

        public CheckController Checks { get; }

        public GuiContext Context { get; }

        public ChecksPage Page { get; }

        public Form Host { get; }

        public RecordTab Tab => (RecordTab)Page.TabOf("record");

        /// <summary>Brings the page and the recording tab to the front and waits (pumping) until the list is drawn.</summary>
        public RecordTab Show(QueueContext ui)
        {
            Page.OnActivated();
            Page.SelectTab("record");
            Assert.True(ui.Until(() => Tab.View is HistoryView.List or HistoryView.Empty or HistoryView.Failed), $"The list stayed {Tab.View}.");
            return Tab;
        }

        public void Advance(TimeSpan span)
        {
            Now += span;
            Hub.Tick();
        }

        public void Dispose()
        {
            Kit.Backend.ReleaseStop();
            Host.Dispose();
            Hub.Dispose();
            Checks.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
            Temp.Dispose();
            Dialogs.AutoAnswer = null;
            Dialogs.Log.Clear();
        }
    }

    // ---- the real main window, never shown ----

    private sealed class WindowRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public WindowRig(Language language = Language.En, params ScriptedRunner[] runners)
        {
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Temp = new Ui.TempRoot();
            Folder = Path.Combine(Temp.Path, "PCMieru", "runs");
            Now = DateTime.UtcNow;
            Kit = new FakeRecordingKit(Folder, () => Now);
            Hub = new RecordingHub(
                () =>
                {
                    Backends++;
                    return Kit.Backend;
                },
                () =>
                {
                    Stores++;
                    return Kit.Store;
                },
                () => Folder,
                () => Now)
            {
                FolderExists = _ => Kit.FolderPresent,
                FileExists = _ => true,
            };
            Queue = new RunnerQueue(runners);
            Checks = Queue.Controller();
            Context = new GuiContext(Services, Pump, _theme, new GuiSettings(), checks: Checks, recording: Hub);
            Form = new MainForm(Context);
            _ = Form.Handle;
            Form.FormClosing += (_, e) => Reasons.Add(e.CloseReason);
            Form.FormClosed += (_, _) => Closed++;
            Dialogs.AutoAnswer = null;
            Dialogs.Log.Clear();
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public Ui.TempRoot Temp { get; }

        public string Folder { get; }

        public DateTime Now { get; set; }

        public FakeRecordingKit Kit { get; }

        public RecordingHub Hub { get; }

        public RunnerQueue Queue { get; }

        public CheckController Checks { get; }

        public GuiContext Context { get; }

        public MainForm Form { get; }

        public int Backends { get; private set; }

        public int Stores { get; private set; }

        public int Closed { get; private set; }

        public List<CloseReason> Reasons { get; } = [];

        public void StartRecording(QueueContext ui) => Wait(ui, Hub.StartAsync());

        public void Dispose()
        {
            Kit.Backend.ReleaseStop();
            Dialogs.AutoAnswer = null;
            Dialogs.Log.Clear();
            Form.Dispose();
            Hub.Dispose();
            Checks.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
            Temp.Dispose();
        }
    }

    // ---- where the tab sits, and that nothing exists before it is shown ----

    [Fact]
    public void Building_the_page_reads_nothing_and_creates_no_recorder_store_or_folder()
    {
        Ui.Run(_ =>
        {
            using var rig = new TabRig(seed: true);

            Assert.Equal(new[] { "system", "load", "storage", "record", "history" }, rig.Page.TabIds);
            Assert.Equal(0, rig.Kit.Store.ListCalls);
            Assert.Equal(0, rig.Kit.Store.ReadCalls);
            Assert.False(rig.Hub.StoreCreated);
            Assert.False(rig.Hub.BackendCreated);
            Assert.Equal(0, rig.Kit.Backend.StartCalls);
            Assert.Empty(rig.Temp.Entries());
            Assert.False(Directory.Exists(rig.Folder));
            Assert.Equal(RecordDetailState.None, rig.Tab.DetailState);
            Assert.Equal(HistoryView.NotLoaded, rig.Tab.View);
        });
    }

    [Fact]
    public void A_saved_recording_tab_reads_nothing_until_the_page_is_brought_to_the_front()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true, savedTab: "record");

            Assert.Equal(0, rig.Kit.Store.ListCalls);
            Assert.False(rig.Hub.StoreCreated);

            rig.Page.OnActivated();
            Assert.True(ui.Until(() => rig.Tab.View == HistoryView.List));

            Assert.True(rig.Hub.StoreCreated);
            Assert.Equal(1, rig.Kit.Store.ListCalls);
            Assert.False(rig.Hub.BackendCreated);
        });
    }

    [Fact]
    public void The_first_show_reads_the_list_off_the_UI_thread_and_an_empty_place_says_so_without_making_the_folder()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            rig.Kit.Store.ListDelay = TimeSpan.FromMilliseconds(300);

            rig.Page.OnActivated();
            rig.Page.SelectTab("record");

            Assert.Equal(HistoryView.Loading, rig.Tab.View);
            Assert.True(ui.Until(() => rig.Tab.View == HistoryView.Empty));
            Assert.DoesNotContain(ui.OwnerThread, rig.Kit.Store.ListThreads);
            Assert.NotEmpty(rig.Kit.Store.ListThreads);
            Assert.Contains("No recordings yet", rig.Tab.CountLabel.Text, StringComparison.Ordinal);
            Assert.Contains("made when the first recording starts", rig.Tab.FolderLabel.Text, StringComparison.Ordinal);
            Assert.Empty(rig.Temp.Entries());
            Assert.False(Directory.Exists(rig.Folder));
        });
    }

    [Fact]
    public void The_real_store_on_a_folder_that_is_not_there_is_an_empty_list_and_makes_nothing()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(real: true);

            Assert.False(rig.Hub.StoreCreated);
            Assert.Empty(rig.Temp.Entries());

            rig.Show(ui);

            Assert.Equal(HistoryView.Empty, rig.Tab.View);
            Assert.True(rig.Hub.StoreCreated);
            Assert.False(rig.Hub.BackendCreated);
            Assert.Empty(rig.Temp.Entries());
            Assert.False(Directory.Exists(rig.Folder));
            Assert.True(rig.Tab.StartButton.Enabled);
        });
    }

    // ---- start, marks, stop ----

    [Fact]
    public void Start_enables_the_marks_and_stop_and_a_stop_gives_a_completed_run_in_the_list()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            var tab = rig.Show(ui);

            Assert.Contains("Not recording", tab.StateLabel.Text, StringComparison.Ordinal);
            Assert.True(tab.StartButton.Enabled);
            Assert.False(tab.StopButton.Enabled);
            Assert.False(tab.MarkButton.Enabled);
            Assert.All(tab.MarkItems, item => Assert.False(item.Enabled));

            Wait(ui, tab.PressStart());

            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);
            Assert.Equal(1, rig.Kit.Backend.StartCalls);
            Assert.False(tab.StartButton.Enabled);
            Assert.True(tab.StopButton.Enabled);
            Assert.True(tab.MarkButton.Enabled);
            Assert.All(tab.MarkItems, item => Assert.True(item.Enabled));

            rig.Advance(TimeSpan.FromSeconds(65));
            Assert.Equal("Recording 1:05   Marks: 0", tab.StateLabel.Text);

            Wait(ui, tab.PressMark(MarkerKind.BenchStart));
            tab.MarkItems[2].PerformClick();
            Assert.True(ui.Until(() => rig.Kit.Backend.MarkKinds.Count == 2));
            ui.Pump();
            Assert.Equal(new[] { MarkerKind.BenchStart, MarkerKind.StressStart }, rig.Kit.Backend.MarkKinds);
            Assert.Equal("Recording 1:05   Marks: 2", tab.StateLabel.Text);

            Wait(ui, tab.PressStop());

            Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);
            Assert.Equal(RunStopReason.User, rig.Kit.Backend.LastStopReason);
            Assert.Contains("Saved", tab.StateLabel.Text, StringComparison.Ordinal);
            Assert.True(tab.StartButton.Enabled);
            Assert.False(tab.StopButton.Enabled);
            Assert.False(tab.MarkButton.Enabled);
            Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1));
            Assert.Equal("Completed", tab.ListText(0, 2));
            Assert.Equal(rig.Hub.LastResult?.RunId, tab.Runs[0].RunId);
        });
    }

    [Fact]
    public void The_mark_menu_has_five_choices_in_the_words_of_each_language()
    {
        Ui.Run(_ =>
        {
            using (var rig = new TabRig(Language.En))
            {
                Assert.Equal(
                    new[] { "Bench start", "Bench end", "Load start", "Load end", "General mark" },
                    rig.Tab.MarkItems.Select(item => item.Text));
                Assert.Equal(
                    new[] { MarkerKind.BenchStart, MarkerKind.BenchEnd, MarkerKind.StressStart, MarkerKind.StressEnd, MarkerKind.Mark },
                    rig.Tab.MarkItems.Select(item => (MarkerKind)item.Tag!));
            }

            using (var rig = new TabRig(Language.Ja))
            {
                Assert.Equal(
                    new[] { "ベンチ開始", "ベンチ終了", "負荷開始", "負荷終了", "汎用の印" },
                    rig.Tab.MarkItems.Select(item => item.Text));
                Assert.Equal("開始", rig.Tab.StartButton.Text);
                Assert.Equal("停止", rig.Tab.StopButton.Text);
            }
        });
    }

    [Fact]
    public void A_refused_start_says_why_in_plain_words_and_nothing_is_recording()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            var tab = rig.Show(ui);
            rig.Kit.Backend.FailStart = RunStartFailure.LowSpace;

            Wait(ui, tab.PressStart());

            Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);
            Assert.Contains("less than 200 MB", tab.Notice.Text, StringComparison.Ordinal);
            Assert.Equal(0, rig.Kit.Backend.StopCalls);
            Assert.True(tab.StartButton.Enabled);
            Assert.False(tab.StopButton.Enabled);
        });
    }

    [Fact]
    public void A_run_that_the_recorder_ends_by_itself_is_told_in_words_and_the_list_follows()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            var tab = rig.Show(ui);
            Wait(ui, tab.PressStart());

            rig.Advance(TimeSpan.FromSeconds(20));
            rig.Kit.Backend.EndByItself(RunStopReason.LowSpace);

            Assert.True(ui.Until(() => rig.Hub.Phase == RecordingPhase.Idle && tab.Runs.Count == 1 && tab.View == HistoryView.List));
            Assert.StartsWith("Stopped and saved: ", tab.StateLabel.Text, StringComparison.Ordinal);
            Assert.Equal(RunStopReason.LowSpace, tab.Runs[0].StopReason);
            Assert.True(tab.StartButton.Enabled);
        });
    }

    // ---- the list ----

    [Fact]
    public void The_list_says_start_length_status_and_a_note_in_each_language_and_only_a_completed_run_has_an_exact_length()
    {
        Ui.Run(ui =>
        {
            using (var rig = new TabRig(Language.En, seed: true))
            {
                var tab = rig.Show(ui);

                Assert.Equal(3, tab.Runs.Count);
                Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"), tab.ListText(0, 0));
                Assert.Equal("10 min 0 s", tab.ListText(0, 1));
                Assert.Equal("Completed", tab.ListText(0, 2));
                Assert.Equal(string.Empty, tab.ListText(0, 3));

                Assert.Equal("about 1 min", tab.ListText(1, 1));
                Assert.Equal("Failed", tab.ListText(1, 2));
                Assert.NotEqual(string.Empty, tab.ListText(1, 3));

                Assert.Equal("about 12 min", tab.ListText(2, 1));
                Assert.Equal("Interrupted", tab.ListText(2, 2));
                Assert.Contains("no end was saved", tab.ListText(2, 3), StringComparison.Ordinal);

                Assert.Contains("estimate", tab.HintLabel.Text, StringComparison.Ordinal);
                var started = rig.Now.AddHours(-3).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                Assert.Equal(started, tab.ListText(0, 0));
            }

            using (var rig = new TabRig(Language.Ja, seed: true))
            {
                var tab = rig.Show(ui);

                Assert.Matches(new Regex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}$"), tab.ListText(0, 0));
                Assert.Equal("10 分 0 秒", tab.ListText(0, 1));
                Assert.Equal("完了", tab.ListText(0, 2));
                Assert.Equal("約 1 分", tab.ListText(1, 1));
                Assert.Equal("失敗", tab.ListText(1, 2));
                Assert.Equal("約 12 分", tab.ListText(2, 1));
                Assert.Equal("中断", tab.ListText(2, 2));
            }
        });
    }

    [Fact]
    public void A_recording_or_cut_off_run_never_shows_the_length_of_its_header_as_exact()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true);
            var running = Guid.NewGuid();
            rig.Kit.Store.Add(FakeRunData.Info(running, rig.Folder, rig.Now.AddMinutes(-5), 735, RunStatus.Recording, null));
            rig.Kit.Store.SetRecording(running);
            var tab = rig.Show(ui);

            var row = tab.Runs.ToList().FindIndex(r => r.RunId == running);
            Assert.True(row >= 0);
            Assert.Equal("Recording", tab.ListText(row, 2));
            Assert.Equal("about 12 min", tab.ListText(row, 1));
            Assert.DoesNotContain("12 min 15 s", tab.ListText(row, 1), StringComparison.Ordinal);

            // The cut-off run says "about" in the list and the exact length only once it is opened and read to its end.
            var cut = tab.Runs.ToList().FindIndex(r => r.RunId == FakeRunData.InterruptedId);
            Assert.Equal("about 12 min", tab.ListText(cut, 1));
            tab.SelectRun(cut);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            Assert.Contains("12 min 22 s", Cells(tab.Summary), StringComparison.Ordinal);
            Assert.Equal("about 12 min", tab.ListText(cut, 1));
        });
    }

    [Fact]
    public void A_recording_just_started_is_under_one_minute_even_hours_later_on_the_clock()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            var tab = rig.Show(ui);
            Wait(ui, tab.PressStart());
            rig.Advance(TimeSpan.FromHours(3));

            tab.Reload();
            Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1));

            Assert.Equal("Recording", tab.ListText(0, 2));
            Assert.Equal("under 1 min", tab.ListText(0, 1));
        });
    }

    // ---- the summary ----

    [Fact]
    public void The_summary_says_missing_as_missing_in_each_language_and_never_as_zero_a_cause_or_a_total()
    {
        Ui.Run(ui =>
        {
            using (var rig = new TabRig(Language.En, seed: true))
            {
                var tab = rig.Show(ui);
                tab.SelectRun(0);
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
                var grid = tab.Summary;
                var all = Cells(grid);
                output.WriteLine(all);

                var notMeasured = RowsOf(grid, SummaryRowKind.NotMeasured).ToList();
                Assert.Equal(2, notMeasured.Count);
                Assert.All(notMeasured, row =>
                {
                    Assert.Equal("not measured", row.Min);
                    Assert.Equal("not measured", row.Avg);
                    Assert.Equal("not measured", row.Max);
                });
                Assert.Contains(notMeasured, row => row.Item.Contains("CPU temperature", StringComparison.Ordinal));

                Assert.Contains("measured 598 s, not measured 2 s", all, StringComparison.Ordinal);
                Assert.Contains("measured 540 s, not measured 60 s", all, StringComparison.Ordinal);
                Assert.Contains("96.2 %", all, StringComparison.Ordinal);
                Assert.Contains("Out of 540 s measured", all, StringComparison.Ordinal);
                Assert.Contains("312 s (300 s under load)", all, StringComparison.Ordinal);
                Assert.Contains("41 s (41 s under load)", all, StringComparison.Ordinal);
                Assert.Contains("Happened together", all, StringComparison.Ordinal);
                Assert.Contains("71", all, StringComparison.Ordinal);
                Assert.Contains("Bench 1", all, StringComparison.Ordinal);
                Assert.Contains("60-180 s (120 s)", all, StringComparison.Ordinal);
                Assert.Contains("Load 1", all, StringComparison.Ordinal);
                Assert.Contains("No end mark", all, StringComparison.Ordinal);
                Assert.Contains("NVIDIA GeForce RTX 5070", all, StringComparison.Ordinal);
                Assert.Contains(RowsOf(grid, SummaryRowKind.Warning), row => row.Tone == CellTone.Warning);

                // Never a cause, never one total of the missing seconds of different readings.
                Assert.DoesNotContain("cause", all, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("in total", all, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("because", all, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotMatch(new Regex(@"not measured 0 s \(of"), all);

                // The cells of a reading that has no value are words, not 0.
                foreach (var row in RowsOf(grid, SummaryRowKind.NotMeasured))
                {
                    Assert.DoesNotMatch(new Regex(@"^\s*0(\.0+)?\s*\W*$"), row.Min);
                    Assert.DoesNotMatch(new Regex(@"^\s*0(\.0+)?\s*\W*$"), row.Avg);
                    Assert.DoesNotMatch(new Regex(@"^\s*0(\.0+)?\s*\W*$"), row.Max);
                }

                Assert.Equal(string.Empty, string.Join(string.Empty, grid.UnreadableCuts()));
            }

            using (var rig = new TabRig(Language.Ja, seed: true))
            {
                var tab = rig.Show(ui);
                tab.SelectRun(0);
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
                var grid = tab.Summary;
                var all = Cells(grid);
                output.WriteLine(all);

                var notMeasured = RowsOf(grid, SummaryRowKind.NotMeasured).ToList();
                Assert.Equal(2, notMeasured.Count);
                Assert.All(notMeasured, row =>
                {
                    Assert.Equal("測れず", row.Min);
                    Assert.Equal("測れず", row.Avg);
                    Assert.Equal("測れず", row.Max);
                });
                Assert.Contains("同時に起きた", all, StringComparison.Ordinal);
                Assert.DoesNotContain("原因", all, StringComparison.Ordinal);
                Assert.DoesNotContain("合計", all, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void A_run_that_is_being_recorded_has_no_summary_no_csv_and_cannot_be_deleted()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            var tab = rig.Show(ui);
            Wait(ui, tab.PressStart());
            tab.Reload();
            Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1));

            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Running));

            Assert.Contains("Being recorded", tab.DetailHead.Text + tab.Notice.Text + tab.HintLabel.Text + Cells(tab.Summary), StringComparison.Ordinal);
            Assert.False(tab.CsvButton.Enabled);
            Assert.False(tab.DeleteButton.Enabled);

            Dialogs.AutoAnswer = true;
            Wait(ui, tab.PressDelete());
            Assert.Equal(0, rig.Kit.Store.DeleteCalls);
            Assert.Empty(Dialogs.Log);
            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);
        });
    }

    [Fact]
    public void A_failed_or_cut_off_run_says_how_it_ended_and_an_unreadable_run_or_list_says_so()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true);
            var tab = rig.Show(ui);

            tab.SelectRun(1);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            var failed = Cells(tab.Summary);
            Assert.Contains("Failed", failed, StringComparison.Ordinal);

            tab.SelectRun(2);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && Cells(tab.Summary) != failed));
            var cut = Cells(tab.Summary);
            Assert.Contains("Interrupted", cut, StringComparison.Ordinal);

            rig.Kit.Store.FailRead(FakeRunData.CompletedId, new IOException("disk gone"));
            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Unreadable));
            Assert.Contains("could not be read", tab.DetailHead.Text + tab.Notice.Text + tab.HintLabel.Text, StringComparison.Ordinal);
            Assert.False(tab.CsvButton.Enabled && tab.Summary.RowCount > 0);

            rig.Kit.Store.ListFailure = new IOException("folder gone");
            tab.Reload();
            Assert.True(ui.Until(() => tab.View == HistoryView.Failed));
        });
    }

    // ---- delete ----

    [Fact]
    public void Delete_asks_first_shows_the_stores_answers_in_words_and_removes_only_the_selected_run()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true);
            var tab = rig.Show(ui);

            // The store says it is the run being recorded (another window of the app): the words, and the run stays.
            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            Assert.True(tab.DeleteButton.Enabled);
            rig.Kit.Store.SetRecording(FakeRunData.CompletedId);
            Dialogs.AutoAnswer = true;
            Wait(ui, tab.PressDelete());
            Assert.Contains("being recorded", tab.Notice.Text, StringComparison.Ordinal);
            Assert.Equal(3, tab.Runs.Count);
            rig.Kit.Store.SetRecording(null);

            // A file that cannot be removed.
            rig.Kit.Store.ForcedDelete = RunDeleteOutcome.Failed;
            Wait(ui, tab.PressDelete());
            Assert.Contains("A file could not be deleted", tab.Notice.Text, StringComparison.Ordinal);
            Assert.Equal(3, tab.Runs.Count);
            rig.Kit.Store.ForcedDelete = null;

            // "No" removes nothing.
            var before = rig.Kit.Store.DeleteCalls;
            Dialogs.AutoAnswer = false;
            Wait(ui, tab.PressDelete());
            Assert.Equal(before, rig.Kit.Store.DeleteCalls);
            Assert.Equal(3, tab.Runs.Count);

            // "Yes" on the failed run removes that run and nothing else.
            tab.SelectRun(1);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == FakeRunData.FailedId));
            Dialogs.AutoAnswer = true;
            Wait(ui, tab.PressDelete());
            Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 2));
            Assert.DoesNotContain(tab.Runs, r => r.RunId == FakeRunData.FailedId);
            Assert.Contains(tab.Runs, r => r.RunId == FakeRunData.CompletedId);
            Assert.Contains(tab.Runs, r => r.RunId == FakeRunData.InterruptedId);
            Assert.Equal(RecordDetailState.None, tab.DetailState);
            Assert.Contains("The recording was deleted", tab.Notice.Text, StringComparison.Ordinal);
        });
    }

    // ---- old records ----

    [Fact]
    public void Old_records_are_shown_on_request_read_only_labelled_as_estimated_and_cannot_be_deleted()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true);
            var tab = rig.Show(ui);
            Assert.Equal(3, tab.Runs.Count);
            Assert.DoesNotContain(tab.Runs, r => r.IsLegacy);

            tab.ToggleLegacy();
            Assert.Equal(4, tab.Runs.Count);
            var row = tab.Runs.ToList().FindIndex(r => r.IsLegacy);
            Assert.True(row >= 0);
            Assert.Contains("Old format, estimated", tab.ListText(row, 3), StringComparison.Ordinal);

            tab.SelectRun(row);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            Assert.Contains("Old format, estimated", Cells(tab.Summary) + tab.DetailHead.Text + tab.Notice.Text + tab.HintLabel.Text, StringComparison.Ordinal);
            Assert.Contains("Read only", tab.DetailHead.Text + tab.Notice.Text + tab.HintLabel.Text, StringComparison.Ordinal);
            Assert.False(tab.DeleteButton.Enabled);

            Dialogs.AutoAnswer = true;
            Wait(ui, tab.PressDelete());
            Assert.Equal(0, rig.Kit.Store.DeleteCalls);
            Assert.Empty(Dialogs.Log);

            tab.ToggleLegacy();
            Assert.Equal(3, tab.Runs.Count);
        });
    }

    [Fact]
    public void A_list_made_shorter_again_by_hiding_the_old_records_starts_under_its_header_with_no_gap()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true, shown: true);
            var tab = rig.Show(ui);
            for (var i = 0; i < 8; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }

            var fit = tab.List.RowsThatFit(tab.List.Height);
            tab.ToggleLegacy();
            Assert.Equal(4, tab.Runs.Count);
            Assert.True(fit >= 3 && fit < 4, "the window shows " + fit + " rows, the test needs room for 3 of 4");
            tab.SelectRun(tab.Runs.ToList().FindIndex(r => r.IsLegacy));
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            for (var i = 0; i < 8; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }

            Assert.True(tab.List.TopRow > 0, "the list did not scroll to the old record: " + tab.List.TopRow);
            tab.ToggleLegacy();
            for (var i = 0; i < 8; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }

            Assert.Equal(3, tab.Runs.Count);
            var problem = tab.List.WholeRowsProblem(out var facts);
            Assert.True(tab.List.TopRow == 0 && problem is null, (problem ?? "whole") + " " + facts + ", first row on show " + tab.List.TopRow);
        });
    }

    // ---- CSV and folder, through the seam ----

    [Fact]
    public void The_csv_and_the_folder_go_to_the_shell_through_the_seam_and_nothing_else_launches()
    {
        Ui.Run(ui =>
        {
            var started = new List<ProcessStartInfo>();
            var previous = ShellActions.Start;
            ShellActions.Start = started.Add;
            try
            {
                using var rig = new TabRig(seed: true);
                var tab = rig.Show(ui);
                tab.SelectRun(0);
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
                var info = rig.Kit.Store.Info(FakeRunData.CompletedId)!;

                Assert.True(tab.CsvButton.Enabled);
                tab.PressOpenCsv();
                var one = Assert.Single(started);
                Assert.Equal(info.CsvPath, one.FileName);
                Assert.True(one.UseShellExecute);

                // A file that is gone is said so and nothing is started.
                started.Clear();
                rig.Hub.FileExists = _ => false;
                tab.PressOpenCsv();
                Assert.Empty(started);
                Assert.Contains("was not found", tab.Notice.Text, StringComparison.Ordinal);
                rig.Hub.FileExists = _ => true;

                // A folder that is not there yet is said so and nothing is started.
                rig.Kit.FolderPresent = false;
                tab.PressOpenFolder();
                Assert.Empty(started);
                Assert.Contains("does not exist yet", tab.Notice.Text, StringComparison.Ordinal);

                rig.Kit.FolderPresent = true;
                tab.PressOpenFolder();
                var folder = Assert.Single(started);
                Assert.Equal("explorer.exe", folder.FileName);
                Assert.Contains(rig.Folder, folder.Arguments, StringComparison.Ordinal);
            }
            finally
            {
                ShellActions.Start = previous;
            }
        });
    }

    // ---- the recording goes on ----

    [Fact]
    public void Switching_tabs_pages_or_hiding_the_tab_does_not_stop_the_recording()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            var tab = rig.Show(ui);
            Wait(ui, tab.PressStart());

            rig.Page.SelectTab("system");
            rig.Page.SelectTab("history");
            rig.Page.OnDeactivated();
            tab.OnHidden();
            ui.Pump();

            Assert.Equal(0, rig.Kit.Backend.StopCalls);
            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);

            rig.Page.OnActivated();
            rig.Page.SelectTab("record");
            Assert.True(tab.StopButton.Enabled);
            Assert.False(tab.StartButton.Enabled);
            Assert.Contains("Recording", tab.StateLabel.Text, StringComparison.Ordinal);
            Assert.Equal(0, rig.Kit.Backend.StopCalls);
        });
    }

    [Fact]
    public void Minimising_the_window_does_not_stop_the_recording_and_the_status_bar_keeps_saying_so()
    {
        Ui.Run(ui =>
        {
            using var rig = new WindowRig();
            rig.StartRecording(ui);
            Assert.True(rig.Form.RecordingIndicatorOn);

            rig.Form.WindowState = FormWindowState.Minimized;
            ui.Pump();
            rig.Now += TimeSpan.FromSeconds(30);
            rig.Hub.Tick();

            Assert.Equal(0, rig.Kit.Backend.StopCalls);
            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);
            Assert.True(rig.Form.RecordingIndicatorOn);
            Assert.Equal("Recording 0:30", rig.Form.RecordingIndicator.Text);
        });
    }

    // ---- the status-bar indicator ----

    [Fact]
    public void The_status_bar_says_recording_with_the_time_on_every_page_and_a_click_goes_to_the_tab()
    {
        Ui.Run(ui =>
        {
            using var rig = new WindowRig();
            Assert.False(rig.Form.RecordingIndicatorOn);

            rig.StartRecording(ui);
            Assert.True(rig.Form.RecordingIndicatorOn);
            Assert.Equal("Recording 0:00", rig.Form.RecordingIndicator.Text);

            rig.Now += TimeSpan.FromSeconds(754);
            rig.Hub.Tick();
            Assert.Equal("Recording 12:34", rig.Form.RecordingIndicator.Text);

            // Another page in front: the indicator is still on, and a click brings the recording tab up.
            Assert.True(rig.Form.ShowPage("processes"));
            Assert.Equal("processes", rig.Form.CurrentPage?.Id);
            Assert.True(rig.Form.RecordingIndicatorOn);

            rig.Form.ClickRecordingIndicator();
            Assert.Equal("checks", rig.Form.CurrentPage?.Id);
            var page = Assert.IsType<ChecksPage>(rig.Form.CurrentPage);
            Assert.Equal("record", page.CurrentId);

            // While the stop is being saved it says so, and it goes out when the run is saved.
            rig.Kit.Backend.HangStop = true;
            var stopping = rig.Hub.StopAsync();
            Assert.Equal("Saving the recording…", rig.Form.RecordingIndicator.Text);
            Assert.True(rig.Form.RecordingIndicatorOn);
            rig.Kit.Backend.ReleaseStop();
            Wait(ui, stopping);
            Assert.False(rig.Form.RecordingIndicatorOn);
        });
    }

    [Fact]
    public void The_status_bar_indicator_is_in_the_words_of_each_language()
    {
        Ui.Run(ui =>
        {
            using var rig = new WindowRig(Language.Ja);
            rig.StartRecording(ui);
            rig.Now += TimeSpan.FromSeconds(754);
            rig.Hub.Tick();

            Assert.Equal("記録中 12:34", rig.Form.RecordingIndicator.Text);
        });
    }

    // ---- closing the window and ending the session ----

    [Fact]
    public void Closing_the_window_while_recording_stops_the_run_with_the_window_close_reason_and_asks_nothing()
    {
        Ui.Run(ui =>
        {
            using var rig = new WindowRig();
            rig.StartRecording(ui);
            rig.Now += TimeSpan.FromSeconds(10);

            rig.Form.Close();

            Assert.Equal(1, rig.Closed);
            Assert.Empty(Dialogs.Log);
            Assert.Equal(1, rig.Kit.Backend.StopCalls);
            Assert.Equal(RunStopReason.WindowClose, rig.Kit.Backend.LastStopReason);
            Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);
            var saved = Assert.Single(rig.Kit.Store.Runs);
            Assert.Equal(RunStatus.Completed, saved.Status);
            Assert.Equal(RunStopReason.WindowClose, saved.StopReason);
        });
    }

    [Fact]
    public void Closing_the_window_with_a_writer_that_hangs_closes_within_the_bound_and_asks_the_stop_once()
    {
        Ui.Run(ui =>
        {
            using var rig = new WindowRig();
            rig.StartRecording(ui);
            rig.Kit.Backend.HangStop = true;

            var watch = Stopwatch.StartNew();
            rig.Form.Close();
            watch.Stop();
            output.WriteLine($"Close with a hanging writer waited {watch.ElapsedMilliseconds} ms (the bound is {RunRecorderExtensions.ExitBound.TotalMilliseconds} ms).");

            Assert.Equal(1, rig.Closed);
            Assert.Equal(1, rig.Kit.Backend.StopCalls);
            Assert.Equal(RunStopReason.WindowClose, rig.Kit.Backend.LastStopReason);
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(1500), $"It gave up too early ({watch.ElapsedMilliseconds} ms): the stop was not waited for.");
            Assert.True(watch.Elapsed < RunRecorderExtensions.ExitBound + TimeSpan.FromSeconds(3), $"It waited {watch.ElapsedMilliseconds} ms: longer than the bound.");
            Assert.Empty(Dialogs.Log);
            rig.Kit.Backend.ReleaseStop();
        });
    }

    [Fact]
    public void Ending_the_Windows_session_stops_the_run_the_same_way_and_a_second_ask_does_not_wait_again()
    {
        Ui.Run(ui =>
        {
            // The ordinary case: the query is answered "yes" after the run is saved, and the window is left to Windows to end.
            using (var rig = new WindowRig())
            {
                rig.StartRecording(ui);
                var answer = SendMessage(rig.Form.Handle, WmQueryEndSession, IntPtr.Zero, IntPtr.Zero);

                Assert.NotEqual(IntPtr.Zero, answer);
                Assert.Equal(new[] { CloseReason.WindowsShutDown }, rig.Reasons);
                Assert.Equal(RunStopReason.WindowClose, rig.Kit.Backend.LastStopReason);
                Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);
                Assert.Empty(Dialogs.Log);
            }

            // A writer that hangs: the first ask waits the bound, the second (Windows asks again) does not wait at all.
            using (var rig = new WindowRig())
            {
                rig.StartRecording(ui);
                rig.Kit.Backend.HangStop = true;

                var first = Stopwatch.StartNew();
                var answer = SendMessage(rig.Form.Handle, WmQueryEndSession, IntPtr.Zero, IntPtr.Zero);
                first.Stop();
                output.WriteLine($"Session end with a hanging writer waited {first.ElapsedMilliseconds} ms.");
                Assert.NotEqual(IntPtr.Zero, answer);
                Assert.True(first.Elapsed >= TimeSpan.FromMilliseconds(1500));
                Assert.True(first.Elapsed < RunRecorderExtensions.ExitBound + TimeSpan.FromSeconds(3));

                var second = Stopwatch.StartNew();
                _ = SendMessage(rig.Form.Handle, WmQueryEndSession, IntPtr.Zero, IntPtr.Zero);
                second.Stop();
                output.WriteLine($"The second ask waited {second.ElapsedMilliseconds} ms.");
                Assert.True(second.Elapsed < TimeSpan.FromMilliseconds(1000));
                Assert.Equal(1, rig.Kit.Backend.StopCalls);
                Assert.All(rig.Reasons, reason => Assert.Equal(CloseReason.WindowsShutDown, reason));
                rig.Kit.Backend.ReleaseStop();
            }
        });
    }

    [Fact]
    public void A_check_and_a_recording_at_once_ask_about_the_check_first_and_stop_the_recording_only_when_the_window_really_closes()
    {
        Ui.Run(ui =>
        {
            var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new ScriptedRunner { SaveGate = save };
            using var rig = new WindowRig(Language.En, runner);
            Assert.True(rig.Checks.TryStart(new CheckRequest(CheckKind.System)));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            rig.StartRecording(ui);
            Dialogs.AutoAnswer = true;

            rig.Form.Close();

            // The first pass was cancelled and asked about the check; the recording is not touched until the window really goes on closing.
            Assert.Single(Dialogs.Log);
            Assert.Equal(0, rig.Closed);
            Assert.Equal(0, rig.Kit.Backend.StopCalls);
            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);

            Assert.True(ui.Until(() => runner.WasStopped));
            save.SetResult();
            Assert.True(ui.Until(() => rig.Closed == 1));

            Assert.Equal(1, rig.Kit.Backend.StopCalls);
            Assert.Equal(RunStopReason.WindowClose, rig.Kit.Backend.LastStopReason);
            Assert.Single(Dialogs.Log);
        });
    }

    [Fact]
    public void Closing_with_no_recording_makes_no_recorder_and_waits_for_nothing()
    {
        Ui.Run(_ =>
        {
            using var rig = new WindowRig();

            rig.Form.Close();

            Assert.Equal(1, rig.Closed);
            Assert.Equal(0, rig.Backends);
            Assert.Equal(0, rig.Stores);
            Assert.Equal(0, rig.Kit.Backend.StopCalls);
            Assert.Empty(Dialogs.Log);
        });
    }

    // ---- lightness: nothing of the recorder before the tab or a start ----

    [Fact]
    public void Nothing_of_the_recorder_exists_after_the_window_starts_until_the_tab_is_shown_or_a_recording_starts()
    {
        Ui.Run(ui =>
        {
            using var rig = new WindowRig();
            Assert.Equal(0, rig.Stores);
            Assert.Equal(0, rig.Backends);

            Assert.NotNull(rig.Form.EnsurePage("checks"));
            Assert.True(rig.Form.ShowPage("checks"));
            ui.Pump();
            Assert.Equal("system", Assert.IsType<ChecksPage>(rig.Form.CurrentPage).CurrentId);
            Assert.Equal(0, rig.Stores);
            Assert.Equal(0, rig.Backends);
            Assert.Equal(0, rig.Kit.Store.ListCalls);
            Assert.Empty(rig.Temp.Entries());

            var page = Assert.IsType<ChecksPage>(rig.Form.CurrentPage);
            page.SelectTab("record");
            Assert.True(ui.Until(() => ((RecordTab)page.TabOf("record")).View is HistoryView.List or HistoryView.Empty));
            Assert.Equal(1, rig.Stores);
            Assert.Equal(0, rig.Backends);

            rig.StartRecording(ui);
            Assert.Equal(1, rig.Backends);
        });
    }

    [Fact]
    public void A_window_on_fake_services_with_no_hub_given_still_makes_nothing_until_used()
    {
        Ui.Run(_ =>
        {
            using var locale = Harness.Locale();
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            using var pump = new SnapshotPump(services);
            using var theme = new ThemeService(ThemeMode.Light);
            var context = new GuiContext(services, pump, theme, new GuiSettings());
            try
            {
                Assert.False(context.Recording.StoreCreated);
                Assert.False(context.Recording.BackendCreated);
                Assert.False(context.Recording.IsActive);
            }
            finally
            {
                context.Checks.Dispose();
                context.Recording.Dispose();
            }
        });
    }
}
