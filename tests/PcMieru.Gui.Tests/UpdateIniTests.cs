using PcMieru.Update.Feed;

namespace PcMieru.Gui.Tests;

/// <summary>
/// gui.ini and the owner's consent (DESIGN-B 4): <c>update.mode</c> is read leniently (anything unknown is "not chosen"), written by a locked read-modify-replace that keeps
/// the keys it does not know, never lets an older object overwrite a newer choice, and says whether the choice reached the file. Every test works in a temp folder; the real gui.ini
/// is never named.
/// </summary>
public sealed class UpdateIniTests
{
    [Theory]
    [InlineData("auto", UpdateMode.Automatic)]
    [InlineData("notify", UpdateMode.Notify)]
    [InlineData("off", UpdateMode.Off)]
    [InlineData("AUTO", UpdateMode.Automatic)]
    [InlineData("  Notify  ", UpdateMode.Notify)]
    [InlineData("", UpdateMode.Ask)]
    [InlineData("ask", UpdateMode.Ask)]
    [InlineData("yes", UpdateMode.Ask)]
    [InlineData("auto,off", UpdateMode.Ask)]
    [InlineData("1", UpdateMode.Ask)]
    public void The_mode_word_is_read_and_anything_else_is_not_chosen(string word, UpdateMode expected)
    {
        var settings = new GuiSettings();
        settings.Apply("update.mode=" + word);

        Assert.Equal(expected, settings.UpdateMode);
    }

    [Fact]
    public void A_missing_key_or_a_file_that_cannot_be_read_is_not_chosen()
    {
        using var root = new Ui.TempRoot();
        Assert.Equal(UpdateMode.Ask, new GuiSettings().UpdateMode);
        Assert.Equal(UpdateMode.Ask, GuiSettings.Load(Path.Combine(root.Path, "missing.ini")).UpdateMode);

        var notAFile = Path.Combine(root.Path, "gui.ini");
        Directory.CreateDirectory(notAFile); // reading a folder as a file fails
        Assert.Equal(UpdateMode.Ask, GuiSettings.Load(notAFile).UpdateMode);

        var noKey = Path.Combine(root.Path, "other.ini");
        File.WriteAllText(noKey, "language=ja\ntheme=dark\n");
        Assert.Equal(UpdateMode.Ask, GuiSettings.Load(noKey).UpdateMode);
    }

    [Fact]
    public void Ask_has_no_line_and_the_three_choices_have_their_word()
    {
        var ask = new GuiSettings();
        Assert.DoesNotContain(ask.ToLines(), line => line.StartsWith("update.mode", StringComparison.Ordinal));

        foreach (var (mode, word) in new[] { (UpdateMode.Automatic, "auto"), (UpdateMode.Notify, "notify"), (UpdateMode.Off, "off") })
        {
            var settings = new GuiSettings();
            Assert.True(settings.TrySaveUpdateMode(mode));
            Assert.Contains("update.mode=" + word, settings.ToLines());
        }
    }

    [Fact]
    public void A_store_with_no_file_keeps_the_choice_in_memory_and_reports_it_saved()
    {
        var settings = new GuiSettings();

        Assert.True(settings.TrySaveUpdateMode(UpdateMode.Off));
        Assert.Equal(UpdateMode.Off, settings.UpdateMode);
        Assert.True(settings.TrySave());
    }

    [Fact]
    public void A_choice_reaches_the_file_by_a_replace_and_leaves_no_temporary_file()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini"); // the folder is made by the save
        var settings = new GuiSettings(ini) { Language = Language.Ja };

        Assert.True(settings.TrySaveUpdateMode(UpdateMode.Notify));
        Assert.Equal(UpdateMode.Notify, settings.UpdateMode);
        Assert.Contains("update.mode=notify", File.ReadAllLines(ini));
        Assert.Contains("language=ja", File.ReadAllLines(ini));
        Assert.Equal(UpdateMode.Notify, GuiSettings.Load(ini).UpdateMode);
        Assert.False(File.Exists(ini + ".tmp"));
        Assert.All(Directory.GetFiles(Path.GetDirectoryName(ini)!), file => Assert.Contains(Path.GetFileName(file), new[] { "gui.ini", "gui.ini.lock" }));

        // A second save replaces the file (the other branch of the save), and the other settings are still there.
        Assert.True(settings.TrySaveUpdateMode(UpdateMode.Off));
        Assert.Contains("update.mode=off", File.ReadAllLines(ini));
        Assert.Equal(Language.Ja, GuiSettings.Load(ini).Language);
        Assert.False(File.Exists(ini + ".tmp"));
    }

    [Fact]
    public void Keys_this_object_never_saw_are_kept_by_its_save()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllLines(ini, ["language=en", "future.key=7", "proc.cols.v=2", "update.mode=off", "zz.newer.build=yes"]);

        // This object was made before the file had those keys: it never read them.
        var writer = new GuiSettings(ini) { Theme = ThemeMode.Dark };
        Assert.True(writer.TrySave());

        var lines = File.ReadAllLines(ini);
        Assert.Contains("future.key=7", lines);
        Assert.Contains("proc.cols.v=2", lines);
        Assert.Contains("zz.newer.build=yes", lines);
        Assert.Contains("update.mode=off", lines);
        Assert.Contains("theme=dark", lines);

        // One that did read them keeps them too, and this object's own value of a key wins over the file's.
        var reader = GuiSettings.Load(ini);
        Assert.Equal("7", reader.GetValue("future.key"));
        reader.SetValue("future.key", "8");
        Assert.True(reader.TrySave());
        Assert.Contains("future.key=8", File.ReadAllLines(ini));
        Assert.Contains("zz.newer.build=yes", File.ReadAllLines(ini));
    }

    [Fact]
    public void An_older_object_never_overwrites_a_newer_choice_when_it_saves_later()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\n");
        var stale = GuiSettings.Load(ini);      // knows no choice
        var fresh = GuiSettings.Load(ini);

        Assert.True(fresh.TrySaveUpdateMode(UpdateMode.Notify));
        stale.Language = Language.Ja;           // something unrelated; the stale object saves it
        Assert.True(stale.TrySave());

        Assert.Contains("update.mode=notify", File.ReadAllLines(ini));
        Assert.Contains("language=ja", File.ReadAllLines(ini));
        Assert.Equal(UpdateMode.Notify, stale.UpdateMode); // and it learned the newest choice from the file

        // The same when the stale object held a choice of its own that has since been replaced.
        var holdsNotify = GuiSettings.Load(ini);
        Assert.Equal(UpdateMode.Notify, holdsNotify.UpdateMode);
        Assert.True(fresh.TrySaveUpdateMode(UpdateMode.Off));
        holdsNotify.IntervalMs = 5000;
        Assert.True(holdsNotify.TrySave());
        Assert.Contains("update.mode=off", File.ReadAllLines(ini));
        Assert.DoesNotContain("update.mode=notify", File.ReadAllLines(ini));
    }

    [Fact]
    public void The_newest_choice_wins_over_whatever_is_in_the_file()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        var first = new GuiSettings(ini);
        var second = new GuiSettings(ini);

        Assert.True(first.TrySaveUpdateMode(UpdateMode.Notify));
        Assert.True(second.TrySaveUpdateMode(UpdateMode.Off));   // never read the first one's choice, still the later choice

        Assert.Equal(UpdateMode.Off, GuiSettings.Load(ini).UpdateMode);
        Assert.True(first.TrySave());                             // the first one's ordinary save does not undo it
        Assert.Equal(UpdateMode.Off, GuiSettings.Load(ini).UpdateMode);
    }

    [Fact]
    public async Task Two_objects_saving_at_once_lose_neither_key_and_the_choice_stays()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        Assert.True(new GuiSettings(ini).TrySaveUpdateMode(UpdateMode.Notify));

        var a = GuiSettings.Load(ini);
        var b = GuiSettings.Load(ini);
        // This test is about the two files' contents, not about how long a click may wait (M3: half a second in the app): on a busy machine, with the other tests running,
        // 40 back-to-back saves of two writers can wait longer than that, so the patience is raised here (it failed once in a run of the tree before the round, at 2 s).
        a.LockAttempts = b.LockAttempts = 500;
        var failures = 0;

        Task Writer(GuiSettings settings, string key) => Task.Run(() =>
        {
            for (var i = 1; i <= 40; i++)
            {
                settings.SetInt(key, i);
                if (!settings.TrySave())
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        await Task.WhenAll(Writer(a, "writer.a"), Writer(b, "writer.b"));

        Assert.Equal(0, failures);
        var lines = File.ReadAllLines(ini);
        Assert.Contains("writer.a=40", lines);
        Assert.Contains("writer.b=40", lines);
        Assert.Contains("update.mode=notify", lines);
        Assert.False(File.Exists(ini + ".tmp"));
        Assert.Equal(UpdateMode.Notify, GuiSettings.Load(ini).UpdateMode);
    }

    [Fact]
    public void A_save_that_cannot_take_the_lock_says_so_and_changes_nothing()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\n");
        var before = File.ReadAllBytes(ini);
        var settings = UpdateKit.Impatient(GuiSettings.Load(ini));

        using (new HeldLock(ini))
        {
            Assert.False(settings.TrySaveUpdateMode(UpdateMode.Notify));
            Assert.Equal(UpdateMode.Ask, settings.UpdateMode);   // the choice did not happen
            Assert.False(settings.TrySave());
            settings.Save();                                      // the quiet one does not throw either
        }

        Assert.Equal(before, File.ReadAllBytes(ini));
        Assert.False(File.Exists(ini + ".tmp"));

        // When the other writer is gone the same call works.
        Assert.True(settings.TrySaveUpdateMode(UpdateMode.Notify));
        Assert.Equal(UpdateMode.Notify, settings.UpdateMode);
    }

    [Fact]
    public void A_file_that_cannot_be_written_says_so_and_leaves_the_mode_as_it_was()
    {
        using var root = new Ui.TempRoot();

        // The folder of the file is a file.
        var blocker = Path.Combine(root.Path, "blocker");
        File.WriteAllText(blocker, "not a folder");
        var underFile = UpdateKit.Impatient(new GuiSettings(Path.Combine(blocker, "gui.ini")));
        Assert.False(underFile.TrySaveUpdateMode(UpdateMode.Notify));
        Assert.Equal(UpdateMode.Ask, underFile.UpdateMode);

        // gui.ini is a folder.
        var asFolder = Path.Combine(root.Path, "gui.ini");
        Directory.CreateDirectory(asFolder);
        var folderSettings = UpdateKit.Impatient(new GuiSettings(asFolder));
        Assert.False(folderSettings.TrySaveUpdateMode(UpdateMode.Off));
        Assert.Equal(UpdateMode.Ask, folderSettings.UpdateMode);
        Assert.False(File.Exists(asFolder + ".tmp"));

        // gui.ini is read-only.
        var readOnly = Path.Combine(root.Path, "ro", "gui.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(readOnly)!);
        File.WriteAllText(readOnly, "language=en\n");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            var roSettings = UpdateKit.Impatient(GuiSettings.Load(readOnly));
            Assert.False(roSettings.TrySaveUpdateMode(UpdateMode.Notify));
            Assert.Equal(UpdateMode.Ask, roSettings.UpdateMode);
            Assert.False(File.Exists(readOnly + ".tmp"));
            Assert.Equal("language=en", File.ReadAllText(readOnly).Trim());
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    [Fact]
    public void An_unknown_mode_in_a_file_that_is_saved_again_does_not_become_a_choice()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllLines(ini, ["update.mode=sometimes", "keep.me=1"]);

        var settings = GuiSettings.Load(ini);
        Assert.Equal(UpdateMode.Ask, settings.UpdateMode);
        Assert.True(settings.TrySave());

        Assert.Equal(UpdateMode.Ask, GuiSettings.Load(ini).UpdateMode);
        Assert.Contains("keep.me=1", File.ReadAllLines(ini));
    }

    [Theory]
    [InlineData("--smoke", "s.json", "--fakes")]
    [InlineData("--measure", "m.jsonl", "--fakes")]
    [InlineData("--fakes")]
    public void A_smoke_or_measure_run_does_not_read_the_owners_choice_and_never_writes_one(params string[] args)
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=ja\nupdate.mode=auto\n");
        var bytes = File.ReadAllBytes(ini);
        Assert.True(GuiArgs.TryParse(args, out var options, out var problem), problem);

        var run = GuiSettings.ForRun(options, ini);

        Assert.Equal(UpdateMode.Ask, run.UpdateMode);     // what the owner chose is not what a smoke or a measurement runs on
        Assert.True(run.TrySaveUpdateMode(UpdateMode.Off)); // kept in memory only
        Assert.Equal(UpdateMode.Off, run.UpdateMode);
        Assert.True(run.TrySave());
        Assert.Equal(bytes, File.ReadAllBytes(ini));
        Assert.Equal([ini], root.Entries());
    }

    [Fact]
    public void A_run_on_fakes_does_not_start_the_update_check_that_would_show_the_dialog_and_the_start_says_step_5_must_be_excluded_too()
    {
        // Settings of a --fakes run are in memory (above); the window must not even ask for a choice in that run. The start is in Program.Main, which no test can call.
        var source = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));

        Assert.Matches(@"if \(options\.MeasureOutput is null && !options\.ForceFakes\)\s*\{\s*form\.Shown \+= \(_, _\) => form\.BeginInvoke\(form\.StartUpdates\);", source);
        Assert.Contains("STEP 5", source);
    }
}
