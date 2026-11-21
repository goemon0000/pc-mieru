using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// What the three tabs that run a check share (System, Load, Storage): the start and the stop button, a short intro, the table of the
/// result, and under it the state of the run and, in the dim tone, when it ran and the overall verdict. The tab draws the controller's
/// state for its kind: a start button that is disabled while any check runs (one run at a time), a stop button that is enabled only
/// while this kind runs, the table of the last result of this kind. It never measures anything and never waits.
/// </summary>
internal abstract class RunCheckTab : CheckTab
{
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly string _introKey;
    private CheckResult? _shown;

    protected RunCheckTab(CheckController controller, Func<int, int> px, string name, string introKey)
        : base(controller, px, name)
    {
        _introKey = introKey;
        StartButton = new Button { AutoSize = true, Name = name + "Start" };
        StopButton = new Button { AutoSize = true, Name = name + "Stop", Enabled = false };
        Grid = new CheckResultGrid { Name = name + "Grid" };
        StateLabel = new Label { AutoSize = true, Name = name + "State", Margin = new Padding(0, 0, px(16), 0) };
        HintLabel = new ToneLabel { AutoSize = true, Name = name + "Hint" };
        IntroLabel = new ToneLabel { AutoSize = false, Name = name + "Intro" };
        IntroLabel.SetTone(CellTone.Dim);
        StartButton.Click += (_, _) => StartClicked();
        StopButton.Click += (_, _) => PressStop();
    }

    /// <summary>The start button, pressed (a test presses it without a window that could take the click).</summary>
    internal void PressStart() => StartClicked();

    /// <summary>The stop button, pressed.</summary>
    internal void PressStop() => Controller.Stop();

    internal Button StartButton { get; }

    internal Button StopButton { get; }

    internal CheckResultGrid Grid { get; }

    internal Label StateLabel { get; }

    internal ToneLabel HintLabel { get; }

    internal ToneLabel IntroLabel { get; }

    /// <summary>The kind of check whose state and result this tab shows now.</summary>
    protected abstract CheckKind CurrentKind { get; }

    /// <summary>The request the start button sends, or null when there is nothing to ask yet.</summary>
    protected abstract CheckRequest? BuildRequest();

    /// <summary>The state of the tab's kind, as the controller has it now.</summary>
    internal CheckKindState CurrentState => Controller.StateOf(CurrentKind);

    /// <summary>Lays the tab out. <paramref name="between"/> lists, top down, what sits between the intro and the table.</summary>
    protected void Compose(Control[] barLeft, Control[] barRight, params Control[] between)
    {
        var pad = Px(12);
        StartButton.MinimumSize = new Size(Px(88), Px(30));
        StopButton.MinimumSize = new Size(Px(88), Px(30));
        IntroLabel.Dock = DockStyle.Top;
        IntroLabel.Height = Px(38);
        IntroLabel.Padding = new Padding(pad, 0, pad, 0);
        IntroLabel.TextAlign = ContentAlignment.MiddleLeft;
        Grid.PrepareColumns(Px, Strings.Get("gui.chk.table.name"));

        var bar = PageLayout.Bar(Name + "Bar", pad, barLeft, barRight);

        // The table is added first and the bar last: the later a docked control is added, the nearer the edge it sits.
        Controls.Add(Grid);
        Controls.Add(PageLayout.Footer(Name + "Footer", pad, StateLabel, HintLabel));
        for (var i = between.Length - 1; i >= 0; i--)
        {
            Controls.Add(between[i]);
        }

        Controls.Add(IntroLabel);
        Controls.Add(bar);
    }

    public override void ApplyLanguage()
    {
        StartButton.Text = Strings.Get(StartKey);
        StopButton.Text = Strings.Get("gui.chk.stop");
        IntroLabel.Text = Strings.Get(_introKey);
        Grid.AccessibleName = Strings.Get("gui.chk.table.name");
        Grid.ApplyLanguage();
        _shown = Grid.Result;
        LanguageApplied();
        RefreshState();
    }

    /// <summary>The key of the start button's text.</summary>
    protected virtual string StartKey => "gui.chk.start";

    /// <summary>A tab writes its own texts here (the language changed).</summary>
    protected virtual void LanguageApplied()
    {
    }

    /// <summary>A tab draws its own parts here, after the common ones (the state changed).</summary>
    protected virtual void StateRefreshed(CheckKindState state)
    {
    }

    public sealed override void RefreshState()
    {
        var kind = CurrentKind;
        var state = Controller.StateOf(kind);
        var active = Controller.Active;
        var busyText = active is { } running ? Strings.Format("gui.chk.busy", Fmt.Dict(("kind", CheckFormat.KindName(running)))) : string.Empty;
        StartButton.Enabled = active is null && CanStart;
        StopButton.Enabled = state.State == CheckRunState.Running;
        _tips.SetToolTip(StartButton, busyText);

        var result = CheckFormat.DisplayResult(state.Run);
        if (!ReferenceEquals(result, _shown))
        {
            _shown = result;
            Grid.Show(result);
        }

        StateLabel.Text = CheckFormat.StateText(state);
        HintLabel.Text = active is { } other && other != kind
            ? busyText
            : result is null ? string.Empty : CheckFormat.ResultLine(result);
        StateRefreshed(state);
    }

    /// <summary>A tab can hold its start back for a reason of its own (nothing chosen yet).</summary>
    protected virtual bool CanStart => true;

    /// <summary>The start button was pressed. One run at a time: a start that the controller refuses changes nothing.</summary>
    protected virtual void StartClicked() => StartWith(BuildRequest());

    protected void StartWith(CheckRequest? request)
    {
        if (request is null)
        {
            return;
        }

        _ = Controller.TryStart(request);
        RefreshState();
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
