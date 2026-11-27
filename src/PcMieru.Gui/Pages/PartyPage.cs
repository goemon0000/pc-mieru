using PcMieru.Core.Siblings;

namespace PcMieru.Gui;

/// <summary>
/// The last navigation entry, "Parts Life": one line saying what it is, a button that opens the Parts Life window (PCMieru.Party.exe, a separate program of the same
/// folder), and a line of its own when that exe is not there. Selecting the entry starts nothing: only the button does (DESIGN-C 3 item 3). When the page is shown it also looks, off the UI
/// thread and by file existence only, for old progress that is waiting (<see cref="PartyImportHint"/>) and says so in one line under the text: that line is the way back after the first-run
/// notice's 「あとで」 (DESIGN-C 4.1 item 2). The page never imports: the choices are in the Parts Life window.
/// </summary>
internal sealed class PartyPage : PageBase
{
    private readonly ToneLabel _about = new() { AutoSize = true, Name = "partyAbout" };
    private readonly Button _open = new() { AutoSize = true, Name = "partyOpen" };
    private readonly ToneLabel _waiting = new() { AutoSize = true, Name = "partyImportWaiting", Visible = false };
    private readonly ToneLabel _status = new() { AutoSize = true, Name = "partyStatus" };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly string? _roamingRoot;
    private PartyOpenResult? _last;
    private PartyImportHintState _hint;

    /// <param name="roamingRoot">The roaming folder the hint looks in. Null: the context's, asked when the page is shown (<see cref="GuiContext.PartyRoamingRoot"/>: a scratch one in the smoke run; in a real run the
    /// context finds the real one on a worker thread; none in a test), and none means the page looks nowhere.</param>
    public PartyPage(GuiContext context, PartyLauncher? launcher = null, string? roamingRoot = null)
        : base(context, "party", "gui.nav.party")
    {
        Launcher = launcher ?? context.PartyLauncherOverride ?? PartyLauncher.Real();
        _roamingRoot = roamingRoot;
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(Px(16)),
        };
        _about.Margin = new Padding(0, 0, 0, Px(12));
        _waiting.Margin = new Padding(0, 0, 0, Px(12));
        _open.Margin = new Padding(0, 0, 0, Px(12));
        _open.Click += (_, _) => Open();
        flow.Controls.Add(_about);
        flow.Controls.Add(_waiting);
        flow.Controls.Add(_open);
        flow.Controls.Add(_status);
        Controls.Add(flow);
        ApplyLanguage();
    }

    internal PartyLauncher Launcher { get; }

    internal Button OpenButton => _open;

    internal ToneLabel StatusLabel => _status;

    /// <summary>The line about old progress that is waiting (empty and hidden when there is none).</summary>
    internal ToneLabel WaitingLabel => _waiting;

    /// <summary>What the last look for old progress found (None before the first one, and always None when the page looks nowhere).</summary>
    internal PartyImportHintState Hint => _hint;

    /// <summary>The look that <see cref="OnActivated"/> started (completed when nothing was started). A test awaits it; the page never does.</summary>
    internal Task HintPending { get; private set; } = Task.CompletedTask;

    public override void OnActivated()
    {
        base.OnActivated();
        if (_roamingRoot is { } root)
        {
            HintPending = LookForOldProgress(() => root);
        }
        else if (Context.PartyRoamingRootKnown)
        {
            // The context's folder: known already (the smoke run's scratch one), or found on the worker thread below (a real run: the real folder may be slow to ask for).
            HintPending = LookForOldProgress(Context.ResolvePartyRoamingRoot);
        }
    }

    /// <summary>One look at the three files, off the UI thread (asking for the folder too); the line follows the answer. The hint never throws (it answers None), and a page closed meanwhile is left alone.</summary>
    private async Task LookForOldProgress(Func<string?> rootOf)
    {
        var state = await Task.Run(() => rootOf() is { } root ? PartyImportHint.Check(root) : PartyImportHintState.None).ConfigureAwait(true);
        if (IsDisposed)
        {
            return;
        }

        _hint = state;
        ShowWaiting();
    }

    /// <summary>What the last press of the button did (null before the first press).</summary>
    internal PartyOpenResult? LastResult => _last;

    /// <summary>The button's action: open the Parts Life window, or bring the open one to the front. Never throws: a missing or unstartable exe is told in the status line.</summary>
    internal void Open()
    {
        _last = Launcher.Open(Strings.Current);
        ShowStatus();
    }

    public override void ApplyLanguage()
    {
        _about.Text = Strings.Get("gui.party.about");
        _open.Text = Strings.Get("gui.party.open");
        _open.AccessibleName = Strings.Get("gui.party.open");
        _tips.SetToolTip(_open, Strings.Get("gui.party.open.tip"));
        _about.SetTone(CellTone.Dim);
        ShowWaiting();
        ShowStatus();
    }

    private void ShowWaiting()
    {
        var waiting = _hint != PartyImportHintState.None;
        _waiting.Text = waiting ? Strings.Get("gui.party.import.waiting") : string.Empty;
        _waiting.Visible = waiting;
        _waiting.SetTone(CellTone.Accent);
    }

    private void ShowStatus()
    {
        switch (_last)
        {
            case PartyOpenResult.Missing:
                _status.Text = Strings.Get("gui.party.missing");
                _status.SetTone(CellTone.Warning);
                break;
            case PartyOpenResult.Failed:
                _status.Text = Strings.Get("gui.party.failed");
                _status.SetTone(CellTone.Warning);
                break;
            default:
                _status.Text = string.Empty;
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal static class PartyStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.nav.party", "パーツぐらし", "Parts Life"),
        new("gui.party.about", "パーツぐらしは、PC のパーツがキャラクターになり、PC を使うほど育っていく別のウィンドウです。", "Parts Life is a separate window where the parts of your PC become characters that level up as you use your PC."),
        new("gui.party.open", "パーツぐらしを開く", "Open Parts Life"),
        new("gui.party.open.tip", "すでに開いていれば、そのウィンドウを前に出します", "If it is already open, its window is brought to the front"),
        new("gui.party.missing", "PCMieru.Party.exe が、このアプリと同じフォルダにありません。", "PCMieru.Party.exe is not in the same folder as this app."),
        new("gui.party.failed", "パーツぐらしを起動できませんでした。", "Parts Life could not be started."),
    ];
}
