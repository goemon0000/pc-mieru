using PcMieru.Cli.Commands;
using PcMieru.Wire;

namespace PcMieru.Cli.Tests.Management;

public sealed class PriorityEcoRunTests
{
    [Theory]
    [InlineData("idle", SettablePriority.Idle)]
    [InlineData("below", SettablePriority.BelowNormal)]
    [InlineData("BelowNormal", SettablePriority.BelowNormal)]
    [InlineData("normal", SettablePriority.Normal)]
    [InlineData("above", SettablePriority.AboveNormal)]
    [InlineData("abovenormal", SettablePriority.AboveNormal)]
    [InlineData(" High ", SettablePriority.High)]
    public void The_priority_names_are_understood_in_any_case(string text, SettablePriority expected) =>
        Assert.Equal(expected, PriorityCommand.ParseLevel(text));

    [Fact]
    public void Realtime_is_refused_with_a_message_that_names_it()
    {
        var ex = Assert.Throws<ArgException>(() => PriorityCommand.ParseLevel("realtime"));
        Assert.Contains("realtime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Priority_sets_the_level_through_the_process_control()
    {
        using var rig = new Rig();
        var started = await Fx.StartedAtAsync(9600);

        var r = await rig.RunAsync("priority", "high", "--pid", "9600", "--started-at", started);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["priority:9600:High"], rig.Control.Calls);
        Assert.Contains("9600 Discord.exe: Priority set to 'high'", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Priority_prints_japanese_when_the_language_is_japanese()
    {
        using var rig = new Rig();

        var r = await rig.RunWithAsync(Language.Ja, "priority", "idle", "--pid", "9600");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("優先度を「低（アイドル）」にしました", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Realtime_exits_2_with_nothing_on_stdout_and_nothing_called()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("priority", "realtime", "--pid", "9600");

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Contains("realtime", r.Err, StringComparison.Ordinal);
        Assert.Empty(rig.Helper.Requests);
    }

    [Theory]
    [InlineData("priority", "--pid", "9600")]
    [InlineData("priority", "high")]
    [InlineData("priority", "turbo", "--pid", "9600")]
    [InlineData("priority", "high", "low", "--pid", "9600")]
    [InlineData("priority", "high", "--pid", "x")]
    [InlineData("eco", "--pid", "9600")]
    [InlineData("eco", "on")]
    [InlineData("eco", "maybe", "--pid", "9600")]
    [InlineData("eco", "on", "off", "--pid", "9600")]
    public async Task A_bad_priority_or_eco_command_line_exits_2(params string[] args)
    {
        using var rig = new Rig();

        var r = await rig.RunAsync(args);

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Empty(rig.Helper.Requests);
    }

    [Fact]
    public async Task Priority_of_a_protected_process_is_refused_with_exit_5()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("priority", "idle", "--pid", "780");

        Assert.Equal(5, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
        Assert.Contains("Protected target; nothing was done", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Priority_of_a_pid_nobody_has_exits_3()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("priority", "idle", "--pid", "4000000000");

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
    }

    [Fact]
    public async Task Priority_with_the_start_time_of_another_process_exits_3()
    {
        using var rig = new Rig();
        var other = await Fx.StartedAtAsync(9900);

        var r = await rig.RunAsync("priority", "idle", "--pid", "9600", "--started-at", other);

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
    }

    [Fact]
    public async Task Priority_denied_with_elevate_sends_the_fixed_helper_request()
    {
        using var rig = new Rig { Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied") };
        var key = await Fx.KeyAsync(9600);

        var r = await rig.RunAsync("priority", "above", "--pid", "9600", "--elevate");

        Assert.Equal(0, r.ExitCode);
        var request = Assert.Single(rig.Helper.Requests);
        Assert.Equal(HelperOperation.ProcessPriority, request.Op);
        Assert.Equal(9600u, request.Pid);
        Assert.Equal(key.CreateTime, request.CreateTime);
        Assert.Equal(SettablePriority.AboveNormal, request.Priority);
        Assert.Equal((true, 0L), Assert.Single(rig.Helper.ResultFileAtCall));
        Assert.Empty(rig.LeftoverFiles());
    }

    [Fact]
    public async Task Priority_denied_without_elevate_only_hints()
    {
        using var rig = new Rig { Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied") };

        var r = await rig.RunAsync("priority", "above", "--pid", "9600");

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("Add --elevate", r.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("on", true, "Efficiency mode is on")]
    [InlineData("off", false, "Efficiency mode is off")]
    public async Task Eco_switches_efficiency_mode(string setting, bool enabled, string line)
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("eco", setting, "--pid", "9600");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["eco:9600:" + enabled], rig.Control.Calls);
        Assert.Contains("9600 Discord.exe: " + line, r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eco_of_a_protected_process_is_refused_with_exit_5()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("eco", "on", "--pid", "4");

        Assert.Equal(5, r.ExitCode);
        Assert.Empty(rig.Control.Calls);
    }

    [Fact]
    public async Task Eco_denied_with_elevate_sends_the_fixed_helper_request()
    {
        using var rig = new Rig { Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied") };

        var r = await rig.RunAsync("eco", "off", "--pid", "9600", "--elevate");

        Assert.Equal(0, r.ExitCode);
        var request = Assert.Single(rig.Helper.Requests);
        Assert.Equal(HelperOperation.ProcessEfficiency, request.Op);
        Assert.Equal(9600u, request.Pid);
        Assert.False(request.Enable);
        Assert.Empty(rig.LeftoverFiles());
    }

    [Fact]
    public async Task Run_hands_the_command_line_to_the_launcher_as_typed()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("run", "notepad.exe C:\\temp\\a b.txt");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(("notepad.exe C:\\temp\\a b.txt", false), Assert.Single(rig.Launcher.Calls));
        Assert.Contains("Started: notepad.exe", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_admin_asks_for_elevation_and_says_so_on_stderr()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("run", "--admin", "regedit.exe");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(("regedit.exe", true), Assert.Single(rig.Launcher.Calls));
        Assert.Contains("Asking for administrator approval", r.Err, StringComparison.Ordinal);
        Assert.Contains("Started as administrator: regedit.exe", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_joins_several_arguments_and_quotes_the_ones_with_spaces()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("run", "--", "cmd", "/c", "echo hi");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("cmd /c \"echo hi\"", Assert.Single(rig.Launcher.Calls).CommandLine);
    }

    [Fact]
    public async Task Run_does_not_take_options_of_the_program_without_a_double_dash()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("run", "notepad.exe", "--foo");

        Assert.Equal(2, r.ExitCode);
        Assert.Empty(rig.Launcher.Calls);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", " ")]
    public async Task Run_without_a_command_exits_2(params string[] args)
    {
        using var rig = new Rig();

        var r = await rig.RunAsync(args);

        Assert.Equal(2, r.ExitCode);
        Assert.Empty(rig.Launcher.Calls);
    }

    [Fact]
    public async Task Run_that_fails_prints_the_reason_on_stdout_and_uses_the_exit_code()
    {
        using var rig = new Rig();
        rig.Launcher.Answer = OperationResult.Fail(OperationCode.NotFound, "no such file");

        var r = await rig.RunAsync("run", "nothing.exe");

        Assert.Equal(3, r.ExitCode);
        Assert.Contains("Not found (no such file)", r.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notepad.exe", "notepad.exe")]
    [InlineData("  notepad.exe  ", "notepad.exe")]
    public void A_single_argument_is_the_command_line_itself(string input, string expected) =>
        Assert.Equal(expected, RunCommand.BuildCommandLine([input]));

    [Theory]
    [InlineData("cmd|/c|echo hi", "cmd /c \"echo hi\"")]
    [InlineData("a|b\"c", "a \"b\\\"c\"")]
    [InlineData("a|C:\\dir with space\\", "a \"C:\\dir with space\\\\\"")]
    [InlineData("a|x\\\"y z", "a \"x\\\\\\\"y z\"")]
    [InlineData("a|", "a \"\"")]
    public void Several_arguments_are_quoted_the_way_the_windows_command_line_parser_reads_them(string joined, string expected) =>
        Assert.Equal(expected, RunCommand.BuildCommandLine(joined.Split('|')));
}
