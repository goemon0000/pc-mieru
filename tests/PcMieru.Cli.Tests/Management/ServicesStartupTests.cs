using System.Text.Json.Nodes;
using PcMieru.Wire;

namespace PcMieru.Cli.Tests.Management;

public sealed class ServicesStartupTests
{
    private static ScriptedServices Chain() => new(
    [
        ScriptedServices.Service("Alpha", ServiceState.Running, "Beta", "Gamma", "Ghost"),
        ScriptedServices.Service("Beta", ServiceState.Running, "Delta"),
        ScriptedServices.Service("Gamma", ServiceState.Stopped, "Epsilon"),
        ScriptedServices.Service("Delta", ServiceState.Running),
        ScriptedServices.Service("Epsilon", ServiceState.Running),
        ScriptedServices.Service("Solo", ServiceState.Running),
    ]);

    [Fact]
    public async Task Services_list_prints_a_table_and_the_count()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("services", "list");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Service", r.OutLines[0], StringComparison.Ordinal);
        Assert.Contains("Spooler", r.Out, StringComparison.Ordinal);
        Assert.Contains("running", r.Out, StringComparison.Ordinal);
        Assert.EndsWith("4 services\n", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_list_running_leaves_out_the_stopped_ones()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("services", "list", "--running");

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("wuauserv", r.Out, StringComparison.Ordinal);
        Assert.EndsWith("2 services\n", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_list_json_is_one_line_with_every_service()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("services", "list", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Single(r.OutLines);
        var array = Assert.IsType<JsonArray>(JsonNode.Parse(r.Out));
        Assert.Equal(4, array.Count);
        Assert.Equal("Spooler", array[0]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("services")]
    [InlineData("services", "frobnicate")]
    [InlineData("services", "start")]
    [InlineData("services", "start", "A", "B")]
    [InlineData("services", "list", "--yes")]
    [InlineData("services", "list", "--elevate")]
    [InlineData("services", "list", "extra")]
    [InlineData("services", "stop", "Spooler", "--json")]
    [InlineData("services", "stop", "Spooler", "--running")]
    public async Task A_bad_services_command_line_exits_2(params string[] args)
    {
        using var rig = new Rig { Services = Chain() };

        var r = await rig.RunAsync(args);

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Empty(rig.Services!.Calls);
        Assert.Empty(rig.Helper.Requests);
    }

    [Fact]
    public async Task Stop_shows_the_state_before_the_change_and_does_it()
    {
        using var rig = new Rig { Services = Chain() };

        var r = await rig.RunAsync("services", "stop", "Solo");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["stop:Solo"], rig.Services!.Calls);
        Assert.Contains("Solo: Current state: running / start type: manual", r.Out, StringComparison.Ordinal);
        Assert.Contains("Solo: Stopped", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_does_not_look_at_dependents_and_does_it()
    {
        using var rig = new Rig { Services = Chain() };

        var r = await rig.RunAsync("services", "start", "Alpha");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["start:Alpha"], rig.Services!.Calls);
        Assert.DoesNotContain("depend", r.Out, StringComparison.Ordinal);
        Assert.Contains("Alpha: Started", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_works_on_the_fake_service_with_a_stopped_dependent()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("services", "restart", "Spooler");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Spooler: Restarted", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_with_running_dependents_lists_them_and_changes_nothing_without_yes()
    {
        using var rig = new Rig { Services = Chain() };

        var r = await rig.RunAsync("services", "stop", "Alpha");

        Assert.Equal(1, r.ExitCode);
        Assert.Empty(rig.Services!.Calls);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("Running services that depend on this one: Beta, Ghost, Delta", r.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Gamma", r.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Epsilon", r.Out, StringComparison.Ordinal);
        Assert.Contains("--yes", r.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task Yes_goes_ahead_even_with_running_dependents(string verb)
    {
        using var rig = new Rig { Services = Chain() };

        var r = await rig.RunAsync("services", verb, "Alpha", "--yes");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal([verb + ":Alpha"], rig.Services!.Calls);
    }

    [Fact]
    public async Task An_unknown_service_exits_3_and_nothing_is_sent_anywhere()
    {
        using var rig = new Rig { Services = Chain() };

        var r = await rig.RunAsync("services", "stop", "Nonexistent", "--elevate");

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(rig.Services!.Calls);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("No service with that name", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_name_is_matched_ignoring_case_and_the_real_name_is_used()
    {
        using var rig = new Rig { Services = Chain() };

        var r = await rig.RunAsync("services", "start", "SOLO");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["start:Solo"], rig.Services!.Calls);
    }

    [Fact]
    public async Task A_denied_service_change_only_hints_without_elevate()
    {
        using var rig = new Rig { Services = Chain() };
        rig.Services!.Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied");

        var r = await rig.RunAsync("services", "start", "Solo");

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("Add --elevate", r.Err, StringComparison.Ordinal);
        Assert.Contains("Solo: Permission denied (Access is denied)", r.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("start", HelperOperation.ServiceStart)]
    [InlineData("stop", HelperOperation.ServiceStop)]
    [InlineData("restart", HelperOperation.ServiceRestart)]
    public async Task A_denied_service_change_with_elevate_goes_through_the_helper(string verb, HelperOperation op)
    {
        using var rig = new Rig { Services = Chain() };
        rig.Services!.Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied");

        var r = await rig.RunAsync("services", verb, "Solo", "--elevate");

        Assert.Equal(0, r.ExitCode);
        var request = Assert.Single(rig.Helper.Requests);
        Assert.Equal(op, request.Op);
        Assert.Equal("Solo", request.ServiceName);
        Assert.Matches("^[0-9a-f]{32}$", request.Nonce);
        Assert.Equal((true, 0L), Assert.Single(rig.Helper.ResultFileAtCall));
        Assert.Empty(rig.LeftoverFiles());
    }

    [Fact]
    public async Task A_helper_failure_is_reported_with_its_code()
    {
        using var rig = new Rig { Services = Chain() };
        rig.Services!.Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied");
        rig.Helper.Answer = request => new HelperOutcome(OperationCode.NotFound, false, new HelperResult(request.Nonce, "service-stop", OperationCode.NotFound, "gone"));

        var r = await rig.RunAsync("services", "stop", "Solo", "--elevate");

        Assert.Equal(3, r.ExitCode);
        Assert.Contains("Solo: Not found (gone)", r.Out, StringComparison.Ordinal);
        Assert.Empty(rig.LeftoverFiles());
    }

    private static StartupItem Item(string id, StartupProvider provider, string name, StartupState state, bool canToggle = true, bool needsHelper = false) =>
        new(id, provider, name, "C:\\Apps\\" + name + ".exe", state, canToggle, needsHelper);

    private static ScriptedStartup Items() => new(
    [
        Item("runHkcu:Discord", StartupProvider.RunHkcu, "Discord", StartupState.Enabled),
        Item("runHkcu:Steam", StartupProvider.RunHkcu, "Steam", StartupState.Disabled),
        Item("runHklm:SecurityHealth", StartupProvider.RunHklm, "SecurityHealth", StartupState.Enabled, needsHelper: true),
        Item("scheduledTask:Updater", StartupProvider.ScheduledTask, "Updater", StartupState.Unknown, canToggle: false),
    ]);

    [Fact]
    public async Task Startup_list_prints_ids_states_and_notes()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("startup", "list");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("runHkcu:Discord", r.Out, StringComparison.Ordinal);
        Assert.Contains("enabled", r.Out, StringComparison.Ordinal);
        Assert.Contains("needs administrator approval to switch", r.Out, StringComparison.Ordinal);
        Assert.Contains("list only (cannot be switched)", r.Out, StringComparison.Ordinal);
        Assert.EndsWith("4 items\n", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_list_json_is_one_line()
    {
        using var rig = new Rig();

        var r = await rig.RunAsync("startup", "list", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Single(r.OutLines);
        Assert.Equal(4, Assert.IsType<JsonArray>(JsonNode.Parse(r.Out)).Count);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("startup", "frobnicate")]
    [InlineData("startup", "enable")]
    [InlineData("startup", "disable", "a", "b")]
    [InlineData("startup", "list", "--elevate")]
    [InlineData("startup", "enable", "runHkcu:Steam", "--json")]
    public async Task A_bad_startup_command_line_exits_2(params string[] args)
    {
        using var rig = new Rig { Startup = Items() };

        var r = await rig.RunAsync(args);

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Empty(rig.Startup!.Calls);
    }

    [Fact]
    public async Task Disable_changes_an_enabled_per_user_item_without_the_helper()
    {
        using var rig = new Rig { Startup = Items() };

        var r = await rig.RunAsync("startup", "disable", "runHkcu:Discord");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["runHkcu:Discord:False"], rig.Startup!.Calls);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("Disabled (nothing was deleted", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enable_turns_a_disabled_item_on_and_the_id_is_matched_ignoring_case()
    {
        using var rig = new Rig { Startup = Items() };

        var r = await rig.RunAsync("startup", "enable", "RUNHKCU:steam");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["runHkcu:Steam:True"], rig.Startup!.Calls);
    }

    [Theory]
    [InlineData("enable", "runHkcu:Discord", "Already enabled")]
    [InlineData("disable", "runHkcu:Steam", "Already disabled")]
    public async Task An_item_already_in_the_wanted_state_is_left_alone(string verb, string id, string line)
    {
        using var rig = new Rig { Startup = Items() };

        var r = await rig.RunAsync("startup", verb, id);

        Assert.Equal(0, r.ExitCode);
        Assert.Empty(rig.Startup!.Calls);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains(line, r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_only_item_cannot_be_switched()
    {
        using var rig = new Rig { Startup = Items() };

        var r = await rig.RunAsync("startup", "enable", "scheduledTask:Updater");

        Assert.Equal(1, r.ExitCode);
        Assert.Empty(rig.Startup!.Calls);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("list-only", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_id_exits_3()
    {
        using var rig = new Rig { Startup = Items() };

        var r = await rig.RunAsync("startup", "disable", "runHkcu:Nothing", "--elevate");

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(rig.Startup!.Calls);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("No startup item with that ID", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_wide_item_that_is_denied_only_hints_without_elevate()
    {
        using var rig = new Rig { Startup = Items() };
        rig.Startup!.Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied");

        var r = await rig.RunAsync("startup", "disable", "runHklm:SecurityHealth");

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(rig.Helper.Requests);
        Assert.Contains("Add --elevate", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_wide_item_with_elevate_sends_the_fixed_helper_request()
    {
        using var rig = new Rig { Startup = Items() };
        rig.Startup!.Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied");

        var r = await rig.RunAsync("startup", "disable", "runHklm:SecurityHealth", "--elevate");

        Assert.Equal(0, r.ExitCode);
        var request = Assert.Single(rig.Helper.Requests);
        Assert.Equal(HelperOperation.StartupHklmSet, request.Op);
        Assert.Equal(StartupProvider.RunHklm, request.Provider);
        Assert.Equal("SecurityHealth", request.ValueName);
        Assert.False(request.Enable);
        Assert.Equal((true, 0L), Assert.Single(rig.Helper.ResultFileAtCall));
        Assert.Empty(rig.LeftoverFiles());
    }

    [Fact]
    public async Task A_per_user_item_that_is_denied_never_raises_an_administrator_prompt()
    {
        using var rig = new Rig { Startup = Items() };
        rig.Startup!.Forced = OperationResult.Fail(OperationCode.Denied, "Access is denied");

        var r = await rig.RunAsync("startup", "disable", "runHkcu:Discord", "--elevate");

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(rig.Helper.Requests);
    }
}
