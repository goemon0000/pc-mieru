using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcMieru.Core.Siblings;
using PcMieru.Party.Game;
using PcMieru.Party.I18n;
using PcMieru.Party.Import;
using PcMieru.Wire.Gate;

namespace PcMieru.Party;

internal static class Program
{
    /// <summary>How long a smoke run keeps its window open before it closes it by itself.</summary>
    internal static readonly TimeSpan SmokeOpenFor = TimeSpan.FromMilliseconds(1500);

    /// <summary>With --shot: how long after the first render the window is written (the feed ticks once a second, the story needs a few ticks to say its first lines).</summary>
    internal static readonly TimeSpan ShotAfter = TimeSpan.FromMilliseconds(3200);

    /// <summary>This exe's own file name: the sibling a second start brings to the front (SiblingWindows looks for it beside the running exe).</summary>
    internal const string PartyExeName = "PCMieru.Party.exe";

    /// <summary>A second start that finds the mutex held but no sibling window yet (the first one is still creating its window) looks again this often...</summary>
    internal static readonly TimeSpan SiblingRetryEvery = TimeSpan.FromMilliseconds(200);

    /// <summary>...for up to this long, then it says the other one is running (exit 2). Not used by a smoke run.</summary>
    internal static readonly TimeSpan SiblingWaitFor = TimeSpan.FromSeconds(3);

    // Exit codes: 0 = ran and ended normally, 1 = did not start (bad command line, the start gate said no, a smoke run that failed), 2 = another Parts Life is the resident.
    [STAThread]
    private static int Main(string[] args)
    {
        // The command line is read first, but it is only strings: no file, no handle, no window. A bad one is the one thing said without the gate (nothing of the app was started).
        if (!PartyArgs.TryParse(args, out var options, out var problem))
        {
            Say(problem, smoke: Array.IndexOf(args, "--smoke") >= 0, PartyStrings.Resolve(null));
            return 1;
        }

        var language = PartyStrings.Resolve(options.Language);

        // 1. The start gate (DESIGN-B 4, DESIGN-C 3.1): before the single-resident mutex, before any setting or save is read, before the first WPF object. Held until Main returns, which is
        // the whole life of the window, so an update cannot swap the files under a running Parts Life. Not open = the reason and the end; nothing else of the app was started.
        using var gate = InstallGate.EnterStartup();
        if (!gate.IsOpen)
        {
            Say(PartyStrings.GateRefused(language, gate), options.Smoke, language);
            return 1;
        }

        // 1a. A measuring start (--measure <dir>, DESIGN-C 8 step 7) leaves here, before anything of the user's is read: it never builds the sign-in store, never runs the recovery and never asks the first-start
        // question (RunMeasure keeps its state in the one folder it was given, under its own resident mutex name).
        if (options.MeasureDir is { } measureDir)
        {
            return RunMeasure(options, measureDir, language);
        }

        // 1b. The sign-in start (DESIGN-C 4.3) put back, BEFORE the resident check: the old Parts Life shares the resident mutex, so after a crash that left both starts on it can win the mutex at sign-in and this
        // process would end at step 2 and never recover. This is the ONE place Party builds the real Run store, and only a real start gets here (a smoke run never builds it: its endings check the counter;
        // source-text tests pin both). The store is handed on to the first start, which asks the question after an import.
        IRunStore? runStore = null;
        if (!options.Smoke)
        {
            runStore = RegistryRunStore.ForCurrentUser();
            Strings.Lang = language == PartyLanguage.Ja ? "ja" : "en";
            var recoveryPrompts = new WpfRunSwitchPrompts();
            GuardedRunSwitch(() => RunSwitchFlow.RecoverAtStart(runStore, ImportPaths.ForCurrentUser().DestinationDir, recoveryPrompts));
        }

        // 2. The one resident of this user (DESIGN-C 3.2). Taken and given back on this thread, the main STA thread that also runs the window.
        // When the resident is another Party of this session and this install, bring it to the front and end with 0 (the user's second click did what it meant).
        // Anything else (another session, the old Parts Life, another install) keeps the plain message and 2. A smoke run never does this: it only checks the start.
        using var resident = ResidentInstance.TryAcquire(ResidentInstance.CurrentName());
        if (resident is null)
        {
            if (!options.Smoke && ActivateResident())
            {
                return 0;
            }

            // No Party window of this session after the retry: what holds the mutex is the old Parts Life (a smoke run never looked, so it keeps the plain text).
            Say(options.Smoke ? PartyStrings.AlreadyRunning(language) : PartyStrings.OldAppRunning(language), options.Smoke, language);
            return 2;
        }

        // 3. A smoke preview of the first-start dialogs: a made-up plan, no engine, no path of the machine (the real paths and the probe are never built here).
        if (options.Smoke && options.ImportPreview is { } preview)
        {
            return RunImportPreview(options, preview, language);
        }

        // 4. The first start (DESIGN-C 4.1): before the save is read. The first file of a new home always comes from the import engine under its lock, never from the save writer.
        // Later / Close = nothing was written and the app ends (0); the next start asks again. A smoke run never gets here: it must not touch the real folders.
        PartyState? loaded = null;
        if (!options.Smoke && !FirstStart(language, runStore!, out loaded))
        {
            return 0;
        }

        // 5. Only now WPF for the window itself.
        return Run(options, loaded);
    }

    /// <summary>
    /// True: <paramref name="state"/> is the state to run with (a save in place and loaded; or, for an unreadable new save the user chose to open anyway, a read-only state that never saves).
    /// False: the user chose Later or Close, nothing was written.
    /// </summary>
    private static bool FirstStart(PartyLanguage language, IRunStore runStore, out PartyState? state)
    {
        Strings.Lang = language == PartyLanguage.Ja ? "ja" : "en";
        // Every engine call runs off this thread inside FirstStartFlow (Task.Run, then a plain wait: no window exists yet); the dialogs run on this STA thread without an Application.
        var paths = ImportPaths.ForCurrentUser();

        // The sign-in start (DESIGN-C 4.3): what an unfinished switch left was already put back in Main (before the resident check). After the flow: when this run brought the old save over, ask once
        // whether the sign-in start follows. The store is the real one of a real start, handed in by Main.
        var runPrompts = new WpfRunSwitchPrompts();

        var engine = new ImportEngine(paths, new WindowsLegacyRunningProbe());
        var report = new FirstStartReport();
        state = StartupLoad.Run(engine, new WpfFirstStartPrompts(), paths.DestinationDir, report);
        if (state is not null && Environment.ProcessPath is { } exe)
        {
            GuardedRunSwitch(() => RunSwitchFlow.AfterImport(report, runStore, paths.DestinationDir, exe, runPrompts));
        }

        return state is not null;
    }

    /// <summary>
    /// The sign-in switch is an extra: whatever goes wrong in it (a registry or window failure the flow did not turn into a result) must never stop Parts Life from starting. The flow already
    /// shows what needs a person; an exception that escapes it shows nothing (nothing is worse than before).
    /// </summary>
    private static void GuardedRunSwitch(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // nothing shown, nothing blocked
        }
    }

    /// <summary>
    /// --measure &lt;dir&gt;: the window with real collection and real play, in a folder that is only ours. Touches nothing of the user: no sign-in store, no recovery, no import question, no real home, no gui.ini.
    /// The resident mutex has its own name, so a real Parts Life or Party of the user is neither blocked nor brought to the front. Stays open until the window is closed (measure.ps1 sends WM_CLOSE).
    /// </summary>
    private static int RunMeasure(PartyOptions options, string dir, PartyLanguage language)
    {
        if (!Directory.Exists(dir))
        {
            Say("--measure: the folder does not exist: " + dir, smoke: true, language);
            return 1;
        }

        using var resident = ResidentInstance.TryAcquire(PartyMeasure.ResidentName());
        if (resident is null)
        {
            Say(PartyStrings.AlreadyRunning(language), smoke: true, language);
            return 2;
        }

        return Run(options, PartyState.Load(dir));
    }

    /// <summary>The smoke run's proof that the owner's Run key was never in reach: the real store was not built. Null when so, otherwise the failure text.</summary>
    private static string? RealStoreBuiltInSmoke() => RegistryRunStore.Constructed == 0 ? null : "the real sign-in store was built in a smoke run";

    /// <summary>--smoke --import-preview: shows the dialog of one case with a made-up plan, writes the shot when asked, and says "party smoke ok". Touches nothing of this machine.</summary>
    private static int RunImportPreview(PartyOptions options, PartyImportPreview which, PartyLanguage language)
    {
        Strings.Lang = language == PartyLanguage.Ja ? "ja" : "en";
        string? failure = null;
        var shotWritten = false;
        try
        {
            var window = new ImportDialogWindow(ImportDialogModels.Preview(which));
            var wait = new DispatcherTimer { Interval = options.ShotPath is null ? SmokeOpenFor : TimeSpan.FromMilliseconds(600) };
            window.ContentRendered += (_, _) => wait.Start();
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                try
                {
                    if (options.ShotPath is { } shot)
                    {
                        WriteShot(window.RenderShot(2.0), shot);
                        shotWritten = true;
                    }
                }
                catch (Exception ex)
                {
                    failure ??= "the shot could not be written: " + ex;
                }

                window.Close();
            };
            window.ShowDialog();
            if (!window.Rendered) failure ??= "the dialog was closed before it was drawn";
        }
        catch (Exception ex)
        {
            failure ??= ex.ToString();
        }

        if (failure is null && options.ShotPath is not null && !shotWritten)
        {
            failure = "the dialog was closed before the shot was written";
        }

        failure ??= RealStoreBuiltInSmoke();

        if (failure is not null)
        {
            Console.Error.WriteLine("party smoke FAILED: " + failure.ReplaceLineEndings(" "));
            return 1;
        }

        Console.Out.WriteLine("party smoke ok");
        return 0;
    }

    /// <summary>
    /// Brings the resident's window to the front. The mutex can be held by a first instance that has not made its window yet (it is still starting), so when no sibling window is found
    /// the find is repeated every <see cref="SiblingRetryEvery"/> for up to <see cref="SiblingWaitFor"/> before this gives up. Runs on the main thread before any window exists: nothing to pump.
    /// </summary>
    private static bool ActivateResident()
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (SiblingWindows.Activate(PartyExeName))
            {
                return true;
            }

            if (waited.Elapsed + SiblingRetryEvery > SiblingWaitFor)
            {
                return false;
            }

            Thread.Sleep(SiblingRetryEvery);
        }
    }

    private static int Run(PartyOptions options, PartyState? loaded)
    {
        // The state, the feed and the writer (DESIGN-C 3 item 6, 8 step 4a). A smoke run never reads or writes a save file: an in-memory fresh state and NO writer.
        // A normal start reads only the new home (PartyState.Load() with no arguments) and saves through one background writer.
        // The smoke run keeps the fake feed for good. A normal start samples this machine itself (SamplerFeed), and gives it back when the window closes.
        IBoardFeed feed;
        PartyState state;
        PartySaveWriter? writer = null;
        if (options.Smoke)
        {
            feed = new FakeBoardFeed();
            state = PartyState.Fresh();
        }
        else
        {
            var sampler = new SamplerFeed();
            sampler.Start();
            feed = sampler;
            state = loaded ?? PartyState.Load();   // the first start loaded it already (read-only when the user went on over an unreadable save: SaveBlocked keeps the writer off)
            writer = new PartySaveWriter(state.SaveBlocked);
        }

        // --lang wins for this run and is NOT written back into the state; without it the language saved in the state, then the OS (DESIGN-C 3 item 4).
        var language = PartyStrings.ForWindow(options.Language, state.Lang);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // The language follow reads gui.ini only in a normal start; a smoke run and a measuring start read nothing of this machine.
        var window = new PartyWindow(language, state, feed, writer, options.Smoke || options.MeasureDir is not null ? null : static () => GuiLanguage.Read());
        if (options.MeasureDir is { } measureFolder)
        {
            // The time of the first non-missing value on the board (DESIGN-C 7), from the OS's creation time of this process. One line in the measuring folder; a failure to write it is not the run's failure.
            var startedUtc = PartyMeasure.ProcessStartUtc();
            window.FirstValueShown = () =>
            {
                try
                {
                    PartyMeasure.WriteFirstValue(measureFolder, (DateTime.UtcNow - startedUtc).TotalMilliseconds);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            };
        }

        // A smoke run has to end on its own and to fail loudly: an exception on the UI thread is kept for the verdict instead of the default (a crash box nobody can click).
        string? failure = null;
        var shotWritten = false;
        if (options.Smoke)
        {
            app.DispatcherUnhandledException += (_, e) =>
            {
                failure ??= e.Exception.ToString();
                e.Handled = true;
                window.Close();
            };

            if (options.ShotPath is { } shot)
            {
                // After the first render (and a few ticks of the feed, so the story has its lines) the window itself is written as a PNG; then the run ends.
                var wait = new DispatcherTimer { Interval = ShotAfter };
                window.ContentRendered += (_, _) => wait.Start();
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    try
                    {
                        if (options.ShotTab is { } tab) window.OpenTabForShot(tab);
                        WriteShot(window.RenderShot(expanded: true, scale: 2.0), shot);
                        shotWritten = true;
                    }
                    catch (Exception ex)
                    {
                        failure ??= "the shot could not be written: " + ex;
                    }

                    window.Close();
                };
            }
            else
            {
                var timer = new DispatcherTimer { Interval = SmokeOpenFor };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    window.Close();
                };
                window.Loaded += (_, _) => timer.Start();
            }
        }

        try
        {
            app.Run(window);
        }
        finally
        {
            (feed as IDisposable)?.Dispose();
        }

        if (!options.Smoke)
        {
            return 0;
        }

        // A failure swallowed by the guarded timers of the window counts too: a smoke run that "ok"s over a swallowed exception checks nothing.
        failure ??= window.FirstFailure;
        if (failure is null && !window.Rendered)
        {
            failure = "the window was closed before it was drawn";
        }

        // A shot run passes only when the PNG was written (the window may be closed after the first render and before the capture).
        if (failure is null && options.ShotPath is not null && !shotWritten)
        {
            failure = "the window was closed before the shot was written";
        }

        failure ??= RealStoreBuiltInSmoke();

        if (failure is not null)
        {
            Console.Error.WriteLine("party smoke FAILED: " + failure.ReplaceLineEndings(" "));
            return 1;
        }

        Console.Out.WriteLine("party smoke ok");
        return 0;
    }

    private static void WriteShot(BitmapSource image, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        // CreateNew: an existing file (a save, anything) is never overwritten or truncated.
        using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(file);
    }

    /// <summary>A message of the shell before or instead of the window: a box for a person, one line on stderr for a run nobody watches (--smoke).</summary>
    private static void Say(string text, bool smoke, PartyLanguage language)
    {
        if (smoke)
        {
            Console.Error.WriteLine("party: " + text.ReplaceLineEndings(" "));
            return;
        }

        MessageBox.Show(text, PartyStrings.Title(language), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
