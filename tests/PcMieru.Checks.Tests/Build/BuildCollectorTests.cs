// Provenance: BuildCollectorTests of PartsLife.Checks.Tests (pl-cloud 25e4571, tests/PartsLife.Checks.Tests/BuildCodeTests.cs), MIT, same owner.
// The first three tests are the old ones made asynchronous (the sources now take a token and return a Task). The old Summary_counts_modules_by_q
// is not ported: it tests the text of the old WPF window, which is not part of this library. The rest is new.
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PcMieru.Checks.Build;
using PcMieru.Checks.SystemInfo;
using PcMieru.Checks.Tests.SystemInfo;

namespace PcMieru.Checks.Tests.Build;

public class BuildCollectorTests
{
    private static readonly RawMemory Stick = new("F4-3200C16-16GVK", "G Skill Intl", 17179869184, 3200, 26, 0);

    private static Task<IReadOnlyList<T>> Rows<T>(params T[] rows) => Task.FromResult<IReadOnlyList<T>>(rows);

    [Fact]
    public async Task A_failing_query_leaves_its_category_empty_instead_of_throwing()
    {
        var src = new BuildSources
        {
            Cpus = _ => Rows("AMD Ryzen 7 5800X 8-Core Processor"),
            Board = _ => throw new System.Management.ManagementException("Access denied"),
            Memory = _ => Rows(Stick, Stick),
            Gpus = _ => throw new DllNotFoundException("dxgi.dll"),
            Disks = _ => throw new UnauthorizedAccessException(),
            Monitors = _ => throw new COMException("WmiMonitorID"),
            Opticals = _ => throw new InvalidOperationException("anything"),
        };
        var c = await BuildCollector.CollectAsync(src);
        Assert.Single(c["cpu"]);
        Assert.Equal(2, c["ram"].Count);
        foreach (var cat in new[] { "motherboard", "gpu", "ssd", "hdd", "monitor", "optical-drive" })
        {
            Assert.Empty(c[cat]);
        }

        var r = BuildCode.Make(c);
        Assert.Equal("""{"v":1,"src":"parts-life","p":{"cpu":[{"n":"AMD Ryzen 7 5800X 8-Core Processor"}],"ram":[{"n":"F4-3200C16-16GVK","cap":16,"spd":3200,"t":"DDR4","mfr":"G Skill Intl","q":2}]}}""",
                     BuildCode.Decode(r.Code));
    }

    [Fact]
    public async Task A_source_that_fails_after_awaiting_is_also_an_empty_category()
    {
        // The exception comes out of the Task, not out of the call that makes it.
        var src = new BuildSources
        {
            Cpus = _ => Rows("CPU one"),
            Gpus = async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("after the first await");
            },
            Monitors = _ => Task.FromException<IReadOnlyList<RawMonitor>>(new InvalidOperationException("a faulted task")),
        };
        var c = await BuildCollector.CollectAsync(src);
        Assert.Single(c["cpu"]);
        Assert.Empty(c["gpu"]);
        Assert.Empty(c["monitor"]);
    }

    [Fact]
    public async Task A_bad_row_inside_a_source_leaves_that_category_empty_and_the_others_alone()
    {
        // BuildRules throws on nothing it is given, but a source can hand over a null where a row is expected.
        var src = new BuildSources
        {
            Cpus = _ => Rows("CPU one"),
            Opticals = _ => Rows(new RawOptical(null!, null!)),
            Memory = _ => Rows(Stick),
        };
        var c = await BuildCollector.CollectAsync(src);
        Assert.Single(c["cpu"]);
        Assert.Single(c["ram"]);
        Assert.Empty(c["optical-drive"]);
    }

    [Fact]
    public async Task Nothing_readable_gives_an_empty_but_valid_code()
    {
        var c = await BuildCollector.CollectAsync(new BuildSources()); // every default source is empty
        Assert.True(c.IsEmpty);
        var r = BuildCode.Make(c);
        Assert.Equal("""{"v":1,"src":"parts-life","p":{}}""", BuildCode.Decode(r.Code));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildCollector.CollectAsync(new BuildSources(), cts.Token));
    }

    [Fact]
    public async Task Cancellation_inside_a_source_is_passed_on_not_turned_into_an_empty_category()
    {
        using var cts = new CancellationTokenSource();
        var src = new BuildSources
        {
            Cpus = _ => Rows("CPU one"),
            Board = async ct =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return (null, null);
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildCollector.CollectAsync(src, cts.Token));
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_callers_is_one_unreadable_category()
    {
        // A source's own timeout also surfaces as an OperationCanceledException; the caller did not cancel, so it is only an empty category.
        var src = new BuildSources
        {
            Cpus = _ => Rows("CPU one"),
            Disks = _ => throw new OperationCanceledException("a timeout of the source"),
            Monitors = _ => Rows(new RawMonitor("UG27PJ", true)),
        };
        var c = await BuildCollector.CollectAsync(src);
        Assert.Single(c["cpu"]);
        Assert.Empty(c["ssd"]);
        Assert.Empty(c["hdd"]);
        Assert.Single(c["monitor"]);
    }

    [Fact]
    public async Task The_sources_are_read_in_the_old_order_and_each_gets_the_token()
    {
        var order = new List<string>();
        var seen = new List<CancellationToken>();
        using var cts = new CancellationTokenSource();

        Task<IReadOnlyList<T>> Note<T>(string what, CancellationToken ct)
        {
            order.Add(what);
            seen.Add(ct);
            return Rows<T>();
        }

        var src = new BuildSources
        {
            Cpus = ct => Note<string>("cpu", ct),
            Board = ct =>
            {
                order.Add("board");
                seen.Add(ct);
                return Task.FromResult<(string?, string?)>((null, null));
            },
            Memory = ct => Note<RawMemory>("memory", ct),
            Gpus = ct => Note<RawGpu>("gpu", ct),
            Disks = ct => Note<RawDisk>("disks", ct),
            Monitors = ct => Note<RawMonitor>("monitors", ct),
            Opticals = ct => Note<RawOptical>("opticals", ct),
        };
        await BuildCollector.CollectAsync(src, cts.Token);
        Assert.Equal(new[] { "cpu", "board", "memory", "gpu", "disks", "monitors", "opticals" }, order.ToArray());
        Assert.All(seen, t => Assert.Equal(cts.Token, t));
    }

    [Fact]
    public async Task A_source_that_throws_does_not_stop_the_ones_after_it()
    {
        var src = new BuildSources
        {
            Cpus = _ => throw new InvalidOperationException(),
            Opticals = _ => Rows(new RawOptical("HL-DT-ST BD-RE WH16NS60", @"SCSI\CDROM&VEN_HL-DT-ST&PROD_BD-RE_WH16NS60\4&1")),
        };
        var c = await BuildCollector.CollectAsync(src);
        Assert.Empty(c["cpu"]);
        Assert.Single(c["optical-drive"]);
    }

    // ---- the categories that could not be read (BuildCodeResult.Unread): "this machine has none" is not "we could not look"

    private static Task<BuildCodeResult> CreateAsync(BuildSources sources, CancellationToken ct = default) =>
        new SourcesBuildCodeProvider(() => sources).CreateAsync(ct);

    private static Func<CancellationToken, Task<IReadOnlyList<T>>> Unread<T>(CheckReason reason) => _ => throw new BuildCategoryUnreadException(reason);

    private static Func<CancellationToken, Task<(string? Manufacturer, string? Product)>> UnreadBoard(CheckReason reason) =>
        _ => throw new BuildCategoryUnreadException(reason);

    private static Action PastTheDeadline(ManualTimeProvider clock) => () => clock.Advance(SystemWmiFacts.Budget + TimeSpan.FromSeconds(1));

    /// <summary>A WMI that answers every question of the Windows readers with no row (read, and nothing there).</summary>
    private static ScriptedWmi AnsweredEmpty() => new ScriptedWmi()
        .Rows(WindowsBuildReaders.CpuQuery.Text)
        .Rows(WindowsBuildReaders.BoardQuery.Text)
        .Rows(WindowsBuildReaders.MemoryQueries[0].Text)
        .Rows(WindowsBuildReaders.VideoControllerQuery.Text)
        .Rows(WindowsBuildReaders.PhysicalDiskQuery.Text)
        .Rows(WindowsBuildReaders.DiskDriveQuery.Text)
        .Rows(WindowsBuildReaders.MonitorQuery.Text)
        .Rows(WindowsBuildReaders.OpticalQuery.Text);

    [Fact]
    public async Task A_config_that_was_read_in_full_has_no_unread_category()
    {
        var read = await CreateAsync(new BuildSources
        {
            Cpus = _ => Rows("AMD Ryzen 7 5800X 8-Core Processor"),
            Memory = _ => Rows(Stick, Stick),
            Monitors = _ => Rows(new RawMonitor("UG27PJ", true)),
        });
        Assert.Empty(read.Unread);

        // A category with nothing in it is read: the default sources answer with no row, and that is no unread entry.
        var none = await CreateAsync(new BuildSources());
        Assert.Empty(none.Unread);
        Assert.True(none.Config.IsEmpty);
    }

    [Fact]
    public async Task A_windows_machine_whose_questions_were_all_answered_has_no_unread_category_even_when_it_has_nothing()
    {
        var r = await CreateAsync(BuildSources.Windows(new WindowsBuildReaders(AnsweredEmpty(), ScriptedGpu.Ok(), new ManualTimeProvider())));

        Assert.Empty(r.Unread);
        Assert.True(r.Config.IsEmpty);
    }

    [Theory]
    [InlineData(CheckReason.Timeout)]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Unsupported)]
    [InlineData(CheckReason.Error)]
    [InlineData(CheckReason.Partial)]
    public async Task A_category_that_could_not_be_read_is_listed_with_its_reason_and_the_others_are_kept(CheckReason reason)
    {
        var r = await CreateAsync(new BuildSources
        {
            Cpus = _ => Rows("AMD Ryzen 7 5800X 8-Core Processor"),
            Memory = _ => Rows(Stick, Stick),
            Gpus = Unread<RawGpu>(reason),
        });

        Assert.Equal(new[] { new BuildUnread("gpu", reason) }, r.Unread.ToArray());
        Assert.Empty(r.Config["gpu"]);
        Assert.Single(r.Config["cpu"]);
        Assert.Equal(2, r.Config["ram"].Single().Q);
        Assert.Equal(
            """{"v":1,"src":"parts-life","p":{"cpu":[{"n":"AMD Ryzen 7 5800X 8-Core Processor"}],"ram":[{"n":"F4-3200C16-16GVK","cap":16,"spd":3200,"t":"DDR4","mfr":"G Skill Intl","q":2}]}}""",
            BuildCode.Decode(r.Code));
    }

    [Fact]
    public async Task The_storage_question_gives_two_unread_categories()
    {
        var r = await CreateAsync(new BuildSources { Disks = Unread<RawDisk>(CheckReason.Denied) });

        Assert.Equal(new[] { new BuildUnread("ssd", CheckReason.Denied), new BuildUnread("hdd", CheckReason.Denied) }, r.Unread.ToArray());
    }

    [Fact]
    public async Task A_source_that_throws_is_a_failed_category_in_every_form_of_throwing()
    {
        var src = new BuildSources
        {
            Cpus = _ => throw new InvalidOperationException(),
            Board = _ => throw new System.Management.ManagementException("Access denied"),
            Memory = _ => Task.FromException<IReadOnlyList<RawMemory>>(new InvalidOperationException("a faulted task")),
            Gpus = async _ =>
            {
                await Task.Yield();
                throw new DllNotFoundException("dxgi.dll");
            },
            Disks = _ => throw new OperationCanceledException("a timeout of the source, not the caller's stop"),
            Monitors = _ => throw new COMException("WmiMonitorID"),
            Opticals = _ => Rows(new RawOptical(null!, null!)), // a bad row: the rules throw on it
        };

        var r = await CreateAsync(src);

        Assert.Equal(BuildConfig.Categories.ToArray(), r.Unread.Select(u => u.Category).ToArray());
        Assert.All(r.Unread, u => Assert.Equal(CheckReason.Error, u.Reason));
        Assert.True(r.Config.IsEmpty);
    }

    [Fact]
    public async Task The_unread_list_follows_the_order_of_the_categories_not_the_order_they_were_read()
    {
        // Read order: cpu, motherboard, ram, gpu, storage, monitor, optical drive. Written order: cpu, gpu, motherboard, ram, ssd, hdd, ...
        var r = await CreateAsync(new BuildSources
        {
            Cpus = Unread<string>(CheckReason.Error),
            Board = UnreadBoard(CheckReason.Denied),
            Memory = Unread<RawMemory>(CheckReason.Unsupported),
            Gpus = Unread<RawGpu>(CheckReason.Timeout),
            Disks = Unread<RawDisk>(CheckReason.Partial),
            Monitors = Unread<RawMonitor>(CheckReason.Error),
            Opticals = Unread<RawOptical>(CheckReason.Denied),
        });

        Assert.Equal(
            new[]
            {
                new BuildUnread("cpu", CheckReason.Error),
                new BuildUnread("gpu", CheckReason.Timeout),
                new BuildUnread("motherboard", CheckReason.Denied),
                new BuildUnread("ram", CheckReason.Unsupported),
                new BuildUnread("ssd", CheckReason.Partial),
                new BuildUnread("hdd", CheckReason.Partial),
                new BuildUnread("monitor", CheckReason.Error),
                new BuildUnread("optical-drive", CheckReason.Denied),
            },
            r.Unread.ToArray());
        Assert.Equal(BuildConfig.Categories.ToArray(), r.Unread.Select(u => u.Category).ToArray());
    }

    [Fact]
    public async Task What_is_unread_is_not_in_the_code_and_not_in_the_saved_json()
    {
        var unreadGpu = await CreateAsync(new BuildSources { Cpus = _ => Rows("CPU one"), Gpus = Unread<RawGpu>(CheckReason.Denied) });
        var noGpu = await CreateAsync(new BuildSources { Cpus = _ => Rows("CPU one") }); // read, and there is none

        Assert.Equal(noGpu.Code, unreadGpu.Code);
        Assert.Equal(noGpu.Json, unreadGpu.Json);
        Assert.Empty(noGpu.Unread);
        Assert.Single(unreadGpu.Unread);
        Assert.DoesNotContain("Denied", BuildCode.Decode(unreadGpu.Code), StringComparison.OrdinalIgnoreCase);
    }

    // ---- a lane that has no free slot (the question is refused at once, the deadline of the category is still all there) and a real timeout

    [Fact]
    public async Task A_gpu_lane_with_no_free_slot_still_tries_the_video_controller_and_the_category_is_read()
    {
        var wmi = AnsweredEmpty().Rows(WindowsBuildReaders.VideoControllerQuery.Text, ScriptedWmi.Row(("Name", "NVIDIA GeForce RTX 3080 Ti"), ("PNPDeviceID", @"PCI\VEN_10DE&DEV_2208\4&2")));
        var gpu = ScriptedGpu.Reason(CheckReason.Timeout); // the clock has not moved: the deadline is not expired

        var r = await CreateAsync(BuildSources.Windows(new WindowsBuildReaders(wmi, gpu, new ManualTimeProvider())));

        Assert.Empty(r.Unread);
        Assert.Single(r.Config["gpu"]);
        Assert.Contains(wmi.Asked, q => q.Text == WindowsBuildReaders.VideoControllerQuery.Text);
    }

    [Fact]
    public async Task A_gpu_lane_with_no_free_slot_on_both_ways_is_unread_for_timeout()
    {
        var wmi = AnsweredEmpty().Reason(WindowsBuildReaders.VideoControllerQuery.Text, CheckReason.Timeout);

        var r = await CreateAsync(BuildSources.Windows(new WindowsBuildReaders(wmi, ScriptedGpu.Reason(CheckReason.Timeout), new ManualTimeProvider())));

        Assert.Equal(new[] { new BuildUnread("gpu", CheckReason.Timeout) }, r.Unread.ToArray());
        Assert.Empty(r.Config["gpu"]);
    }

    [Fact]
    public async Task A_gpu_that_really_ran_out_of_time_is_unread_and_the_video_controller_is_not_asked()
    {
        var clock = new ManualTimeProvider();
        var wmi = AnsweredEmpty().Rows(WindowsBuildReaders.VideoControllerQuery.Text, ScriptedWmi.Row(("Name", "X"), ("PNPDeviceID", @"PCI\X")));
        var gpu = ScriptedGpu.TimesOut(PastTheDeadline(clock));

        var r = await CreateAsync(BuildSources.Windows(new WindowsBuildReaders(wmi, gpu, clock)));

        Assert.Equal(new[] { new BuildUnread("gpu", CheckReason.Timeout) }, r.Unread.ToArray());
        Assert.Empty(r.Config["gpu"]);
        Assert.DoesNotContain(wmi.Asked, q => q.Text == WindowsBuildReaders.VideoControllerQuery.Text);
    }

    // ---- the caller's stop

    [Fact]
    public async Task A_stop_that_came_during_the_last_category_is_not_lost()
    {
        using var cts = new CancellationTokenSource();
        var src = new BuildSources
        {
            Cpus = _ => Rows("CPU one"),
            Opticals = async _ =>
            {
                await cts.CancelAsync();
                return Array.Empty<RawOptical>(); // the source itself came back normally; the caller has already asked to stop
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildCollector.CollectAsync(src, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateAsync(src, cts.Token));
    }

    // ---- the way in from outside: IBuildCodeProvider

    [Fact]
    public async Task The_sample_machine_is_a_fixed_synthetic_one_that_was_read_in_full()
    {
        var provider = BuildCodeProviders.Sample();

        var a = await provider.CreateAsync(default);
        var b = await provider.CreateAsync(default);

        Assert.Equal(a.Code, b.Code);
        Assert.Equal(a.Json, b.Json);
        Assert.Empty(a.Unread);
        Assert.Empty(a.Notes);
        foreach (var category in BuildConfig.Categories)
        {
            Assert.NotEmpty(a.Config[category]);
        }

        Assert.Equal(2, a.Config["ram"].Single().Q);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(BuildCode.Decode(a.Code)), JsonNode.Parse(a.Json)));
    }

    [Fact]
    public void The_windows_provider_is_made_without_reading_anything()
    {
        // The sources are made (and WMI is touched) only when CreateAsync runs; the factory itself reads nothing.
        var provider = BuildCodeProviders.Windows();

        Assert.NotNull(provider);
        Assert.IsAssignableFrom<IBuildCodeProvider>(provider);
    }

    [Fact]
    public void The_public_surface_of_the_build_code_is_the_provider_the_result_and_the_parts_only()
    {
        var exported = typeof(BuildCode).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "PcMieru.Checks.Build")
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "BuildCode", "BuildCodeProviders", "BuildCodeResult", "BuildConfig", "BuildNote", "BuildNoteKind", "BuildPart", "BuildUnread", "IBuildCodeProvider" },
            exported);

        // The collector, the rules and Make are not in it (internal): only the two factories of the providers are public methods here.
        Assert.Empty(typeof(BuildCode).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal(
            new[] { "Sample", "Windows" },
            typeof(BuildCodeProviders).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "CreateAsync" }, typeof(IBuildCodeProvider).GetMethods().Select(m => m.Name).ToArray());
    }

    // ---- what the config hands out cannot be cast back to something that changes it

    private static BuildConfig SomeConfig()
    {
        var c = new BuildConfig();
        c.Set("cpu", [new BuildPart("AMD Ryzen 7 5800X 8-Core Processor")]);
        c.Set("gpu", [new BuildPart("NVIDIA GeForce RTX 5070", Vram: 12)]);
        c.Set("ram", [new BuildPart("F4-3200C16-16GVK", Cap: 16, Speed: 3200, Type: "DDR4", Mfr: "G Skill Intl", Q: 2)]);
        return c;
    }

    [Fact]
    public void The_categories_cannot_be_cast_back_to_an_array_and_reordered()
    {
        var order = new[] { "cpu", "gpu", "motherboard", "ram", "ssd", "hdd", "monitor", "optical-drive" };
        var codeBefore = BuildCode.Make(SomeConfig()).Code;

        // The attempts: an array cast, a list cast, and a write through each interface the object has.
        Assert.Null(BuildConfig.Categories as string[]);
        Assert.Null(BuildConfig.Categories as List<string>);

        var generic = Assert.IsAssignableFrom<IList<string>>(BuildConfig.Categories);
        Assert.True(generic.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => generic[0] = "gpu");
        Assert.Throws<NotSupportedException>(() => generic.Add("x"));
        Assert.Throws<NotSupportedException>(() => generic.Insert(0, "x"));
        Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => generic.Remove("cpu"));
        Assert.Throws<NotSupportedException>(() => generic.Clear());

        var plain = Assert.IsAssignableFrom<IList>(BuildConfig.Categories);
        Assert.True(plain.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => plain[1] = "cpu");
        Assert.Throws<NotSupportedException>(() => plain.Add("x"));

        // Nothing moved: the same order, the same code.
        Assert.Equal(order, BuildConfig.Categories.ToArray());
        Assert.Equal(new[] { "cpu", "gpu", "ram" }, SomeConfig().NonEmpty.ToArray());
        Assert.Equal(codeBefore, BuildCode.Make(SomeConfig()).Code);
    }

    [Fact]
    public void A_list_taken_from_the_indexer_cannot_be_cast_back_to_a_list_and_changed()
    {
        var c = SomeConfig();
        var codeBefore = BuildCode.Make(c).Code;
        var cpu = c["cpu"];

        Assert.Null(cpu as List<BuildPart>);
        Assert.Null(cpu as BuildPart[]);

        var generic = Assert.IsAssignableFrom<IList<BuildPart>>(cpu);
        Assert.True(generic.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => generic.Add(new BuildPart("x")));
        Assert.Throws<NotSupportedException>(() => generic.Insert(0, new BuildPart("x")));
        Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => generic.Remove(generic[0]));
        Assert.Throws<NotSupportedException>(() => generic[0] = new BuildPart("x"));
        Assert.Throws<NotSupportedException>(() => generic.Clear());

        var plain = Assert.IsAssignableFrom<IList>(cpu);
        Assert.True(plain.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => plain.Add(new BuildPart("x")));
        Assert.Throws<NotSupportedException>(() => plain.Clear());

        // Nothing changed: the part is there, the count is the same, and so is the code.
        Assert.Equal("AMD Ryzen 7 5800X 8-Core Processor", Assert.Single(c["cpu"]).Name);
        Assert.Equal(1, c.Count("cpu"));
        Assert.Equal(codeBefore, BuildCode.Make(c).Code);
    }

    [Fact]
    public void A_category_with_no_parts_and_a_name_that_is_not_one_are_read_only_too_and_stay_empty()
    {
        var c = SomeConfig();

        foreach (var name in new[] { "monitor", "no-such-category" })
        {
            var parts = c[name];
            Assert.Null(parts as List<BuildPart>);
            var generic = Assert.IsAssignableFrom<IList<BuildPart>>(parts);
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic.Add(new BuildPart("x")));
        }

        // The empty list that is handed out for "nothing" is shared: a failed write must not have left anything in it.
        Assert.Empty(c["monitor"]);
        Assert.Empty(c["no-such-category"]);
        Assert.Empty(new BuildConfig()["no-such-category"]);
        Assert.Equal(new[] { "cpu", "gpu", "ram" }, c.NonEmpty.ToArray());
    }

    [Fact]
    public async Task The_notes_and_the_unread_list_of_a_result_cannot_be_changed_through_a_cast()
    {
        var r = await CreateAsync(new BuildSources { Gpus = Unread<RawGpu>(CheckReason.Timeout) });

        Assert.Null(r.Notes as List<BuildNote>);
        Assert.Null(r.Unread as List<BuildUnread>);
        Assert.Throws<NotSupportedException>(() => ((IList<BuildUnread>)r.Unread).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BuildUnread>)r.Unread).Add(new BuildUnread("cpu", CheckReason.Error)));
        Assert.Throws<NotSupportedException>(() => ((IList<BuildNote>)r.Notes).Add(new BuildNote(BuildNoteKind.MemoryFirstKindOnly, "ram")));
        Assert.Equal(new[] { new BuildUnread("gpu", CheckReason.Timeout) }, r.Unread.ToArray());
    }
}
