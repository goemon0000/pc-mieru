using Microsoft.Win32;
using PcMieru.Core.Siblings;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Siblings;

/// <summary>
/// DESIGN-C 3 item 5: the logon setting, against the in-memory store only. No test here (or anywhere) builds the real registry store: the real HKCU Run and StartupApproved\Run of the
/// machine are never read or written by a test.
/// </summary>
public sealed class PartyLogonTests
{
    private const string Exe = @"C:\Apps\PCMieru\PCMieru.Party.exe";
    private const string Name = PartyLogon.ValueName;
    private static readonly string Wanted = "\"" + Exe + "\" --logon";

    private static byte[] Disabled() => StartupApprovedCodec.EncodeDisabled(132_000_000_000_000_000);

    // --- the value ---

    [Fact]
    public void The_value_is_the_quoted_absolute_path_and_the_logon_argument_under_the_name_PCMieru_Party()
    {
        Assert.Equal("PCMieru.Party", PartyLogon.ValueName);
        Assert.Equal(Wanted, PartyLogon.ExpectedValue(Exe));
        Assert.Equal("\"C:\\Program Files\\PC Mieru\\PCMieru.Party.exe\" --logon", PartyLogon.ExpectedValue(@"C:\Program Files\PC Mieru\PCMieru.Party.exe"));
    }

    // --- Read: every state ---

    [Fact]
    public void Nothing_there_is_off()
    {
        var store = new MemoryRunStore();
        Assert.Equal(PartyLogonState.Off, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void A_stamp_without_a_value_is_still_off()
    {
        var store = new MemoryRunStore();
        store.SeedApproval(Name, StartupApprovedCodec.EncodeEnabled());
        Assert.Equal(PartyLogonState.Off, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void Our_value_with_no_stamp_is_on_because_windows_runs_an_entry_that_has_none()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, Exe));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(6u)]
    public void Our_value_with_an_enabled_stamp_is_on(uint flag)
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        var stamp = new byte[StartupApprovedCodec.Length];
        BitConverter.TryWriteBytes(stamp, flag);
        store.SeedApproval(Name, stamp);
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, Exe));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(7u)]
    public void Our_value_with_a_disabled_stamp_is_disabled_in_windows(uint flag)
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        var stamp = new byte[StartupApprovedCodec.Length];
        BitConverter.TryWriteBytes(stamp, flag);
        store.SeedApproval(Name, stamp);
        Assert.Equal(PartyLogonState.DisabledInWindows, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void A_value_that_names_another_exe_is_another_placement_whatever_its_stamp_says()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, "\"D:\\Other\\PCMieru.Party.exe\" --logon");
        Assert.Equal(PartyLogonState.OtherPlacement, PartyLogon.Read(store, Exe));
        store.SeedApproval(Name, Disabled());
        Assert.Equal(PartyLogonState.OtherPlacement, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void The_path_is_compared_without_case_and_after_it_is_made_absolute()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, "\"c:\\apps\\pcmieru\\..\\PCMieru\\PCMieru.PARTY.exe\" --logon");
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, Exe));
    }

    [Theory]
    [InlineData(@"C:\Apps\PCMieru\PCMieru.Party.exe --logon")]
    [InlineData("\"C:\\Apps\\PCMieru\\PCMieru.Party.exe\"")]
    [InlineData("\"C:\\Apps\\PCMieru\\PCMieru.Party.exe\" --other")]
    [InlineData("\"C:\\Apps\\PCMieru\\PCMieru.Party.exe --logon")]
    [InlineData("\"\" --logon")]
    [InlineData("")]
    public void A_command_line_that_is_not_the_form_we_write_is_unknown(string text)
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, text);
        Assert.Equal(PartyLogonState.Unknown, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void A_value_of_another_type_is_unknown()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, "5", RegistryValueKind.DWord);
        Assert.Equal(PartyLogonState.Unknown, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void An_expand_string_value_of_our_form_is_read_like_a_string()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted, RegistryValueKind.ExpandString);
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, Exe));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[0])]
    public void A_stamp_of_an_unknown_format_is_unknown_not_enabled(byte[] stamp)
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.SeedApproval(Name, stamp);
        Assert.Equal(PartyLogonState.Unknown, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void A_stamp_of_another_type_is_unknown()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.SeedApproval(Name, new byte[StartupApprovedCodec.Length], RegistryValueKind.DWord);
        Assert.Equal(PartyLogonState.Unknown, PartyLogon.Read(store, Exe));
    }

    [Theory]
    [InlineData(RunStoreCall.ReadRun)]
    [InlineData(RunStoreCall.ReadApproval)]
    public void A_read_that_fails_is_unknown_and_never_throws(RunStoreCall call)
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.FailOn(call, new IOException("injected"));
        Assert.Equal(PartyLogonState.Unknown, PartyLogon.Read(store, Exe));
    }

    [Theory]
    [InlineData(RunStoreCall.ReadRun)]
    [InlineData(RunStoreCall.ReadApproval)]
    public void A_read_that_is_denied_is_unknown_not_enabled(RunStoreCall call)
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.DenyOn(call);
        Assert.Equal(PartyLogonState.Unknown, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void A_read_touches_only_our_two_values_and_writes_nothing()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        PartyLogon.Read(store, Exe);
        Assert.All(store.Calls, c => Assert.Equal(Name, c.Name));
        Assert.DoesNotContain(store.Calls, c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
    }

    // --- Enable ---

    [Fact]
    public void Enable_writes_the_run_value_and_leaves_an_absent_stamp_absent()
    {
        var store = new MemoryRunStore();
        var result = PartyLogon.Enable(store, Exe);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(PartyLogonFailure.None, result.Failure);
        Assert.Equal(new RunValue(RegistryValueKind.String, Wanted), store.ReadRun(Name));
        Assert.Null(store.ReadApproval(Name));
        Assert.Equal([Name], store.RunNames);
        Assert.Empty(store.ApprovalNames);
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, Exe));
        Assert.All(store.Calls, c => Assert.Equal(Name, c.Name));
    }

    [Fact]
    public void Enable_turns_a_disabled_stamp_back_on()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.SeedApproval(Name, Disabled());
        Assert.Equal(PartyLogonState.DisabledInWindows, PartyLogon.Read(store, Exe));

        var result = PartyLogon.Enable(store, Exe);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(StartupApprovedCodec.EncodeEnabled(), store.ReadApproval(Name)!.Bytes);
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void Enable_leaves_an_enabled_stamp_exactly_as_it_is()
    {
        var store = new MemoryRunStore();
        var six = new byte[StartupApprovedCodec.Length];
        BitConverter.TryWriteBytes(six, 6u);
        store.SeedApproval(Name, six);
        Assert.True(PartyLogon.Enable(store, Exe).Ok);
        Assert.Equal(six, store.ReadApproval(Name)!.Bytes);
        Assert.DoesNotContain(store.Calls, c => c.Call == RunStoreCall.WriteApproval);
    }

    [Fact]
    public void Enable_replaces_the_registration_of_another_placement_because_the_user_asked_for_this_one()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, "\"D:\\Other\\PCMieru.Party.exe\" --logon");
        Assert.True(PartyLogon.Enable(store, Exe).Ok);
        Assert.Equal(Wanted, store.ReadRun(Name)!.Text);
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, Exe));
    }

    [Fact]
    public void Enable_is_repeatable()
    {
        var store = new MemoryRunStore();
        Assert.True(PartyLogon.Enable(store, Exe).Ok);
        Assert.True(PartyLogon.Enable(store, Exe).Ok);
        Assert.Equal([Name], store.RunNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PCMieru.Party.exe")]
    [InlineData(@"..\PCMieru.Party.exe")]
    [InlineData(@"\PCMieru.Party.exe")]
    public void Enable_refuses_a_path_that_is_not_absolute_and_writes_nothing(string path)
    {
        var store = new MemoryRunStore();
        var result = PartyLogon.Enable(store, path);
        Assert.False(result.Ok);
        Assert.Equal(PartyLogonFailure.InvalidPath, result.Failure);
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData(RunStoreCall.ReadRun)]
    [InlineData(RunStoreCall.WriteRun)]
    public void Enable_with_a_failing_first_step_reports_it_and_leaves_the_store_as_it_was(RunStoreCall call)
    {
        var store = new MemoryRunStore();
        store.FailOn(call, new IOException("injected"));
        var result = PartyLogon.Enable(store, Exe);
        Assert.False(result.Ok);
        Assert.Equal(PartyLogonFailure.Error, result.Failure);
        Assert.Empty(store.RunNames);
        Assert.Empty(store.ApprovalNames);
    }

    [Theory]
    [InlineData(RunStoreCall.ReadRun)]
    [InlineData(RunStoreCall.WriteRun)]
    [InlineData(RunStoreCall.ReadApproval)]
    public void Enable_denied_on_a_call_is_a_denied_result_and_not_an_exception(RunStoreCall call)
    {
        var store = new MemoryRunStore();
        store.DenyOn(call);
        var result = PartyLogon.Enable(store, Exe);
        Assert.False(result.Ok);
        Assert.Equal(PartyLogonFailure.Denied, result.Failure);
        Assert.Empty(store.RunNames);
    }

    [Fact]
    public void Enable_that_cannot_make_the_stamp_puts_the_run_value_back_as_it_was()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, "\"D:\\Other\\PCMieru.Party.exe\" --logon", RegistryValueKind.ExpandString);
        store.SeedApproval(Name, Disabled());
        store.DenyOn(RunStoreCall.WriteApproval);

        var result = PartyLogon.Enable(store, Exe);
        Assert.False(result.Ok);
        Assert.Equal(PartyLogonFailure.Denied, result.Failure);
        Assert.Equal(new RunValue(RegistryValueKind.ExpandString, "\"D:\\Other\\PCMieru.Party.exe\" --logon"), store.ReadRun(Name));
        Assert.Equal(Disabled(), store.ReadApproval(Name)!.Bytes);
    }

    [Fact]
    public void Enable_that_cannot_make_the_stamp_removes_a_run_value_that_was_not_there_before()
    {
        var store = new MemoryRunStore();
        store.SeedApproval(Name, Disabled());
        store.FailOn(RunStoreCall.WriteApproval, new IOException("injected"));
        var result = PartyLogon.Enable(store, Exe);
        Assert.False(result.Ok);
        Assert.Equal(PartyLogonFailure.Error, result.Failure);
        Assert.Empty(store.RunNames);
    }

    [Fact]
    public void Enable_reports_not_verified_when_the_read_back_does_not_show_the_setting()
    {
        // The stamp is rewritten but a store that drops the write (a stand-in for a registry that did not take it) shows it as still disabled.
        var store = new DroppingStampStore();
        store.SeedApproval(Name, Disabled());
        var result = PartyLogon.Enable(store, Exe);
        Assert.False(result.Ok);
        Assert.Equal(PartyLogonFailure.NotVerified, result.Failure);
        Assert.Contains("DisabledInWindows", result.Detail);
    }

    private sealed class DroppingStampStore : IRunStore
    {
        private readonly MemoryRunStore _inner = new();

        public void SeedApproval(string name, byte[] bytes) => _inner.SeedApproval(name, bytes);

        public RunValue? ReadRun(string name) => _inner.ReadRun(name);

        public void WriteRun(string name, string text, RegistryValueKind kind = RegistryValueKind.String) => _inner.WriteRun(name, text, kind);

        public void DeleteRun(string name) => _inner.DeleteRun(name);

        public ApprovalValue? ReadApproval(string name) => _inner.ReadApproval(name);

        public void WriteApproval(string name, byte[] bytes, RegistryValueKind kind = RegistryValueKind.Binary)
        {
            // dropped
        }

        public void DeleteApproval(string name) => _inner.DeleteApproval(name);
    }

    // --- Disable ---

    [Fact]
    public void Disable_removes_our_run_value_and_our_stamp_and_nothing_else()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.SeedApproval(Name, Disabled());
        store.SeedRun("Other", "\"C:\\other.exe\"");
        store.SeedApproval("Other", StartupApprovedCodec.EncodeEnabled());

        var result = PartyLogon.Disable(store);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(["Other"], store.RunNames);
        Assert.Equal(["Other"], store.ApprovalNames);
        Assert.Equal(PartyLogonState.Off, PartyLogon.Read(store, Exe));
        Assert.All(store.Calls, c => Assert.Equal(Name, c.Name));
    }

    [Fact]
    public void Disable_with_nothing_there_is_fine()
    {
        var store = new MemoryRunStore();
        Assert.True(PartyLogon.Disable(store).Ok);
    }

    [Theory]
    [InlineData(RunStoreCall.DeleteRun)]
    [InlineData(RunStoreCall.DeleteApproval)]
    [InlineData(RunStoreCall.ReadRun)]
    [InlineData(RunStoreCall.ReadApproval)]
    public void Disable_with_a_failing_call_returns_a_result_and_never_throws(RunStoreCall call)
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.SeedApproval(Name, StartupApprovedCodec.EncodeEnabled());
        store.FailOn(call, new IOException("injected"));
        var result = PartyLogon.Disable(store);
        Assert.False(result.Ok);
        Assert.Equal(PartyLogonFailure.Error, result.Failure);
    }

    [Fact]
    public void Disable_denied_on_the_run_value_does_not_touch_the_stamp()
    {
        var store = new MemoryRunStore();
        store.SeedRun(Name, Wanted);
        store.SeedApproval(Name, StartupApprovedCodec.EncodeEnabled());
        store.DenyOn(RunStoreCall.DeleteRun);
        var result = PartyLogon.Disable(store);
        Assert.Equal(PartyLogonFailure.Denied, result.Failure);
        Assert.NotNull(store.ReadApproval(Name));
        Assert.DoesNotContain(store.Calls, c => c.Call == RunStoreCall.DeleteApproval);
    }

    // --- the memory store itself ---

    [Fact]
    public void The_memory_store_keeps_type_and_raw_content_and_a_healed_store_works_again()
    {
        var store = new MemoryRunStore();
        store.WriteRun("a", "x %PATH%", RegistryValueKind.ExpandString);
        Assert.Equal(new RunValue(RegistryValueKind.ExpandString, "x %PATH%"), store.ReadRun("A"));
        var bytes = new byte[] { 1, 2, 3 };
        store.WriteApproval("a", bytes, RegistryValueKind.Binary);
        bytes[0] = 99;
        Assert.Equal([1, 2, 3], store.ReadApproval("a")!.Bytes);

        store.DenyOn(RunStoreCall.ReadRun);
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadRun("a"));
        store.Heal();
        Assert.NotNull(store.ReadRun("a"));
        Assert.Throws<ArgumentException>(() => store.WriteRun("a", "x", RegistryValueKind.DWord));
    }

    [Fact]
    public void The_real_store_has_not_been_built_by_this_test_run()
    {
        Assert.Equal(0, RegistryRunStore.Constructed);
    }
}
