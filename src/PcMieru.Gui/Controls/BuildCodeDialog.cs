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
    private readonly Font _mono;
    private readonly Font _bold;
    private bool _saving;

    private readonly record struct PartRow(string Category, string Name, string Detail, string Count);

    /// <param name="copyToClipboard">Puts the text on the clipboard; false when the clipboard was busy.</param>
    public BuildCodeDialog(ThemeService theme, BuildCodeResult result, Func<string, bool> copyToClipboard)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
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
        _mono = new Font("Consolas", Font.SizeInPoints);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

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

            // Every part that goes into the code is meant to be seen: the list grows to its rows (a long one scrolls after ten, so the dialog stays on a screen).
            var rowHeight = Math.Max(Font.Height + LogicalToDeviceUnits(14), LogicalToDeviceUnits(28));
            _parts.Size = new Size(width, (rowHeight * (Math.Min(_rows.Count, MaxShownRows) + 1)) + LogicalToDeviceUnits(6));
            _parts.Margin = new Padding(0, 0, 0, pad / 4);
            _parts.SizeChanged += (_, _) => _parts.FitLastColumn();
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
        _code.Size = new Size(width, LogicalToDeviceUnits(84));
        _code.Margin = new Padding(0, 0, 0, pad / 4);
        _code.AccessibleName = Strings.Get("gui.hw.pcb1.code");
        root.Controls.Add(_code);

        if (result.Notes.Count > 0)
        {
            Head(_notesHead, Strings.Get("gui.hw.pcb1.notes"), headMargin, root);
            _notes.Text = string.Join("\n", result.Notes.Select(static n => "・" + BuildCodeText.Note(n)));
            _notes.MaximumSize = new Size(width, 0);
            root.Controls.Add(_notes);
        }

        Head(_unreadHead, Strings.Get("gui.hw.pcb1.unread"), headMargin, root);
        _unread.Text = result.Unread.Count > 0
            ? string.Join("\n", result.Unread.Select(static u => "・" + BuildCodeText.UnreadLine(u)))
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

        AcceptButton = _copy;
        CancelButton = _close;
        ActiveControl = _copy;
        _copy.Click += (_, _) => CopyNow();
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        theme.Apply(this);
        _code.Font = _mono;
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

    private bool _noActivate;

    /// <summary>The smoke run shows the dialog to take its picture: it must not take the keyboard focus, as the main window does not.</summary>
    internal void PreventActivation() => _noActivate = true;

    protected override bool ShowWithoutActivation => _noActivate;

    internal Button SaveButton => _save;

    internal GridView PartsList => _parts;

    /// <summary>What the Copy button does; the tests call it without a mouse. The clipboard gets the code string and nothing else.</summary>
    internal void CopyNow()
    {
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
        if (_saving)
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
            _save.Enabled = true;
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
