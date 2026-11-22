using System.Globalization;
using System.Text;
using PcMieru.Checks.Build;

namespace PcMieru.Gui;

/// <summary>
/// "構成コードをコピー（/my-config 用）": shows what the configuration code (PCB1) holds BEFORE anything leaves the app: the parts that go
/// into it (category, name, count), the code string itself, what was left out of it (the notes, in words) and the categories that could not
/// be read (so a short or empty configuration is explained, not silently short). Copy puts exactly the code string on the clipboard; Save
/// writes exactly the JSON the library gives as the saved form. Nothing is sent anywhere; the dialog has no "open the site" button.
/// </summary>
internal sealed class BuildCodeDialog : Form
{
    /// <summary>The name the save dialog proposes.</summary>
    internal const string DefaultFileName = "pc-config.json";

    /// <summary>How many parts the list shows before it scrolls.</summary>
    private const int MaxShownRows = 10;

    /// <summary>The fewest parts the list shows, and the fewest lines of the code the box shows, when the screen is too low for the dialog.</summary>
    private const int MinShownRows = 3;

    private const int MinCodeLines = 3;

    private readonly BuildCodeResult _result;
    private readonly Func<string, bool> _copyToClipboard;
    private readonly List<PartRow> _rows = [];
    private readonly Label _lead = new() { Name = "pcb1Lead", AutoSize = true };
    private readonly Label _partsHead = new() { Name = "pcb1PartsHead", AutoSize = true };
    private readonly GridView _parts = new() { Name = "pcb1Parts" };
    private readonly Label _partsNone = new() { Name = "pcb1PartsNone", AutoSize = true };
    private readonly Label _codeHead = new() { Name = "pcb1CodeHead", AutoSize = true };
    private readonly TextBox _code = new() { Name = "pcb1Code", Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = false };
    private readonly Label _notesHead = new() { Name = "pcb1NotesHead", AutoSize = true };
    private readonly Label _notes = new() { Name = "pcb1Notes", AutoSize = true };
    private readonly Label _unreadHead = new() { Name = "pcb1UnreadHead", AutoSize = true };
    private readonly Label _unread = new() { Name = "pcb1Unread", AutoSize = true };
    private readonly Label _status = new() { Name = "pcb1Result", AutoSize = true };
    private readonly Button _copy = new() { Name = "pcb1Copy", AutoSize = true };
    private readonly Button _save = new() { Name = "pcb1Save", AutoSize = true };
    private readonly Button _close = new() { Name = "pcb1Close", DialogResult = DialogResult.Cancel, AutoSize = true };
    private Font _mono;
    private Font _bold;
    private bool _fontsReady;
    private readonly bool _empty;
    private TableLayoutPanel _root = null!;
    private int _codeLines = CodeLines;
    private bool _saving;

    private readonly record struct PartRow(string Category, string Name, string Detail, string Count);

    /// <param name="copyToClipboard">Puts the text on the clipboard; false when the clipboard was busy.</param>
    public BuildCodeDialog(ThemeService theme, BuildCodeResult result, Func<string, bool> copyToClipboard)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        _empty = result.Config.IsEmpty;
        _copyToClipboard = copyToClipboard;
        Name = "BuildCodeDialog";
        Text = Strings.Get("gui.hw.pcb1.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var pad = LogicalToDeviceUnits(16);
        var width = LogicalToDeviceUnits(780);
        var headMargin = new Padding(0, pad / 2, 0, pad / 4);
        _bold = new Font(Font, FontStyle.Bold);
        _mono = MonoFont();

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _root = root;

        _lead.Text = Strings.Get("gui.hw.pcb1.lead");
        _lead.MaximumSize = new Size(width, 0);
        _lead.Margin = new Padding(0, 0, 0, pad / 4);
        root.Controls.Add(_lead);

        BuildRows();
        Head(_partsHead, Strings.Get("gui.hw.pcb1.parts"), headMargin, root);
        if (_rows.Count > 0)
        {
            PageLayout.Prepare(
                _parts,
                Strings.Get("gui.hw.pcb1.parts"),
                (Strings.Get("gui.hw.pcb1.col.category"), LogicalToDeviceUnits(120), HorizontalAlignment.Left),
                (Strings.Get("gui.hw.pcb1.col.name"), LogicalToDeviceUnits(370), HorizontalAlignment.Left),
                (Strings.Get("gui.hw.pcb1.col.detail"), LogicalToDeviceUnits(230), HorizontalAlignment.Left),
                (Strings.Get("gui.hw.pcb1.col.count"), LogicalToDeviceUnits(56), HorizontalAlignment.Right));
            _parts.Dock = DockStyle.None;
            _parts.FillColumn = 1;
            _parts.Cells = Cell;
            _parts.VirtualListSize = _rows.Count;

            // Every part that goes into the code is meant to be seen: the list grows to its rows (a long one scrolls after ten, so the dialog stays
            // on a screen; on a screen too low for that, FitToHeight gives up rows).
            _parts.Size = new Size(width, PartsListHeight(Math.Min(_rows.Count, MaxShownRows)));
            _parts.Margin = new Padding(0, 0, 0, pad / 4);
            _parts.SizeChanged += (_, _) => FitPartColumns();
            _parts.HandleCreated += (_, _) =>
            {
                // The header exists now: its real height (not the guess) decides how high the list is for whole rows. Not in this call: the native
                // list has no columns or rows yet while its handle is being made, so it is asked once that is over.
                _parts.BeginInvoke(() =>
                {
                    if (!_parts.IsDisposed)
                    {
                        FitPartColumns();
                        FitToScreen();
                    }
                });
            };
            _parts.ColumnsRescaled += (_, _) => FitPartColumns();
            _parts.CellTip = PartTip;
            FitPartColumns();
            root.Controls.Add(_parts);
        }
        else
        {
            _partsNone.Text = Strings.Get("gui.hw.pcb1.parts.none");
            _partsNone.Margin = new Padding(0, 0, 0, pad / 4);
            root.Controls.Add(_partsNone);
        }

        Head(_codeHead, Strings.Get("gui.hw.pcb1.code"), headMargin, root);
        _code.Text = result.Code;
        _code.Width = width;
        _code.Margin = new Padding(0, 0, 0, pad / 4);
        _code.AccessibleName = Strings.Get("gui.hw.pcb1.code");
        root.Controls.Add(_code);

        if (result.Notes.Count > 0)
        {
            Head(_notesHead, Strings.Get("gui.hw.pcb1.notes"), headMargin, root);
            _notes.Text = string.Join("\n", BuildCodeText.NoteLines(result.Notes).Select(static line => BuildCodeText.Bullet + line));
            _notes.MaximumSize = new Size(width, 0);
            root.Controls.Add(_notes);
        }

        Head(_unreadHead, Strings.Get("gui.hw.pcb1.unread"), headMargin, root);
        _unread.Text = result.Unread.Count > 0
            ? string.Join("\n", result.Unread.Select(static u => BuildCodeText.Bullet + BuildCodeText.UnreadLine(u)))
            : Strings.Get("gui.hw.pcb1.unread.none");
        _unread.MaximumSize = new Size(width, 0);
        root.Controls.Add(_unread);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, pad / 2, 0, 0) };
        _close.Text = Strings.Get("gui.common.close");
        _save.Text = Strings.Get("gui.hw.pcb1.save");
        _copy.Text = Strings.Get("gui.common.copy");
        foreach (var button in new[] { _close, _save, _copy })
        {
            button.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        }

        _status.Margin = new Padding(0, LogicalToDeviceUnits(8), LogicalToDeviceUnits(12), 0);
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_status);
        root.Controls.Add(buttons);
        Controls.Add(root);

        // An empty configuration has nothing to copy or save: both buttons are off (the line above the code says why) and Enter closes the window.
        _copy.Enabled = !_empty;
        _save.Enabled = !_empty;
        AcceptButton = _empty ? _close : _copy;
        CancelButton = _close;
        ActiveControl = _empty ? _close : _copy;
        _copy.Click += (_, _) => CopyNow();
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        theme.Apply(this);
        _code.Font = _mono;
        _fontsReady = true;
        // The box is whole lines high (no half line at the bottom): its client area is CodeLines lines of the code's own font plus a little air.
        // The list and the box give up rows and lines when the screen is too low for the whole dialog.
        FitToScreen();
        _status.ForeColor = theme.Palette.TextDim;
        _partsNone.ForeColor = theme.Palette.TextDim;
        if (result.Unread.Count == 0)
        {
            _unread.ForeColor = theme.Palette.TextDim;
        }
    }

    /// <summary>Where the save dialog asks for a file. The default is the system's SaveFileDialog; a test puts its own here. Null: nothing was chosen.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IWin32Window, string?>? SavePicker { get; set; }

    /// <summary>The code string the dialog shows (and Copy puts on the clipboard).</summary>
    internal string CodeText => _code.Text;

    /// <summary>The parts as the list shows them: the category word, the name, the details and the count.</summary>
    internal IReadOnlyList<(string Category, string Name, string Detail, string Count)> PartLines =>
        _rows.Select(static r => (r.Category, r.Name, r.Detail, r.Count)).ToArray();

    /// <summary>The sentences of the notes, one per line (empty when nothing was left out).</summary>
    internal string NotesText => _result.Notes.Count > 0 ? _notes.Text : string.Empty;

    /// <summary>The lines of the categories that could not be read, or the sentence that every one was read.</summary>
    internal string UnreadText => _unread.Text;

    /// <summary>The result line next to the buttons (copied, saved, or why not).</summary>
    internal string Result => _status.Text;

    internal Button CopyButton => _copy;

    /// <summary>How many lines of the code the box shows before it scrolls.</summary>
    internal const int CodeLines = 5;

    /// <summary>The box the code is in, for tests and the smoke run.</summary>
    internal TextBox CodeBox => _code;

    /// <summary>The height of the code box's client area: <see cref="CodeLinesShown"/> lines of the code's font and a little air, never a part of a line.</summary>
    internal int CodeBoxClientHeight => (_codeLines * CodeLineHeight) + LogicalToDeviceUnits(CodeBoxPadding);

    /// <summary>The air in the code box, in logical pixels.</summary>
    internal const int CodeBoxPadding = 6;

    /// <summary>The line height of the code's font (the box's own, so a window at another scale is asked what it has now), in pixels.</summary>
    internal int CodeLineHeight => _code.Font.Height;

    /// <summary>How many lines the box shows now: <see cref="CodeLines"/>, or fewer on a screen too low for the whole dialog.</summary>
    internal int CodeLinesShown => _codeLines;

    /// <summary>How many parts the list shows now before it scrolls: ten, or fewer on a screen too low for the whole dialog.</summary>
    internal int PartRowsShown => _rows.Count == 0 ? 0 : _parts.RowsThatFit(_parts.Height);

    /// <summary>The height the whole window needs now: its content and the frame round it.</summary>
    internal int PreferredHeight => _root.GetPreferredSize(Size.Empty).Height + (Height - ClientSize.Height);

    /// <summary>
    /// The height of the list for this many rows: the header as the list measures it now, the rows as the list measures them, and the border.
    /// Measured, not assumed (a header that is not one row high, as at 150%, left half a row under the last one).
    /// </summary>
    private int PartsListHeight(int rows) => _parts.HeightForRows(rows);

    /// <summary>
    /// The code's font: monospace, and the same visual size as the dialog's own text. Consolas is drawn smaller than the dialog's font at the
    /// same point size, so it is a tenth bigger. It is made from the dialog's current font, so it follows the scale the window is at.
    /// </summary>
    private Font MonoFont() => new("Consolas", Font.SizeInPoints * 1.1f);

    /// <summary>
    /// The heads and the code box have fonts of their own, made from the dialog's font; when that font changes (the window goes to another
    /// scale) they are made again from the new one, or they stay at the old size while everything round them grows.
    /// </summary>
    /// <remarks>
    /// A control keeps the font it has when the new one is equal to it in value (the Font property compares by value), so an old font is
    /// never disposed after a new one was set: a font that is the same as the one in use is dropped and the one in use stays. (Disposing the
    /// old one first left the code box with a disposed font at 150%, and the first line height asked of it threw.)
    /// </remarks>
    internal void RefreshFonts()
    {
        if (!_fontsReady)
        {
            return;
        }

        var bold = new Font(Font, FontStyle.Bold);
        if (bold.Equals(_bold))
        {
            bold.Dispose();
        }
        else
        {
            var old = _bold;
            _bold = bold;
            foreach (var head in new[] { _partsHead, _codeHead, _notesHead, _unreadHead })
            {
                head.Font = _bold;
            }

            old.Dispose();
        }

        var mono = MonoFont();
        if (mono.Equals(_mono))
        {
            mono.Dispose();
        }
        else
        {
            var old = _mono;
            _mono = mono;
            _code.Font = _mono;
            old.Dispose();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RefreshFonts();
    }

    /// <summary>
    /// Makes the window as low as the screen allows: the list gives up rows first (down to <see cref="MinShownRows"/>; it scrolls), then the box
    /// gives up lines of the code (down to <see cref="MinCodeLines"/>; it scrolls too). Each call starts from the full size, so a window that
    /// goes to a roomier screen gets its rows back. Returns the height the window needs then.
    /// </summary>
    internal int FitToHeight(int available)
    {
        var floorRows = Math.Min(_rows.Count, MinShownRows);
        var rows = Math.Min(_rows.Count, MaxShownRows);
        var lines = CodeLines;
        SetHeights(rows, lines);
        while (PreferredHeight > available && (rows > floorRows || lines > MinCodeLines))
        {
            if (rows > floorRows)
            {
                rows--;
            }
            else
            {
                lines--;
            }

            SetHeights(rows, lines);
        }

        return PreferredHeight;
    }

    private void SetHeights(int rows, int lines)
    {
        if (_rows.Count > 0)
        {
            _parts.Height = PartsListHeight(rows);
        }

        _codeLines = lines;
        _code.ClientSize = new Size(_code.ClientSize.Width, CodeBoxClientHeight);
    }

    /// <summary>Fits the window to the working area of the screen it is on (the primary one while it has no window yet), leaving a little air.</summary>
    private void FitToScreen()
    {
        var area = IsHandleCreated ? Screen.FromControl(this).WorkingArea : (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        FitToHeight(area.Height - LogicalToDeviceUnits(24));
    }

    /// <summary>
    /// The columns of the list are made for what the rows hold, in pixels of the scale the window is at now: the category, the detail and the
    /// count take what their longest text needs (the design widths are the least), and the name takes what is left over, so the names of the
    /// parts get the room the other columns do not need. Widths written in logical pixels went wrong at a scale where the font grows with it.
    /// </summary>
    private void FitPartColumns()
    {
        if (_rows.Count == 0 || _parts.Columns.Count != 4)
        {
            return;
        }

        var detailCeiling = Math.Max(_parts.ClientSize.Width * 45 / 100, _parts.Scaled(120));
        _parts.Columns[0].Width = Math.Max(_parts.ContentNeed(0), _parts.Scaled(80));
        _parts.Columns[2].Width = Math.Min(Math.Max(_parts.ContentNeed(2), _parts.Scaled(60)), detailCeiling);
        _parts.Columns[3].Width = Math.Max(_parts.ContentNeed(3), _parts.Scaled(56));
        _parts.FitLastColumn();
    }

    protected override void OnLoad(EventArgs e)
    {
        // The window is on its screen now: a screen lower than the first guess takes rows and lines away before the window is placed.
        FitToScreen();
        base.OnLoad(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // The window is laid out and on its screen: the list is asked for its header and rows once more (only a height that differs is a change).
        FitPartColumns();
        FitToScreen();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);

        // The window and everything in it are scaled after this call returns: the fit is done once that is over.
        if (IsHandleCreated)
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed)
                {
                    RefreshFonts();
                    FitPartColumns();
                    FitToScreen();
                }
            });
        }
    }

    private bool _noActivate;

    /// <summary>The smoke run shows the dialog to take its picture: it must not take the keyboard focus, as the main window does not.</summary>
    internal void PreventActivation() => _noActivate = true;

    protected override bool ShowWithoutActivation => _noActivate;

    internal Button SaveButton => _save;

    internal GridView PartsList => _parts;

    /// <summary>What the Copy button does; the tests call it without a mouse. The clipboard gets the code string and nothing else.</summary>
    internal void CopyNow()
    {
        if (_empty)
        {
            return;
        }

        var ok = _copyToClipboard(_result.Code);
        _status.Text = Strings.Get(ok ? "gui.common.copied" : "gui.hw.pcb1.copy.failed");
    }

    /// <summary>
    /// What the Save button does. The file is the JSON the library gives as the saved form, written as it is (UTF-8 without a byte order
    /// mark) off the UI thread. A failure is said with a fixed text and the type and HRESULT of the exception: never its message, which
    /// can carry the path or the user's name.
    /// </summary>
    internal async Task SaveAsync()
    {
        if (_saving || _empty)
        {
            return;
        }

        var path = (SavePicker ?? PickWithSystemDialog)(this);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _saving = true;
        _save.Enabled = false;
        var json = _result.Json;
        string status;
        try
        {
            await Task.Run(() => File.WriteAllText(path, json, new UTF8Encoding(false))).ConfigureAwait(true);
            status = Strings.Get("gui.hw.pcb1.saved");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            status = Strings.Format("gui.hw.pcb1.save.failed", Fmt.Dict(("detail", CheckFormat.ExceptionDetail(ex))));
        }

        _saving = false;
        if (!IsDisposed)
        {
            _save.Enabled = !_empty;
            _status.Text = status;
        }
    }

    private static string? PickWithSystemDialog(IWin32Window owner)
    {
        using var dialog = new SaveFileDialog
        {
            Title = Strings.Get("gui.hw.pcb1.save.title"),
            Filter = Strings.Get("gui.diag.filter.json"),
            FileName = DefaultFileName,
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    private void BuildRows()
    {
        foreach (var category in BuildConfig.Categories)
        {
            foreach (var part in _result.Config[category])
            {
                _rows.Add(new PartRow(BuildCodeText.Category(category), part.Name, BuildCodeText.Detail(part), part.Q.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return new GridCell(string.Empty);
        }

        var r = _rows[row];
        return column switch
        {
            0 => new GridCell(r.Category),
            1 => new GridCell(r.Name),
            2 => new GridCell(r.Detail, CellTone.Dim),
            _ => new GridCell(r.Count),
        };
    }

    /// <summary>
    /// The full text of a name or a detail that the column cuts (a long model name, a part with many details). Nothing for a cell that fits: a
    /// tooltip that repeats what is already on the screen is noise.
    /// </summary>
    private string? PartTip(int row, int column)
    {
        if (row < 0 || row >= _rows.Count || column is not (1 or 2) || column >= _parts.Columns.Count)
        {
            return null;
        }

        var text = column == 1 ? _rows[row].Name : _rows[row].Detail;
        return text.Length > 0 && _parts.IsCut(row, column) ? text : null;
    }

    private void Head(Label label, string text, Padding margin, Control parent)
    {
        label.Text = text;
        label.Font = _bold;
        label.Margin = margin;
        parent.Controls.Add(label);
    }

    /// <summary>
    /// Shows the dialog. With <see cref="Dialogs.AutoAnswer"/> set (smoke run and tests) it only logs, so an automated run never waits for a
    /// click. Without it nothing is logged: <see cref="Dialogs.Log"/> lives as long as the app.
    /// </summary>
    public static void Open(IWin32Window? owner, ThemeService theme, BuildCodeResult result, Func<string, bool> copyToClipboard)
    {
        if (Dialogs.AutoAnswer is not null)
        {
            Dialogs.Log.Add(Strings.Get("gui.hw.pcb1.title") + " | " + result.Code);
            return;
        }

        using var form = new BuildCodeDialog(theme, result, copyToClipboard);
        form.ShowDialog(owner);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mono.Dispose();
            _bold.Dispose();
        }

        base.Dispose(disposing);
    }
}
