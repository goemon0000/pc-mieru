using System.Windows;
using System.Windows.Threading;
using PcMieru.Wire.Gate;

namespace PcMieru.Party;

internal static class Program
{
    /// <summary>How long a smoke run keeps its window open before it closes it by itself.</summary>
    internal static readonly TimeSpan SmokeOpenFor = TimeSpan.FromMilliseconds(1500);

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
        // TODO(DESIGN-C 3.2, step 4): when the resident is another Party of this session, send it "activate" (restore and bring it to the front) before exiting 2.
        using var resident = ResidentInstance.TryAcquire(ResidentInstance.CurrentName());
        if (resident is null)
        {
            Say(PartyStrings.AlreadyRunning(language), options.Smoke, language);
            return 2;
        }

        // 3. Only now WPF.
        return Run(options, language);
    }

    private static int Run(PartyOptions options, PartyLanguage language)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new PartyWindow(language);

        // A smoke run has to end on its own and to fail loudly: an exception on the UI thread is kept for the verdict instead of the default (a crash box nobody can click).
        string? failure = null;
        if (options.Smoke)
        {
            app.DispatcherUnhandledException += (_, e) =>
            {
                failure ??= e.Exception.ToString();
                e.Handled = true;
                window.Close();
            };
            var timer = new DispatcherTimer { Interval = SmokeOpenFor };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window.Close();
            };
            window.Loaded += (_, _) => timer.Start();
        }

        app.Run(window);

        if (!options.Smoke)
        {
            return 0;
        }

        if (failure is null && !window.Rendered)
        {
            failure = "the window was closed before it was drawn";
        }

        if (failure is not null)
        {
            Console.Error.WriteLine("party smoke FAILED: " + failure.ReplaceLineEndings(" "));
            return 1;
        }

        Console.Out.WriteLine("party smoke ok");
        return 0;
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
