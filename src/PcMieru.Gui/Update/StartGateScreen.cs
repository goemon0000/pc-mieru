using PcMieru.Wire.Gate;

namespace PcMieru.Gui;

/// <summary>
/// The start gate of PCMieru.exe (DESIGN-B 4), before anything else of the app exists: no services, no windows, no sampling and no update timer. Open = the entry is returned and Main
/// holds it until the app ends. Not open = a plain window says why (ja or en, light or dark like the app) and the app does not start; for a busy gate (an update is being installed)
/// the window has 「もう一度試す」, which asks again and carries on when the gate is free. For an interrupted update (a recovery that is pending or failed) it has 「回復する」 when I\update\recover.exe is
/// there and is the file the records vouch for: that hands over to recover.exe and ends the app (see <see cref="RunnerGateRecovery"/>).
///
/// An unattended run (--smoke, --measure) never waits on a window: the reason goes to stderr and the run ends with 1. A smoke run gets its gate the same way every start does: the gate file
/// beside the exe (the publish folder has it; a dev build gets a copy from build/GateNextToExe.targets; a test that starts the exe from a temp folder writes it there).
/// </summary>
internal static class StartGateScreen
{
    /// <summary>
    /// Enters the gate; null means the app must not start (the reason was shown or written). <paramref name="enter"/> is the seam the tests use: the real call is
    /// <see cref="InstallGate.EnterStartup()"/>; <paramref name="recovery"/> is the one for the recovery button (the real one starts I\update\recover.exe).
    /// </summary>
    public static GateEntry? Enter(ThemeService theme, bool unattended, TextWriter error, Func<GateEntry>? enter = null, IGateRecovery? recovery = null)
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

        using var window = new StartGateWindow(theme, entry, enter, recovery);
        window.ShowDialog();
        return window.Opened;
    }
}

/// <summary>
/// The reason window of a start that was refused. Plain: one text (what is wrong, what to do), under an interrupted update one dim line that says which folder this is, 「もう一度試す」 (busy only),
/// 「回復する」 (an interrupted update, and only when recover.exe is there and trusted) and 「閉じる」.
/// </summary>
internal sealed class StartGateWindow : Form
{
    private readonly Func<GateEntry> _enter;
    private readonly IGateRecovery _recovery;
    private readonly Button _recover = new() { Name = "gateRecover", AutoSize = true };
    // Whether recovery can be offered for the entry as it is now: asked once per entry (it reads and hashes recover.exe), not on every layout.
    private bool? _recoveryAvailable;
    private bool _recoveryFailedToStart;
    private readonly Label _reason = new() { Name = "gateReason", AutoSize = true };
    // UseMnemonic off: the line can hold a folder path, and a folder called "R&D" must be shown with its &.
    private readonly ToneLabel _hint = new() { Name = "gateHint", AutoSize = true, UseMnemonic = false };
    private readonly Button _retry = new() { Name = "gateRetry", AutoSize = true };
    private readonly Button _close = new() { Name = "gateClose", AutoSize = true };
    private readonly ThemeService _theme;
    private GateEntry _current;
    private bool _noActivate;
    private int _basePad;

    public StartGateWindow(ThemeService theme, GateEntry refused, Func<GateEntry> enter, IGateRecovery? recovery = null)
    {
        _theme = theme;
        _current = refused;
        _enter = enter;
        _recovery = recovery ?? RunnerGateRecovery.Instance;
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
        _recover.Text = Strings.Get("gui.gate.recover");
        _recover.MinimumSize = new Size(LogicalToDeviceUnits(120), LogicalToDeviceUnits(32));
        buttons.Controls.Add(_recover);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _retry;
        CancelButton = _close;
        _retry.Click += (_, _) => RetryNow();
        _recover.Click += (_, _) => RecoverNow();
        // The folder line is broken by hand at backslashes against the font and width it has now; when the window moves to a screen with another scale, the font and the width change, so it is broken again.
        DpiChanged += (_, _) => BeginInvoke(Fill);
        Fill();
        _hint.SetTone(CellTone.Dim);
        _theme.Apply(this);
    }

    /// <summary>The entry of the retry that succeeded; null when the window ended with the gate still closed.</summary>
    internal GateEntry? Opened { get; private set; }

    /// <summary>The reason as shown.</summary>
    internal string ReasonText => _reason.Text;

    /// <summary>The second, dim line as shown: the folder of an interrupted update (broken at backslashes), or what to do for a link in the folder. Empty when there is none.</summary>
    internal string HintText => _hint.Text;

    /// <summary>The dim line itself (its font and its widest line are what the tests and the smoke run measure).</summary>
    internal Label HintLabel => _hint;

    /// <summary>
    /// Null when the dim line is as it must be: each of its lines fits the width the label has now, and a line is broken only after a backslash (or after the words in front of the path).
    /// Otherwise what is wrong, for a test or a smoke check.
    /// </summary>
    internal string? HintProblem()
    {
        var lines = _hint.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var width = TextRenderer.MeasureText(lines[i], _hint.Font).Width;
            if (width > _hint.MaximumSize.Width)
            {
                return "line " + (i + 1) + " is " + width + " px wide, the label takes " + _hint.MaximumSize.Width + ": " + lines[i];
            }

            if (i < lines.Length - 1 && !lines[i].EndsWith('\\') && !lines[i].EndsWith(':'))
            {
                return "line " + (i + 1) + " ends inside a name: " + lines[i];
            }
        }

        return null;
    }

    internal Button RetryButton => _retry;

    /// <summary>
    /// "Try again" is offered (only for a busy gate). Control.Visible says false for every control of a window that is not shown yet, so this is what a test (and the default button) can ask
    /// before the window is on the screen.
    /// </summary>
    internal bool RetryOffered { get; private set; }

    internal Button CloseButton => _close;

    internal Button RecoverButton => _recover;

    /// <summary>
    /// 「回復する」 is offered: the entry is an interrupted update and the recovery says it can run (recover.exe is the recorded file); it goes away when the start of it failed once. Like
    /// <see cref="RetryOffered"/>, this is what a test and the default button ask, because Control.Visible is false for every control of a window that is not shown yet.
    /// </summary>
    internal bool RecoverOffered { get; private set; }

    /// <summary>True once 「回復する」 has started recover.exe: the window then closes and the app ends (recover.exe starts it again when the install can be used).</summary>
    internal bool RecoveryStarted { get; private set; }

    /// <summary>
    /// The localized reason of a refused entry: one text that says what is wrong and what to do (a new folder from the downloaded ZIP file). No file path is named in it. This is the text without
    /// 「回復する」; <see cref="ReasonText"/> is the one the window shows (it points at the button when the button is there).
    /// </summary>
    internal static string ReasonFor(GateEntry entry) => ReasonFor(entry, recoverOffered: false, recoveryFailedToStart: false);

    /// <summary>
    /// The same, for a window that has the recovery button (<paramref name="recoverOffered"/>: the text says what the button does, then the new folder as what to do when that does not work) or
    /// whose recovery could not be started (<paramref name="recoveryFailedToStart"/>: the new folder only, and what went wrong in one plain sentence).
    /// </summary>
    internal static string ReasonFor(GateEntry entry, bool recoverOffered, bool recoveryFailedToStart) => entry.Outcome switch
    {
        GateOutcome.Busy => Strings.Get("gui.gate.busy"),
        GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed when recoveryFailedToStart => Strings.Get("gui.gate.recover.failed"),
        GateOutcome.RecoveryPending => Strings.Get(recoverOffered ? "gui.gate.recoverypending.button" : "gui.gate.recoverypending"),
        GateOutcome.RecoveryFailed => Strings.Get(recoverOffered ? "gui.gate.recoveryfailed.button" : "gui.gate.recoveryfailed"),
        GateOutcome.MissingGate => Strings.Get("gui.gate.missinggate"),
        GateOutcome.Denied => Strings.Get("gui.gate.denied"),
        GateOutcome.ReparseRefused => Strings.Get("gui.gate.reparserefused"),
        GateOutcome.Unreadable => Strings.Get("gui.gate.unreadable"),
        _ => entry.Reason,
    };

    // The second line is dim and short. An interrupted update says which folder it is (the person may have several copies); a link in the folder keeps its one sentence of advice. Every other
    // state has all it has to say in the reason, and Busy has the button right under it.
    private string HintFor(GateEntry entry) => entry.Outcome switch
    {
        GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed => FolderLine(entry),
        GateOutcome.ReparseRefused => Strings.Get("gui.gate.reparserefused.hint"),
        _ => string.Empty,
    };

    /// <summary>
    /// 「このフォルダー: C:\...」 broken at backslashes to the width of the label (see <see cref="FolderLineWrap"/>). Empty when the folder is not known or a name in it is wider than a line: a path is
    /// never broken inside a name, so such a path is left out instead.
    /// </summary>
    private string FolderLine(GateEntry entry)
    {
        var folder = Path.GetDirectoryName(entry.GatePath);
        if (string.IsNullOrEmpty(folder))
        {
            return string.Empty;
        }

        var prefix = Strings.Format("gui.gate.folder", Fmt.Dict(("path", string.Empty)));
        var room = _hint.MaximumSize.Width * 94 / 100; // a little short of the label's width: the real label measures with its own padding and, after a scale change, hinting
        return FolderLineWrap.Wrap(prefix, folder, line => TextRenderer.MeasureText(line, _hint.Font).Width, room) ?? string.Empty;
    }

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
        _recoveryAvailable = null;
        _recoveryFailedToStart = false;
        Fill();
        return false;
    }

    /// <summary>
    /// What 「回復する」 does; the tests call it without a mouse. The shared gate is not held for an interrupted update (the entry is already closed), and is let go of here anyway; then recover.exe is
    /// started (it waits for this program to end, recovers, and starts the app again) and the window closes. False when it could not be started: the window says so, offers the new folder as the way on,
    /// and the button is gone.
    /// </summary>
    internal bool RecoverNow()
    {
        if (!RecoverOffered)
        {
            return false;
        }

        _current.Dispose();
        if (!_recovery.Start(_current, out _))
        {
            _recoveryFailedToStart = true;
            Fill();
            return false;
        }

        RecoveryStarted = true;
        DialogResult = DialogResult.Cancel; // Opened stays null: the app does not start from here
        Close();
        return true;
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
        var interrupted = _current.Outcome is GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed;
        if (interrupted && !_recoveryFailedToStart)
        {
            _recoveryAvailable ??= _recovery.IsAvailable(_current);
        }

        RecoverOffered = interrupted && !_recoveryFailedToStart && _recoveryAvailable == true;
        _reason.Text = ReasonFor(_current, RecoverOffered, _recoveryFailedToStart);
        _hint.Text = HintFor(_current);
        // No empty line under a reason that has nothing to add: the label goes, and the reason takes the space the label's margin gave.
        _hint.Visible = _hint.Text.Length > 0;
        _reason.Margin = new Padding(0, 0, 0, _hint.Visible ? _basePad / 2 : _basePad);
        RetryOffered = _current.Outcome == GateOutcome.Busy;
        _retry.Visible = RetryOffered;
        _recover.Visible = RecoverOffered;
        AcceptButton = RecoverOffered ? _recover : RetryOffered ? _retry : _close;
    }
}

internal static class GateStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.gate.title", "PCミエル", "PC Mieru"),
        new("gui.gate.retry", "もう一度試す", "Try again"),

        new("gui.gate.recover", "回復する", "Recover"),

        // Every refusal is one text: what is wrong, then what to do. None of them names a file or a folder, and none suggests that the person deleted or moved something. The texts WITHOUT a button are the
        // plain ones (recover.exe is not there or is not the file the records vouch for: the advice is a new folder); the texts WITH the button say what it does and keep the new folder for when it does not work.
        new(
            "gui.gate.busy",
            "PCミエルのファイルが使用中です。しばらく待ってから、もう一度試してください。",
            "PC Mieru's files are in use right now. Wait a moment and try again."),

        new(
            "gui.gate.recoverypending",
            "前回の更新が途中で止まったため、このフォルダーからは PCミエルを開けません。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。",
            "The last update did not finish, so PC Mieru cannot open from this folder. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there."),
        new(
            "gui.gate.recoveryfailed",
            "前回の更新を元に戻せなかったため、このフォルダーからは PCミエルを開けません。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。",
            "PC Mieru could not undo the last update, so it cannot open from this folder. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there."),
        new(
            "gui.gate.recoverypending.button",
            "前回の更新が途中で止まったため、このフォルダーからは PCミエルを開けません。「回復する」を押すと、途中の更新を片づけてから PCミエルを開きます。うまくいかないときは、ダウンロードした ZIP を新しいフォルダーに展開し、そこから開いてください。",
            "The last update did not finish, so PC Mieru cannot open from this folder. Press \"Recover\" to clear up the unfinished update and then open PC Mieru. If that does not work, unzip the downloaded ZIP file into a new folder and open PC Mieru from there."),
        new(
            "gui.gate.recoveryfailed.button",
            "前回の更新を元に戻せなかったため、このフォルダーからは PCミエルを開けません。「回復する」を押すと、もう一度元に戻してみます。うまくいかないときは、ダウンロードした ZIP を新しいフォルダーに展開し、そこから開いてください。",
            "PC Mieru could not undo the last update, so it cannot open from this folder. Press \"Recover\" to try putting the earlier version back once more. If that does not work, unzip the downloaded ZIP file into a new folder and open PC Mieru from there."),
        // The button was pressed and recover.exe could not be started: nothing was changed, and the new folder is the way on.
        new(
            "gui.gate.recover.failed",
            "回復のプログラムを起動できませんでした。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。",
            "The recovery program could not be started. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there."),
        // The one dim line under an interrupted update: which folder this is. {path} is the last thing in it (the line is broken after the words and at backslashes, never inside a name).
        new("gui.gate.folder", "このフォルダー: {path}", "This folder: {path}"),

        new(
            "gui.gate.missinggate",
            "起動に必要なファイルが見つからないため、PCミエルを開けません。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。",
            "PC Mieru cannot open because a required file is missing. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there."),

        new(
            "gui.gate.denied",
            "必要なファイルを開けないため、PCミエルを起動できません。ダウンロードした ZIP をもう一度、デスクトップなど、自分が使える場所の新しいフォルダーに展開して開いてください。それでも開けないときは、PC の管理者に相談してください。",
            "PC Mieru cannot open a required file. Unzip the downloaded ZIP file into a new folder in a place you can use, such as the Desktop, and open PC Mieru from there. If it still cannot open, contact the person who manages this PC."),

        new(
            "gui.gate.reparserefused",
            "PCミエルのフォルダーの中に、別の場所を指すリンクがあるため、安全のため開きません。",
            "There is a link to another place inside the PC Mieru folder, so for safety it does not open."),
        new(
            "gui.gate.reparserefused.hint",
            "ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。",
            "Unzip the downloaded ZIP file into a new folder and open PC Mieru from there."),

        // The look at the gate file could not be made at all (a path too long for the system, a read error of the disk): not a missing file and not a refusal of access, so it says that first,
        // then gives the advice of the access-denied text, with a short path as the thing to try.
        new(
            "gui.gate.unreadable",
            "起動に必要なファイル PCMieru.gate.txt の状態を確かめられませんでした。ダウンロードした ZIP をもう一度、デスクトップなど、自分が使える場所の、パスの短い新しいフォルダーに展開して開いてください。それでも開けないときは、PC の管理者に相談してください。",
            "The state of PCMieru.gate.txt, which is needed to start, could not be checked. Unzip the downloaded ZIP file into a new folder with a short path in a place you can use, such as the Desktop, and open PC Mieru from there. If it still cannot open, contact the person who manages this PC."),
    ];
}

/// <summary>
/// Breaks a line that ends in a folder path so that the line is never broken inside a name. The label of the window breaks only at spaces and, for a word wider than the line, anywhere; a path has no
/// spaces to break at, so it would be cut in the middle of a folder's name. Here the break goes after a backslash (or, when even the first name does not fit after the words in front, after those words).
/// </summary>
internal static class FolderLineWrap
{
    /// <summary>
    /// <paramref name="prefix"/> + <paramref name="path"/> in lines no wider than <paramref name="room"/> (as <paramref name="width"/> measures them), joined with a line feed; the text unchanged when it fits.
    /// Null when a name between two backslashes is wider than a line by itself: the caller leaves the path out rather than breaking a name.
    /// </summary>
    public static string? Wrap(string prefix, string path, Func<string, int> width, int room)
    {
        var lines = new List<string>();
        var line = prefix;
        foreach (var piece in Pieces(path))
        {
            if (width(line + piece) <= room)
            {
                line += piece;
                continue;
            }

            if (width(piece) > room)
            {
                return null;
            }

            lines.Add(line.TrimEnd());
            line = piece;
        }

        lines.Add(line);
        return string.Join('\n', lines);
    }

    /// <summary>"C:\Users\me" becomes "C:\", "Users\", "me": every piece but the last ends with the backslash it was cut after.</summary>
    private static IEnumerable<string> Pieces(string path)
    {
        var start = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\')
            {
                yield return path[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < path.Length)
        {
            yield return path[start..];
        }
    }
}
