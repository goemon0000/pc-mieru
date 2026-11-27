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
    public void Later_does_not_write_back_the_older_values_this_window_loaded_over_what_another_window_saved_since()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\ntheme=light\ninterval=1000\n");
        var first = GuiSettings.Load(ini);      // this window read the file at its start
        var second = GuiSettings.Load(ini);     // another PC Mieru window

        second.Language = Language.Ja;
        second.Theme = ThemeMode.Dark;
        second.IntervalMs = 5000;
        second.SetValue("win.w", "1234");
        Assert.True(second.TrySave());

        Assert.True(first.TrySavePartyImportLater());   // first still holds en / light / 1000

        var lines = File.ReadAllLines(ini);
        Assert.Contains("language=ja", lines);
        Assert.Contains("theme=dark", lines);
        Assert.Contains("interval=5000", lines);
        Assert.Contains("win.w=1234", lines);
        Assert.Contains("party.import=later", lines);
        var loaded = GuiSettings.Load(ini);
        Assert.Equal(Language.Ja, loaded.Language);
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal(5000, loaded.IntervalMs);
        Assert.True(loaded.PartyImportLater);
    }

    [Fact]
    public void Later_changes_only_its_own_line_and_every_other_line_stays_as_it_is_on_disk()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "# a note\nlanguage=EN\ninterval=700\nparty.import = no\nfuture.key=keep\n");

        Assert.True(GuiSettings.Load(ini).TrySavePartyImportLater());

        Assert.Equal(["# a note", "language=EN", "interval=700", "party.import=later", "future.key=keep"], File.ReadAllLines(ini));
        Assert.False(File.Exists(ini + ".tmp"));
    }

    [Fact]
    public void Later_adds_its_line_at_the_end_and_a_file_that_is_not_there_gets_that_line_only()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=ja\ntheme=dark\n");
        Assert.True(GuiSettings.Load(ini).TrySavePartyImportLater());
        Assert.Equal(["language=ja", "theme=dark", "party.import=later"], File.ReadAllLines(ini));

        var fresh = Path.Combine(root.Path, "fresh", "gui.ini");
        var settings = new GuiSettings(fresh) { Theme = ThemeMode.Dark };   // a value of this object's own, which nobody saved
        Assert.True(settings.TrySavePartyImportLater());
        Assert.Equal(["party.import=later"], File.ReadAllLines(fresh));
        Assert.True(settings.PartyImportLater);
    }

    [Fact]
    public void A_save_that_cannot_get_the_lock_fails_and_leaves_the_file_and_the_memory_as_they_were()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\n");
        var settings = GuiSettings.Load(ini);
        settings.LockAttempts = 3;
        settings.LockWaitMs = 1;

        using (new FileStream(ini + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(settings.TrySavePartyImportLater());
        }

        Assert.Equal(["language=en"], File.ReadAllLines(ini));
        Assert.False(settings.PartyImportLater);
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
