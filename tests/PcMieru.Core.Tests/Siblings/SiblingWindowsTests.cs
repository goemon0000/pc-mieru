using PcMieru.Core.Siblings;

namespace PcMieru.Core.Tests.Siblings;

/// <summary>
/// DESIGN-C 2.2 and 3 items 2-4: the one rule both exes use to find a sibling window. A process of the same session whose image is the same file as the EXPECTED exe
/// (compared without case, after GetFullPath), and never ourselves. Moved here from the Party.Core tests, with the same cases.
/// </summary>
public sealed class SiblingWindowsTests
{
    private const string Ours = @"C:\Apps\PCMieru\PCMieru.Party.exe";

    private static IReadOnlyList<int> Pick(int selfPid, int selfSession, params (int Pid, int Session, string? Path)[] others) =>
        SiblingWindows.Pick(others, selfPid, selfSession, Ours);

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
    public void The_other_exe_of_the_same_folder_is_not_picked()
    {
        // The expected file is one exact file: the main window's exe in the same folder is not the Party's sibling.
        Assert.Empty(Pick(100, 1, (200, 1, @"C:\Apps\PCMieru\PCMieru.exe")));
    }

    [Fact]
    public void Our_own_process_is_not_picked()
    {
        Assert.Empty(Pick(100, 1, (100, 1, Ours)));
        Assert.Equal(new[] { 200 }, Pick(100, 1, (100, 1, Ours), (200, 1, Ours)));
    }

    [Fact]
    public void Nothing_is_picked_when_the_expected_path_is_unknown()
    {
        Assert.Empty(SiblingWindows.Pick([(200, 1, (string?)Ours)], 100, 1, null));
        Assert.Empty(SiblingWindows.Pick([(200, 1, (string?)Ours)], 100, 1, "  "));
    }

    [Fact]
    public void The_expected_exe_is_beside_the_running_one_and_never_found_by_name()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "PCMieru.Party.exe"), SiblingWindows.ExpectedPath("PCMieru.Party.exe"));
    }

    [Fact]
    public void Looking_for_a_sibling_that_is_not_running_finds_nothing_and_posting_to_it_posts_nothing()
    {
        // No process runs under this name: every call answers "none" without throwing, and a message id 0 is never posted.
        Assert.Empty(SiblingWindows.Find("PCMieru.NoSuchSibling.exe"));
        Assert.False(SiblingWindows.Activate("PCMieru.NoSuchSibling.exe"));
        Assert.Equal(0, SiblingWindows.Notify("PCMieru.NoSuchSibling.exe", 0xC123u, allowForeground: true));
        Assert.Equal(0, SiblingWindows.Notify("PCMieru.Party.exe", 0, allowForeground: false));
    }
}
