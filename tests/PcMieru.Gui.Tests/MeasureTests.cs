using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PcMieru.Core.Collection;

namespace PcMieru.Gui.Tests;

/// <summary>
/// <c>--measure &lt;file&gt;</c> (DESIGN-A 10, step 12): the two render-time marks <c>tools/measure.ps1</c> reads, and the promise that a measurement runs on the
/// default settings and never writes gui.ini or eco-state.json. Nothing here looks at the clock: the marks carry a clock the test hands in, and the paint test
/// feeds the list one sample at a time (no pump, no timer) into a window that is on show far off the screen and sends the list the WM_PAINT itself.
/// </summary>
public class MeasureTests
{
    private static readonly DateTime T0 = new DateTime(2026, 11, 21, 1, 2, 3, DateTimeKind.Utc).AddTicks(4_567_890);

    // ---- the command line ----

    [Fact]
    public void Measure_needs_a_file()
    {
        Assert.False(GuiArgs.TryParse(["--measure"], out _, out var problem));
        Assert.Contains("--measure", problem, StringComparison.Ordinal);
        Assert.False(GuiArgs.TryParse(["--measure", string.Empty], out _, out _));
    }

    [Fact]
    public void Measure_is_read_with_its_file_and_may_go_with_the_fakes()
    {
        Assert.True(GuiArgs.TryParse(["--measure", "marks.jsonl", "--fakes"], out var o, out var problem));
        Assert.Equal(string.Empty, problem);
        Assert.Equal("marks.jsonl", o.MeasureOutput);
        Assert.True(o.ForceFakes);

        // The process page is where the marks are taken: the measurement starts there, whatever the first page is.
        Assert.Equal("processes", o.StartPage);
        Assert.Contains(PageCatalog.All, e => e.Id == o.StartPage);

        // The services stay off PC Mieru's own state files (eco-state.json) for the whole run.
        Assert.True(o.CoreOptions.NoStateFiles);
        Assert.True(o.CoreOptions.ForceFakes);
    }

    [Fact]
    public void A_normal_start_has_no_measure_and_keeps_the_state_files_and_the_first_page()
    {
        Assert.True(GuiArgs.TryParse([], out var normal, out _));
        Assert.Null(normal.MeasureOutput);
        Assert.Null(normal.StartPage);
        Assert.False(normal.CoreOptions.NoStateFiles);
        Assert.False(normal.CoreOptions.ForceFakes);

        Assert.True(GuiArgs.TryParse(["--page", "hardware", "--fakes"], out var page, out _));
        Assert.Equal("hardware", page.StartPage);
        Assert.False(page.CoreOptions.NoStateFiles);
        Assert.True(page.CoreOptions.ForceFakes);
    }

    [Theory]
    [InlineData("--measure", "m.jsonl", "--page", "hardware")]
    [InlineData("--measure", "m.jsonl", "--smoke", "s.json")]
    [InlineData("--smoke", "s.json", "--measure", "m.jsonl")]
    public void Measure_is_not_combined_with_a_page_or_a_smoke_run(params string[] args)
    {
        Assert.False(GuiArgs.TryParse(args, out _, out var problem));
        Assert.Contains("--measure", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Measure_is_in_no_text_the_user_sees()
    {
        Assert.All(GuiStrings.All, e =>
        {
            Assert.DoesNotContain("--measure", e.En, StringComparison.Ordinal);
            Assert.DoesNotContain("--measure", e.Ja, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Only_the_command_line_makes_the_marker_file()
    {
        // A normal start builds no MeasureMarks at all (the context's Measure stays null), so no file is opened, created or written.
        // The only place that makes one is Program.cs, behind the switch.
        var callers = Directory.EnumerateFiles(Harness.GuiSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("MeasureMarks.Create(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Equal(new[] { "Program.cs" }, callers);

        var program = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));
        Assert.Contains("if (options.MeasureOutput is { } markerFile)", program, StringComparison.Ordinal);
        Assert.True(program.IndexOf("if (options.MeasureOutput is { } markerFile)", StringComparison.Ordinal) < program.IndexOf("MeasureMarks.Create(", StringComparison.Ordinal));
    }

    // ---- the marks ----

    [Fact]
    public void A_mark_is_one_json_line_with_the_time_in_seven_fraction_digits_and_a_Z()
    {
        var lines = new List<string>();
        var marks = new MeasureMarks(lines.Add, () => T0);

        marks.Painted(hasRows: true, hasCpuValues: false);
        marks.Painted(hasRows: true, hasCpuValues: true);

        Assert.Equal(
            [
                "{\"mark\":\"first_list\",\"utc\":\"2026-11-21T01:02:03.4567890Z\"}",
                "{\"mark\":\"cpu_list\",\"utc\":\"2026-11-21T01:02:03.4567890Z\"}",
            ],
            lines);

        // Trailing zeros are kept: always 7 digits.
        var zero = new List<string>();
        new MeasureMarks(zero.Add, () => new DateTime(2026, 11, 21, 1, 2, 3, DateTimeKind.Utc)).Painted(true, false);
        Assert.Equal("{\"mark\":\"first_list\",\"utc\":\"2026-11-21T01:02:03.0000000Z\"}", Assert.Single(zero));
    }

    [Fact]
    public void The_cpu_mark_never_comes_before_the_rows_and_each_mark_is_written_once()
    {
        var lines = new List<string>();
        var marks = new MeasureMarks(lines.Add, () => T0);

        marks.Painted(hasRows: false, hasCpuValues: true); // CPU values with no row painted cannot happen, and must not mark anything
        marks.Painted(hasRows: false, hasCpuValues: false);
        Assert.Empty(lines);
        Assert.False(marks.Complete);

        marks.Painted(hasRows: true, hasCpuValues: false);
        marks.Painted(hasRows: true, hasCpuValues: false);
        Assert.Equal([MeasureMarks.FirstList], marks.Written);
        Assert.Single(lines);
        Assert.False(marks.Complete);

        marks.Painted(hasRows: true, hasCpuValues: true);
        for (var i = 0; i < 5; i++)
        {
            marks.Painted(hasRows: true, hasCpuValues: true);
            marks.Painted(hasRows: true, hasCpuValues: false);
        }

        Assert.Equal([MeasureMarks.FirstList, MeasureMarks.CpuList], marks.Written);
        Assert.Equal(2, lines.Count);
        Assert.True(marks.Complete);
    }

    [Fact]
    public void Rows_and_cpu_values_in_the_first_paint_are_written_in_that_order()
    {
        var lines = new List<string>();
        var marks = new MeasureMarks(lines.Add, () => T0);
        marks.Painted(hasRows: true, hasCpuValues: true);
        Assert.Equal([MeasureMarks.FirstList, MeasureMarks.CpuList], marks.Written);
        Assert.Contains("first_list", lines[0], StringComparison.Ordinal);
        Assert.Contains("cpu_list", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_mark_that_cannot_be_written_does_not_disturb_the_window_and_is_not_written_later()
    {
        var failing = true;
        var lines = new List<string>();
        var marks = new MeasureMarks(
            line =>
            {
                if (failing)
                {
                    throw new IOException("disk full");
                }

                lines.Add(line);
            },
            () => T0);

        marks.Painted(hasRows: true, hasCpuValues: false);
        failing = false;
        marks.Painted(hasRows: true, hasCpuValues: false);
        marks.Painted(hasRows: true, hasCpuValues: true);

        // first_list is missing from the file (the meter says "missing"); a later write would carry a later time than the paint it names.
        Assert.Single(lines);
        Assert.Contains("cpu_list", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_watch_ends_sixty_seconds_after_the_first_paint_and_a_late_cpu_mark_is_not_written()
    {
        var now = T0;
        var lines = new List<string>();
        var marks = new MeasureMarks(lines.Add, () => now);

        marks.Painted(hasRows: true, hasCpuValues: false);
        Assert.False(marks.Done);

        // Inside the window the CPU mark still comes (one tick short of the limit).
        now = T0 + MeasureMarks.WatchTime - TimeSpan.FromTicks(1);
        marks.Painted(hasRows: true, hasCpuValues: true);
        Assert.Equal([MeasureMarks.FirstList, MeasureMarks.CpuList], marks.Written);
        Assert.False(marks.Expired);
        Assert.True(marks.Done); // both marks written: done, though not expired

        // The same run with the CPU paint 60 s after the first paint: expired, the CPU mark stays missing, nothing is written afterwards.
        now = T0;
        var late = new List<string>();
        var lateMarks = new MeasureMarks(late.Add, () => now);
        lateMarks.Painted(hasRows: true, hasCpuValues: false);
        now = T0 + MeasureMarks.WatchTime;
        lateMarks.Painted(hasRows: true, hasCpuValues: true);
        Assert.True(lateMarks.Expired);
        Assert.True(lateMarks.Done);
        Assert.False(lateMarks.Complete);
        Assert.Equal([MeasureMarks.FirstList], lateMarks.Written);
        lateMarks.Painted(hasRows: true, hasCpuValues: true);
        Assert.Single(late);
    }

    [Fact]
    public void The_watch_also_ends_after_thirty_samples_counted_from_the_first_paint_not_before_it()
    {
        var now = T0;
        var lines = new List<string>();
        var marks = new MeasureMarks(lines.Add, () => now);

        // Samples before any paint are not counted: a slow first paint does not eat the allowance.
        for (var i = 0; i < 100; i++)
        {
            marks.Sampled();
        }

        Assert.False(marks.Done);

        marks.Painted(hasRows: true, hasCpuValues: false);
        for (var i = 1; i < MeasureMarks.WatchSamples; i++)
        {
            marks.Sampled();
            Assert.False(marks.Done);
        }

        marks.Sampled();
        Assert.True(marks.Expired);
        Assert.True(marks.Done);

        marks.Painted(hasRows: true, hasCpuValues: true);
        Assert.Equal([MeasureMarks.FirstList], marks.Written);
        Assert.Single(lines);
    }

    [Fact]
    public void A_sample_that_comes_sixty_seconds_after_the_first_paint_ends_the_watch_and_a_finished_watch_is_not_expired()
    {
        var now = T0;
        var marks = new MeasureMarks(_ => { }, () => now);
        marks.Painted(hasRows: true, hasCpuValues: false);
        now = T0 + MeasureMarks.WatchTime;
        marks.Sampled();
        Assert.True(marks.Expired);

        // Both marks written: Done, never Expired, and a later sample changes nothing.
        now = T0;
        var complete = new MeasureMarks(_ => { }, () => now);
        complete.Painted(hasRows: true, hasCpuValues: true);
        now = T0 + MeasureMarks.WatchTime + MeasureMarks.WatchTime;
        complete.Sampled();
        Assert.True(complete.Complete);
        Assert.True(complete.Done);
        Assert.False(complete.Expired);
    }

    [Fact]
    public void A_bad_marker_path_is_one_stderr_line_and_no_file_and_the_gui_has_no_message_box_for_it()
    {
        using var root = new Ui.TempRoot();
        var path = Path.Combine(root.Path, "no", "such", "folder", "marks.jsonl");
        using var error = new StringWriter();

        Assert.False(Program.TryOpenMeasureMarks(path, error, out var marks));
        Assert.Null(marks);
        var lines = error.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.StartsWith("--measure: ", lines[0], StringComparison.Ordinal);
        Assert.Empty(root.Entries());

        // A good path makes the file, says nothing, and hands the marks back.
        var good = Path.Combine(root.Path, "marks.jsonl");
        using var quiet = new StringWriter();
        Assert.True(Program.TryOpenMeasureMarks(good, quiet, out var made));
        Assert.NotNull(made);
        Assert.Equal(string.Empty, quiet.ToString());
        Assert.Equal([good], root.Entries());

        // Not a box: the measurement is unattended, so the measure path of Program.cs never opens one (the other start problems may).
        var program = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));
        var start = program.IndexOf("internal static bool TryOpenMeasureMarks(", StringComparison.Ordinal);
        Assert.True(start > 0);
        Assert.DoesNotContain("MessageBox", program[start..], StringComparison.Ordinal);
        Assert.DoesNotContain("--measure: \" + ex.Message, \"PCMieru\"", program, StringComparison.Ordinal);
        var guard = program.IndexOf("if (options.MeasureOutput is { } markerFile)", StringComparison.Ordinal);
        Assert.True(guard > 0);
        var block = program.Substring(guard, 200);
        Assert.DoesNotContain("MessageBox", block, StringComparison.Ordinal);
        Assert.Contains("TryOpenMeasureMarks(markerFile, Console.Error", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_problem_of_a_measure_run_goes_to_stderr_as_well()
    {
        Assert.True(Program.IsMeasureRun(["--measure"]));
        Assert.True(Program.IsMeasureRun(["--fakes", "--measure", "m.jsonl"]));
        Assert.False(Program.IsMeasureRun([]));
        Assert.False(Program.IsMeasureRun(["--smoke", "s.json"]));

        var program = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));
        Assert.Contains("if (IsMeasureRun(args))", program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_marker_file_is_made_at_once_as_utf8_without_a_bom_with_one_object_per_line()
    {
        using var root = new Ui.TempRoot();
        var path = Path.Combine(root.Path, "marks.jsonl");
        File.WriteAllText(path, "left over from an earlier run\n");

        var marks = MeasureMarks.Create(path);
        Assert.Empty(File.ReadAllBytes(path)); // made (emptied) before any window exists

        marks.Painted(hasRows: true, hasCpuValues: false);
        marks.Painted(hasRows: true, hasCpuValues: true);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        var times = new List<DateTime>();
        foreach (var (line, mark) in lines.Zip(new[] { "first_list", "cpu_list" }))
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal(["mark", "utc"], doc.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(mark, doc.RootElement.GetProperty("mark").GetString());
            var utc = doc.RootElement.GetProperty("utc").GetString()!;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", utc);
            var parsed = DateTime.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
            times.Add(parsed);
        }

        Assert.True(times[0] <= times[1]);

        // Only the marker file is there.
        Assert.Equal([path], root.Entries());
    }

    [Fact]
    public void A_marker_path_that_cannot_be_made_is_an_error_before_anything_is_created()
    {
        using var root = new Ui.TempRoot();
        var path = Path.Combine(root.Path, "no", "such", "folder", "marks.jsonl");
        Assert.ThrowsAny<IOException>(() => MeasureMarks.Create(path));
        Assert.Empty(root.Entries());
    }

    // ---- the default settings, and no file written ----

    private static GuiOptions Options(params string[] args)
    {
        Assert.True(GuiArgs.TryParse(args, out var options, out var problem), problem);
        return options;
    }

    private const string OwnersIni = "language=ja\ntheme=dark\ninterval=5000\nalwaysontop=1\ntextsize=large\nnet.meter.autostart=1\nwin.w=900\nwin.h=600\nproc.view=flat\nproc.sort=Memory\nproc.cols=Name,Memory\n";

    [Fact]
    public void A_measure_run_has_the_default_settings_whatever_the_owner_chose_and_saves_nothing()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, OwnersIni);
        var bytes = File.ReadAllBytes(ini);
        var written = File.GetLastWriteTimeUtc(ini);

        // (A plain --fakes run is not "normal" any more: it is in memory too, see UpdateIniTests. The normal start here is no option at all.)
        var normal = GuiSettings.ForRun(Options(), ini);
        Assert.Equal(ThemeMode.Dark, normal.Theme); // the file is read in a normal start: what follows is not a vacuous result

        var measure = GuiSettings.ForRun(Options("--measure", "m.jsonl", "--fakes"), ini);
        var defaults = new GuiSettings();
        Assert.Equal(defaults.ToLines(), measure.ToLines());
        Assert.Equal(defaults.Theme, measure.Theme);
        Assert.Equal(defaults.IntervalMs, measure.IntervalMs);
        Assert.Equal(defaults.TextSize, measure.TextSize);
        Assert.False(measure.AlwaysOnTop);
        Assert.False(measure.NetMeterAutostart);
        Assert.Null(measure.GetValue("win.w")); // the window size falls back to the default (MainForm.StartSize)
        Assert.Null(measure.GetValue("proc.cols"));

        // Saving, now or on close, writes nothing: not the owner's file, not any other.
        measure.SetInt("win.w", 1);
        measure.Save();
        Assert.Equal(bytes, File.ReadAllBytes(ini));
        Assert.Equal(written, File.GetLastWriteTimeUtc(ini));
        Assert.Equal([ini], root.Entries());

        // A start with no gui.ini at all leaves none behind either.
        var missing = Path.Combine(root.Path, "elsewhere", "gui.ini");
        GuiSettings.ForRun(Options("--measure", "m.jsonl"), missing).Save();
        Assert.False(File.Exists(missing));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public void Closing_the_window_of_a_measure_run_writes_no_gui_ini_and_leaves_the_owners_alone()
    {
        Ui.Run(queue =>
        {
            using var locale = Harness.Locale();
            using var settingsRoot = new Ui.TempRoot();
            using var markRoot = new Ui.TempRoot();
            var ini = Path.Combine(settingsRoot.Path, "gui.ini");
            File.WriteAllText(ini, OwnersIni);
            var bytes = File.ReadAllBytes(ini);
            var written = File.GetLastWriteTimeUtc(ini);
            var marker = Path.Combine(markRoot.Path, "marks.jsonl");

            var options = Options("--measure", marker, "--fakes");
            var settings = GuiSettings.ForRun(options, ini);
            using var theme = new ThemeService(settings.Theme);
            using var services = PcMieruServices.Create(options.CoreOptions);
            using var pump = new SnapshotPump(services, settings.IntervalMs);
            var marks = MeasureMarks.Create(marker);
            var context = new GuiContext(services, pump, theme, settings, measure: marks);
            using var form = new MainForm(context);

            // Where the measurement starts: the process page.
            Assert.True(form.ShowPage(options.StartPage!));
            Assert.Equal("processes", form.CurrentPage?.Id);
            _ = form.Handle;
            form.Close();

            Assert.True(form.IsDisposed);
            Assert.Equal(bytes, File.ReadAllBytes(ini));
            Assert.Equal(written, File.GetLastWriteTimeUtc(ini));
            Assert.Equal([ini], settingsRoot.Entries());
            Assert.Equal([marker], markRoot.Entries());
        });
    }

    [Fact]
    public void A_normal_start_saves_its_settings_on_close_so_the_test_above_can_fail()
    {
        // The same close with the non-measure settings writes the file: that is what makes "wrote nothing" above mean something.
        Ui.Run(queue =>
        {
            using var locale = Harness.Locale();
            using var settingsRoot = new Ui.TempRoot();
            var ini = Path.Combine(settingsRoot.Path, "gui.ini");
            // The settings are those of a normal start (no option: the file is read and written); the data are fakes, so nothing of this PC is read.
            var settings = GuiSettings.ForRun(Options(), ini);
            using var theme = new ThemeService(settings.Theme);
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            using var pump = new SnapshotPump(services, settings.IntervalMs);
            var context = new GuiContext(services, pump, theme, settings);
            using var form = new MainForm(context);
            _ = form.Handle;
            Assert.Null(context.Measure);
            form.Close();

            Assert.True(File.Exists(ini));
        });
    }

    // ---- the paint ----

    /// <summary>A window that never takes the focus, for the paint test.</summary>
    private sealed class QuietHost : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>
    /// The process page in a window that is on show at (-24000, -24000), as the smoke run does it (never in front of the owner, never in the task bar), on
    /// fake data, with no pump: the test hands the page one sample at a time, so what has been painted when is decided by the test, not by a timer.
    /// </summary>
    private sealed class PaintRig : IDisposable
    {
        private readonly IDisposable _locale = Harness.Locale();
        private readonly ThemeService _theme = new(ThemeMode.Light);
        private readonly QuietHost _host;

        public PaintRig(bool measure)
        {
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Marks = measure ? new MeasureMarks(Lines.Add, NextTime) : null;
            Context = new GuiContext(Services, Pump, _theme, new GuiSettings(), measure: Marks);
            Page = new ProcessesPage(Context);
            _host = new QuietHost
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-24000, -24000),
                FormBorderStyle = FormBorderStyle.None,
            };
            _ = _host.Handle;
            _host.ClientSize = new Size(_host.LogicalToDeviceUnits(1280), _host.LogicalToDeviceUnits(800));
            _host.Controls.Add(Page);
            _theme.Apply(_host);
            _host.Show();
            Application.DoEvents();
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public MeasureMarks? Marks { get; }

        public GuiContext Context { get; }

        public ProcessesPage Page { get; }

        public List<string> Lines { get; } = [];

        private DateTime _now = T0;

        private DateTime NextTime()
        {
            _now = _now.AddTicks(10_000);
            return _now;
        }

        /// <summary>The page takes one sample (tick 0 has no CPU values, from tick 1 on it has them) and the list is asked to paint until it has painted.</summary>
        public void Sample(int tick)
        {
            Page.OnSampled(FakeSnapshots.Create(T0, tick));
            PaintList();
        }

        public void PaintList()
        {
            // A window that is entirely off the screen has no visible region, so Windows never sends it a WM_PAINT of its own. The test therefore sends the
            // list the WM_PAINT itself, with a memory device context in wParam (the common list view paints into the context it is given): the message goes
            // through GridView.WndProc exactly as a real paint does, and the native list runs its owner-draw pass into that context. Invalidate, Update and
            // the message loop stay in front of it, so a window that does have a visible region (a test run on a desk that shows it) paints for real too.
            Page.List.Invalidate();
            Page.List.Update();
            Application.DoEvents();
            using var bitmap = new Bitmap(Page.List.ClientSize.Width, Page.List.ClientSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                SendMessage(Page.List.Handle, WmPaint, hdc, IntPtr.Zero);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        private const int WmPaint = 0x000F;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public void Dispose()
        {
            _host.Dispose();
            Page.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
        }
    }

    [Fact]
    public void The_first_paint_with_rows_is_marked_first_list_and_the_first_with_cpu_values_cpu_list_once_each()
    {
        Ui.Run(queue =>
        {
            using var rig = new PaintRig(measure: true);

            // Nothing is painted before the first sample: no row, no mark.
            rig.PaintList();
            Assert.Empty(rig.Lines);

            // First sample: rows, but no CPU value yet (no baseline). A paint of that is first_list and nothing else.
            rig.Sample(0);
            Assert.True(rig.Page.VisibleRowCount > 0);
            Assert.Equal([MeasureMarks.FirstList], rig.Marks!.Written);
            Assert.Single(rig.Lines);
            Assert.False(rig.Marks.Complete);
            Assert.NotNull(rig.Page.List.PaintObserver);
            Assert.True(rig.Page.List.RecordsPaintedCells); // the same probe as in the without-measure test, here true: it can tell the two apart

            // Painting again with the same data adds nothing.
            rig.PaintList();
            Assert.Equal([MeasureMarks.FirstList], rig.Marks.Written);

            // Second sample: CPU values. cpu_list now, after first_list.
            rig.Sample(1);
            Assert.Equal([MeasureMarks.FirstList, MeasureMarks.CpuList], rig.Marks.Written);
            Assert.Equal(2, rig.Lines.Count);
            Assert.Contains("\"first_list\"", rig.Lines[0], StringComparison.Ordinal);
            Assert.Contains("\"cpu_list\"", rig.Lines[1], StringComparison.Ordinal);
            Assert.True(rig.Marks.Complete);

            // Later samples and paints write nothing more, and the list stops watching.
            rig.Sample(2);
            rig.Sample(3);
            Assert.Equal(2, rig.Lines.Count);
            Assert.Null(rig.Page.List.PaintObserver);
            Assert.False(rig.Page.List.RecordsPaintedCells); // detached: what was recorded is dropped
        });
    }

    [Fact]
    public void A_list_that_never_shows_cpu_values_stops_being_watched_after_thirty_samples_and_cpu_list_stays_missing()
    {
        Ui.Run(queue =>
        {
            using var rig = new PaintRig(measure: true);

            // Tick 0 again and again: rows, never a CPU value. The first paint is first_list; the watch counts the samples that come after it.
            rig.Sample(0);
            Assert.Equal([MeasureMarks.FirstList], rig.Marks!.Written);
            for (var i = 1; i < MeasureMarks.WatchSamples; i++)
            {
                rig.Sample(0);
                Assert.NotNull(rig.Page.List.PaintObserver);
                Assert.False(rig.Marks.Expired);
            }

            // The 30th sample after the first paint ends the watch (the 31st sample of the run).
            rig.Sample(0);
            Assert.True(rig.Marks.Expired);
            Assert.True(rig.Marks.Done);
            Assert.Null(rig.Page.List.PaintObserver);
            Assert.False(rig.Page.List.RecordsPaintedCells);

            // A CPU value after that is not marked: the mark stays missing, and nothing is recorded or written any more.
            rig.Sample(1);
            rig.Sample(2);
            Assert.Equal([MeasureMarks.FirstList], rig.Marks.Written);
            Assert.Single(rig.Lines);
            Assert.False(rig.Page.List.RecordsPaintedCells);
        });
    }

    [Fact]
    public void The_marks_are_taken_at_the_paint_not_when_the_data_arrives()
    {
        Ui.Run(queue =>
        {
            using var rig = new PaintRig(measure: true);

            // The data is in the model and handed to the list, but no paint has run yet when OnSampled returns: nothing is marked until it paints.
            rig.Page.OnSampled(FakeSnapshots.Create(T0, 0));
            Assert.True(rig.Page.Model.Count > 0);
            Assert.Empty(rig.Lines);

            rig.PaintList();
            Assert.Equal([MeasureMarks.FirstList], rig.Marks!.Written);
        });
    }

    [Fact]
    public void Without_measure_the_list_watches_nothing_and_no_file_is_touched()
    {
        Ui.Run(queue =>
        {
            using var rig = new PaintRig(measure: false);

            Assert.Null(rig.Context.Measure);
            Assert.Null(rig.Page.List.PaintObserver);
            Assert.False(rig.Page.List.RecordsPaintedCells);
            rig.Sample(0);
            rig.Sample(1);
            rig.Sample(2);
            Assert.True(rig.Page.VisibleRowCount > 0);

            // Nothing is watched after real paints of real rows: the observer is not wired, and the grid never built the drawn-cell list (a second
            // guard: a grid that recorded cells for nobody would fail here even with the observer left null).
            Assert.Null(rig.Page.List.PaintObserver);
            Assert.False(rig.Page.List.RecordsPaintedCells);
            Assert.Empty(rig.Lines);

            // The page's own hook does nothing either, even when asked directly.
            rig.Page.NotePainted([(0, 0)]);
            Assert.Empty(rig.Lines);
        });
    }

    [Fact]
    public void A_paint_of_group_headers_only_is_not_a_paint_of_rows_and_a_hidden_cpu_column_is_not_cpu_values()
    {
        Ui.Run(queue =>
        {
            using var rig = new PaintRig(measure: true);
            rig.Page.OnSampled(FakeSnapshots.Create(T0, 1));
            var rows = Enumerable.Range(0, rig.Page.Model.Count).Where(i => rig.Page.Model[i].Process is not null).ToArray();
            Assert.NotEmpty(rows);
            var cpu = rig.Page.VisibleColumns.ToList().IndexOf(ProcessColumn.Cpu);
            Assert.True(cpu >= 0, "CPU is one of the default columns");

            // Cells of rows that do not exist (a stale paint after the list got shorter) are nothing.
            rig.Page.NotePainted([(rig.Page.Model.Count + 5, 0), (-1, 0)]);
            Assert.Empty(rig.Lines);

            // A name cell of a process row is a row; it is not CPU.
            rig.Page.NotePainted([(rows[0], 0)]);
            Assert.Equal([MeasureMarks.FirstList], rig.Marks!.Written);

            // A CPU cell with a value is CPU.
            rig.Page.NotePainted([(rows[0], cpu)]);
            Assert.Equal([MeasureMarks.FirstList, MeasureMarks.CpuList], rig.Marks.Written);
        });
    }

    // ---- tools/measure.ps1 reads the checks page's title from the navigation list; the script's copy of it is pinned here ----

    [Fact]
    public void The_titles_tools_measure_ps1_looks_for_in_the_navigation_list_are_the_checks_pages_ja_and_en_titles()
    {
        var entry = GuiStrings.All.Single(e => e.Key == "gui.nav.checks");
        var script = File.ReadAllText(Path.Combine(Harness.Root, "tools", "measure.ps1"));
        var line = script.Split('\n').Single(l => l.StartsWith("$script:ChecksTitles", StringComparison.Ordinal));

        Assert.Contains("'" + entry.En + "'", line, StringComparison.Ordinal);
        foreach (var c in entry.Ja)
        {
            Assert.Contains("[char]0x" + ((int)c).ToString("X4", CultureInfo.InvariantCulture), line, StringComparison.Ordinal);
        }

        // The script is ASCII only (Windows PowerShell 5.1 reads a file without a BOM as ANSI), so the Japanese title is written as char codes.
        Assert.True(File.ReadAllBytes(Path.Combine(Harness.Root, "tools", "measure.ps1")).All(b => b < 128), "tools/measure.ps1 must stay ASCII only");
    }
}
