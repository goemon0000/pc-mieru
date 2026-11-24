using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// <c>--measure-record</c> (DESIGN-B 11, step 8): the measured run that records itself for <c>tools/measure.ps1 -Mode record</c>. The switches and what
/// they refuse (so a measurement can never write into the user's own folders), the settings the run never reads or writes, and the driver that starts
/// the recording, stops it the way the Stop button does and leaves its facts in one JSON object per line. Nothing here waits for the clock: the driver
/// is handed a clock and a delay the test moves by hand, and the recorder is the fake of <c>--fakes</c> (it writes no file).
/// </summary>
public class MeasureRecordTests
{
    private static readonly DateTime T0 = new DateTime(2026, 11, 22, 3, 4, 5, DateTimeKind.Utc).AddTicks(1_234_567);

    /// <summary>A full path that is made by nobody, outside the user's own folders (the system temp folder).</summary>
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "pcm-record-plan-test", "runs");

    private static Dictionary<string, string> Given(params string[] pairs)
    {
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            given.Add(pairs[i], pairs[i + 1]);
        }

        return given;
    }

    private static string Refused(params string[] args)
    {
        Assert.False(GuiArgs.TryParse(args, out _, out var problem), "this command line must be refused: " + string.Join(' ', args));
        Assert.NotEqual(string.Empty, problem);
        return problem;
    }

    // ---- the command line ----

    [Fact]
    public void Without_the_switches_there_is_no_plan_and_the_ordinary_start_is_untouched()
    {
        Assert.True(GuiArgs.TryParse(["--measure", "m.jsonl"], out var measure, out var problem), problem);
        Assert.Null(measure.MeasureRecord);
        Assert.True(GuiArgs.TryParse([], out var normal, out _));
        Assert.Null(normal.MeasureRecord);

        Assert.True(MeasureRecordPlan.TryRead(Given(), measureRun: false, out var plan, out problem));
        Assert.Null(plan);
        Assert.Equal(string.Empty, problem);
    }

    [Fact]
    public void The_folder_alone_is_a_plan_with_the_documented_defaults()
    {
        Assert.True(GuiArgs.TryParse(["--measure", "m.jsonl", "--measure-record", Folder], out var o, out var problem), problem);
        Assert.Equal(new MeasureRecordPlan(Path.GetFullPath(Folder), 60, 180, 0), o.MeasureRecord);
        Assert.Equal(60, MeasureRecordPlan.DefaultLeadSeconds);
        Assert.Equal(180, MeasureRecordPlan.DefaultRecordSeconds);

        // --measure still owns the start page and the state files: the new switches add a recording and take nothing away.
        Assert.Equal("processes", o.StartPage);
        Assert.True(o.CoreOptions.NoStateFiles);
    }

    [Fact]
    public void Every_switch_is_read_and_the_order_on_the_line_does_not_matter()
    {
        Assert.True(GuiArgs.TryParse(["--measure-record-interval", "2000", "--measure-record-for", "20", "--measure", "m.jsonl", "--measure-record-lead", "0", "--measure-record", Folder, "--fakes"], out var o, out var problem), problem);
        Assert.Equal(new MeasureRecordPlan(Path.GetFullPath(Folder), 0, 20, 2000), o.MeasureRecord);
        Assert.True(o.ForceFakes);

        // A folder with dots in it is read as the folder it names.
        Assert.True(GuiArgs.TryParse(["--measure", "m.jsonl", "--measure-record", Path.Combine(Path.GetTempPath(), "a", "..", "b")], out var dots, out problem), problem);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "b")), dots.MeasureRecord!.Folder);
    }

    [Fact]
    public void A_record_switch_without_measure_is_refused_and_says_which()
    {
        Assert.Contains("--measure-record is only for a --measure run", Refused("--measure-record", Folder), StringComparison.Ordinal);
        Assert.Contains("--measure-record-for is only for a --measure run", Refused("--measure-record-for", "30"), StringComparison.Ordinal);
        Assert.Contains("--measure-record-interval is only for a --measure run", Refused("--fakes", "--measure-record-interval", "2000"), StringComparison.Ordinal);

        // The smoke run is not a measured run either: --measure is what carries the promise to leave the user's settings alone.
        Assert.Contains("only for a --measure run", Refused("--smoke", "s.json", "--measure-record", Folder), StringComparison.Ordinal);
    }

    [Fact]
    public void The_other_switches_need_the_folder_to_mean_anything()
    {
        Assert.Contains("--measure-record-for needs --measure-record <folder>", Refused("--measure", "m.jsonl", "--measure-record-for", "30"), StringComparison.Ordinal);
        Assert.Contains("--measure-record-lead needs --measure-record <folder>", Refused("--measure", "m.jsonl", "--measure-record-lead", "5"), StringComparison.Ordinal);
        Assert.Contains("--measure-record-interval needs --measure-record <folder>", Refused("--measure", "m.jsonl", "--measure-record-interval", "500"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_switch_needs_a_value_and_may_be_given_once()
    {
        Assert.Contains("--measure-record needs a value", Refused("--measure", "m.jsonl", "--measure-record"), StringComparison.Ordinal);
        Assert.Contains("--measure-record needs a value", Refused("--measure", "m.jsonl", "--measure-record", string.Empty), StringComparison.Ordinal);
        Assert.Contains("--measure-record-for needs a value", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record-for"), StringComparison.Ordinal);
        Assert.Contains("--measure-record is given twice", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record", Folder), StringComparison.Ordinal);
        Assert.Contains("--measure-record-for is given twice", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record-for", "9", "--measure-record-for", "10"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runs")]
    [InlineData(@".\runs")]
    [InlineData(@"..\runs")]
    [InlineData(@"\runs")]
    [InlineData(@"C:runs")]
    public void The_folder_must_be_a_full_path_with_a_drive_so_it_cannot_land_beside_the_program(string relative)
    {
        Assert.Contains("--measure-record needs a full path", Refused("--measure", "m.jsonl", "--measure-record", relative), StringComparison.Ordinal);
    }

    [Fact]
    public void The_users_own_folders_are_refused_as_a_recording_folder_so_a_measurement_never_writes_there()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.False(string.IsNullOrEmpty(documents));
        Assert.False(string.IsNullOrEmpty(roaming));

        // The real list is the one the shipped command line uses: each of the three, the folder itself and anything under it.
        foreach (var root in new[] { documents, local, roaming })
        {
            foreach (var inside in new[] { root, Path.Combine(root, "PCMieru", "runs"), Path.Combine(root, "x") + Path.DirectorySeparatorChar, root.ToUpperInvariant(), Path.Combine(root, "a", "..", "b") })
            {
                var problem = Refused("--measure", "m.jsonl", "--measure-record", inside);
                Assert.Contains("--measure-record must be outside the user's own folders", problem, StringComparison.Ordinal);
            }
        }

        // The legacy root of the old app (Documents\PartsLife\runs) is under Documents too.
        Assert.Contains("must be outside the user's own folders", Refused("--measure", "m.jsonl", "--measure-record", Path.Combine(documents, "PartsLife", "runs")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_neighbour_whose_name_only_starts_the_same_is_not_inside()
    {
        using var temp = new Ui.TempRoot();
        var root = Path.Combine(temp.Path, "Documents");
        string[] roots = [root];

        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", root + "2"), true, roots, out var plan, out var problem), problem);
        Assert.Equal(root + "2", plan!.Folder);
        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", Path.Combine(temp.Path, "Other", "Documents")), true, roots, out _, out problem), problem);
        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", Path.Combine(root, "..", "Documents2")), true, roots, out _, out problem), problem);

        Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", root), true, roots, out plan, out problem));
        Assert.Null(plan);
        Assert.Contains(root, problem, StringComparison.Ordinal);
        Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", Path.Combine(root, "runs")), true, roots, out _, out _));
        Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", Path.Combine(temp.Path, "DOCUMENTS", "runs")), true, roots, out _, out _));
        Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", root + Path.DirectorySeparatorChar), true, roots, out _, out _));
        Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", Path.Combine(root, "..", "Documents", "x")), true, roots, out _, out _));

        // A root that is not given (an empty special folder) refuses nothing.
        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", root), true, [string.Empty, "  "], out _, out problem), problem);
    }

    [Theory]
    [InlineData("--measure-record-lead", "0", true)]
    [InlineData("--measure-record-lead", "3600", true)]
    [InlineData("--measure-record-lead", "3601", false)]
    [InlineData("--measure-record-lead", "-1", false)]
    [InlineData("--measure-record-lead", "1.5", false)]
    [InlineData("--measure-record-lead", "abc", false)]
    [InlineData("--measure-record-lead", " 5", false)]
    [InlineData("--measure-record-for", "5", true)]
    [InlineData("--measure-record-for", "7200", true)]
    [InlineData("--measure-record-for", "4", false)]
    [InlineData("--measure-record-for", "0", false)]
    [InlineData("--measure-record-for", "7201", false)]
    [InlineData("--measure-record-for", "99999999999", false)]
    [InlineData("--measure-record-interval", "500", true)]
    [InlineData("--measure-record-interval", "1000", true)]
    [InlineData("--measure-record-interval", "2000", true)]
    [InlineData("--measure-record-interval", "5000", true)]
    [InlineData("--measure-record-interval", "0", false)]
    [InlineData("--measure-record-interval", "1500", false)]
    [InlineData("--measure-record-interval", "2000.0", false)]
    [InlineData("--measure-record-interval", "fast", false)]
    public void The_numbers_are_whole_and_inside_their_ranges(string name, string value, bool accepted)
    {
        var args = new[] { "--measure", "m.jsonl", "--measure-record", Folder, name, value };
        if (accepted)
        {
            Assert.True(GuiArgs.TryParse(args, out var o, out var problem), problem);
            var plan = o.MeasureRecord!;
            var number = int.Parse(value, CultureInfo.InvariantCulture);
            Assert.Equal(number, name switch
            {
                MeasureRecordPlan.LeadSwitch => plan.LeadSeconds,
                MeasureRecordPlan.ForSwitch => plan.RecordSeconds,
                _ => plan.UserIntervalMs,
            });
        }
        else
        {
            Assert.Contains(name, Refused(args), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_refusals_of_the_numbers_say_the_range()
    {
        Assert.Contains("a whole number of seconds from 0 to 3600", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record-lead", "x"), StringComparison.Ordinal);
        Assert.Contains("a whole number of seconds from 5 to 7200", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record-for", "1"), StringComparison.Ordinal);
        Assert.Contains("must be one of 500, 1000, 2000, 5000", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record-interval", "3"), StringComparison.Ordinal);
        Assert.Equal(new[] { 500, 1000, 2000, 5000 }, GuiSettings.IntervalChoices);
    }

    [Fact]
    public void The_series_switches_are_a_number_of_recordings_a_gap_and_one_collection_and_default_to_one_recording_thirty_seconds_and_no_collection()
    {
        Assert.True(GuiArgs.TryParse(["--measure", "m.jsonl", "--measure-record", Folder], out var plain, out var problem), problem);
        Assert.Equal(1, plain.MeasureRecord!.Times);
        Assert.Equal(30, plain.MeasureRecord.GapSeconds);
        Assert.Equal(0, plain.MeasureRecord.GcSeconds);

        Assert.True(GuiArgs.TryParse(["--measure-record-gc", "60", "--measure", "m.jsonl", "--measure-record-times", "4", "--measure-record", Folder, "--measure-record-gap", "45", "--measure-record-for", "60"], out var o, out problem), problem);
        Assert.Equal(new MeasureRecordPlan(Path.GetFullPath(Folder), 60, 60, 0, 4, 45, 60), o.MeasureRecord);

        // Like the others they are only for a measured run, and mean nothing without the folder.
        Assert.Contains("--measure-record-times is only for a --measure run", Refused("--measure-record-times", "4"), StringComparison.Ordinal);
        Assert.Contains("--measure-record-gc needs --measure-record <folder>", Refused("--measure", "m.jsonl", "--measure-record-gc", "60"), StringComparison.Ordinal);
        Assert.Contains("--measure-record-gap needs a value", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record-gap"), StringComparison.Ordinal);
        Assert.Contains("--measure-record-times is given twice", Refused("--measure", "m.jsonl", "--measure-record", Folder, "--measure-record-times", "2", "--measure-record-times", "3"), StringComparison.Ordinal);
        Assert.True(Program.IsMeasureRun(["--measure-record-times", "4"]));
        Assert.True(Program.IsMeasureRun(["--measure-record-gc"]));
    }

    [Theory]
    [InlineData("--measure-record-times", "1", true)]
    [InlineData("--measure-record-times", "20", true)]
    [InlineData("--measure-record-times", "0", false)]
    [InlineData("--measure-record-times", "21", false)]
    [InlineData("--measure-record-times", "two", false)]
    [InlineData("--measure-record-gap", "0", true)]
    [InlineData("--measure-record-gap", "3600", true)]
    [InlineData("--measure-record-gap", "3601", false)]
    [InlineData("--measure-record-gap", "-1", false)]
    [InlineData("--measure-record-gc", "1", true)]
    [InlineData("--measure-record-gc", "3600", true)]
    [InlineData("--measure-record-gc", "0", false)]
    [InlineData("--measure-record-gc", "3601", false)]
    [InlineData("--measure-record-gc", "1.5", false)]
    public void The_series_numbers_are_whole_and_inside_their_ranges(string name, string value, bool accepted)
    {
        var args = new[] { "--measure", "m.jsonl", "--measure-record", Folder, name, value };
        if (accepted)
        {
            Assert.True(GuiArgs.TryParse(args, out var o, out var problem), problem);
            Assert.Equal(int.Parse(value, CultureInfo.InvariantCulture), name switch
            {
                MeasureRecordPlan.TimesSwitch => o.MeasureRecord!.Times,
                MeasureRecordPlan.GapSwitch => o.MeasureRecord!.GapSeconds,
                _ => o.MeasureRecord!.GcSeconds,
            });
        }
        else
        {
            var problem = Refused(args);
            Assert.Contains(name, problem, StringComparison.Ordinal);
            Assert.Contains(name == MeasureRecordPlan.TimesSwitch ? "recordings" : "seconds", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_command_line_problem_of_a_record_switch_goes_to_stderr_as_well()
    {
        Assert.True(Program.IsMeasureRun(["--measure-record", "x"]));
        Assert.True(Program.IsMeasureRun(["--measure-record-interval", "500"]));
        Assert.True(Program.IsMeasureRun(["--measure-record-for"]));
        Assert.True(Program.IsMeasureRun(["--measure"]));
        Assert.False(Program.IsMeasureRun([]));
        Assert.False(Program.IsMeasureRun(["--smoke", "s.json"]));
        Assert.False(Program.IsMeasureRun(["--fakes", "--lang", "ja"]));
    }

    // ---- no settings read or written ----

    private const string OwnersIni = "language=ja\ntheme=dark\ninterval=5000\nalwaysontop=1\ntextsize=large\nnet.meter.autostart=1\nwin.w=900\nwin.h=600\nproc.view=flat\nproc.sort=Memory\nproc.cols=Name,Memory\n";

    private static GuiOptions Options(params string[] args)
    {
        Assert.True(GuiArgs.TryParse(args, out var options, out var problem), problem);
        return options;
    }

    [Fact]
    public void The_user_interval_of_the_run_is_set_in_memory_and_the_owners_settings_are_neither_read_nor_written()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, OwnersIni);
        var bytes = File.ReadAllBytes(ini);
        var written = File.GetLastWriteTimeUtc(ini);

        var options = Options("--measure", "m.jsonl", "--fakes", "--measure-record", Folder, "--measure-record-interval", "2000");
        var settings = GuiSettings.ForRun(options, ini);
        var defaults = new GuiSettings();
        Assert.Equal(defaults.ToLines(), settings.ToLines()); // the owner's 5000 and dark theme are not read
        Assert.Equal(defaults.IntervalMs, settings.IntervalMs);

        options.MeasureRecord!.ApplyTo(settings);
        Assert.Equal(2000, settings.IntervalMs);
        Assert.Equal(defaults.Theme, settings.Theme);

        // Saving, now or on close, writes nothing: not the owner's file, not any other, not the folder of the recording.
        settings.Save();
        Assert.Equal(bytes, File.ReadAllBytes(ini));
        Assert.Equal(written, File.GetLastWriteTimeUtc(ini));
        Assert.Equal([ini], root.Entries());
        Assert.False(Directory.Exists(options.MeasureRecord.Folder));

        // No interval on the line: nothing changes.
        var plain = GuiSettings.ForRun(Options("--measure", "m.jsonl", "--measure-record", Folder), ini);
        Options("--measure", "m.jsonl", "--measure-record", Folder).MeasureRecord!.ApplyTo(plain);
        Assert.Equal(defaults.IntervalMs, plain.IntervalMs);
    }

    [Fact]
    public void Program_applies_the_interval_before_the_pump_is_built_from_it_and_attaches_after_the_window_exists()
    {
        var program = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));
        var apply = program.IndexOf("options.MeasureRecord?.ApplyTo(settings);", StringComparison.Ordinal);
        var pump = program.IndexOf("new SnapshotPump(services, settings.IntervalMs)", StringComparison.Ordinal);
        var hub = program.IndexOf("recording: MeasureRecording.CreateHub(services, options.MeasureRecord)", StringComparison.Ordinal);
        var window = program.IndexOf("new MainForm(context)", StringComparison.Ordinal);
        var attach = program.IndexOf("MeasureRecording.TryAttach(recordPlan, options.MeasureOutput!, context, form, Console.Error", StringComparison.Ordinal);
        var page = program.IndexOf("form.ShowPage(page)", StringComparison.Ordinal);
        var run = program.IndexOf("Application.Run(form)", StringComparison.Ordinal);

        Assert.True(apply > 0 && pump > apply, "the interval of the run must be in the settings before the pump takes it");
        Assert.True(hub > pump && window > hub, "the window's hub is the one for the plan's folder");
        Assert.True(attach > window && run > attach && page > attach, "the driver hangs on the window after it is made and before it runs");

        // A failure to make the events file ends the run on stderr at once, like the marker file (no message box in an unattended run).
        var fail = program.IndexOf("return 1;", attach, StringComparison.Ordinal);
        Assert.InRange(fail - attach, 1, 200);
        Assert.DoesNotContain("MessageBox", program.Substring(attach, fail - attach), StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_the_window_of_a_record_run_writes_no_gui_ini_and_makes_only_the_marker_and_events_files()
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
            var recordFolder = Path.Combine(markRoot.Path, "recordings");

            var options = Options("--measure", marker, "--fakes", "--measure-record", recordFolder, "--measure-record-interval", "2000", "--measure-record-lead", "3600");
            var plan = options.MeasureRecord!;
            var settings = GuiSettings.ForRun(options, ini);
            plan.ApplyTo(settings);
            using var theme = new ThemeService(settings.Theme);
            using var services = PcMieruServices.Create(options.CoreOptions);
            using var pump = new SnapshotPump(services, settings.IntervalMs);
            Assert.Equal(2000, pump.IntervalMs);
            var marks = MeasureMarks.Create(marker);
            var hub = MeasureRecording.CreateHub(services, plan);
            Assert.NotNull(hub);
            var context = new GuiContext(services, pump, theme, settings, measure: marks, recording: hub);
            using var form = new MainForm(context);

            Assert.True(MeasureRecording.TryAttach(plan, options.MeasureOutput!, context, form, TextWriter.Null, out var recording));
            Assert.NotNull(recording);
            var events = MeasureRecording.EventsPathFor(marker);
            Assert.Equal(Path.Combine(markRoot.Path, "marks.record.jsonl"), events);

            Assert.True(form.ShowPage(options.StartPage!));
            _ = form.Handle;
            form.Close();
            Assert.True(form.IsDisposed);

            // The window is gone: the driver is too (a begin after that is nothing), and it never started a recording.
            recording!.Begin();
            Assert.Empty(ReadShared(events));
            Assert.Equal(RecordingPhase.Idle, hub!.Phase);

            Assert.Equal(bytes, File.ReadAllBytes(ini));
            Assert.Equal(written, File.GetLastWriteTimeUtc(ini));
            Assert.Equal([ini], settingsRoot.Entries());
            Assert.Equal([marker, events], markRoot.Entries().OrderBy(p => p, StringComparer.Ordinal).ToArray());
            Assert.False(Directory.Exists(recordFolder));
        });
    }

    // ---- the recorder the run's window has ----

    [Fact]
    public void The_hub_of_a_record_run_writes_into_the_plans_folder_and_making_it_creates_nothing()
    {
        Ui.Run(queue =>
        {
            using var locale = Harness.Locale();
            using var temp = new Ui.TempRoot();
            var folder = Path.Combine(temp.Path, "recordings");
            var plan = new MeasureRecordPlan(folder, 60, 180, 0);
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });

            Assert.Null(MeasureRecording.CreateHub(services, null));

            using var real = MeasureRecording.CreateHub(services, plan, fake: false)!;
            Assert.Equal(folder, real.Folder);
            Assert.Equal(RecordingPhase.Idle, real.Phase);
            Assert.False(real.StoreCreated);
            Assert.Empty(temp.Entries());

            // With --fakes the recorder is the in-memory one: whatever the folder is, the disk is not asked.
            using var fake = MeasureRecording.CreateHub(services, plan)!;
            Assert.Equal(RecordingPhase.Idle, fake.Phase);
            Assert.Empty(temp.Entries());
        });
    }

    // ---- the driver ----

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale = Harness.Locale();

        public Rig(int leadSeconds = 7, int recordSeconds = 11, int userIntervalMs = 1000, int times = 1, int gapSeconds = 30, int gcSeconds = 0)
        {
            Temp = new Ui.TempRoot();
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services, userIntervalMs);
            Kit = new FakeRecordingKit(Path.Combine(Temp.Path, "runs"), () => Now);
            Hub = Kit.CreateHub();
            Plan = new MeasureRecordPlan(Path.Combine(Temp.Path, "runs"), leadSeconds, recordSeconds, 0, times, gapSeconds, gcSeconds);
            Recording = new MeasureRecording(Plan, Hub, Pump, Lines.Add, () => Now, Delay, () => Memory, Compact, () => Roots());
        }

        /// <summary>The user's own folders the driver asks the guard about before each recording (none, unless a test names some).</summary>
        public Func<IEnumerable<string>> Roots { get; set; } = () => [];

        public Ui.TempRoot Temp { get; }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public FakeRecordingKit Kit { get; }

        public RecordingHub Hub { get; }

        public MeasureRecordPlan Plan { get; }

        public MeasureRecording Recording { get; }

        public DateTime Now { get; set; } = T0;

        /// <summary>What the process's memory reads (private bytes, managed heap); one compacting collection takes 10 MB and 8 MB off it.</summary>
        public MeasureRecording.MemoryReading Memory { get; set; } = new(200_000_000, 60_000_000);

        public int Compactions { get; private set; }

        public List<string> Lines { get; } = [];

        /// <summary>The waits the driver asked for, in order, each held until the test lets it go.</summary>
        public List<(TimeSpan Span, TaskCompletionSource Gate)> Asked { get; } = [];

        public string[] Events() => [.. Lines.Select(l => Json(l).GetProperty("event").GetString()!)];

        public JsonElement Line(int index) => Json(Lines[index]);

        public JsonElement Last(string name) => Json(Lines.Last(l => Json(l).GetProperty("event").GetString() == name));

        private void Compact()
        {
            Compactions++;
            Memory = new MeasureRecording.MemoryReading(Memory.PrivateBytes - 10_000_000, Memory.ManagedBytes - 8_000_000);
        }

        private Task Delay(TimeSpan span, CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = token.Register(() => gate.TrySetCanceled(token));
            Asked.Add((span, gate));
            return gate.Task;
        }

        public void Dispose()
        {
            Recording.Dispose();
            Kit.Backend.ReleaseStop();
            Hub.Dispose();
            Pump.Dispose();
            Services.Dispose();
            Temp.Dispose();
            _locale.Dispose();
        }
    }

    private static JsonElement Json(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_driver_begins_waits_the_lead_starts_waits_stops_the_way_the_button_does_and_says_each_step_once()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 7, recordSeconds: 11);
            rig.Kit.Backend.HangStop = true;

            rig.Recording.Begin();
            Assert.Equal(["begin"], rig.Events());
            var begin = rig.Line(0);
            Assert.Equal(rig.Plan.Folder, begin.GetProperty("folder").GetString());
            Assert.Equal(7, begin.GetProperty("leadSeconds").GetInt32());
            Assert.Equal(11, begin.GetProperty("recordSeconds").GetInt32());
            Assert.Equal(1000, begin.GetProperty("userIntervalMs").GetInt32());
            Assert.Equal(1000, begin.GetProperty("effectiveIntervalMs").GetInt32());
            Assert.Equal(T0.ToString("O", CultureInfo.InvariantCulture), begin.GetProperty("utc").GetString());
            Assert.Equal(MeasureRecording.PhaseBefore, rig.Recording.Phase);

            // Nothing starts before the lead is over.
            Assert.Single(rig.Asked);
            Assert.Equal(TimeSpan.FromSeconds(7), rig.Asked[0].Span);
            ui.Pump();
            Assert.Equal(0, rig.Kit.Backend.StartCalls);
            Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);

            // A second begin does nothing.
            rig.Recording.Begin();
            Assert.Single(rig.Lines);
            Assert.Single(rig.Asked);

            // The gap Core measured between snapshots before the recording: one line per snapshot, whatever the number (it jitters around the period).
            rig.Recording.Observe(2004);
            rig.Recording.Observe(1997);
            Assert.Equal(["begin", "period", "period"], rig.Events());
            var before = rig.Line(1);
            Assert.Equal("before", before.GetProperty("phase").GetString());
            Assert.Equal(2004, before.GetProperty("coreIntervalMs").GetInt32());
            Assert.Equal(1000, before.GetProperty("userIntervalMs").GetInt32());
            Assert.Equal(1000, before.GetProperty("effectiveIntervalMs").GetInt32());
            Assert.Equal(1, before.GetProperty("snapshotsInPhase").GetInt32());
            Assert.Equal(1997, rig.Line(2).GetProperty("coreIntervalMs").GetInt32());
            Assert.Equal(2, rig.Line(2).GetProperty("snapshotsInPhase").GetInt32());

            // The lead is over: the recording is started through the hub, as the Start button does.
            rig.Asked[0].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Asked.Count == 2), "the recording never started");
            Assert.Equal(1, rig.Kit.Backend.StartCalls);
            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);
            Assert.Equal(MeasureRecording.PhaseRecording, rig.Recording.Phase);
            Assert.Equal(TimeSpan.FromSeconds(11), rig.Asked[1].Span);
            var start = rig.Last("start");
            Assert.True(start.GetProperty("ok").GetBoolean());
            Assert.Equal(0, start.GetProperty("startMs").GetInt64());
            Assert.Equal(rig.Hub.Run!.RunId.ToString("D"), start.GetProperty("runId").GetString());
            Assert.Equal(rig.Hub.Run.JsonPath, start.GetProperty("json").GetString());
            Assert.Equal(rig.Hub.Run.CsvPath, start.GetProperty("csv").GetString());

            // While it records the snapshots come every second: each is a line in the new phase, and the count starts again.
            rig.Recording.Observe(1008);
            rig.Recording.Observe(994);
            Assert.Equal(["begin", "period", "period", "start", "period", "period"], rig.Events());
            var during = rig.Line(4);
            Assert.Equal("recording", during.GetProperty("phase").GetString());
            Assert.Equal(1008, during.GetProperty("coreIntervalMs").GetInt32());
            Assert.Equal(1, during.GetProperty("snapshotsInPhase").GetInt32());
            Assert.Equal(994, rig.Line(5).GetProperty("coreIntervalMs").GetInt32());
            Assert.Equal(2, rig.Line(5).GetProperty("snapshotsInPhase").GetInt32());

            // The record time is over: the stop is the user's stop, and the writer's drain is what the stop took.
            rig.Now = T0.AddSeconds(10);
            rig.Asked[1].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Events().Contains("stop")), "the stop never began");
            Assert.Equal(RecordingPhase.Stopping, rig.Hub.Phase);
            Assert.Equal(MeasureRecording.PhaseStopping, rig.Recording.Phase);
            var stop = rig.Last("stop");
            Assert.Equal("user", stop.GetProperty("reason").GetString());
            Assert.False(stop.GetProperty("selfEnded").GetBoolean());
            Assert.DoesNotContain("stopped", rig.Events());

            rig.Now = T0.AddSeconds(10).AddMilliseconds(2500);
            rig.Kit.Backend.ReleaseStop();
            Assert.True(ui.Until(() => rig.Events().Contains("stopped")), "the stop never finished");
            Assert.Equal(1, rig.Kit.Backend.StopCalls);
            Assert.Equal(RunStopReason.User, rig.Kit.Backend.LastStopReason);
            Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);
            Assert.Equal(MeasureRecording.PhaseAfter, rig.Recording.Phase);
            var stopped = rig.Last("stopped");
            var result = rig.Hub.LastResult!;
            Assert.Equal(2500, stopped.GetProperty("stopMs").GetInt64());
            Assert.True(stopped.GetProperty("saved").GetBoolean());
            Assert.Equal(result.Status.ToString(), stopped.GetProperty("status").GetString());
            Assert.Equal("User", stopped.GetProperty("stopReason").GetString());
            Assert.Equal(result.Rows, stopped.GetProperty("rows").GetInt64());
            Assert.Equal(result.DroppedTicks, stopped.GetProperty("droppedTicks").GetInt64());
            Assert.Equal(result.DurationUs, stopped.GetProperty("durationUs").GetInt64());
            Assert.Equal(result.JsonPath, stopped.GetProperty("json").GetString());
            Assert.Equal(result.CsvPath, stopped.GetProperty("csv").GetString());
            Assert.Equal(T0.AddSeconds(10).AddMilliseconds(2500).ToString("O", CultureInfo.InvariantCulture), stopped.GetProperty("utc").GetString());

            // After the stop the gap is whatever Core measures again (the user's, here 2 s): the line carries it and the new phase.
            rig.Recording.Observe(2011);
            Assert.Equal(["begin", "period", "period", "start", "period", "period", "stop", "stopped", "period"], rig.Events());
            var after = rig.Line(8);
            Assert.Equal("after", after.GetProperty("phase").GetString());
            Assert.Equal(2011, after.GetProperty("coreIntervalMs").GetInt32());
            Assert.Equal(1, after.GetProperty("snapshotsInPhase").GetInt32());

            // Every line is one object with the event and a time of 7 digits and a Z (the form the script compares with the marks).
            foreach (var line in rig.Lines)
            {
                Assert.DoesNotContain('\n', line);
                var json = Json(line);
                Assert.Equal(JsonValueKind.Object, json.ValueKind);
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", json.GetProperty("utc").GetString()!);
                Assert.True(json.TryGetProperty("event", out _));
            }

            Assert.Equal(2, rig.Asked.Count); // the lead and the record time, nothing else was waited for
        });
    }

    [Fact]
    public void A_series_records_again_after_each_gap_numbers_its_lines_and_makes_the_one_collection_only_after_the_last_stop()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 7, recordSeconds: 11, times: 3, gapSeconds: 30, gcSeconds: 60);
            rig.Recording.Begin();
            var begin = rig.Line(0);
            Assert.Equal(3, begin.GetProperty("times").GetInt32());
            Assert.Equal(30, begin.GetProperty("gapSeconds").GetInt32());
            Assert.Equal(60, begin.GetProperty("gcSeconds").GetInt32());

            // The lead once, then record / gap / record / gap / record, then the wait for the collection.
            var expected = new[] { 7, 11, 30, 11, 30, 11, 60 };
            for (var step = 0; step < expected.Length; step++)
            {
                Assert.True(ui.Until(() => rig.Asked.Count == step + 1), $"the wait {step + 1} was never asked for");
                Assert.Equal(TimeSpan.FromSeconds(expected[step]), rig.Asked[step].Span);
                Assert.Equal(0, rig.Compactions);
                if (step < expected.Length - 1)
                {
                    rig.Asked[step].Gate.SetResult();
                }
            }

            Assert.Equal(3, rig.Kit.Backend.StartCalls);
            Assert.Equal(3, rig.Kit.Backend.StopCalls);
            Assert.Equal(["start", "stop", "stopped", "start", "stop", "stopped", "start", "stop", "stopped"], rig.Events().Where(e => e is "start" or "stop" or "stopped").ToArray());
            Assert.Equal([1, 1, 1, 2, 2, 2, 3, 3, 3], rig.Lines.Select(Json).Where(j => j.TryGetProperty("run", out _)).Select(j => j.GetProperty("run").GetInt32()).ToArray());
            Assert.DoesNotContain("gc", rig.Events());

            // The collection is one line with what the memory read just before it and just after it.
            rig.Now = T0.AddSeconds(500);
            rig.Asked[^1].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Events().Contains("gc")), "the collection never ran");
            Assert.Equal(1, rig.Compactions);
            var gc = rig.Last("gc");
            Assert.Equal(60, gc.GetProperty("afterSeconds").GetInt32());
            Assert.Equal(200_000_000, gc.GetProperty("privateBefore").GetInt64());
            Assert.Equal(190_000_000, gc.GetProperty("privateAfter").GetInt64());
            Assert.Equal(60_000_000, gc.GetProperty("managedBefore").GetInt64());
            Assert.Equal(52_000_000, gc.GetProperty("managedAfter").GetInt64());
            Assert.Equal(0, gc.GetProperty("collectMs").GetInt64());
            Assert.Equal("gc", rig.Events()[^1]);
            Assert.Equal(7, rig.Asked.Count); // and nothing is waited for after it
        });
    }

    [Fact]
    public void A_series_that_cannot_start_a_recording_ends_there_and_makes_no_collection()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 1, recordSeconds: 5, times: 3, gapSeconds: 2, gcSeconds: 9);
            rig.Kit.Backend.FailStart = RunStartFailure.LowSpace;
            rig.Recording.Begin();
            rig.Asked[0].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Events().Contains("start")), "the start was never tried");
            var start = rig.Last("start");
            Assert.False(start.GetProperty("ok").GetBoolean());
            Assert.Equal(1, start.GetProperty("run").GetInt32());
            ui.Pump();
            Assert.Single(rig.Asked);
            Assert.Equal(0, rig.Compactions);
            Assert.DoesNotContain("gc", rig.Events());
        });
    }

    [Fact]
    public void The_folder_is_asked_of_the_guard_again_before_each_recording_and_one_that_became_a_link_into_a_users_folder_ends_the_series()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 1, recordSeconds: 5, times: 3, gapSeconds: 2);
            var documents = Path.Combine(rig.Temp.Path, "Documents");
            Directory.CreateDirectory(documents);
            rig.Roots = () => [documents];
            rig.Recording.Begin();

            // The first recording: the folder is a plain one (nothing is there yet), so it starts.
            rig.Asked[0].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Asked.Count == 2), "the first recording never started");
            Assert.Equal(1, rig.Kit.Backend.StartCalls);
            rig.Asked[1].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Asked.Count == 3 && rig.Events().Contains("stopped")), "the first recording never ended");

            // In the gap the plan's folder becomes a link into Documents: the second recording must not start (the plan was read long before this).
            var link = Junction(rig.Plan.Folder, documents);
            try
            {
                rig.Asked[2].Gate.SetResult();
                Assert.True(ui.Until(() => rig.Events().Count(e => e == "start") == 2), "the second start was never answered");
                var start = rig.Last("start");
                Assert.False(start.GetProperty("ok").GetBoolean());
                Assert.Equal(2, start.GetProperty("run").GetInt32());
                Assert.Equal("folder", start.GetProperty("problem").GetString());
                Assert.Contains("outside the user's own folders", start.GetProperty("detail").GetString(), StringComparison.Ordinal);
                Assert.Equal(1, rig.Kit.Backend.StartCalls);
                Assert.Empty(Directory.GetFileSystemEntries(documents));

                // The series ends there: no record time, no third recording.
                ui.Pump();
                Assert.Equal(3, rig.Asked.Count);
                Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);
            }
            finally
            {
                Directory.Delete(link);
            }
        });
    }

    [Fact]
    public void Every_snapshot_is_a_period_line_even_when_the_number_repeats_and_the_phase_it_came_in_is_on_it()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 1, recordSeconds: 5);
            rig.Recording.Begin();
            rig.Recording.Observe(1000);
            rig.Recording.Observe(1000);
            rig.Recording.Observe(1000);
            Assert.Equal(["begin", "period", "period", "period"], rig.Events());
            Assert.Equal(3, rig.Last("period").GetProperty("snapshotsInPhase").GetInt32());

            // A second later than the first, the same number, the same phase: still one line (there is no beat and no collapsing of equal numbers).
            rig.Now = T0.AddSeconds(30);
            rig.Recording.Observe(1000);
            Assert.Equal(5, rig.Lines.Count);
            Assert.Equal(4, rig.Last("period").GetProperty("snapshotsInPhase").GetInt32());
            Assert.Equal("before", rig.Last("period").GetProperty("phase").GetString());

            rig.Asked[0].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Asked.Count == 2));
            rig.Recording.Observe(1000);
            Assert.Equal("recording", rig.Last("period").GetProperty("phase").GetString());
            Assert.Equal(1, rig.Last("period").GetProperty("snapshotsInPhase").GetInt32());
            Assert.Equal(1000, rig.Last("period").GetProperty("effectiveIntervalMs").GetInt32());
        });
    }

    [Fact]
    public void A_snapshot_the_pump_delivers_becomes_a_period_line_with_the_gap_Core_measured_on_it_and_not_the_interval_the_pump_is_set_to()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 60, recordSeconds: 60, userIntervalMs: 2000);
            rig.Recording.Begin();
            rig.Pump.Start();
            Assert.True(PumpingUntil(ui, () => rig.Pump.History.Count >= 1));
            for (var i = 0; i < 3; i++)
            {
                var seen = rig.Pump.History.Count;
                var tick = rig.Pump.TickAsync();
                Assert.True(PumpingUntil(ui, () => tick.IsCompleted && rig.Pump.History.Count > seen));
            }

            var snapshots = rig.Pump.History.ToArray();
            var periods = rig.Lines.Select(Json).Where(l => l.GetProperty("event").GetString() == "period").ToArray();
            Assert.Equal(snapshots.Length, periods.Length);
            for (var i = 0; i < snapshots.Length; i++)
            {
                Assert.Equal(snapshots[i].IntervalMs, periods[i].GetProperty("coreIntervalMs").GetInt32());
                Assert.Equal(2000, periods[i].GetProperty("effectiveIntervalMs").GetInt32());
                Assert.Equal(i + 1, periods[i].GetProperty("snapshotsInPhase").GetInt32());
            }

            // The extra snapshots a tick asked for came sooner than the 2000 ms beat: the line is the measured gap, so it is not the pump's setting.
            Assert.Contains(snapshots, s => s.IntervalMs != 2000);
        });
    }

    [Fact]
    public void A_period_line_says_the_period_the_sampler_loop_runs_at_and_the_leases_it_holds_next_to_the_users_setting()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 60, recordSeconds: 60, userIntervalMs: 2000);
            rig.Recording.Begin();
            Assert.Equal(2000, rig.Line(0).GetProperty("loopPeriodMs").GetInt32());
            Assert.Equal(0, rig.Line(0).GetProperty("leases").GetInt32());
            rig.Recording.Observe(2001);
            Assert.Equal(2000, rig.Last("period").GetProperty("userIntervalMs").GetInt32());
            Assert.Equal(2000, rig.Last("period").GetProperty("loopPeriodMs").GetInt32());
            Assert.Equal(0, rig.Last("period").GetProperty("leases").GetInt32());

            // A recording holds one lease and the loop runs at its own 1 s: the user's setting and the pump's effective interval do not show it, the line does.
            using (rig.Services.Sampler.AcquireRecordingLease())
            {
                rig.Recording.Observe(1001);
                Assert.Equal(2000, rig.Last("period").GetProperty("userIntervalMs").GetInt32());
                Assert.Equal(2000, rig.Last("period").GetProperty("effectiveIntervalMs").GetInt32());
                Assert.Equal(1000, rig.Last("period").GetProperty("loopPeriodMs").GetInt32());
                Assert.Equal(1, rig.Last("period").GetProperty("leases").GetInt32());
                Assert.Equal(1000, rig.Pump.LoopPeriodMs);
                Assert.Equal(1, rig.Pump.RecordingLeases);
            }

            rig.Recording.Observe(1999);
            Assert.Equal(2000, rig.Last("period").GetProperty("loopPeriodMs").GetInt32());
            Assert.Equal(0, rig.Last("period").GetProperty("leases").GetInt32());
        });
    }

    /// <summary>The pump delivers through the window's handle and a refresh resumes on the UI context, so the test thread plays both message loops until the condition holds (the bound only ends a broken test).</summary>
    private static bool PumpingUntil(QueueContext ui, Func<bool> condition, int timeoutMs = 20_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            System.Windows.Forms.Application.DoEvents();
            ui.Pump();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void A_recording_that_the_recorder_ended_by_itself_is_said_so_and_the_stop_is_no_work()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 1, recordSeconds: 5);
            rig.Recording.Begin();
            rig.Asked[0].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Asked.Count == 2));
            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);

            // Low space or a write error ends the run before the time is up; the window sees it through the run's own completion.
            Assert.NotNull(rig.Kit.Backend.EndByItself(RunStopReason.IoError));
            Assert.True(ui.Until(() => rig.Hub.Phase == RecordingPhase.Idle));

            rig.Asked[1].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Events().Contains("stopped")));
            Assert.True(rig.Last("stop").GetProperty("selfEnded").GetBoolean());
            Assert.Equal(0, rig.Kit.Backend.StopCalls); // nothing was stopped: the hub had nothing recording
            var stopped = rig.Last("stopped");
            Assert.Equal(0, stopped.GetProperty("stopMs").GetInt64());
            Assert.True(stopped.GetProperty("saved").GetBoolean());
            Assert.Equal("Failed", stopped.GetProperty("status").GetString());
            Assert.Equal("IoError", stopped.GetProperty("stopReason").GetString());
        });
    }

    [Fact]
    public void A_start_that_is_refused_is_one_line_with_the_reason_and_nothing_follows()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 2, recordSeconds: 5);
            rig.Kit.Backend.FailStart = RunStartFailure.LowSpace;
            rig.Recording.Begin();
            rig.Asked[0].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Events().Contains("start")));

            var start = rig.Last("start");
            Assert.False(start.GetProperty("ok").GetBoolean());
            Assert.Equal(rig.Hub.ProblemKey, start.GetProperty("problem").GetString());
            Assert.NotEqual(string.Empty, start.GetProperty("problem").GetString());
            Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);
            Assert.Equal(MeasureRecording.PhaseBefore, rig.Recording.Phase);

            // No record time was waited for, nothing was stopped, and the run did not end in an error line.
            ui.Pump();
            Assert.Single(rig.Asked);
            Assert.Equal(["begin", "start"], rig.Events());
            Assert.Equal(0, rig.Kit.Backend.StopCalls);
        });
    }

    [Fact]
    public void Disposing_the_driver_cancels_its_waits_without_a_start_a_line_or_an_error()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig();
            rig.Recording.Begin();
            Assert.Single(rig.Asked);

            rig.Recording.Dispose();
            Assert.True(rig.Asked[0].Gate.Task.IsCanceled);
            ui.Pump();
            Assert.True(ui.Until(() => ui.Queued == 0));

            Assert.Equal(["begin"], rig.Events());
            Assert.Equal(0, rig.Kit.Backend.StartCalls);
            Assert.Equal(RecordingPhase.Idle, rig.Hub.Phase);

            // What reaches it afterwards is ignored: no line, no second begin.
            rig.Recording.Observe(1000);
            rig.Recording.Begin();
            Assert.Equal(["begin"], rig.Events());
            rig.Recording.Dispose();
        });
    }

    [Fact]
    public void A_driver_that_is_disposed_while_recording_leaves_the_stop_to_the_window_close()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(leadSeconds: 1, recordSeconds: 50);
            rig.Recording.Begin();
            rig.Asked[0].Gate.SetResult();
            Assert.True(ui.Until(() => rig.Asked.Count == 2));

            // The window closed: the hub's own close stops the run (reason WindowClose); the driver only stops waiting.
            rig.Recording.Dispose();
            Assert.True(rig.Asked[1].Gate.Task.IsCanceled);
            Assert.True(ui.Until(() => ui.Queued == 0));
            Assert.Equal(["begin", "start"], rig.Events());
            Assert.Equal(0, rig.Kit.Backend.StopCalls);
            Assert.Equal(RecordingPhase.Recording, rig.Hub.Phase);

            var result = rig.Hub.StopForClose();
            Assert.NotNull(result);
            Assert.Equal(RunStopReason.WindowClose, rig.Kit.Backend.LastStopReason);
        });
    }

    [Fact]
    public void A_line_that_cannot_be_written_is_missing_and_the_run_goes_on()
    {
        Ui.Run(ui =>
        {
            using var locale = Harness.Locale();
            using var temp = new Ui.TempRoot();
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            using var pump = new SnapshotPump(services);
            var kit = new FakeRecordingKit(Path.Combine(temp.Path, "runs"));
            using var hub = kit.CreateHub();
            var gates = new List<TaskCompletionSource>();
            var spans = new List<TimeSpan>();
            var written = 0;
            using var recording = new MeasureRecording(
                new MeasureRecordPlan(temp.Path, 1, 5, 0),
                hub,
                pump,
                _ =>
                {
                    written++;
                    throw new IOException("the disk is full");
                },
                null,
                (span, token) =>
                {
                    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = token.Register(() => gate.TrySetCanceled(token));
                    spans.Add(span);
                    gates.Add(gate);
                    return gate.Task;
                });

            recording.Begin();
            gates[0].SetResult();
            Assert.True(ui.Until(() => gates.Count == 2));
            Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5) }, spans.ToArray());
            Assert.Equal(RecordingPhase.Recording, hub.Phase);
            Assert.True(written >= 2, "begin and start were both tried");
        });
    }

    [Fact]
    public void The_events_file_is_beside_the_marks_file_and_named_after_it()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "marks.record.jsonl"), MeasureRecording.EventsPathFor(Path.Combine(Path.GetTempPath(), "x", "marks.jsonl")));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "r1-main.record.jsonl"), MeasureRecording.EventsPathFor(Path.Combine(Path.GetTempPath(), "x", "r1-main.log")));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "plain.record.jsonl"), MeasureRecording.EventsPathFor(Path.Combine(Path.GetTempPath(), "y", "..", "x", "plain")));
    }

    [Fact]
    public void An_events_file_that_cannot_be_made_is_one_line_on_stderr_and_no_driver()
    {
        Ui.Run(queue =>
        {
            using var locale = Harness.Locale();
            using var temp = new Ui.TempRoot();
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            using var pump = new SnapshotPump(services);
            using var theme = new ThemeService(ThemeMode.Light);
            var plan = new MeasureRecordPlan(Path.Combine(temp.Path, "recordings"), 60, 180, 0);
            var hub = MeasureRecording.CreateHub(services, plan);
            var context = new GuiContext(services, pump, theme, new GuiSettings(), recording: hub);
            using var form = new Form();

            var error = new StringWriter();
            var marker = Path.Combine(temp.Path, "no", "such", "folder", "marks.jsonl");
            Assert.False(MeasureRecording.TryAttach(plan, marker, context, form, error, out var recording));
            Assert.Null(recording);
            Assert.StartsWith("--measure-record: ", error.ToString(), StringComparison.Ordinal);
            Assert.Single(error.ToString().TrimEnd().Split('\n'));
            Assert.Empty(temp.Entries());
        });
    }

    [Fact]
    public void The_driver_begins_when_the_window_is_shown_and_ends_when_it_is_disposed()
    {
        Ui.Run(queue =>
        {
            using var locale = Harness.Locale();
            using var temp = new Ui.TempRoot();
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            using var pump = new SnapshotPump(services, 2000);
            using var theme = new ThemeService(ThemeMode.Light);
            var plan = new MeasureRecordPlan(Path.Combine(temp.Path, "recordings"), 3600, 180, 0);
            var hub = MeasureRecording.CreateHub(services, plan)!;
            var context = new GuiContext(services, pump, theme, new GuiSettings(), recording: hub);
            var marker = Path.Combine(temp.Path, "marks.jsonl");
            var events = MeasureRecording.EventsPathFor(marker);
            File.WriteAllText(events, "left over from an earlier run\n");

            var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000), Size = new Size(300, 200) };
            try
            {
                Assert.True(MeasureRecording.TryAttach(plan, marker, context, form, TextWriter.Null, out var recording));
                Assert.Empty(ReadShared(events)); // emptied before any window shows, as the marker file is
                Assert.Equal(MeasureRecording.PhaseBefore, recording!.Phase);

                form.Show();
                var shown = false;
                for (var i = 0; i < 200 && !shown; i++)
                {
                    Application.DoEvents();
                    queue.Pump();
                    shown = ReadShared(events).Length > 0;
                    if (!shown)
                    {
                        Thread.Sleep(10);
                    }
                }

                Assert.True(shown, "no begin line after the window was shown");
                var lines = ReadSharedLines(events);
                Assert.Single(lines);
                var begin = Json(lines[0]);
                Assert.Equal("begin", begin.GetProperty("event").GetString());
                Assert.Equal(plan.Folder, begin.GetProperty("folder").GetString());
                Assert.Equal(2000, begin.GetProperty("userIntervalMs").GetInt32());
                Assert.Equal(3600, begin.GetProperty("leadSeconds").GetInt32());
                Assert.Equal(RecordingPhase.Idle, hub.Phase); // the lead is an hour: nothing started
            }
            finally
            {
                form.Dispose();
            }

            // The window's end is the driver's end: its wait is cancelled and nothing is written after it.
            queue.Pump();
            Assert.Single(ReadSharedLines(events));
            Assert.Equal(RecordingPhase.Idle, hub.Phase);
        });
    }

    // ---- the folder guard: the same place under another spelling ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint length);

    private static string ShortFormOf(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        return length == 0 || length >= buffer.Capacity ? path : buffer.ToString();
    }

    /// <summary>Makes a junction (a link to a folder; it needs no elevation). The caller removes it with <see cref="Directory.Delete(string)"/>, which removes only the link.</summary>
    private static string Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var said = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink failed: " + said);
        return link;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string[] ReadSharedLines(string path) =>
        Encoding.UTF8.GetString(ReadShared(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void A_start_with_no_record_switch_asks_nothing_of_Windows_and_a_given_one_asks_once()
    {
        var asked = 0;
        Func<IEnumerable<string>> roots = () =>
        {
            asked++;
            return [];
        };

        Assert.True(MeasureRecordPlan.TryRead(Given(), true, roots, out var plan, out var problem), problem);
        Assert.Null(plan);
        Assert.True(MeasureRecordPlan.TryRead(Given(), false, roots, out plan, out problem), problem);
        Assert.Null(plan);
        Assert.Equal(0, asked);

        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", Folder), true, roots, out plan, out problem), problem);
        Assert.NotNull(plan);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void The_users_folders_include_the_redirected_documents_and_the_plain_documents_of_the_profile()
    {
        var roots = MeasureRecordPlan.UserDataFolders();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), roots);
        Assert.Contains(Path.Combine(profile, "Documents"), roots);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), roots);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru"), roots);

        foreach (var inside in new[] { Path.Combine(profile, "Documents"), Path.Combine(profile, "Documents", "runs") })
        {
            Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", inside), true, out var plan, out var problem), inside);
            Assert.Null(plan);
            Assert.Contains("outside the user's own folders", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_short_names_of_the_users_folders_are_the_same_folders_and_are_refused()
    {
        var proved = 0;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) })
        {
            var shortRoot = ShortFormOf(root);
            if (shortRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            proved++;
            foreach (var inside in new[] { shortRoot, Path.Combine(shortRoot, "PCMieru", "runs") })
            {
                Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", inside), true, out var plan, out var problem), inside + " is " + root);
                Assert.Null(plan);
                Assert.Contains("outside the user's own folders", problem, StringComparison.Ordinal);
            }
        }

        Assert.True(proved > 0, "this machine writes no 8.3 short names for the profile, so nothing was proved");
    }

    [Fact]
    public void A_junction_into_a_users_folder_is_refused_and_one_into_a_harmless_place_is_allowed()
    {
        using var temp = new Ui.TempRoot();
        var documents = Path.Combine(temp.Path, "Documents");
        var harmless = Path.Combine(temp.Path, "Elsewhere");
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(harmless);
        var intoDocuments = Junction(Path.Combine(temp.Path, "scratch-link"), documents);
        var intoHarmless = Junction(Path.Combine(temp.Path, "other-link"), harmless);
        try
        {
            string[] roots = [documents];
            foreach (var through in new[] { intoDocuments, Path.Combine(intoDocuments, "runs"), Path.Combine(intoDocuments, "a", "b") })
            {
                Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", through), true, roots, out var plan, out var problem), through);
                Assert.Null(plan);
                Assert.Contains("outside the user's own folders", problem, StringComparison.Ordinal);
            }

            var allowed = Path.Combine(intoHarmless, "runs");
            Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", allowed), true, roots, out var ok, out var why), why);
            Assert.Equal(allowed, ok!.Folder);
        }
        finally
        {
            Directory.Delete(intoDocuments);
            Directory.Delete(intoHarmless);
        }

        Assert.True(Directory.Exists(documents));
        Assert.True(Directory.Exists(harmless));
    }

    /// <summary>The same local path as a machine reaches it through its own administrative share: <c>C:\a\b</c> is <c>\\host\C$\a\b</c>.</summary>
    private static string ThroughShare(string host, string path) => @"\\" + host + @"\" + path[0] + "$" + path[2..];

    [Fact]
    public void A_device_path_or_a_loopback_share_that_leads_into_a_users_folder_is_refused_and_so_is_any_spelling_that_is_not_a_drive_letter()
    {
        using var temp = new Ui.TempRoot();
        var documents = Path.Combine(temp.Path, "Documents");
        var harmless = Path.Combine(temp.Path, "Elsewhere");
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(harmless);
        string[] roots = [documents];

        var spellings = new List<string>
        {
            @"\\.\" + Path.Combine(documents, "x"),
            @"\\.\" + documents,
            @"\\?\" + Path.Combine(documents, "x"),
            ThroughShare("localhost", Path.Combine(documents, "x")),
            ThroughShare("127.0.0.1", Path.Combine(documents, "x")),
            ThroughShare("127.0.0.1", documents),
            ThroughShare(Environment.MachineName, Path.Combine(documents, "x")),
        };
        foreach (var through in spellings)
        {
            Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", through), true, roots, out var plan, out var problem), through);
            Assert.Null(plan);
            Assert.Contains("local drive", problem, StringComparison.Ordinal);
        }

        // A folder outside the user's own ones is refused as well when it is not spelled with a drive letter: where it leads cannot be told from its words.
        foreach (var through in new[] { @"\\.\" + Path.Combine(harmless, "x"), ThroughShare("localhost", Path.Combine(harmless, "x")), @"\\server\share\runs" })
        {
            Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", through), true, roots, out var plan, out var problem), through);
            Assert.Null(plan);
            Assert.Contains("local drive", problem, StringComparison.Ordinal);
        }

        // The same folder with its letter is a plain one and is as allowed as before.
        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", Path.Combine(harmless, "x")), true, roots, out var ok, out var why), why);
        Assert.Equal(Path.Combine(harmless, "x"), ok!.Folder);
    }

    [Fact]
    public void A_stream_spelling_of_a_users_folder_is_refused()
    {
        using var temp = new Ui.TempRoot();
        var documents = Path.Combine(temp.Path, "Documents");
        Directory.CreateDirectory(documents);
        string[] roots = [documents];

        foreach (var through in new[]
        {
            Path.Combine(temp.Path, "Documents::$INDEX_ALLOCATION", "x"),
            Path.Combine(temp.Path, "Documents::$INDEX_ALLOCATION"),
            Path.Combine(temp.Path, "Documents:name", "x"),
            Path.Combine(documents, "x:stream"),
        })
        {
            Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", through), true, roots, out var plan, out var problem), through);
            Assert.Null(plan);
            Assert.NotEqual(string.Empty, problem);
        }
    }

    [Theory]
    [InlineData(@"C:\a", true)]
    [InlineData(@"c:\a\b\c", true)]
    [InlineData(@"E:\MovedFromC\Documents\x", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"\\.\C:\a", false)]
    [InlineData(@"\\?\C:\a", false)]
    [InlineData(@"\\?\UNC\localhost\C$\a", false)]
    [InlineData(@"\\localhost\C$\a", false)]
    [InlineData(@"\\127.0.0.1\E$\a", false)]
    [InlineData(@"\\server\share\a", false)]
    [InlineData(@"C:a", false)]
    [InlineData("C:", false)]
    [InlineData(@"C:/a", false)]
    [InlineData(@"1:\a", false)]
    [InlineData(@"C:\a::$INDEX_ALLOCATION\x", false)]
    [InlineData(@"C:\a\b:stream", false)]
    [InlineData(@"C:\a:b", false)]
    [InlineData("", false)]
    public void Only_a_drive_letter_a_colon_a_backslash_and_no_other_colon_is_a_plain_local_path(string path, bool plain)
    {
        Assert.Equal(plain, FolderGuard.IsPlainLocalPath(path));
    }

    [Fact]
    public void The_users_folders_include_the_desktop_the_downloads_and_the_install_folder()
    {
        var roots = MeasureRecordPlan.UserDataFolders();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = KnownFolders.Downloads();

        // Downloads is a folder Windows moves like Documents, and only Windows can say where it went.
        Assert.False(string.IsNullOrWhiteSpace(downloads));
        Assert.True(FolderGuard.IsPlainLocalPath(downloads!), downloads);
        Assert.Contains(downloads, roots);
        Assert.Contains(Path.Combine(profile, "Downloads"), roots);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), roots);
        Assert.Contains(Path.Combine(profile, "Desktop"), roots);
        Assert.Contains(AppContext.BaseDirectory, roots);

        foreach (var root in new[]
        {
            downloads,
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(profile, "Desktop"),
            AppContext.BaseDirectory,
        })
        {
            foreach (var inside in new[] { root, Path.Combine(root, "runs"), Path.Combine(root, "a", "b") })
            {
                Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", inside), true, out var plan, out var problem), inside);
                Assert.Null(plan);
                Assert.Contains("outside the user's own folders", problem, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_link_that_leads_nowhere_is_refused_because_where_it_goes_is_not_known()
    {
        using var temp = new Ui.TempRoot();
        var gone = Path.Combine(temp.Path, "gone");
        Directory.CreateDirectory(gone);
        var link = Junction(Path.Combine(temp.Path, "dangling"), gone);
        Directory.Delete(gone);
        try
        {
            foreach (var through in new[] { link, Path.Combine(link, "runs") })
            {
                Assert.False(MeasureRecordPlan.TryRead(Given("--measure-record", through), true, [Path.Combine(temp.Path, "Documents")], out var plan, out var problem), through);
                Assert.Null(plan);
                Assert.Contains("cannot be followed", problem, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_folder_that_is_not_there_yet_under_a_plain_scratch_folder_is_allowed_as_it_was_written()
    {
        using var temp = new Ui.TempRoot();
        var folder = Path.Combine(temp.Path, "new", "deeper", "runs");

        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", folder), true, out var plan, out var problem), problem);
        Assert.Equal(folder, plan!.Folder);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "new")));

        // The same folder through its short spelling is the same folder and is as allowed.
        var shortTemp = ShortFormOf(temp.Path);
        Assert.True(MeasureRecordPlan.TryRead(Given("--measure-record", Path.Combine(shortTemp, "new", "runs")), true, out _, out problem), problem);
    }

    [Fact]
    public void The_events_file_is_held_open_by_one_stream_for_the_run_and_free_once_the_window_is_gone()
    {
        Ui.Run(queue =>
        {
            using var locale = Harness.Locale();
            using var temp = new Ui.TempRoot();
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            using var pump = new SnapshotPump(services, 2000);
            using var theme = new ThemeService(ThemeMode.Light);
            var plan = new MeasureRecordPlan(Path.Combine(temp.Path, "recordings"), 3600, 180, 0);
            var hub = MeasureRecording.CreateHub(services, plan)!;
            var context = new GuiContext(services, pump, theme, new GuiSettings(), recording: hub);
            var marker = Path.Combine(temp.Path, "marks.jsonl");
            var events = MeasureRecording.EventsPathFor(marker);

            var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000), Size = new Size(300, 200) };
            try
            {
                Assert.True(MeasureRecording.TryAttach(plan, marker, context, form, TextWriter.Null, out var recording));
                Assert.NotNull(recording);

                // Held from the start of the run: nobody else can take the file for themselves, and a reader (the script) still can read it.
                Assert.Throws<IOException>(() => new FileStream(events, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
                Assert.Empty(ReadShared(events));

                form.Show();
                Assert.True(Ui.PumpUntil(() => ReadShared(events).Length > 0), "no begin line after the window was shown");
                Assert.Throws<IOException>(() => new FileStream(events, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
                Assert.Single(ReadSharedLines(events));
            }
            finally
            {
                form.Dispose();
            }

            queue.Pump();
            using (new FileStream(events, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        });
    }

    // ---- the scripts that read all this ----

    [Fact]
    public void The_scripts_say_the_exact_form_of_the_switches_the_update_todo_and_refuse_a_user_folder_before_anything_is_made()
    {
        var script = File.ReadAllText(Path.Combine(Harness.Root, "tools", "measure.ps1"));

        // The help says the exact form of the test-only switches (a reader of the script must not have to read the C#).
        Assert.Contains("PCMieru.exe --measure <marks.jsonl> --measure-record <folder> [--measure-record-lead <s>] [--measure-record-for <s>]", script, StringComparison.Ordinal);
        Assert.Contains("[--measure-record-interval <ms>]", script, StringComparison.Ordinal);
        Assert.Contains("is refused without --measure", script, StringComparison.Ordinal);

        // The states of DESIGN-B 10 that are not measured here are a TODO in the help, in the words the design uses.
        var todo = script.IndexOf("TODO (DESIGN-B section 10, the update side)", StringComparison.Ordinal);
        Assert.True(todo > 0, "the TODO for the update-side states");
        var todoText = script.Substring(todo, 400);
        Assert.Contains("waiting for a check", todoText, StringComparison.Ordinal);
        Assert.Contains("a check is due", todoText, StringComparison.Ordinal);

        // The four switches the script passes are the four the GUI reads.
        foreach (var name in new[] { MeasureRecordPlan.FolderSwitch, MeasureRecordPlan.LeadSwitch, MeasureRecordPlan.ForSwitch, MeasureRecordPlan.IntervalSwitch })
        {
            Assert.Contains(" " + name + " ", script, StringComparison.Ordinal);
        }

        // The output folder is checked against the user's own folders before the first folder is made or probed.
        var refuse = script.IndexOf("Get-RecordFolderProblem $Output", StringComparison.Ordinal);
        var probe = script.IndexOf("Assert-WritableFolder $Output", StringComparison.Ordinal);
        Assert.True(refuse > 0 && probe > refuse, "the user-folder refusal comes before the first write");
        foreach (var folder in new[] { "MyDocuments", "LocalApplicationData", "ApplicationData", "DesktopDirectory", "KnownDownloads" })
        {
            Assert.Contains(folder, script, StringComparison.Ordinal);
        }

        // The guard function names the same three rules as the GUI: a plain drive-letter path, as written and as resolved, and the roots against both.
        var guard = script.IndexOf("function Get-RecordFolderProblem", StringComparison.Ordinal);
        Assert.True(guard > 0, "the folder guard function");
        var guardText = script.Substring(guard, Math.Min(2500, script.Length - guard));
        Assert.Contains("Test-PlainLocalPath $full", guardText, StringComparison.Ordinal);
        Assert.Contains("Test-PlainLocalPath $real", guardText, StringComparison.Ordinal);
        Assert.Contains("Test-InsideFolder $root $full", guardText, StringComparison.Ordinal);
        Assert.Contains("Test-InsideFolder $root $real", guardText, StringComparison.Ordinal);

        // Both scripts stay ASCII (Windows PowerShell 5.1 reads a file without a BOM as ANSI) and CRLF (what the other scripts of the tool use).
        foreach (var name in new[] { "measure.ps1", "measure.selftest.ps1" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(Harness.Root, "tools", name));
            Assert.True(bytes.All(b => b < 128), "tools/" + name + " must stay ASCII only");
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.Equal(text.Count(c => c == '\n'), text.Split("\r\n").Length - 1);
        }
    }
}
