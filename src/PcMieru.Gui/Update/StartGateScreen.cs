using PcMieru.Wire.Gate;

namespace PcMieru.Gui;

/// <summary>
/// The start gate of PCMieru.exe (DESIGN-B 4), before anything else of the app exists: no services, no windows, no sampling and no update timer. Open = the entry is returned and Main
/// holds it until the app ends. Not open = a plain window says why (ja or en, light or dark like the app) and the app does not start; for a busy gate (an update is being installed)
/// the window has 「もう一度試す」, which asks again and carries on when the gate is free.
///
/// An unattended run (--smoke, --measure) never waits on a window: the reason goes to stderr and the run ends with 1. A smoke run gets its gate the same way every start does: the gate file
/// beside the exe (the publish folder has it; a dev build gets a copy from build/GateNextToExe.targets; a test that starts the exe from a temp folder writes it there).
/// </summary>
internal static class StartGateScreen
{
    /// <summary>
    /// Enters the gate; null means the app must not start (the reason was shown or written). <paramref name="enter"/> is the seam the tests use: the real call is
    /// <see cref="InstallGate.EnterStartup()"/>.
    /// </summary>
    public static GateEntry? Enter(ThemeService theme, bool unattended, TextWriter error, Func<GateEntry>? enter = null)
    {
        enter ??= InstallGate.EnterStartup;
        var entry = enter();
        if (entry.IsOpen)
        {
            return entry;
        }

        if (unattended)
        {
            error.WriteLine("PCMieru: " + entry.Reason + (entry.RecoveryNotePath is { } note ? " (see " + note + ")" : string.Empty));
            entry.Dispose();
            return null;
        }

        // An automated check (tests, smoke through a seam) answers for the window and never waits on a click.
        if (Dialogs.AutoAnswer is not null)
        {
            Dialogs.Log.Add(Strings.Get("gui.gate.title") + " | " + StartGateWindow.ReasonFor(entry));
            entry.Dispose();
            return null;
        }

        using var window = new StartGateWindow(theme, entry, enter);
        window.ShowDialog();
        return window.Opened;
    }
}

/// <summary>The reason window of a start that was refused. Plain: one reason, what to do, 「もう一度試す」 (busy only) and 「閉じる」.</summary>
internal sealed class StartGateWindow : Form
{
    private readonly Func<GateEntry> _enter;
    private readonly Label _reason = new() { Name = "gateReason", AutoSize = true };
    private readonly ToneLabel _hint = new() { Name = "gateHint", AutoSize = true };
    private readonly Button _retry = new() { Name = "gateRetry", AutoSize = true };
    private readonly Button _close = new() { Name = "gateClose", AutoSize = true };
    private readonly ThemeService _theme;
    private GateEntry _current;
    private bool _noActivate;
    private int _basePad;

    public StartGateWindow(ThemeService theme, GateEntry refused, Func<GateEntry> enter)
    {
        _theme = theme;
        _current = refused;
        _enter = enter;
        Name = "StartGateWindow";
        Text = Strings.Get("gui.gate.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var pad = LogicalToDeviceUnits(16);
        _basePad = pad;
        var width = LogicalToDeviceUnits(520);
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _reason.MaximumSize = new Size(width, 0);
        _reason.Margin = new Padding(0, 0, 0, pad / 2);
        root.Controls.Add(_reason);

        _hint.MaximumSize = new Size(width, 0);
        _hint.Margin = new Padding(0, 0, 0, pad);
        root.Controls.Add(_hint);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        _close.Text = Strings.Get("gui.common.close");
        _close.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        _close.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(_close);
        _retry.Text = Strings.Get("gui.gate.retry");
        _retry.MinimumSize = new Size(LogicalToDeviceUnits(120), LogicalToDeviceUnits(32));
        buttons.Controls.Add(_retry);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _retry;
        CancelButton = _close;
        _retry.Click += (_, _) => RetryNow();
        Fill();
        _hint.SetTone(CellTone.Dim);
        _theme.Apply(this);
    }

    /// <summary>The entry of the retry that succeeded; null when the window ended with the gate still closed.</summary>
    internal GateEntry? Opened { get; private set; }

    /// <summary>The reason as shown.</summary>
    internal string ReasonText => _reason.Text;

    /// <summary>What the person can do, as shown.</summary>
    internal string HintText => _hint.Text;

    internal Button RetryButton => _retry;

    /// <summary>
    /// "Try again" is offered (only for a busy gate). Control.Visible says false for every control of a window that is not shown yet, so this is what a test (and the default button) can ask
    /// before the window is on the screen.
    /// </summary>
    internal bool RetryOffered { get; private set; }

    internal Button CloseButton => _close;

    /// <summary>The localized reason of a refused entry (the file path of a recovery note filled in).</summary>
    internal static string ReasonFor(GateEntry entry) => entry.Outcome switch
    {
        GateOutcome.Busy => Strings.Get("gui.gate.busy"),
        GateOutcome.RecoveryPending => Strings.Format("gui.gate.recoverypending", Note(entry)),
        GateOutcome.RecoveryFailed => Strings.Format("gui.gate.recoveryfailed", Note(entry)),
        GateOutcome.MissingGate => Strings.Get("gui.gate.missinggate"),
        GateOutcome.Denied => Strings.Get("gui.gate.denied"),
        GateOutcome.ReparseRefused => Strings.Get("gui.gate.reparserefused"),
        GateOutcome.Unreadable => Strings.Get("gui.gate.unreadable"),
        _ => entry.Reason,
    };

    // Busy has no second line: the reason already says what to do ("try again in a moment") and the button is right under it.
    private static string HintFor(GateEntry entry) => entry.Outcome switch
    {
        GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed => Strings.Get("gui.gate.recovery.hint"),
        GateOutcome.MissingGate => Strings.Get("gui.gate.missinggate.hint"),
        GateOutcome.Denied => Strings.Get("gui.gate.denied.hint"),
        GateOutcome.ReparseRefused => Strings.Get("gui.gate.reparserefused.hint"),
        GateOutcome.Unreadable => Strings.Get("gui.gate.unreadable.hint"),
        _ => string.Empty,
    };

    // STEP 4 (the install step): the file this names (I\update\RECOVER.txt) and the launch of recover.exe from here are made there; not yet. Until then no journal is ever
    // written, so the two recovery outcomes cannot happen outside a test, and the window only says where the instructions will be.
    private static IReadOnlyDictionary<string, string> Note(GateEntry entry) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = entry.RecoveryNotePath ?? string.Empty };

    /// <summary>What 「もう一度試す」 does; the tests call it without a mouse. True (and the window closes) when the gate opened.</summary>
    internal bool RetryNow()
    {
        var next = _enter();
        if (next.IsOpen)
        {
            Opened = next;
            _current.Dispose();
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }

        _current.Dispose();
        _current = next;
        Fill();
        return false;
    }

    /// <summary>Unattended runs (shots, tests) must not take the keyboard focus.</summary>
    internal void PreventActivation() => _noActivate = true;

    protected override bool ShowWithoutActivation => _noActivate;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Fill()
    {
        _reason.Text = ReasonFor(_current);
        _hint.Text = HintFor(_current);
        // No empty line under a reason that has nothing to add: the label goes, and the reason takes the space the label's margin gave.
        _hint.Visible = _hint.Text.Length > 0;
        _reason.Margin = new Padding(0, 0, 0, _hint.Visible ? _basePad / 2 : _basePad);
        RetryOffered = _current.Outcome == GateOutcome.Busy;
        _retry.Visible = RetryOffered;
        AcceptButton = RetryOffered ? _retry : _close;
    }
}

internal static class GateStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.gate.title", "PCミエル", "PC Mieru"),
        new("gui.gate.retry", "もう一度試す", "Try again"),

        new(
            "gui.gate.busy",
            "ほかのプログラムが PCミエルのファイルを使っています（更新中など）。しばらくしてからもう一度試してください。",
            "Another program is using PC Mieru's files right now (an update, for example). Try again in a moment."),

        new(
            "gui.gate.recoverypending",
            "前回の更新が最後まで終わっていないため、PCミエルを開けません。直し方は次のファイルに書いてあります: {path}",
            "The last update did not finish, so PC Mieru cannot open. How to repair it is written in this file: {path}"),
        new(
            "gui.gate.recoveryfailed",
            "前回の更新を元に戻そうとしましたが、できませんでした。PCミエルを開けません。直し方は次のファイルに書いてあります: {path}",
            "PC Mieru tried to undo the last update and could not, so it cannot open. How to repair it is written in this file: {path}"),
        new(
            "gui.gate.recovery.hint",
            "分からないときは、配布の ZIP をもう一度展開した新しいフォルダーから開いてください。",
            "If in doubt, unzip the downloaded ZIP file again into a new folder and open it from there."),

        new(
            "gui.gate.missinggate",
            "起動に必要なファイル PCMieru.gate.txt が、PCミエルのフォルダーに見つかりません。",
            "The file PCMieru.gate.txt, which is needed to start, is not in the PC Mieru folder."),
        new(
            "gui.gate.missinggate.hint",
            "削除や移動をしていないか確かめてください。配布の ZIP をもう一度展開すると直ります。",
            "Check that it was not deleted or moved. Unzipping the downloaded ZIP file again repairs it."),

        new(
            "gui.gate.denied",
            "起動に必要なファイル PCMieru.gate.txt を、この Windows ユーザーでは開けません。",
            "This Windows user cannot open PCMieru.gate.txt, which is needed to start."),
        new(
            "gui.gate.denied.hint",
            "フォルダーの置き場所や、ファイルのアクセス権を確かめてください。",
            "Check where the folder is and the access rights of the file."),

        new(
            "gui.gate.reparserefused",
            "PCミエルのフォルダーの中に、別の場所を指すリンクがあるため、安全のため開きません。",
            "There is a link to another place inside the PC Mieru folder, so for safety it does not open."),
        new(
            "gui.gate.reparserefused.hint",
            "配布の ZIP をもう一度展開した、ふつうのフォルダーから開いてください。",
            "Open it from an ordinary folder made by unzipping the downloaded ZIP file again."),

        // The look at the gate file could not be made at all (a path too long for the system, a read error of the disk): not a missing file and not a refusal of access.
        new(
            "gui.gate.unreadable",
            "起動に必要なファイル PCMieru.gate.txt の状態を確かめられませんでした。",
            "The state of PCMieru.gate.txt, which is needed to start, could not be checked."),
        new(
            "gui.gate.unreadable.hint",
            "フォルダーの場所（パスが長すぎないか）を確かめて、もう一度開いてください。直らないときは、配布の ZIP をもう一度展開した新しいフォルダーから開いてください。",
            "Check where the folder is (a path that is too long, for example) and open it again. If that does not help, unzip the downloaded ZIP file again into a new folder and open it from there."),
    ];
}
