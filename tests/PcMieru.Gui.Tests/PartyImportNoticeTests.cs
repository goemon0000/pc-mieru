using PcMieru.Core.Siblings;

namespace PcMieru.Gui.Tests;

/// <summary>
/// DESIGN-C 4.1 items 1-2, the main window's side: gui.ini's party.import, the first-run notice, and the Parts Life page's line. The window only notices that old progress is waiting; it never
/// imports. Every test uses temporary folders (the roaming folder, gui.ini); no real folder is named, and no process is started (the launcher is a fake, dialogs answer by themselves).
/// </summary>
public sealed class PartyImportNoticeTests
{
    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly bool? _auto;

        public Rig(Language language = Language.En, GuiSettings? settings = null, string? roamingRoot = null)
        {
            _locale = Harness.Locale(language);
            _auto = Dialogs.AutoAnswer;
            Dialogs.AutoAnswer = false; // a dialog never opens and never waits for a click
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Context = new GuiContext(Services, Pump, new ThemeService(ThemeMode.Light), settings ?? new GuiSettings())
            {
                PartyRoamingRoot = roamingRoot,
                PartyLauncherOverride = new PartyLauncher(() => false, _ => true, (path, arguments) =>
                {
                    Starts.Add(arguments);
                    return true;
                }),
            };
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiContext Context { get; }

        /// <summary>The command lines the fake launcher was asked to start.</summary>
        public List<string> Starts { get; } = [];

        public void Dispose()
        {
            Dialogs.AutoAnswer = _auto;
            Pump.Dispose();
            Services.Dispose();
            _locale.Dispose();
        }
    }

    private static string Waiting(string root, params string[] folders)
    {
        foreach (var folder in folders)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"), "{}");
        }

        return root;
    }

    // --- the notice ---

    [Fact]
    public void The_notice_names_the_old_app_it_found_and_has_two_buttons_with_the_first_as_the_default()
    {
        using var rig = new Rig(Language.Ja);
        using (var life = new PartyImportDialog(rig.Context.Theme, PartyImportHintState.FromPartsLife))
        {
            Assert.Equal("パーツぐらしの続き", life.Text);
            Assert.Equal("旧パーツぐらし（PartsLife）の続きが見つかりました。パーツぐらしを開くと、引き継ぐかどうかを選べます。旧版の保存はそのまま残ります。", life.LeadLine);
            Assert.Equal("パーツぐらしを開く", life.OpenButton.Text);
            Assert.Equal("あとで", life.LaterButton.Text);
            Assert.Same(life.OpenButton, life.AcceptButton);
            Assert.Equal(PartyImportAnswer.Dismissed, life.Chosen);
        }

        using var former = new PartyImportDialog(rig.Context.Theme, PartyImportHintState.FromPartsParty);
        Assert.StartsWith("旧名のパーツぐらし（PartsParty）の続きが見つかりました。", former.LeadLine);
    }

    [Fact]
    public void The_notice_is_written_in_english_too()
    {
        using var rig = new Rig(Language.En);
        using var life = new PartyImportDialog(rig.Context.Theme, PartyImportHintState.FromPartsLife);
        Assert.Equal("Continue Parts Life", life.Text);
        Assert.Contains("(PartsLife)", life.LeadLine);
        Assert.Contains("stays as it is", life.LeadLine);
        Assert.Equal("Open Parts Life", life.OpenButton.Text);
        Assert.Equal("Later", life.LaterButton.Text);
        using var former = new PartyImportDialog(rig.Context.Theme, PartyImportHintState.FromPartsParty);
        Assert.Contains("(PartsParty)", former.LeadLine);
    }

    [Fact]
    public void The_buttons_choose_and_the_cross_does_not()
    {
        using var rig = new Rig();
        // (A button of a window that was never shown cannot be pressed with PerformClick; the smoke run presses them in a shown dialog. Answer is what each Click calls.)
        using (var open = new PartyImportDialog(rig.Context.Theme, PartyImportHintState.FromPartsLife))
        {
            open.Answer(PartyImportAnswer.Open);
            Assert.Equal(PartyImportAnswer.Open, open.Chosen);
            Assert.Equal(DialogResult.OK, open.DialogResult);
        }

        using (var later = new PartyImportDialog(rig.Context.Theme, PartyImportHintState.FromPartsLife))
        {
            later.Answer(PartyImportAnswer.Later);
            Assert.Equal(PartyImportAnswer.Later, later.Chosen);
            Assert.Equal(DialogResult.OK, later.DialogResult);
        }

        using var cross = new PartyImportDialog(rig.Context.Theme, PartyImportHintState.FromPartsLife);
        cross.Close();
        Assert.Equal(PartyImportAnswer.Dismissed, cross.Chosen);
    }

    [Fact]
    public void An_unattended_run_only_logs_the_notice_and_chooses_nothing()
    {
        using var rig = new Rig();
        var logged = Dialogs.Log.Count;
        Assert.Equal(PartyImportAnswer.Dismissed, PartyImportDialog.Show(null, rig.Context.Theme, PartyImportHintState.FromPartsLife));
        Assert.Equal(logged + 1, Dialogs.Log.Count);
        Assert.Contains("Continue Parts Life", Dialogs.Log[^1]);
        Assert.Empty(rig.Starts);
    }

    [Fact]
    public void The_window_shows_the_notice_when_old_progress_is_waiting_and_in_an_unattended_run_only_logs_it()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roamingRoot: Waiting(roaming.Path, "PartsLife"));
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var logged = Dialogs.Log.Count;

            var done = form.NoticePartyImportAsync();
            Assert.True(ui.Until(() => done.IsCompleted));

            Assert.Equal(logged + 1, Dialogs.Log.Count);
            Assert.Contains("Continue Parts Life", Dialogs.Log[^1]);
            Assert.False(rig.Context.Settings.PartyImportLater);
            Assert.Empty(rig.Starts);
        });
    }

    [Fact]
    public void The_window_says_nothing_when_there_is_nothing_to_say()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            var logged = 0;

            // nothing old
            using (var rig = new Rig(roamingRoot: roaming.Path))
            {
                using var form = new MainForm(rig.Context);
                _ = form.Handle;
                logged = Dialogs.Log.Count;
                var done = form.NoticePartyImportAsync();
                Assert.True(ui.Until(() => done.IsCompleted));
                Assert.Equal(logged, Dialogs.Log.Count);
            }

            // a save of the new window is already there
            using (var rig = new Rig(roamingRoot: Waiting(roaming.Path, "PartsLife", Path.Combine("PCMieru", "party"))))
            {
                using var form = new MainForm(rig.Context);
                _ = form.Handle;
                var done = form.NoticePartyImportAsync();
                Assert.True(ui.Until(() => done.IsCompleted));
                Assert.Equal(logged, Dialogs.Log.Count);
            }
        });
    }

    [Fact]
    public void A_run_that_names_no_roaming_folder_looks_nowhere_and_says_nothing()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(roamingRoot: null);
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var logged = Dialogs.Log.Count;
            var done = form.NoticePartyImportAsync();
            Assert.True(ui.Until(() => done.IsCompleted));
            Assert.Equal(logged, Dialogs.Log.Count);
            Assert.Null(rig.Context.PartyRoamingRoot);
        });
    }

    [Fact]
    public void Later_chosen_before_means_no_notice_this_time()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            var settings = new GuiSettings();
            settings.Apply("party.import=later");
            using var rig = new Rig(settings: settings, roamingRoot: Waiting(roaming.Path, "PartsLife"));
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var logged = Dialogs.Log.Count;
            var done = form.NoticePartyImportAsync();
            Assert.True(ui.Until(() => done.IsCompleted));
            Assert.Equal(logged, Dialogs.Log.Count);
        });
    }

    [Fact]
    public void Later_is_saved_to_gui_ini_and_the_cross_saves_nothing()
    {
        Ui.Run(ui =>
        {
            using var folder = new Ui.TempRoot();
            var ini = Path.Combine(folder.Path, "gui.ini");
            using var rig = new Rig(settings: new GuiSettings(ini));
            using var form = new MainForm(rig.Context);
            _ = form.Handle;

            form.ApplyPartyImportAnswer(PartyImportAnswer.Dismissed);
            Assert.True(form.PartyLaterSave.IsCompleted);
            Assert.False(File.Exists(ini));
            Assert.False(rig.Context.Settings.PartyImportLater);

            form.ApplyPartyImportAnswer(PartyImportAnswer.Later);
            Assert.True(ui.Until(() => form.PartyLaterSave.IsCompleted));
            Assert.Contains("party.import=later", File.ReadAllText(ini));
            Assert.True(GuiSettings.Load(ini).PartyImportLater);
            Assert.True(rig.Context.Settings.PartyImportLater);
            Assert.Empty(rig.Starts);
        });
    }

    [Fact]
    public void Later_does_not_hold_the_window_while_the_file_is_locked_and_is_saved_when_the_lock_goes()
    {
        Ui.Run(ui =>
        {
            using var folder = new Ui.TempRoot();
            var ini = Path.Combine(folder.Path, "gui.ini");
            using var rig = new Rig(settings: new GuiSettings(ini) { LockAttempts = 500, LockWaitMs = 10 });   // 5 s of retries
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var said = new List<string>();
            rig.Context.StatusMessage += said.Add;

            var holder = new FileStream(ini + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);   // another window is saving
            try
            {
                form.ApplyPartyImportAnswer(PartyImportAnswer.Later);
                Assert.False(form.PartyLaterSave.IsCompleted);   // the click came back while the save still waits for the lock
            }
            finally
            {
                holder.Dispose();
            }

            Assert.True(ui.Until(() => form.PartyLaterSave.IsCompleted));
            Assert.True(GuiSettings.Load(ini).PartyImportLater);
            Assert.True(rig.Context.Settings.PartyImportLater);
            Assert.Empty(said);
        });
    }

    [Fact]
    public void A_later_that_could_not_be_saved_is_said_on_the_status_line()
    {
        Ui.Run(ui =>
        {
            using var folder = new Ui.TempRoot();
            var ini = Path.Combine(folder.Path, "gui.ini");
            using var rig = new Rig(settings: new GuiSettings(ini) { LockAttempts = 3, LockWaitMs = 1 });
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var said = new List<string>();
            rig.Context.StatusMessage += said.Add;

            using (new FileStream(ini + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                form.ApplyPartyImportAnswer(PartyImportAnswer.Later);
                Assert.True(ui.Until(() => form.PartyLaterSave.IsCompleted));
            }

            Assert.Equal(["Could not save the 'later' choice"], said);
            Assert.False(rig.Context.Settings.PartyImportLater);
            Assert.False(File.Exists(ini));
        });
    }

    // --- the notice never opens over another dialog ---

    [Fact]
    public void The_notice_waits_while_a_dialog_is_open_over_the_window_and_comes_once_it_is_free()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roamingRoot: Waiting(roaming.Path, "PartsLife"));
            using var form = new MainForm(rig.Context) { FreePollMs = 5 };
            _ = form.Handle;
            form.Enabled = false;   // what a dialog open over the window does to it (a dialog disables its owner)
            Assert.True(form.IsBlockedByDialog);
            var logged = Dialogs.Log.Count;

            var done = form.NoticePartyImportAsync();
            Assert.False(ui.Until(() => done.IsCompleted, 400), "the notice is still waiting");
            Assert.Equal(logged, Dialogs.Log.Count);

            form.Enabled = true;
            Assert.False(form.IsBlockedByDialog);
            Assert.True(ui.Until(() => done.IsCompleted));
            Assert.Equal(logged + 1, Dialogs.Log.Count);
            Assert.Contains("Continue Parts Life", Dialogs.Log[^1]);
        });
    }

    [Fact]
    public void A_notice_that_is_waiting_when_the_window_closes_is_never_shown()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roamingRoot: Waiting(roaming.Path, "PartsLife"));
            using var form = new MainForm(rig.Context) { FreePollMs = 5 };
            _ = form.Handle;
            form.Enabled = false;
            var logged = Dialogs.Log.Count;

            var done = form.NoticePartyImportAsync();
            Assert.False(ui.Until(() => done.IsCompleted, 200));

            form.Dispose();
            Assert.True(ui.Until(() => done.IsCompleted));
            Assert.Equal(logged, Dialogs.Log.Count);
        });
    }

    // --- the real roaming folder is asked for off the UI thread ---

    [Fact]
    public void A_resolver_is_asked_on_a_worker_thread_once_for_the_notice_and_the_page_and_the_window_start_asks_nothing()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            Waiting(roaming.Path, "PartsLife");
            using var rig = new Rig();
            var asked = new List<int>();
            rig.Context.PartyRoamingRootResolver = () =>
            {
                lock (asked)
                {
                    asked.Add(Environment.CurrentManagedThreadId);
                }

                return roaming.Path;
            };
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            Assert.Empty(asked);   // making the window asked nothing
            Assert.Null(rig.Context.PartyRoamingRoot);   // "not resolved yet" is what the UI thread sees

            var logged = Dialogs.Log.Count;
            var done = form.NoticePartyImportAsync();
            using var page = new PartyPage(rig.Context);
            page.OnActivated();
            Assert.True(ui.Until(() => done.IsCompleted && page.HintPending.IsCompleted));

            Assert.Equal(logged + 1, Dialogs.Log.Count);
            Assert.Equal(PartyImportHintState.FromPartsLife, page.Hint);
            var once = Assert.Single(asked);
            Assert.NotEqual(Environment.CurrentManagedThreadId, once);
            Assert.Equal(roaming.Path, rig.Context.PartyRoamingRoot);
        });
    }

    [Fact]
    public void A_resolver_that_fails_or_answers_nothing_means_no_folder_and_a_silent_window()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig();
            var calls = 0;
            rig.Context.PartyRoamingRootResolver = () =>
            {
                Interlocked.Increment(ref calls);
                throw new IOException("the redirected folder is gone");
            };
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var logged = Dialogs.Log.Count;

            var done = form.NoticePartyImportAsync();
            using var page = new PartyPage(rig.Context);
            page.OnActivated();
            Assert.True(ui.Until(() => done.IsCompleted && page.HintPending.IsCompleted));

            Assert.Equal(logged, Dialogs.Log.Count);
            Assert.Equal(PartyImportHintState.None, page.Hint);
            Assert.Null(rig.Context.PartyRoamingRoot);
            Assert.Equal(1, calls);   // asked once, not again by the page
        });
    }

    [Fact]
    public void Open_shows_the_Parts_Life_page_and_presses_its_button()
    {
        using var rig = new Rig();
        using var form = new MainForm(rig.Context);
        _ = form.Handle;
        Assert.True(form.ShowPage("processes"));

        form.ApplyPartyImportAnswer(PartyImportAnswer.Open);

        Assert.Equal("party", form.CurrentPage?.Id);
        Assert.Equal("--lang en", Assert.Single(rig.Starts));
        Assert.Equal(PartyOpenResult.Started, ((PartyPage)form.CurrentPage!).LastResult);
        Assert.False(rig.Context.Settings.PartyImportLater);
    }

    [Fact]
    public void The_notice_is_started_only_by_StartUpdates_after_the_update_choice_and_the_real_roaming_folder_is_named_only_by_a_real_start()
    {
        var main = File.ReadAllText(Path.Combine(Harness.GuiSource, "Shell", "MainForm.cs"));
        var choice = main.IndexOf("UpdateChoiceDialog.Open(", StringComparison.Ordinal);
        var notice = main.IndexOf("PartyNotice = NoticePartyImportAsync();", StringComparison.Ordinal);
        Assert.True(choice > 0 && notice > choice, "the notice follows the update choice in StartUpdates");

        // RealRoamingRoot is named in Program.cs inside the real-run block only (as a method that a worker thread calls later, never called there); no other source and no test reaches the real folder.
        var users = Directory.GetFiles(Harness.GuiSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.ReadAllText(f).Contains("RealRoamingRoot", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f) ?? string.Empty)
            .ToArray();
        Assert.Equal(["Program.cs"], users);
        var program = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));
        Assert.Matches(@"if \(readResult\)\s*\{[^}]*PartyRoamingRootResolver = PcMieru\.Core\.Siblings\.PartyImportHint\.RealRoamingRoot;", program);
        Assert.DoesNotContain("RealRoamingRoot(", program, StringComparison.Ordinal);       // handed over, not asked for, on the thread that starts the window
        Assert.DoesNotContain("GetFolderPath", program, StringComparison.Ordinal);
        Assert.DoesNotContain("PartyRoamingRoot =", program, StringComparison.Ordinal);       // the resolved folder is only ever set by the resolver's caller (GuiContext) and the smoke run

        // Only Program.cs sets a resolver in the app's sources (the smoke run and every test leave it null or pass a fake of their own).
        var setters = Directory.GetFiles(Harness.GuiSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.ReadAllText(f).Contains("PartyRoamingRootResolver =", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f) ?? string.Empty)
            .ToArray();
        Assert.Equal(["Program.cs"], setters);

        // The work that needs the folder asks the context from a worker thread (Task.Run), not from the UI thread.
        Assert.Contains("Task.Run(() => _context.ResolvePartyRoamingRoot()", main, StringComparison.Ordinal);
        // The recovery at start finds the folder through the context's journal path (which asks ResolvePartyRoamingRoot), inside the worker too.
        Assert.Matches(@"Task\.Run\(\(\) =>\s*\{\s*if \(_context\.ResolveLogonJournalPath\(\) is not \{ \} journal\)", main);
        Assert.Contains("PartyRoamingRootKnown && ResolvePartyRoamingRoot() is { } root", File.ReadAllText(Path.Combine(Harness.GuiSource, "Shell", "GuiContext.cs")), StringComparison.Ordinal);
        Assert.DoesNotMatch(@"_context\.PartyRoamingRoot(?!Known)\b", main);   // the UI thread never reads the folder itself (Known is only a yes/no)
    }

    [Fact]
    public void A_smoke_runs_stand_ins_are_in_the_context_before_the_window_and_so_before_any_startup_page()
    {
        var program = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));
        var prepare = program.IndexOf("SmokeRun.Prepare(context);", StringComparison.Ordinal);
        var window = program.IndexOf("new MainForm(context)", StringComparison.Ordinal);
        var page = program.IndexOf("form.ShowPage(page)", StringComparison.Ordinal);
        Assert.True(prepare > 0 && prepare < window && window < page, "Prepare comes before the window and before the startup page");

        // What Prepare puts in: a scratch folder (no resolver, so never the real folder), a launcher that starts nothing, an in-memory store.
        Ui.Run(_ =>
        {
            using var rig = new Rig();
            var context = new GuiContext(rig.Services, rig.Pump, rig.Context.Theme, new GuiSettings());
            SmokeRun.Prepare(context);
            var scratch = context.PartyRoamingRoot;
            try
            {
                Assert.NotNull(scratch);
                Assert.StartsWith(Path.GetTempPath(), scratch, StringComparison.OrdinalIgnoreCase);
                Assert.Null(context.PartyRoamingRootResolver);
                Assert.NotNull(context.PartyLauncherOverride);
                Assert.IsType<MemoryRunStore>(context.LogonStore);
                Assert.Equal(PartyImportHintState.FromPartsLife, PartyImportHint.Check(scratch));

                // a page made right after (the --page case) holds the stand-ins, and pressing its button starts nothing
                using var party = new PartyPage(context);
                Assert.Same(context.PartyLauncherOverride, party.Launcher);
                party.Open();
                Assert.Equal(PartyOpenResult.Started, party.LastResult);
            }
            finally
            {
                SmokeRun.Discard(context);
            }

            Assert.False(Directory.Exists(scratch));
        });
    }

    // --- the Parts Life page's line ---

    [Fact]
    public void The_page_says_old_progress_is_waiting_when_it_is_shown()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig();
            using var page = new PartyPage(rig.Context, roamingRoot: Waiting(roaming.Path, "PartsLife"));
            Assert.False(page.WaitingLabel.Visible);
            Assert.Equal(string.Empty, page.WaitingLabel.Text);

            page.OnActivated();
            Assert.True(ui.Until(() => page.HintPending.IsCompleted));

            Assert.Equal(PartyImportHintState.FromPartsLife, page.Hint);
            Assert.True(page.WaitingLabel.Visible);
            Assert.Equal("Old Parts Life progress is waiting. Open Parts Life to choose whether to bring it over.", page.WaitingLabel.Text);
            Assert.Empty(rig.Starts);
            Assert.Equal(string.Empty, page.StatusLabel.Text);
        });
    }

    [Fact]
    public void The_line_follows_the_language_and_the_page_never_starts_anything_by_looking()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig();
            using var page = new PartyPage(rig.Context, roamingRoot: Waiting(roaming.Path, "PartsParty"));
            page.OnActivated();
            Assert.True(ui.Until(() => page.HintPending.IsCompleted));
            Assert.Equal(PartyImportHintState.FromPartsParty, page.Hint);

            Strings.Current = Language.Ja;
            page.ApplyLanguage();
            Assert.Equal("旧パーツぐらしの続きがあります。開くと、引き継ぐかどうかを選べます。", page.WaitingLabel.Text);
            Assert.Empty(rig.Starts);
        });
    }

    [Fact]
    public void The_page_says_nothing_when_a_save_of_the_new_window_is_there_or_nothing_old_is()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig();
            using (var page = new PartyPage(rig.Context, roamingRoot: roaming.Path))
            {
                page.OnActivated();
                Assert.True(ui.Until(() => page.HintPending.IsCompleted));
                Assert.Equal(PartyImportHintState.None, page.Hint);
                Assert.False(page.WaitingLabel.Visible);
            }

            Waiting(roaming.Path, "PartsLife", Path.Combine("PCMieru", "party"));
            using (var page = new PartyPage(rig.Context, roamingRoot: roaming.Path))
            {
                page.OnActivated();
                Assert.True(ui.Until(() => page.HintPending.IsCompleted));
                Assert.Equal(PartyImportHintState.None, page.Hint);
                Assert.False(page.WaitingLabel.Visible);
            }
        });
    }

    [Fact]
    public void The_line_goes_when_the_save_of_the_new_window_appears_and_the_page_is_shown_again()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig();
            using var page = new PartyPage(rig.Context, roamingRoot: Waiting(roaming.Path, "PartsLife"));
            page.OnActivated();
            Assert.True(ui.Until(() => page.HintPending.IsCompleted));
            Assert.True(page.WaitingLabel.Visible);

            Waiting(roaming.Path, Path.Combine("PCMieru", "party"));
            page.OnActivated();
            Assert.True(ui.Until(() => page.HintPending.IsCompleted));
            Assert.False(page.WaitingLabel.Visible);
            Assert.Equal(string.Empty, page.WaitingLabel.Text);
        });
    }

    [Fact]
    public void A_page_that_is_given_no_folder_takes_the_contexts_and_without_either_looks_nowhere()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using (var none = new Rig(roamingRoot: null))
            {
                using var page = new PartyPage(none.Context);
                page.OnActivated();
                Assert.True(page.HintPending.IsCompleted);
                Assert.Equal(PartyImportHintState.None, page.Hint);
                Assert.False(page.WaitingLabel.Visible);
            }

            using var named = new Rig(roamingRoot: Waiting(roaming.Path, "PartsLife"));
            using var fromContext = new PartyPage(named.Context);
            fromContext.OnActivated();
            Assert.True(ui.Until(() => fromContext.HintPending.IsCompleted));
            Assert.Equal(PartyImportHintState.FromPartsLife, fromContext.Hint);
        });
    }

    [Fact]
    public void The_new_texts_exist_in_both_languages_and_use_no_placeholder()
    {
        foreach (var entry in PartyImportStrings.All)
        {
            Assert.StartsWith("gui.party.import.", entry.Key, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja), entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(entry.En), entry.Key);
            Assert.DoesNotContain('{', entry.Ja);
            Assert.DoesNotContain('{', entry.En);
        }

        Assert.Equal(6, PartyImportStrings.All.Length);
    }
}
