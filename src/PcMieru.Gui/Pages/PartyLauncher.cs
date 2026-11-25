using System.Diagnostics;
using PcMieru.Core.Siblings;

namespace PcMieru.Gui;

/// <summary>What pressing the button of the Parts Life page did.</summary>
internal enum PartyOpenResult
{
    /// <summary>PCMieru.Party.exe was started.</summary>
    Started,

    /// <summary>A Parts Life window of this folder and session was already open and was brought to the front. Nothing was started.</summary>
    Activated,

    /// <summary>PCMieru.Party.exe is not beside this exe (a build without it): nothing happened, and the page says so.</summary>
    Missing,

    /// <summary>The exe is there but could not be started.</summary>
    Failed,
}

/// <summary>
/// Opens PCMieru.Party.exe from the folder this exe runs from, or brings its window to the front when it is already open (DESIGN-C 2.2 and 3 item 3). Nothing here runs
/// by itself: only the button calls <see cref="Open"/>, so choosing the page in the navigation starts nothing. The three actions are injected so the tests start no process.
/// </summary>
internal sealed class PartyLauncher
{
    internal const string ExeName = "PCMieru.Party.exe";

    private readonly Func<bool> _activate;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, string, bool> _start;

    public PartyLauncher(Func<bool> activate, Func<string, bool> exists, Func<string, string, bool> start)
    {
        _activate = activate;
        _exists = exists;
        _start = start;
    }

    /// <summary>The launcher of a real run: the sibling window of the same folder and session, the file on disk, and a plain process start in the exe's own folder.</summary>
    public static PartyLauncher Real() => new(
        () => SiblingWindows.Activate(ExeName),
        File.Exists,
        static (path, arguments) =>
        {
            try
            {
                using var started = Process.Start(new ProcessStartInfo(path, arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
                });
                return started is not null;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return false;
            }
        });

    /// <summary>The command line handed to the Parts Life window: the language the GUI shows at this moment.</summary>
    public static string Arguments(Language language) => "--lang " + (language == Language.En ? "en" : "ja");

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, ExeName);

    public PartyOpenResult Open(Language language)
    {
        var path = ExePath;
        if (!_exists(path))
        {
            return PartyOpenResult.Missing;
        }

        // An open window of this folder comes to the front instead of a second one (the Party exe would itself bring the first one forward and exit).
        if (_activate())
        {
            return PartyOpenResult.Activated;
        }

        return _start(path, Arguments(language)) ? PartyOpenResult.Started : PartyOpenResult.Failed;
    }
}
