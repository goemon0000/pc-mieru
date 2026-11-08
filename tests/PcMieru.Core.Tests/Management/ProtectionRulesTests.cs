using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

public class ProtectionRulesTests
{
    private const string System32 = @"C:\Windows\System32";
    private static readonly SelfIdentity Self = new(5000, @"C:\Program Files\PC Mieru");

    private static ProcessFacts Facts(uint pid = 1234, string? path = @"C:\Tools\app.exe", bool? critical = false, bool? protection = false) =>
        new(pid, path, critical, protection);

    private static ProtectionAssessment Evaluate(ProcessFacts facts) => ProtectionRules.Evaluate(facts, Self, System32);

    [Fact]
    public void A_fully_answered_ordinary_process_is_allowed()
    {
        var verdict = Evaluate(Facts());
        Assert.Equal(ProtectionOutcome.Allowed, verdict.Outcome);
        Assert.True(verdict.MayOperate);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public void Pid_0_and_4_are_the_system(uint pid)
    {
        var verdict = Evaluate(Facts(pid));
        Assert.Equal(ProtectionReason.SystemProcess, verdict.Reason);
        Assert.False(verdict.MayOperate);
    }

    [Fact]
    public void Our_own_pid_is_protected_even_when_the_path_is_unknown()
    {
        var verdict = Evaluate(Facts(5000, path: null, critical: null, protection: null));
        Assert.Equal(ProtectionReason.Self, verdict.Reason);
        Assert.Equal(ProtectionOutcome.Protected, verdict.Outcome);
    }

    [Theory]
    [InlineData(@"C:\Program Files\PC Mieru\PCMieru.exe")]
    [InlineData(@"c:\program files\pc mieru\pcmi.exe")]
    [InlineData(@"C:\Program Files\PC Mieru\PCMieru.Helper.exe")]
    [InlineData(@"C:\Program Files\PC Mieru\PCMieru.SensorHost.exe")]
    [InlineData(@"\\?\C:\Program Files\PC Mieru\PCMieru.exe")]
    public void Our_executables_in_our_folder_are_protected(string path)
    {
        Assert.Equal(ProtectionReason.Self, Evaluate(Facts(path: path)).Reason);
    }

    [Fact]
    public void A_same_named_file_in_another_folder_is_not_us()
    {
        var verdict = Evaluate(Facts(path: @"C:\Users\x\Downloads\PCMieru.exe"));
        Assert.Equal(ProtectionOutcome.Allowed, verdict.Outcome);
    }

    [Fact]
    public void Critical_processes_are_protected()
    {
        Assert.Equal(ProtectionReason.Critical, Evaluate(Facts(critical: true)).Reason);
    }

    [Fact]
    public void Protected_process_levels_are_protected()
    {
        Assert.Equal(ProtectionReason.ProtectedProcess, Evaluate(Facts(protection: true)).Reason);
    }

    [Theory]
    [InlineData("smss.exe")]
    [InlineData("csrss.exe")]
    [InlineData("wininit.exe")]
    [InlineData("services.exe")]
    [InlineData("lsass.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("fontdrvhost.exe")]
    [InlineData("dwm.exe")]
    public void Windows_core_binaries_in_System32_are_protected(string name)
    {
        var verdict = Evaluate(Facts(path: System32 + "\\" + name));
        Assert.Equal(ProtectionReason.WindowsCore, verdict.Reason);
        Assert.False(verdict.MayOperate);
    }

    [Fact]
    public void A_core_name_outside_System32_is_just_a_process()
    {
        Assert.True(Evaluate(Facts(path: @"C:\Users\x\AppData\dwm.exe")).MayOperate);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(@"C:\Tools\app.exe", null, false)]
    [InlineData(@"C:\Tools\app.exe", false, null)]
    public void Any_unanswered_question_refuses(string? path, bool? critical, bool? protection)
    {
        var verdict = Evaluate(Facts(path: path, critical: critical, protection: protection));
        Assert.Equal(ProtectionOutcome.Unknown, verdict.Outcome);
        Assert.Equal(ProtectionReason.CannotConfirm, verdict.Reason);
        Assert.False(verdict.MayOperate);
    }

    [Fact]
    public void A_definite_protected_answer_wins_over_an_unanswered_one()
    {
        var verdict = Evaluate(Facts(path: null, critical: true, protection: null));
        Assert.Equal(ProtectionOutcome.Protected, verdict.Outcome);
        Assert.Equal(ProtectionReason.Critical, verdict.Reason);
    }

    [Fact]
    public void Every_reason_has_a_message_key()
    {
        foreach (var reason in Enum.GetValues<ProtectionReason>().Where(r => r != ProtectionReason.None))
        {
            Assert.StartsWith("mgmt.", ProcessGuard.KeyFor(reason));
        }
    }
}
