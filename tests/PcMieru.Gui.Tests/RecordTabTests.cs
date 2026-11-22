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

    /// <summary>The header of a column is whole: the grid draws it whole (<c>HeaderNeed</c>), and it passes the smoke run's own rule for every list (the text as the system measures it, and 14 px, in the column).</summary>
    private static void AssertHeaderWhole(RunSummaryGrid grid, int column, string where)
    {
        var width = grid.Columns[column].Width;
        Assert.True(width >= grid.HeaderNeed(column), $"{where}: the header of column {column} needs {grid.HeaderNeed(column)} px and has {width}");
        var smoke = TextRenderer.MeasureText(grid.Columns[column].Text, grid.Font).Width + grid.Scaled(14);
        Assert.True(width >= smoke, $"{where}: the header '{grid.Columns[column].Text}' of column {column} needs {smoke} px by the smoke run's rule and has {width}");
    }

    // ---- the page and tab in a window that is never shown ----

    private sealed class TabRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);
        private int _folderAsked;

        public TabRig(Language language = Language.En, bool seed = false, string? savedTab = null, bool real = false, bool shown = false, Size? size = null)
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

            Hub = real ? RecordingComposition.Create(Services, root: Folder, fake: false) : Kit.CreateHub(() => { Interlocked.Increment(ref _folderAsked); return Folder; });
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
                Host.Size = size ?? new Size(900, 600);
                Host.Show();
            }
        }

        public PcMieruServices Services { get; }

        /// <summary>How many times the hub asked where the recordings are kept (the made-up provider counts).</summary>
        public int FolderAsked => Volatile.Read(ref _folderAsked);

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
    public void The_run_being_recorded_shows_a_running_clock_and_the_estimate_comes_from_the_data_not_the_clock()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig();
            var tab = rig.Show(ui);
            Wait(ui, tab.PressStart());
            rig.Advance(TimeSpan.FromHours(3));

            tab.Reload();
            Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1));

            // The run that is going shows the clock of the recording (m:ss, h:mm:ss), not an estimate.
            Assert.Equal("Recording", tab.ListText(0, 2));
            Assert.Equal("3:00:00", tab.ListText(0, 1));

            // What is estimated for a run nobody is recording any more comes from what was written, never from the hours that went by on the clock.
            Assert.Equal("under 1 min", RecordFormat.ListLength(tab.Runs[0]));
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
                // The number cells hold the dash (not the words four times over, which made every number column wide) and the last column says it in words.
                Assert.All(notMeasured, row =>
                {
                    Assert.Equal(Fmt.Dash, row.Min);
                    Assert.Equal(Fmt.Dash, row.Avg);
                    Assert.Equal(Fmt.Dash, row.Max);
                    Assert.Equal(Fmt.Dash, row.Sustained);
                    Assert.Contains("not measured", row.Time, StringComparison.OrdinalIgnoreCase);
                });
                Assert.Contains(notMeasured, row => row.Item.Contains("CPU temperature", StringComparison.Ordinal));

                Assert.Contains("Measured: 598 s / Not measured: 2 s", all, StringComparison.Ordinal);
                Assert.Contains("Measured: 540 s / Not measured: 60 s", all, StringComparison.Ordinal);
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
                    Assert.Equal(Fmt.Dash, row.Min);
                    Assert.Equal(Fmt.Dash, row.Avg);
                    Assert.Equal(Fmt.Dash, row.Max);
                    Assert.Equal(Fmt.Dash, row.Sustained);
                    Assert.True(row.Time.Contains("測れず", StringComparison.Ordinal) || row.Time.Contains("測っていません", StringComparison.Ordinal), row.Time);
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
            Assert.Contains("Read-only: this app will not change or move the original file.", tab.DetailHead.Text + tab.Notice.Text + tab.HintLabel.Text, StringComparison.Ordinal);
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
            using var rig = new TabRig(seed: true, shown: true, size: new Size(900, 760));
            var tab = rig.Show(ui);
            Assert.True(Ui.PumpUntil(() => tab.List.RowRect(0) is not null), "the native list never laid its first row out");

            // The room is made from a measured row, not from a window size: the bar between the list and the summary takes the list to three whole
            // rows (a window that shows three of the four rows once the old record is listed).
            tab.Splitter.MoveBy(tab.List.HeightForRows(3) - tab.List.Height);
            var rowHeight = tab.List.RowRect(0)!.Value.Height;
            Ui.Settle(() => tab.List.Height + "/" + tab.List.RowsThatFit(tab.List.Height));
            var fit = tab.List.RowsThatFit(tab.List.Height);
            Assert.True(fit >= 3 && fit < 4, $"the list shows {fit} rows of {rowHeight} px, the test needs room for 3 of 4");

            tab.ToggleLegacy();
            Assert.Equal(4, tab.Runs.Count);
            tab.SelectRun(tab.Runs.ToList().FindIndex(r => r.IsLegacy));
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            Assert.True(Ui.PumpUntil(() => tab.List.TopRow > 0), "the list did not scroll to the old record: " + tab.List.TopRow);

            tab.ToggleLegacy();
            Assert.Equal(3, tab.Runs.Count);
            Ui.Settle(() => tab.List.TopRow + "/" + tab.List.VirtualListSize + "/" + tab.List.WholeRowsProblem(out _));
            var problem = tab.List.WholeRowsProblem(out var facts);
            Assert.True(tab.List.TopRow == 0 && problem is null, (problem ?? "whole") + " " + facts + ", first row on show " + tab.List.TopRow);
        });
    }

    // ---- the screen after review: the words, the bar between the list and the summary, nothing cut ----

    private static int RowOf(RecordTab tab, Guid id) => tab.Runs.ToList().FindIndex(r => r.RunId == id);

    private static void KeyDownOn(Control control, Keys key) =>
        typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, [new KeyEventArgs(key)]);

    private static void MouseOn(Control control, string handler, int y) =>
        typeof(Control).GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, 4, y, 0)]);

    private static int ListRows(RecordTab tab) => tab.List.RowsThatFit(tab.List.Height);

    private static string Stripped(string text) => text.Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>What one column says over all the lines a row of the summary is drawn on, joined and without spaces: whatever a row says must be on the screen whole, in whatever lines it wraps to.</summary>
    private static string Drawn(RunSummaryGrid grid, int row, int column)
    {
        var text = new System.Text.StringBuilder();
        for (var line = 0; line < grid.LineCount; line++)
        {
            if (grid.RowOfLine(line) == row)
            {
                text.Append(grid.LineText(line, column));
            }
        }

        return Stripped(text.ToString());
    }

    [Fact]
    public void The_empty_state_the_mark_button_and_the_footer_say_what_the_review_asked_in_each_language()
    {
        Ui.Run(ui =>
        {
            using (var rig = new TabRig(Language.En))
            {
                var tab = rig.Show(ui);
                Assert.Equal(HistoryView.Empty, tab.View);
                Assert.Equal("1. Press Start. 2. Run your benchmark or stress test and add marks if needed. 3. Press Stop to see the summary.", tab.DetailHead.Text);
                Assert.Contains("Add mark", tab.MarkButton.Text, StringComparison.Ordinal);
            }

            using (var rig = new TabRig(Language.Ja))
            {
                var tab = rig.Show(ui);
                Assert.Equal("① 開始を押す → ② ベンチや負荷テストを動かし、必要なら印を付ける → ③ 停止して結果を見る", tab.DetailHead.Text);
                Assert.Contains("印を付ける", tab.MarkButton.Text, StringComparison.Ordinal);
            }

            using (var rig = new TabRig(Language.En, seed: true))
            {
                var tab = rig.Show(ui);
                Assert.Equal("Lengths marked 'about' are estimates. Open an interrupted or failed recording to see the length of the data saved.", tab.HintLabel.Text);
                Assert.Equal("3 recordings", tab.CountLabel.Text);
            }

            using (var rig = new TabRig(Language.Ja, seed: true))
            {
                var tab = rig.Show(ui);
                Assert.Equal("『約』は目安です。中断・失敗した記録は、開くと残っているデータの長さを確認できます。", tab.HintLabel.Text);
            }
        });
    }

    [Fact]
    public void One_recording_is_singular_in_English_and_the_count_does_not_change_in_Japanese()
    {
        using (Harness.Locale(Language.En))
        {
            Assert.Equal("1 recording", RecordFormat.RecordingCount(1));
            Assert.Equal("0 recordings", RecordFormat.RecordingCount(0));
            Assert.Equal("2 recordings", RecordFormat.RecordingCount(2));
        }

        using (Harness.Locale(Language.Ja))
        {
            Assert.Equal("1 件の記録", RecordFormat.RecordingCount(1));
            Assert.Equal("2 件の記録", RecordFormat.RecordingCount(2));
        }
    }

    [Fact]
    public void A_run_being_recorded_shows_a_running_clock_and_a_stop_in_progress_says_it_is_being_saved_in_the_row_and_the_details()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(Language.En);
            var tab = rig.Show(ui);
            Wait(ui, tab.PressStart());
            Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1));
            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Running));
            Assert.Equal("0:00", tab.ListText(0, 1));

            rig.Advance(TimeSpan.FromSeconds(65));
            Assert.Equal("1:05", tab.ListText(0, 1));
            Assert.Equal("Recording", tab.ListText(0, 2));
            Assert.Equal(string.Empty, tab.ListText(0, 3));
            Assert.Contains("1:05", tab.DetailHead.Text, StringComparison.Ordinal);

            // Told to stop: the row and the details say so, the clock stops where it was and does not go on while the run is saved.
            rig.Kit.Backend.HangStop = true;
            var stopping = tab.PressStop();
            Assert.True(ui.Until(() => rig.Hub.Phase == RecordingPhase.Stopping && tab.ListText(0, 2) == "Stopping and saving"));
            rig.Now += TimeSpan.FromSeconds(30);
            Assert.Equal("1:05", tab.ListText(0, 1));
            Assert.Contains("Stopping and saving", tab.DetailHead.Text, StringComparison.Ordinal);

            rig.Kit.Backend.ReleaseStop();
            Wait(ui, stopping);
            Assert.True(ui.Until(() => tab.ListText(0, 2) == "Completed"));
        });
    }

    [Fact]
    public void A_stop_in_progress_is_in_the_words_of_each_language()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(Language.Ja);
            var tab = rig.Show(ui);
            Wait(ui, tab.PressStart());
            Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1));
            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Running));
            rig.Kit.Backend.HangStop = true;
            var stopping = tab.PressStop();
            Assert.True(ui.Until(() => rig.Hub.Phase == RecordingPhase.Stopping && tab.ListText(0, 2) == "停止して保存中"));
            Assert.Contains("停止して保存中", tab.DetailHead.Text, StringComparison.Ordinal);
            rig.Kit.Backend.ReleaseStop();
            Wait(ui, stopping);
        });
    }

    [Fact]
    public void The_state_column_of_the_list_is_made_wide_enough_again_when_a_stop_in_progress_changes_its_words()
    {
        Ui.Run(ui =>
        {
            foreach (var (language, words) in new[] { (Language.En, "Stopping and saving"), (Language.Ja, "停止して保存中") })
            {
                using var rig = new TabRig(language, shown: true);
                var tab = rig.Show(ui);
                Wait(ui, tab.PressStart());
                Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1));
                Assert.True(Ui.PumpUntil(() => tab.List.RowRect(0) is not null));
                Assert.False(tab.List.IsCut(0, 2), $"{language}: the recording row is cut before the stop");

                // The words of a run that is being saved are longer than those of a run that is being recorded: the column grows with them.
                rig.Kit.Backend.HangStop = true;
                var stopping = tab.PressStop();
                Assert.True(ui.Until(() => rig.Hub.Phase == RecordingPhase.Stopping && tab.ListText(0, 2) == words));
                Assert.True(Ui.PumpUntil(() => !tab.List.IsCut(0, 2)), $"{language}: [{tab.ListText(0, 2)}] is cut in a column of {tab.List.Columns[2].Width} px");

                rig.Kit.Backend.ReleaseStop();
                Wait(ui, stopping);
            }
        });
    }

    [Fact]
    public void A_failed_save_says_what_to_check_in_each_language()
    {
        Ui.Run(ui =>
        {
            foreach (var (language, expected) in new[]
            {
                (Language.En, "Saving failed. You can view the data that was saved. Check the folder's free space and write permission, then try again."),
                (Language.Ja, "保存に失敗しました。残った記録は確認できます。保存先の空き容量や書き込みの許可を確かめて、もう一度試してください。"),
            })
            {
                using var rig = new TabRig(language);
                var tab = rig.Show(ui);
                Wait(ui, tab.PressStart());
                rig.Advance(TimeSpan.FromSeconds(10));
                rig.Kit.Backend.EndByItself(RunStopReason.IoError);
                Assert.True(ui.Until(() => rig.Hub.Phase == RecordingPhase.Idle && rig.Hub.LastResult is { Status: RunStatus.Failed }));
                Assert.True(ui.Until(() => tab.StateLabel.Text == expected), tab.StateLabel.Text);
            }
        });
    }

    [Fact]
    public void The_summary_of_a_run_that_was_cut_short_says_the_length_of_the_saved_data_and_the_sustained_value_is_explained()
    {
        Ui.Run(ui =>
        {
            foreach (var (language, saved, length, sustained, name) in new[]
            {
                (Language.En, "Length of the saved data", "Length: ", "Sustained:", "Speed limited by temperature"),
                (Language.Ja, "残っているデータの長さ", "記録の長さ: ", "持続値:", "温度による速度の制限"),
            })
            {
                using var rig = new TabRig(language, seed: true);
                var tab = rig.Show(ui);
                tab.SelectRun(RowOf(tab, FakeRunData.InterruptedId));
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == FakeRunData.InterruptedId));
                Assert.Contains(saved, Cells(tab.Summary), StringComparison.Ordinal);

                tab.SelectRun(RowOf(tab, FakeRunData.CompletedId));
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == FakeRunData.CompletedId));
                var all = Cells(tab.Summary);
                Assert.DoesNotContain(saved, all, StringComparison.Ordinal);
                Assert.Contains(length, all, StringComparison.Ordinal);
                Assert.Contains(name, all, StringComparison.Ordinal);
                Assert.DoesNotContain("ソフトウェアの温度による減速", all, StringComparison.Ordinal);
                Assert.DoesNotContain("Software thermal slowdown", all, StringComparison.Ordinal);

                // The sustained value: a note in the table, right under the readings, says how it is made (it is there only where a run has readings), and the column's header has a tip.
                Assert.Contains(tab.Summary.Rows, row => row.Kind == SummaryRowKind.Note && row.Time.StartsWith(sustained, StringComparison.Ordinal));
                Assert.Equal(Strings.Get("gui.rtab.sum.sustained.tip"), tab.Summary.HeaderTip!(4));
                Assert.Null(tab.Summary.HeaderTip!(1));
            }
        });
    }

    [Fact]
    public void The_missing_time_is_said_as_measured_and_not_measured_in_Japanese_and_the_old_record_note_is_the_read_only_sentence()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(Language.Ja, seed: true);
            var tab = rig.Show(ui);
            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            Assert.Contains("測れた時間: 598 秒／測れなかった時間: 2 秒", Cells(tab.Summary), StringComparison.Ordinal);

            tab.ToggleLegacy();
            tab.SelectRun(tab.Runs.ToList().FindIndex(r => r.IsLegacy));
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == FakeRunData.LegacyId));
            Assert.Contains("読み取り専用です。このアプリは元のファイルを変更・移動しません。", tab.DetailHead.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Nothing_the_summary_says_is_cut_and_a_long_sentence_stays_whole_on_the_lines_it_wraps_to()
    {
        Ui.Run(ui =>
        {
            foreach (var language in new[] { Language.En, Language.Ja })
            {
                using var rig = new TabRig(language, seed: true, shown: true, size: new Size(760, 820));
                var tab = rig.Show(ui);
                Assert.True(Ui.PumpUntil(() => tab.List.RowRect(0) is not null));

                foreach (var id in new[] { FakeRunData.CompletedId, FakeRunData.FailedId, FakeRunData.InterruptedId })
                {
                    tab.SelectRun(RowOf(tab, id));
                    Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == id));
                    Ui.Settle(() => tab.Summary.RowCount + "/" + tab.Summary.LineCount + "/" + tab.Summary.Width);
                    var grid = tab.Summary;
                    Assert.True(grid.RowCount > 0);
                    output.WriteLine($"{language} {id}: {grid.RowCount} rows on {grid.LineCount} lines, columns " + string.Join("/", grid.Columns.Cast<ColumnHeader>().Select(static c => c.Width)) + ", grid " + grid.Width + " px, row " + grid.MeasuredRowHeight);

                    Assert.Equal(string.Empty, string.Join(" | ", grid.UnreadableCuts()));

                    // Every header is whole (the last one, "Time and notes", was cut to "Time and" at 200 %).
                    for (var column = 0; column < grid.Columns.Count; column++)
                    {
                        AssertHeaderWhole(grid, column, $"{language} {id}");
                    }

                    // Every sentence is whole on the lines it is drawn on (a sentence is split between lines, never shortened).
                    for (var row = 0; row < grid.RowCount; row++)
                    {
                        var summaryRow = grid.Rows[row];
                        if (summaryRow.Kind == SummaryRowKind.Record || (summaryRow.Kind == SummaryRowKind.Note && summaryRow.Item.Length == 0))
                        {
                            var sentence = Stripped(summaryRow.Kind == SummaryRowKind.Record ? summaryRow.Item : summaryRow.Time);
                            Assert.True(Drawn(grid, row, 0) == sentence, $"{language} {id} row {row} lost words: said [{sentence}] drawn [{Drawn(grid, row, 0)}]");
                        }
                        else
                        {
                            foreach (var column in new[] { 0, 5 })
                            {
                                var said = Stripped(grid.TextAt(row, column));
                                Assert.True(Drawn(grid, row, column) == said, $"{language} {id} row {row} column {column} lost words: said [{said}] drawn [{Drawn(grid, row, column)}]");
                            }
                        }
                    }

                    var lines = string.Join("\n", Enumerable.Range(0, grid.LineCount).Select(line => grid.LineText(line, 0) + grid.LineText(line, 5)));
                    Assert.DoesNotContain("…", lines, StringComparison.Ordinal);
                }

                // The list: when, how long and the state are never cut; the note may end in an ellipsis because the head of the details says it whole.
                for (var row = 0; row < tab.Runs.Count; row++)
                {
                    for (var column = 0; column < 3; column++)
                    {
                        Assert.False(tab.List.IsCut(row, column), $"{language} list cell {row}:{column} [{tab.ListText(row, column)}] is cut");
                    }

                    if (RecordFormat.NoteText(tab.Runs[row]) is { Length: > 0 } note)
                    {
                        var id = tab.Runs[row].RunId;
                        tab.SelectRun(row);
                        Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == id));
                        Assert.Contains(note, tab.DetailHead.Text, StringComparison.Ordinal);
                    }
                }
            }
        });
    }

    [Fact]
    public void The_summary_never_asks_for_a_line_of_the_old_run_while_it_changes_to_a_run_with_fewer_rows()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true, shown: true, size: new Size(1000, 820));
            var tab = rig.Show(ui);
            Assert.True(Ui.PumpUntil(() => tab.List.RowRect(0) is not null));
            var grid = tab.Summary;

            // The window is made to ask for every line the grid has while the columns change width, which is while the grid is between two runs
            // (the smoke run saw the native list ask for a line then, and the grid answered from the rows of one run with the lines of the other).
            var asked = 0;
            var errors = new List<string>();
            void Probe(object? sender, ColumnWidthChangedEventArgs e)
            {
                asked++;
                try
                {
                    for (var line = 0; line < 300; line++)
                    {
                        _ = grid.LineText(line, 0);
                        _ = grid.LineText(line, 5);
                        _ = grid.RowOfLine(line);
                    }
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    errors.Add(ex.Message);
                }
            }

            grid.ColumnWidthChanged += Probe;
            try
            {
                foreach (var id in new[] { FakeRunData.CompletedId, FakeRunData.FailedId, FakeRunData.CompletedId, FakeRunData.InterruptedId, FakeRunData.FailedId })
                {
                    tab.SelectRun(RowOf(tab, id));
                    Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == id));
                    Ui.Settle(() => grid.RowCount + "/" + grid.LineCount);
                }

                // A run that is not there any more: the summary is emptied while it still holds the lines of the last one.
                grid.Show(null);
                Ui.Settle(() => grid.RowCount + "/" + grid.LineCount);
            }
            finally
            {
                grid.ColumnWidthChanged -= Probe;
            }

            Assert.True(asked > 0, "the columns never changed width, so nothing was asked in between");
            Assert.Empty(errors);
            Assert.Equal(0, grid.LineCount);
        });
    }

    [Fact]
    public void The_headers_of_the_summary_are_whole_by_the_smoke_rules_measure_at_every_text_size_with_nothing_in_it_too()
    {
        // The smoke run measures every list header with the system's own padding and 14 px, at the text size the window really has (the default is one point
        // above the system's: "Average" was 72 px and needed 73). With no run open the columns have no readings to be as wide as, so the header alone carries them.
        Ui.Run(ui =>
        {
            foreach (var language in new[] { Language.En, Language.Ja })
            {
                foreach (var size in new[] { TextSize.Small, TextSize.Normal, TextSize.Large })
                {
                    using var rig = new TabRig(language, shown: true, size: new Size(760, 820));
                    var system = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
                    rig.Host.Font = new Font(system.FontFamily, TextSizes.Points(size, system.SizeInPoints), system.Style, GraphicsUnit.Point);
                    var tab = rig.Show(ui);
                    Ui.Settle(() => tab.Summary.Width + "/" + tab.Summary.Columns[1].Width + "/" + tab.Summary.Font.SizeInPoints);
                    Assert.Equal(0, tab.Summary.RowCount);
                    output.WriteLine($"{language} {size}: {tab.Summary.DeviceDpi} dpi, font {tab.Summary.Font.Name} {tab.Summary.Font.SizeInPoints} pt, grid {tab.Summary.Width} px, columns " + string.Join("/", tab.Summary.Columns.Cast<ColumnHeader>().Select(c => $"{c.Text}={c.Width}(need {tab.Summary.HeaderNeed(c.Index)}, smoke {TextRenderer.MeasureText(c.Text, tab.Summary.Font).Width + tab.Summary.Scaled(14)})")));
                    for (var column = 0; column < tab.Summary.Columns.Count; column++)
                    {
                        AssertHeaderWhole(tab.Summary, column, $"{language} {size} with nothing open");
                    }
                }
            }
        });
    }

    [Fact]
    public void With_a_run_open_the_summary_has_most_of_the_height_and_the_bar_moves_the_list_by_whole_rows_for_this_session_only()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true, shown: true, size: new Size(1000, 820));
            var tab = rig.Show(ui);
            Assert.True(Ui.PumpUntil(() => tab.List.RowRect(0) is not null && tab.Body.ClientSize.Height > 0));
            Ui.Settle(() => tab.List.Height + "/" + tab.DetailPanel.Height);
            var settingsBefore = string.Join("\n", rig.Settings.ToLines());
            var heightBeforeSelecting = tab.List.Height;

            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            Ui.Settle(() => tab.List.Height + "/" + tab.DetailPanel.Height + "/" + tab.Summary.LineCount);
            Assert.Equal(heightBeforeSelecting, tab.List.Height);

            var divided = tab.Body.ClientSize.Height - tab.Splitter.Height;
            output.WriteLine($"body {tab.Body.ClientSize.Height}, list {tab.List.Height} ({ListRows(tab)} rows), summary part {tab.DetailPanel.Height} of {divided} = {tab.DetailPanel.Height * 100 / divided} %, row {tab.List.MeasuredRowHeight}");
            Assert.True(tab.DetailPanel.Height * 100 >= divided * 60, $"the summary has {tab.DetailPanel.Height} of {divided}");
            Assert.True(ListRows(tab) >= 2);
            Assert.Equal(tab.List.HeightForRows(ListRows(tab)), tab.List.Height);
            Assert.Equal(divided, tab.List.Height + tab.DetailPanel.Height);

            // The bar is something a person can take hold of (a keyboard user too) and has a name.
            Assert.True(tab.Splitter.TabStop);
            Assert.Equal(AccessibleRole.Grip, tab.Splitter.AccessibleRole);
            Assert.Equal("Divider between the list and the summary (move it up or down)", tab.Splitter.AccessibleName);
            Assert.Equal(tab.List.MeasuredRowHeight, tab.Splitter.KeyStep);

            // Dragged all the way up: one row. Down by one row's height: one more. A drag that moves less than half a row does not change the rows.
            var row = tab.List.MeasuredRowHeight;
            tab.Splitter.MoveBy(-10_000);
            Assert.Equal(1, ListRows(tab));
            tab.Splitter.MoveBy(row);
            Assert.Equal(2, ListRows(tab));
            tab.Splitter.MoveBy((row / 2) - 2);
            Assert.Equal(2, ListRows(tab));
            Assert.Equal(tab.List.HeightForRows(2), tab.List.Height);

            // The arrow keys move it a row at a time; the mouse moves it by the distance dragged.
            KeyDownOn(tab.Splitter, Keys.Down);
            Assert.Equal(3, ListRows(tab));
            KeyDownOn(tab.Splitter, Keys.Up);
            KeyDownOn(tab.Splitter, Keys.Up);
            Assert.Equal(1, ListRows(tab));
            MouseOn(tab.Splitter, "OnMouseDown", 5);
            Assert.True(tab.Splitter.IsDragging);
            MouseOn(tab.Splitter, "OnMouseMove", 5 + row);
            Assert.Equal(2, ListRows(tab));
            MouseOn(tab.Splitter, "OnMouseUp", 5 + row);
            Assert.False(tab.Splitter.IsDragging);

            // Dragged all the way down: the summary keeps room for itself.
            tab.Splitter.MoveBy(10_000);
            Assert.True(tab.DetailPanel.Height >= tab.List.Scaled(150), $"the summary has only {tab.DetailPanel.Height} px");
            Assert.True(ListRows(tab) >= 2);

            // The window made taller keeps the share and the whole rows.
            rig.Host.Height += 120;
            Ui.Settle(() => tab.Body.ClientSize.Height + "/" + tab.List.Height);
            Assert.Equal(tab.List.HeightForRows(ListRows(tab)), tab.List.Height);

            // Session only: nothing about the bar was written to the settings (no gui.ini key).
            Assert.Equal(settingsBefore, string.Join("\n", rig.Settings.ToLines()));
        });
    }

    [Fact]
    public void The_readings_come_first_and_the_note_on_the_sustained_value_is_a_row_right_under_them()
    {
        Ui.Run(ui =>
        {
            foreach (var (language, sustained, record) in new[] { (Language.En, "Sustained:", "This recording"), (Language.Ja, "持続値:", "この記録") })
            {
                using var rig = new TabRig(language, seed: true);
                var tab = rig.Show(ui);
                foreach (var id in new[] { FakeRunData.CompletedId, FakeRunData.FailedId, FakeRunData.InterruptedId })
                {
                    tab.SelectRun(RowOf(tab, id));
                    Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == id));
                    var rows = tab.Summary.Rows.ToList();

                    // The first thing in the table is the readings; what the recording is comes after them, under a heading of its own.
                    Assert.Equal(SummaryRowKind.Heading, rows[0].Kind);
                    Assert.Equal(Strings.Get("gui.rtab.sum.head.metrics"), rows[0].Item);
                    var lastReading = rows.FindLastIndex(static r => r.Kind is SummaryRowKind.Metric or SummaryRowKind.NotMeasured);
                    Assert.True(lastReading > 0, $"{language} {id}: no readings");

                    // The note on the sustained value is a row, there only where there are readings with values, and it is the row under the last reading.
                    var hasMetric = rows.Any(static r => r.Kind == SummaryRowKind.Metric);
                    var noteAt = rows.FindIndex(r => r.Kind == SummaryRowKind.Note && r.Item.Length == 0 && r.Time.StartsWith(sustained, StringComparison.Ordinal));
                    Assert.Equal(hasMetric, noteAt >= 0);
                    if (hasMetric)
                    {
                        Assert.Equal(lastReading + 1, noteAt);
                    }

                    var headingAt = rows.FindIndex(r => r.Kind == SummaryRowKind.Heading && r.Item == record);
                    Assert.True(headingAt > (hasMetric ? noteAt : lastReading), $"{language} {id}: the heading '{record}' is at {headingAt}");
                    Assert.Equal(SummaryRowKind.Record, rows[headingAt + 1].Kind);
                }
            }
        });
    }

    [Fact]
    public void At_the_room_a_1080p_screen_at_200_percent_leaves_the_summary_shows_several_readings_whole_and_every_header_is_whole()
    {
        Ui.Run(ui =>
        {
            foreach (var language in new[] { Language.En, Language.Ja })
            {
                // The tab has about 740 x 410 logical pixels on a 1920 x 1080 screen at 200 % (the real one is measured by the smoke run at 200 %); the host is
                // made so big that the tab is that big (the window frame and the page header take 16 x 83 of it).
                using var rig = new TabRig(language, seed: true, shown: true, size: new Size(756, 493));
                var tab = rig.Show(ui);
                Assert.True(Ui.PumpUntil(() => tab.List.RowRect(0) is not null));
                tab.SelectRun(RowOf(tab, FakeRunData.CompletedId));
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == FakeRunData.CompletedId));
                var grid = tab.Summary;
                Ui.Settle(() => tab.List.Height + "/" + grid.Height + "/" + grid.LineCount);

                var fit = grid.RowsThatFit(grid.Height);
                var whole = grid.ReadingsWholeAtTop();
                output.WriteLine($"{language}: tab {tab.Width}x{tab.Height}, body {tab.Body.ClientSize.Height}, list {tab.List.Height} px, detail part {tab.DetailPanel.Height} (header {tab.DetailHeader.Height}), summary {grid.Height} px = {fit} lines of {grid.MeasuredRowHeight} px, {whole} readings whole; columns " + string.Join("/", grid.Columns.Cast<ColumnHeader>().Select(static c => c.Width)));
                Assert.True(whole >= 4, $"{language}: only {whole} readings are whole in the {fit} lines of the summary");

                for (var column = 0; column < grid.Columns.Count; column++)
                {
                    AssertHeaderWhole(grid, column, language.ToString());
                }
            }
        });
    }

    [Fact]
    public void A_low_window_still_shows_a_row_of_the_list_and_the_summary_keeps_its_minimum()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true, shown: true, size: new Size(900, 560));
            var tab = rig.Show(ui);
            Assert.True(Ui.PumpUntil(() => tab.List.RowRect(0) is not null));
            tab.SelectRun(0);
            Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
            Ui.Settle(() => tab.Body.ClientSize.Height + "/" + tab.List.Height + "/" + tab.DetailPanel.Height);
            output.WriteLine($"body {tab.Body.ClientSize.Height}, list {tab.List.Height} ({ListRows(tab)} rows), summary part {tab.DetailPanel.Height}");
            Assert.True(ListRows(tab) >= 1);
            Assert.True(tab.DetailPanel.Height >= tab.List.Scaled(150) || ListRows(tab) == 1, "the summary has " + tab.DetailPanel.Height + " px");
        });
    }

    [Fact]
    public void Every_tone_reads_at_4_5_to_1_or_better_on_every_surface_it_is_drawn_on_in_both_themes()
    {
        // WCAG 2 AA for normal text. The selected row is drawn in the palette's selection colour, and the list's own selection overlay darkens it by
        // a sixth (measured on the screenshots: 204,228,247 became 174,194,210 and 0,80,140 became 0,68,119), so both are checked.
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            using var theme = new ThemeService(mode);
            var palette = theme.Palette;
            var darkened = Color.FromArgb((int)(palette.Selection.R * 0.85), (int)(palette.Selection.G * 0.85), (int)(palette.Selection.B * 0.85));
            foreach (var tone in Enum.GetValues<CellTone>())
            {
                foreach (var (surface, name) in new[] { (palette.Surface, "the surface"), (palette.Window, "the window") })
                {
                    var plain = BuildCodeUiTests.ContrastRatio(GridView.ToneColor(palette, tone, selected: false), surface);
                    Assert.True(plain >= 4.5, $"{mode} {tone} on {name}: {plain:F2}:1");
                }

                foreach (var (surface, name) in new[] { (palette.Selection, "the selection"), (darkened, "the darkened selection") })
                {
                    var chosen = BuildCodeUiTests.ContrastRatio(GridView.ToneColor(palette, tone, selected: true), surface);
                    Assert.True(chosen >= 4.5, $"{mode} {tone} selected on {name}: {chosen:F2}:1");
                }
            }
        }
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
                Wait(ui, tab.PressOpenCsv());
                var one = Assert.Single(started);
                Assert.Equal(info.CsvPath, one.FileName);
                Assert.True(one.UseShellExecute);

                // A file that is gone is said so and nothing is started.
                started.Clear();
                rig.Hub.FileExists = _ => false;
                Wait(ui, tab.PressOpenCsv());
                Assert.Empty(started);
                Assert.Contains("was not found", tab.Notice.Text, StringComparison.Ordinal);
                rig.Hub.FileExists = _ => true;

                // A folder that is not there yet is said so and nothing is started.
                rig.Kit.FolderPresent = false;
                Wait(ui, tab.PressOpenFolder());
                Assert.Empty(started);
                Assert.Contains("does not exist yet", tab.Notice.Text, StringComparison.Ordinal);

                rig.Kit.FolderPresent = true;
                Wait(ui, tab.PressOpenFolder());
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

    // ---- after the review: lightness, the disk off the UI thread, the old record's copy, delete in flight, the close ----

    [Fact]
    public void The_default_recordings_folder_is_asked_of_Windows_when_the_hub_is_first_asked_for_it_never_when_it_is_made()
    {
        Ui.Run(_ =>
        {
            using var locale = Harness.Locale();
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            var asked = 0;
            using var hub = RecordingComposition.Create(
                services,
                fake: false,
                defaultFolder: () =>
                {
                    asked++;
                    return Path.Combine(Path.GetTempPath(), "pcm-counting-default", "runs");
                });

            Assert.Equal(0, asked);
            Assert.False(hub.StoreCreated);
            Assert.False(hub.BackendCreated);

            var folder = hub.Folder;
            Assert.EndsWith(Path.Combine("pcm-counting-default", "runs"), folder, StringComparison.Ordinal);
            Assert.Equal(folder, hub.Folder);
            Assert.Equal(1, asked);

            // A folder that was given is never looked up.
            var given = Path.Combine(Path.GetTempPath(), "pcm-given-folder");
            using var withRoot = RecordingComposition.Create(services, root: given, fake: false, defaultFolder: () => throw new InvalidOperationException("asked"));
            Assert.Equal(given, withRoot.Folder);
        });
    }

    [Fact]
    public void The_tab_asks_for_the_folder_once_when_it_is_first_shown_and_the_buttons_leave_the_disk_to_a_worker()
    {
        Ui.Run(ui =>
        {
            var started = new List<ProcessStartInfo>();
            var previous = ShellActions.Start;
            ShellActions.Start = started.Add;
            try
            {
                using var rig = new TabRig(seed: true);
                var disk = new System.Collections.Concurrent.ConcurrentBag<int>();
                rig.Hub.FolderExists = _ =>
                {
                    disk.Add(Environment.CurrentManagedThreadId);
                    return true;
                };
                rig.Hub.FileExists = _ =>
                {
                    disk.Add(Environment.CurrentManagedThreadId);
                    return true;
                };

                // Building the page and the tab did not ask; showing the tab does, once, and every drawing after it uses the answer.
                Assert.Equal(0, rig.FolderAsked);
                var tab = rig.Show(ui);
                Assert.Equal(1, rig.FolderAsked);
                tab.SelectRun(0);
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));

                Wait(ui, tab.PressOpenCsv());
                Wait(ui, tab.PressOpenFolder());
                tab.Reload();
                Assert.True(ui.Until(() => tab.View == HistoryView.List));
                tab.ApplyLanguage();

                Assert.Equal(1, rig.FolderAsked);
                Assert.Equal(2, started.Count);
                Assert.NotEmpty(disk);
                Assert.DoesNotContain(ui.OwnerThread, disk);
            }
            finally
            {
                ShellActions.Start = previous;
            }
        });
    }

    [Fact]
    public void Open_csv_of_an_old_record_opens_a_copy_in_the_temp_folder_every_time_and_never_the_original()
    {
        Ui.Run(ui =>
        {
            var started = new List<ProcessStartInfo>();
            var previous = ShellActions.Start;
            ShellActions.Start = started.Add;
            try
            {
                using var rig = new TabRig(seed: true);
                var copies = new List<(string From, string To)>();
                var temp = Path.Combine(rig.Temp.Path, "tmp");
                rig.Hub.TempFolder = () => temp;
                rig.Hub.CopyFile = (from, to) => copies.Add((from, to));
                var tab = rig.Show(ui);
                tab.ToggleLegacy();
                var row = tab.Runs.ToList().FindIndex(r => r.IsLegacy);
                Assert.True(row >= 0);
                tab.SelectRun(row);
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
                var old = rig.Kit.Store.Info(FakeRunData.LegacyId)!;
                var expected = Path.Combine(temp, "PCMieru-legacy-" + old.LegacyId + ".csv");

                Assert.True(tab.CsvButton.Enabled);
                Wait(ui, tab.PressOpenCsv());
                var copy = Assert.Single(copies);
                Assert.Equal(old.CsvPath, copy.From);
                Assert.Equal(expected, copy.To);
                var shell = Assert.Single(started);
                Assert.Equal(expected, shell.FileName);
                Assert.NotEqual(old.CsvPath, shell.FileName);

                // The second time the same copy is made again (it is replaced, never a pile of files).
                Wait(ui, tab.PressOpenCsv());
                Assert.Equal(2, copies.Count);
                Assert.Equal(expected, copies[1].To);
                Assert.All(started, one => Assert.Equal(expected, one.FileName));

                // The app's own recording is opened itself, with no copy.
                tab.ToggleLegacy();
                tab.SelectRun(tab.Runs.ToList().FindIndex(r => r.RunId == FakeRunData.CompletedId));
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == FakeRunData.CompletedId));
                started.Clear();
                Wait(ui, tab.PressOpenCsv());
                Assert.Equal(2, copies.Count);
                Assert.Equal(rig.Kit.Store.Info(FakeRunData.CompletedId)!.CsvPath, Assert.Single(started).FileName);

                // A copy that cannot be made is said in words and nothing is opened.
                tab.ToggleLegacy();
                tab.SelectRun(tab.Runs.ToList().FindIndex(r => r.IsLegacy));
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found && tab.SelectedRunId == FakeRunData.LegacyId));
                started.Clear();
                rig.Hub.CopyFile = (_, _) => throw new IOException("the temp folder is full");
                Wait(ui, tab.PressOpenCsv());
                Assert.Empty(started);
                Assert.Contains("IOException 0x", tab.Notice.Text, StringComparison.Ordinal);
            }
            finally
            {
                ShellActions.Start = previous;
            }
        });
    }

    [Fact]
    public void The_copy_of_an_old_csv_is_made_on_disk_under_a_name_of_the_run_and_the_original_is_left_alone()
    {
        Ui.Run(_ =>
        {
            using var rig = new TabRig(seed: true);
            var old = rig.Kit.Store.Info(FakeRunData.LegacyId)!;
            var original = Path.Combine(rig.Temp.Path, "old", "sensors.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.WriteAllText(original, "t,v\n1,2\n");
            var bytes = File.ReadAllBytes(original);
            var written = File.GetLastWriteTimeUtc(original);
            var temp = Path.Combine(rig.Temp.Path, "tmp");
            Directory.CreateDirectory(temp);
            var hub = new RecordingHub(() => rig.Kit.Backend, () => rig.Kit.Store, () => rig.Folder, () => rig.Now) { TempFolder = () => temp };

            var path = hub.CsvToOpen(old with { CsvPath = original });

            Assert.Equal(Path.Combine(temp, "PCMieru-legacy-" + old.LegacyId + ".csv"), path);
            Assert.Equal(bytes, File.ReadAllBytes(path));

            // The program that opened it changed it: the next open puts the original's content back, and the original never changed.
            File.WriteAllText(path, "edited");
            Assert.Equal(path, hub.CsvToOpen(old with { CsvPath = original }));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(bytes, File.ReadAllBytes(original));
            Assert.Equal(written, File.GetLastWriteTimeUtc(original));
            Assert.Single(Directory.GetFiles(temp));

            // The app's own file is the file itself.
            var own = rig.Kit.Store.Info(FakeRunData.CompletedId)!;
            Assert.Equal(own.CsvPath, hub.CsvToOpen(own));
            hub.Dispose();
        });
    }

    [Fact]
    public void The_delete_button_is_off_while_a_delete_is_in_flight_and_a_second_press_asks_nothing()
    {
        Ui.Run(ui =>
        {
            using var rig = new TabRig(seed: true);
            using var hold = new ManualResetEventSlim(false);
            rig.Kit.Store.DeleteHold = hold;
            try
            {
                var tab = rig.Show(ui);
                tab.SelectRun(0);
                Assert.True(ui.Until(() => tab.DetailState == RecordDetailState.Found));
                Assert.True(tab.DeleteButton.Enabled);
                Dialogs.AutoAnswer = true;

                var first = tab.PressDelete();
                Assert.True(ui.Until(() => rig.Kit.Store.DeleteCalls == 1), "The delete never reached the store.");
                Assert.False(first.IsCompleted);
                Assert.False(tab.DeleteButton.Enabled, "The button stayed on while the files were being removed.");

                var asked = Dialogs.Log.Count;
                Wait(ui, tab.PressDelete());
                Assert.Equal(1, rig.Kit.Store.DeleteCalls);
                Assert.Equal(asked, Dialogs.Log.Count);

                hold.Set();
                Wait(ui, first);
                Assert.True(ui.Until(() => tab.View == HistoryView.List && tab.Runs.Count == 2));
                Assert.Equal(1, rig.Kit.Store.DeleteCalls);
            }
            finally
            {
                hold.Set();
            }
        });
    }

    [Fact]
    public void Closing_for_the_window_tells_nobody_in_the_middle_of_the_close_and_tells_a_window_that_stays_open_afterwards()
    {
        Ui.Run(ui =>
        {
            // The close is refused after all (another program vetoes the end of the Windows session): the window stays, the hub is idle, and the
            // screens hear of it once the message loop has the thread again. Not a moment sooner: no handler runs in the middle of a close.
            using (var rig = new WindowRig())
            {
                rig.StartRecording(ui);
                ui.Pump();
                var changes = 0;
                rig.Hub.Changed += () => changes++;

                Assert.NotNull(rig.Hub.StopForClose());
                Assert.Equal(0, changes);
                Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);

                ui.Pump();
                Assert.Equal(1, changes);
            }

            // The window really goes away: the hub is disposed with it and nothing is told afterwards.
            using (var rig = new WindowRig())
            {
                rig.StartRecording(ui);
                ui.Pump();
                var changes = 0;
                rig.Hub.Changed += () => changes++;

                Assert.NotNull(rig.Hub.StopForClose());
                rig.Hub.Dispose();
                ui.Pump();
                Assert.Equal(0, changes);
            }
        });
    }
}
