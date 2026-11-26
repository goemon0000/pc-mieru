namespace PcMieru.Gui.Tests;

/// <summary>
/// gui.ini's party.import (DESIGN-C 4.1 item 2): the one word "later", read like the other keys and saved through the locked write that keeps every other line. Temporary folders only; the
/// real gui.ini is never named.
/// </summary>
public sealed class PartyImportIniTests
{
    [Theory]
    [InlineData("later", true)]
    [InlineData("LATER", true)]
    [InlineData("  later  ", true)]
    [InlineData("", false)]
    [InlineData("now", false)]
    [InlineData("1", false)]
    public void Only_the_word_later_reads_as_later(string word, bool expected)
    {
        var settings = new GuiSettings();
        Assert.False(settings.PartyImportLater);
        settings.Apply("party.import=" + word);
        Assert.Equal(expected, settings.PartyImportLater);
    }

    [Fact]
    public void A_missing_file_or_one_without_the_key_is_not_later()
    {
        using var root = new Ui.TempRoot();
        Assert.False(GuiSettings.Load(Path.Combine(root.Path, "missing.ini")).PartyImportLater);
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=ja\ntheme=dark\n");
        Assert.False(GuiSettings.Load(ini).PartyImportLater);
    }

    [Fact]
    public void Later_is_saved_through_the_locked_write_and_everything_else_in_the_file_stays()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\nupdate.mode=notify\nwin.w=1111\nfuture.key=keep\n");
        var settings = GuiSettings.Load(ini);

        Assert.True(settings.TrySavePartyImportLater());

        var text = File.ReadAllText(ini);
        Assert.Contains("party.import=later", text);
        Assert.Contains("update.mode=notify", text);
        Assert.Contains("win.w=1111", text);
        Assert.Contains("future.key=keep", text);
        Assert.True(GuiSettings.Load(ini).PartyImportLater);
        Assert.False(File.Exists(ini + ".tmp"));
    }

    [Fact]
    public void An_older_object_saving_later_does_not_erase_the_choice_the_file_already_holds()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\n");
        var seenBefore = GuiSettings.Load(ini);          // loaded before the choice was made
        Assert.True(GuiSettings.Load(ini).TrySavePartyImportLater());

        seenBefore.Theme = ThemeMode.Dark;
        Assert.True(seenBefore.TrySave());               // an ordinary save of another setting

        Assert.True(GuiSettings.Load(ini).PartyImportLater);
        Assert.Contains("theme=dark", File.ReadAllText(ini));
    }

    [Fact]
    public void A_store_with_no_file_remembers_later_in_memory_and_writes_nothing()
    {
        using var root = new Ui.TempRoot();
        var settings = new GuiSettings();
        Assert.True(settings.TrySavePartyImportLater());
        Assert.True(settings.PartyImportLater);
        Assert.Empty(root.Entries());
    }
}
