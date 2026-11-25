namespace PcMieru.Party.Core.Tests;

/// <summary>DESIGN-C 3 item 2: a second start brings forward only a Party of the same session and the same image; never itself.</summary>
public sealed class ResidentActivationTests
{
    private const string Ours = @"C:\Apps\PCMieru\PCMieru.Party.exe";

    private static IReadOnlyList<int> Pick(int selfPid, int selfSession, params (int Pid, int Session, string? Path)[] others) =>
        ResidentActivation.Pick(others, selfPid, selfSession, Ours);

    [Fact]
    public void The_same_session_and_the_same_path_is_picked()
    {
        Assert.Equal(new[] { 200 }, Pick(100, 1, (200, 1, Ours)));
    }

    [Fact]
    public void The_path_is_compared_without_case_and_after_normalising()
    {
        Assert.Equal(new[] { 200, 300 }, Pick(100, 1,
            (200, 1, @"c:\apps\pcmieru\PCMIERU.PARTY.EXE"),
            (300, 1, @"C:\Apps\PCMieru\..\PCMieru\PCMieru.Party.exe")));
    }

    [Fact]
    public void Another_session_is_not_picked()
    {
        Assert.Empty(Pick(100, 1, (200, 2, Ours)));
    }

    [Fact]
    public void Another_path_is_not_picked()
    {
        Assert.Empty(Pick(100, 1, (200, 1, @"C:\Other\PCMieru.Party.exe"), (201, 1, null), (202, 1, "")));
    }

    [Fact]
    public void Our_own_process_is_not_picked()
    {
        Assert.Empty(Pick(100, 1, (100, 1, Ours)));
        Assert.Equal(new[] { 200 }, Pick(100, 1, (100, 1, Ours), (200, 1, Ours)));
    }

    [Fact]
    public void Nothing_is_picked_when_our_own_path_is_unknown()
    {
        Assert.Empty(ResidentActivation.Pick([(200, 1, (string?)Ours)], 100, 1, null));
    }
}
