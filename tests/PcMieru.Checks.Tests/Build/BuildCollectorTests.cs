// Provenance: BuildCollectorTests of PartsLife.Checks.Tests (pl-cloud 25e4571, tests/PartsLife.Checks.Tests/BuildCodeTests.cs), MIT, same owner.
// The first three tests are the old ones made asynchronous (the sources now take a token and return a Task). The old Summary_counts_modules_by_q
// is not ported: it tests the text of the old WPF window, which is not part of this library. The rest is new.
using System.Runtime.InteropServices;
using PcMieru.Checks.Build;

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
}
