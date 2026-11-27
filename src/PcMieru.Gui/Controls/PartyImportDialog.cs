using PcMieru.Core.Siblings;

namespace PcMieru.Gui;

/// <summary>What the owner did with the notice (<see cref="PartyImportDialog"/>).</summary>
internal enum PartyImportAnswer
{
    /// <summary>The cross, Esc, or an unattended run: nothing was chosen and nothing is saved (the notice comes again at the next start).</summary>
    Dismissed,

    /// <summary>「パーツぐらしを開く」: the Parts Life window is opened, where the import and its three choices are.</summary>
    Open,

    /// <summary>「あとで」: gui.ini remembers it (party.import=later) and the notice is not shown again; the Parts Life page still says that progress is waiting.</summary>
    Later,
}

/// <summary>
/// The first-run notice that old Parts Life progress is waiting (DESIGN-C 4.1 items 1-2). It decides nothing about the import: the import, its three choices (引き継ぐ / あとで / 新しく始める)
/// and every check live in PCMieru.Party.exe's first start. This window only says that something is waiting and sends the owner there, or lets them put it off. Modelled on
/// <see cref="UpdateChoiceDialog"/>: themed, fixed size, the primary button is the default, Esc and the cross are "not answered".
/// </summary>
internal sealed class PartyImportDialog : Form
{
    private readonly Label _lead = new() { Name = "partyImportLead", AutoSize = true };
    private readonly Button _open = new() { Name = "partyImportOpen", AutoSize = true };
    private readonly Button _later = new() { Name = "partyImportLater", AutoSize = true };
    private readonly ThemeService _theme;
    private bool _noActivate;

    public PartyImportDialog(ThemeService theme, PartyImportHintState state)
    {
        _theme = theme;
        State = state;
        Name = "PartyImportDialog";
        Text = Strings.Get("gui.party.import.title");
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
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lead.Text = LeadText(state);
        _lead.MaximumSize = new Size(LogicalToDeviceUnits(480), 0);
        _lead.Margin = new Padding(0, 0, 0, pad);
        root.Controls.Add(_lead);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, pad / 2, 0, 0) };
        var size = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        _open.Text = Strings.Get("gui.party.open");
        _open.MinimumSize = size;
        _later.Text = Strings.Get("gui.party.import.later");
        _later.MinimumSize = size;
        buttons.Controls.Add(_open);
        buttons.Controls.Add(_later);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _open;
        ActiveControl = _open;
        _open.Click += (_, _) => Answer(PartyImportAnswer.Open);
        _later.Click += (_, _) => Answer(PartyImportAnswer.Later);
        _theme.Apply(this);
    }

    /// <summary>Which old save the notice is about (the text names it).</summary>
    internal PartyImportHintState State { get; }

    /// <summary>What was chosen; <see cref="PartyImportAnswer.Dismissed"/> until a button is pressed.</summary>
    internal PartyImportAnswer Chosen { get; private set; }

    internal Button OpenButton => _open;

    internal Button LaterButton => _later;

    internal string LeadLine => _lead.Text;

    /// <summary>The text of the notice for the old save found; the Parts Life page's line is a shorter one (<c>gui.party.import.waiting</c>).</summary>
    internal static string LeadText(PartyImportHintState state) =>
        Strings.Get(state == PartyImportHintState.FromPartsParty ? "gui.party.import.lead.former" : "gui.party.import.lead");

    /// <summary>What a press of a button does; the tests call it without a mouse.</summary>
    internal void Answer(PartyImportAnswer answer)
    {
        Chosen = answer;
        DialogResult = answer == PartyImportAnswer.Dismissed ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    /// <summary>Unattended runs (smoke) must not take the keyboard focus.</summary>
    internal void PreventActivation() => _noActivate = true;

    protected override bool ShowWithoutActivation => _noActivate;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Shows the notice and returns the answer. With <see cref="Dialogs.AutoAnswer"/> set (smoke run and tests) it only logs and returns <see cref="PartyImportAnswer.Dismissed"/>: an
    /// automated run never waits for a click, never opens the Parts Life window and never chooses for the owner.
    /// </summary>
    public static PartyImportAnswer Show(IWin32Window? owner, ThemeService theme, PartyImportHintState state)
    {
        if (Dialogs.AutoAnswer is not null)
        {
            Dialogs.Log.Add(Strings.Get("gui.party.import.title") + " | " + LeadText(state));
            return PartyImportAnswer.Dismissed;
        }

        using var form = new PartyImportDialog(theme, state);
        form.ShowDialog(owner);
        return form.Chosen;
    }
}

/// <summary>The texts of the notice and of the Parts Life page's line about waiting progress (ja and en). These name the old app because the owner knows it by that name.</summary>
internal static class PartyImportStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.party.import.title", "パーツぐらしの続き", "Continue Parts Life"),
        new("gui.party.import.lead", "旧パーツぐらし（PartsLife）の続きが見つかりました。パーツぐらしを開くと、引き継ぐかどうかを選べます。旧版の保存はそのまま残ります。", "Progress from the old Parts Life (PartsLife) was found. Open Parts Life to choose whether to bring it over. The old version's save stays as it is."),
        new("gui.party.import.lead.former", "旧名のパーツぐらし（PartsParty）の続きが見つかりました。パーツぐらしを開くと、引き継ぐかどうかを選べます。旧版の保存はそのまま残ります。", "Progress from Parts Life under its former name (PartsParty) was found. Open Parts Life to choose whether to bring it over. The old version's save stays as it is."),
        new("gui.party.import.later", "あとで", "Later"),
        new("gui.party.import.waiting", "旧パーツぐらしの続きがあります。開くと、引き継ぐかどうかを選べます。", "Old Parts Life progress is waiting. Open Parts Life to choose whether to bring it over."),
        new("gui.party.import.later.failed", "あとでの設定を保存できませんでした", "Could not save the 'later' choice"),
    ];
}
