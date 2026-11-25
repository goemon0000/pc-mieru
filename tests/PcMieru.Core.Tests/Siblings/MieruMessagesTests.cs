using PcMieru.Core.Siblings;

namespace PcMieru.Core.Tests.Siblings;

/// <summary>DESIGN-C 2.2: the two window messages, and the one fixed action each receiver takes on them.</summary>
public sealed class MieruMessagesTests
{
    private const uint OpenChecksId = 0xC123;
    private const uint LanguageChangedId = 0xC124;

    [Fact]
    public void The_names_are_the_two_of_the_design_and_each_registers_to_its_own_nonzero_id()
    {
        Assert.Equal("PCMieru.OpenChecks.v1", MieruMessages.OpenChecksName);
        Assert.Equal("PCMieru.LanguageChanged.v1", MieruMessages.LanguageChangedName);
        Assert.NotEqual(0u, MieruMessages.OpenChecks);
        Assert.NotEqual(0u, MieruMessages.LanguageChanged);
        Assert.NotEqual(MieruMessages.OpenChecks, MieruMessages.LanguageChanged);
        Assert.InRange(MieruMessages.OpenChecks, 0xC000u, 0xFFFFu);   // a registered message is in the string-message range
    }

    [Fact]
    public void The_main_window_shows_the_checks_on_the_open_checks_message_and_does_nothing_else()
    {
        Assert.Equal(MieruMessageAction.ShowChecks, MieruMessages.ForMainWindow(OpenChecksId, OpenChecksId));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForMainWindow(LanguageChangedId, OpenChecksId));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForMainWindow(0x0010, OpenChecksId));   // WM_CLOSE
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForMainWindow(0, OpenChecksId));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForMainWindow(0, 0));   // a message that failed to register matches nothing, not even WM_NULL
    }

    [Fact]
    public void The_party_window_reads_the_language_again_on_the_language_message_and_does_nothing_else()
    {
        Assert.Equal(MieruMessageAction.ReloadLanguage, MieruMessages.ForParty(LanguageChangedId, LanguageChangedId));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForParty(OpenChecksId, LanguageChangedId));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForParty(0x0010, LanguageChangedId));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForParty(0, LanguageChangedId));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForParty(0, 0));
    }

    [Fact]
    public void The_real_ids_dispatch_the_same_way_and_never_cross()
    {
        Assert.Equal(MieruMessageAction.ShowChecks, MieruMessages.ForMainWindow(MieruMessages.OpenChecks));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForMainWindow(MieruMessages.LanguageChanged));
        Assert.Equal(MieruMessageAction.ReloadLanguage, MieruMessages.ForParty(MieruMessages.LanguageChanged));
        Assert.Equal(MieruMessageAction.None, MieruMessages.ForParty(MieruMessages.OpenChecks));
    }
}
