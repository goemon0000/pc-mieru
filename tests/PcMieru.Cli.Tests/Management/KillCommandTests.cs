using PcMieru.Wire;

namespace PcMieru.Cli.Tests.Management;

public sealed class KillCommandTests
{
    [Fact]
    public async Task Kill_by_pid_with_started_at_ends_that_process_and_says_what_it_did()
    {
        using var rig = new Rig();
        var started = await Fx.StartedAtAsync(9600);

        var r = await rig.RunAsync("kill", "--pid", "9600", "--started-at", started);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["terminate:9600"], rig.Control.Calls);
        Assert.Contains("Will terminate:", r.Out, StringComparison.Ordinal);
        Assert.Contains("9600 Discord.exe: Terminated", r.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("No --started-at", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_by_pid_without_started_at_says_which_process_it_chose()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", "9600");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["terminate:9600"], rig.Control.Calls);
        Assert.Contains("No --started-at: acting on the process that has PID 9600", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_with_a_start_time_of_another_process_does_nothing_and_exits_3()
    {
        using var rig = new Rig();
        var other = await Fx.StartedAtAsync(9900);

        var r = await rig.RunAsync("kill", "--pid", "9600", "--started-at", other);

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Equal(string.Empty, r.Out);
        Assert.Contains("is no longer the process started at that time", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_accepts_the_start_time_as_a_utc_date()
    {
        using var rig = new Rig();
        var key = await Fx.KeyAsync(9600);
        var date = key.StartedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        var r = await rig.RunAsync("kill", "--pid", "9600", "--started-at", date);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["terminate:9600"], rig.Control.Calls);
    }

    [Fact]
    public async Task Kill_for_a_pid_nobody_has_exits_3_and_calls_nothing()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", "4000000000");

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Contains("No process with that PID", r.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kill", "--pid", "abc")]
    [InlineData("kill", "--pid", "-5")]
    [InlineData("kill", "--pid", "9600", "--started-at", "yesterday")]
    [InlineData("kill")]
    [InlineData("kill", "--pid", "9600", "--name", "chrome")]
    [InlineData("kill", "--name", "chrome", "--started-at", "1")]
    [InlineData("kill", "--pid", "9600", "--all")]
    [InlineData("kill", "--name", "chrome", "--elevate")]
    [InlineData("kill", "--pid", "9600", "--tree", "--elevate")]
    [InlineData("kill", "--name", " ")]
    [InlineData("kill", "9600")]
    public async Task A_bad_kill_command_line_exits_2_without_touching_anything(params string[] args)
    {
        using var rig = new Rig();

        var r = await rig.RunAsync(args);

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Empty(rig.Helper.Requests);
    }

    [Theory]
    [InlineData("780")]
    [InlineData("4")]
    [InlineData("960")]
    public async Task A_protected_process_is_refused_with_exit_5_and_never_offered_to_terminate(string pid)
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", pid);

        Assert.Equal(5, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("Protected target; nothing was done", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_protected_process_stays_refused_even_with_elevate()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", "780", "--elevate");

        Assert.Equal(5, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Empty(rig.Helper.Requests);
    }

    [Fact]
    public async Task A_name_with_several_matches_lists_them_and_ends_nothing_without_all()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--name", "chrome");

        Assert.Equal(6, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Contains("4 processes match 'chrome'", r.Err, StringComparison.Ordinal);
        foreach (var pid in new[] { "7200", "7340", "7388", "7452" })
        {
            Assert.Contains(pid, r.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task All_ends_every_match_and_summarizes()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--name", "chrome.exe", "--all");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["terminate:7200", "terminate:7340", "terminate:7388", "terminate:7452"], rig.Control.Calls);
        Assert.Contains("4 terminated, 0 left alone (protected), 0 failed", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_shows_the_targets_and_ends_nothing()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--name", "chrome", "--all", "--dry-run");

        Assert.Equal(0, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Contains("--dry-run: nothing was terminated", r.Out, StringComparison.Ordinal);
        Assert.Contains("7452", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_on_only_protected_targets_exits_5()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", "780", "--dry-run");

        Assert.Equal(5, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
    }

    [Theory]
    [InlineData("rome")]
    [InlineData("hrom")]
    [InlineData("chrome2")]
    public async Task A_name_is_matched_whole_never_as_a_part(string name)
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--name", name, "--all");

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Contains("No process with that name", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_is_matched_ignoring_case()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--name", "DISCORD");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["terminate:9600"], rig.Control.Calls);
    }

    [Fact]
    public async Task A_name_with_one_protected_match_is_refused_with_exit_5()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--name", "csrss");

        Assert.Equal(5, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
    }

    [Fact]
    public async Task Tree_plans_first_shows_every_target_and_ends_the_whole_tree()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", "7200", "--tree");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["plan:7200", "tree:7200:4"], rig.Control.Calls);
        Assert.Contains("Will terminate the tree", r.Out, StringComparison.Ordinal);
        Assert.Contains("Terminated the tree (4 processes)", r.Out, StringComparison.Ordinal);
        foreach (var pid in new[] { "7340", "7388", "7452" })
        {
            Assert.Contains(pid, r.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Tree_dry_run_only_plans()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", "7200", "--tree", "--dry-run");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["plan:7200"], rig.Control.Calls);
        Assert.Contains("--dry-run: nothing was terminated", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tree_with_a_protected_root_never_even_plans()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--pid", "940", "--tree");

        Assert.Equal(5, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
    }

    [Fact]
    public async Task Tree_names_the_protected_descendants_it_leaves_alone()
    {
        var root = await Fx.KeyAsync(7200);
        var child = await Fx.KeyAsync(7340);
        var csrss = await Fx.KeyAsync(780);
        using var rig = new Rig { PlanOverride = new TerminatePlan(root, [root, child], [csrss]) };

        var r = await rig.RunAsync("kill", "--pid", "7200", "--tree");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Left alone because they are protected: 780 csrss.exe", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tree_with_several_name_matches_in_one_tree_plans_the_top_one_only()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("kill", "--name", "chrome", "--all", "--tree");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["plan:7200", "tree:7200:4"], rig.Control.Calls);
    }

    [Fact]
    public async Task A_failed_tree_shows_each_failing_member()
    {
        var root = await Fx.KeyAsync(7200);
        using var rig = new Rig
        {
            PlanOverride = new TerminatePlan(root, [root], []),
            Forced = new OperationResult(
                OperationCode.PartialSuccess,
                "1 of 2",
                [new OperationDetail("7340 chrome.exe", OperationCode.Denied, "access"), new OperationDetail("7200 chrome.exe", OperationCode.Ok, string.Empty)]),
        };

        var r = await rig.RunAsync("kill", "--pid", "7200", "--tree");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Partly done", r.Out, StringComparison.Ordinal);
        Assert.Contains("  7340 chrome.exe: Permission denied (access)", r.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("  7200 chrome.exe", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_without_elevate_gives_the_hint_and_exits_4_without_any_helper()
    {
        using var rig = new Rig { Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied") };

        var r = await rig.RunAsync("kill", "--pid", "9600");

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("Add --elevate", r.Err, StringComparison.Ordinal);
        Assert.Contains("Permission denied (Access is denied)", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_with_elevate_goes_through_the_helper_with_a_fresh_nonce_and_cleans_up()
    {
        using var rig = new Rig { Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied") };
        var key = await Fx.KeyAsync(9600);

        var r = await rig.RunAsync("kill", "--pid", "9600", "--elevate");

        Assert.Equal(0, r.ExitCode);
        var request = Assert.Single(rig.Helper.Requests);
        Assert.Equal(HelperOperation.ProcessTerminate, request.Op);
        Assert.Equal(9600u, request.Pid);
        Assert.Equal(key.CreateTime, request.CreateTime);
        Assert.Matches("^[0-9a-f]{32}$", request.Nonce);
        Assert.StartsWith(rig.HelperDirectory, request.ResultPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((true, 0L), Assert.Single(rig.Helper.ResultFileAtCall));
        Assert.Empty(rig.LeftoverFiles());
        Assert.Contains("Asking for administrator approval", r.Err, StringComparison.Ordinal);
        Assert.Contains("9600 Discord.exe: Terminated", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declined_uac_prompt_exits_4_and_cleans_up()
    {
        using var rig = new Rig { Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied") };
        rig.Helper.Answer = _ => new HelperOutcome(OperationCode.Denied, true, null);

        var r = await rig.RunAsync("kill", "--pid", "9600", "--elevate");

        Assert.Equal(4, r.ExitCode);
        Assert.Contains("Administrator approval was not given", r.Out, StringComparison.Ordinal);
        Assert.Empty(rig.LeftoverFiles());
    }

    [Fact]
    public async Task The_japanese_text_is_used_when_the_language_is_japanese()
    {
        using var rig = new Rig();

        var r = await rig.RunWithAsync(Language.Ja, "kill", "--pid", "9600");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("終了しました", r.Out, StringComparison.Ordinal);
        Assert.Contains("終了する対象:", r.Out, StringComparison.Ordinal);
    }
}
