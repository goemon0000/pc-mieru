using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// The storage check: the state of each disk and, under each counter, how much it grew since the previous record
/// ("1,204 → 1,227（+23）"). Under normal rights Windows refuses some disks; the result then says so and offers
/// "read again as administrator", which starts a NEW storage check that may ask for the administrator's permission and is saved as a run of
/// its own. The earlier result is not changed.
/// </summary>
internal sealed class StorageCheckTab : RunCheckTab
{
    private readonly Button _elevate = new() { AutoSize = true, Name = "checksStorageElevate", Visible = false };
    private readonly ToneLabel _notice = new() { AutoSize = false, Name = "checksStorageNotice", Visible = false };
    private readonly ToolTip _tips = new() { ShowAlways = true };

    public StorageCheckTab(CheckController controller, Func<int, int> px)
        : base(controller, px, "checksStorage", "gui.chk.sto.intro")
    {
        _elevate.Click += (_, _) => PressElevate();
        _elevate.MinimumSize = new Size(Px(120), Px(30));
        StartButton.TabIndex = 0;
        StopButton.TabIndex = 1;
        _elevate.TabIndex = 2;
        Grid.TabIndex = 3;

        var pad = Px(12);
        _notice.SetTone(CellTone.Accent);
        _notice.Dock = DockStyle.Top;
        _notice.Height = Px(24);
        _notice.Padding = new Padding(pad, 0, pad, 0);
        _notice.TextAlign = ContentAlignment.MiddleLeft;

        Compose([StartButton, StopButton], [_elevate], _notice);
        ApplyLanguage();
    }

    internal Button ElevateButton => _elevate;

    /// <summary>"Read again as administrator", pressed: a NEW storage check that may ask for the permission and is saved as a run of its own.</summary>
    internal void PressElevate() => StartWith(new CheckRequest(CheckKind.Storage, null, AllowElevation: true));

    internal ToneLabel Notice => _notice;

    /// <summary>True when "read again as administrator" is on offer. (A control's own Visible reads false while a parent is hidden, and a test's window is never shown; this says what the tab decided.)</summary>
    internal bool ElevateOffered { get; private set; }

    protected override CheckKind CurrentKind => CheckKind.Storage;

    protected override CheckRequest? BuildRequest() => new(CheckKind.Storage);

    protected override void LanguageApplied()
    {
        _elevate.Text = Strings.Get("gui.chk.sto.elevate");
        _tips.SetToolTip(_elevate, Strings.Get("gui.chk.sto.elevate.tip"));
        _notice.Text = Strings.Get("gui.chk.sto.denied");
    }

    protected override void StateRefreshed(CheckKindState state)
    {
        // The offer is for a result that was read under normal rights and left a disk denied. A run that was already the elevated one does
        // not offer itself again (a refused permission is tried again from the start button), and nothing is offered while a run is going.
        var running = state.State is CheckRunState.Running or CheckRunState.Stopping;
        var denied = !running
            && state.Request?.AllowElevation != true
            && CheckFormat.HasDeniedDisk(CheckFormat.DisplayResult(state.Run));
        ElevateOffered = denied;
        _elevate.Visible = denied;
        _notice.Visible = denied;
        _elevate.Enabled = !Controller.IsBusy;
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
