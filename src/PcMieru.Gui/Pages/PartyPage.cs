namespace PcMieru.Gui;

/// <summary>
/// The last navigation entry, "Parts Life": one line saying what it is, a button that opens the Parts Life window (PCMieru.Party.exe, a separate program of the same
/// folder), and a line of its own when that exe is not there. Selecting the entry starts nothing: only the button does (DESIGN-C 3 item 3).
/// </summary>
internal sealed class PartyPage : PageBase
{
    private readonly ToneLabel _about = new() { AutoSize = true, Name = "partyAbout" };
    private readonly Button _open = new() { AutoSize = true, Name = "partyOpen" };
    private readonly ToneLabel _status = new() { AutoSize = true, Name = "partyStatus" };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private PartyOpenResult? _last;

    public PartyPage(GuiContext context, PartyLauncher? launcher = null)
        : base(context, "party", "gui.nav.party")
    {
        Launcher = launcher ?? PartyLauncher.Real();
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(Px(16)),
        };
        _about.Margin = new Padding(0, 0, 0, Px(12));
        _open.Margin = new Padding(0, 0, 0, Px(12));
        _open.Click += (_, _) => Open();
        flow.Controls.Add(_about);
        flow.Controls.Add(_open);
        flow.Controls.Add(_status);
        Controls.Add(flow);
        ApplyLanguage();
    }

    internal PartyLauncher Launcher { get; }

    internal Button OpenButton => _open;

    internal ToneLabel StatusLabel => _status;

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
        ShowStatus();
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
