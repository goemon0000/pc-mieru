using System.Windows;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcMieru.Party.Game;
using PcMieru.Wire.Gate;

namespace PcMieru.Party;

internal static class Program
{
    /// <summary>How long a smoke run keeps its window open before it closes it by itself.</summary>
    internal static readonly TimeSpan SmokeOpenFor = TimeSpan.FromMilliseconds(1500);

    /// <summary>With --shot: how long after the first render the window is written (the feed ticks once a second, the story needs a few ticks to say its first lines).</summary>
    internal static readonly TimeSpan ShotAfter = TimeSpan.FromMilliseconds(3200);

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

        // 2. The one resident of this user (DESIGN-C 3.2). Taken and given back on this thread, the main STA thread that also runs the window.
        // When the resident is another Party of this session and this install, bring it to the front and end with 0 (the user's second click did what it meant).
        // Anything else (another session, the old Parts Life, another install) keeps the plain message and 2. A smoke run never does this: it only checks the start.
        using var resident = ResidentInstance.TryAcquire(ResidentInstance.CurrentName());
        if (resident is null)
        {
            if (!options.Smoke && ResidentActivator.TryActivateExisting())
            {
                return 0;
            }

            Say(PartyStrings.AlreadyRunning(language), options.Smoke, language);
            return 2;
        }

        // 3. Only now WPF.
        return Run(options);
    }

    private static int Run(PartyOptions options)
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
            state = PartyState.Load();
            writer = new PartySaveWriter(state.SaveBlocked);
        }

        // --lang wins for this run and is NOT written back into the state; without it the language saved in the state, then the OS (DESIGN-C 3 item 4).
        var language = PartyStrings.ForWindow(options.Language, state.Lang);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new PartyWindow(language, state, feed, writer);

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
