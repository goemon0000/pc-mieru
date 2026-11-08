using System.Globalization;

namespace PcMieru.Gui;

/// <summary>
/// "CPU の割り当て": one check box per logical processor of the process's processor group (at most 64, the width of the
/// mask) and a button that checks all of them again. It only collects a mask; the caller hands it to
/// <see cref="IProcessAffinityControl"/>, which refuses protected processes, an empty mask, CPUs the system does not
/// have and processes that span processor groups. Nothing is applied from here.
/// </summary>
internal sealed class AffinityDialog : Form
{
    /// <summary>The mask is a 64-bit word, so no more than this many boxes can exist.</summary>
    internal const int MaxCpus = 64;

    private readonly List<(int Index, CheckBox Box)> _boxes = [];
    private readonly Button _all = new() { Name = "affinityAll", AutoSize = true };
    private readonly Button _ok = new() { Name = "affinityOk", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly Button _cancel = new() { Name = "affinityCancel", DialogResult = DialogResult.Cancel, AutoSize = true };
    private readonly Label _count = new() { Name = "affinityCount", AutoSize = true };
    private bool _loading = true;

    public AffinityDialog(ThemeService theme, string target, AffinityInfo info)
    {
        Name = "AffinityDialog";
        Text = Strings.Get("gui.proc.affinity.title");
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

        var name = new Label
        {
            Name = "affinityTarget",
            Text = target,
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(460), 0),
            Margin = new Padding(0, 0, 0, pad / 4),
        };
        var message = new Label
        {
            Name = "affinityMessage",
            Text = Strings.Get("gui.proc.affinity.msg"),
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(460), 0),
            Margin = new Padding(0, 0, 0, pad / 2),
        };
        root.Controls.Add(name);
        root.Controls.Add(message);

        var columns = Math.Min(AffinityColumns(info.SystemMask), 8);
        var grid = new TableLayoutPanel
        {
            Name = "affinityGrid",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            Margin = new Padding(0, 0, 0, pad / 2),
        };
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        var shown = 0;
        for (var cpu = 0; cpu < MaxCpus; cpu++)
        {
            if (((info.SystemMask >> cpu) & 1UL) == 0)
            {
                continue;
            }

            var label = Strings.Format("gui.proc.affinity.cpu", Fmt.Dict(("n", cpu.ToString(CultureInfo.CurrentCulture))));
            var box = new CheckBox
            {
                Name = "affinityCpu" + cpu.ToString(CultureInfo.InvariantCulture),
                Text = label,
                AccessibleName = label,
                AutoSize = true,
                Checked = ((info.ProcessMask >> cpu) & 1UL) != 0,
                Margin = new Padding(0, 0, LogicalToDeviceUnits(14), LogicalToDeviceUnits(4)),
            };
            box.CheckedChanged += (_, _) => UpdateState();
            _boxes.Add((cpu, box));
            grid.Controls.Add(box, shown % columns, shown / columns);
            shown++;
        }

        root.Controls.Add(grid);

        _count.Margin = new Padding(0, 0, 0, pad / 2);
        root.Controls.Add(_count);

        var buttons = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _all.Text = Strings.Get("gui.proc.affinity.all");
        _all.MinimumSize = new Size(LogicalToDeviceUnits(120), LogicalToDeviceUnits(32));
        _all.Anchor = AnchorStyles.Left;
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false };
        _ok.Text = Strings.Get("gui.proc.affinity.ok");
        _ok.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        _cancel.Text = Strings.Get("gui.common.cancel");
        _cancel.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        right.Controls.Add(_cancel);
        right.Controls.Add(_ok);
        buttons.Controls.Add(_all, 0, 0);
        buttons.Controls.Add(right, 1, 0);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _ok;
        CancelButton = _cancel;
        _all.Click += (_, _) => SelectAll();
        _loading = false;
        UpdateState();
        theme.Apply(this);
    }

    /// <summary>The CPUs that are checked, as the bits IProcessAffinityControl takes (bit n = logical processor n).</summary>
    public ulong Mask => _boxes.Where(b => b.Box.Checked).Aggregate(0UL, (mask, b) => mask | (1UL << b.Index));

    /// <summary>How many boxes the dialog has (one per CPU the system mask names).</summary>
    internal int CpuCount => _boxes.Count;

    /// <summary>False while nothing is checked: an empty mask is not a valid assignment, so "apply" waits.</summary>
    internal bool CanApply => _ok.Enabled;

    internal string CountText => _count.Text;

    /// <summary>"Back to all": every CPU the system has is checked again (the process's original, unrestricted state).</summary>
    internal void SelectAll()
    {
        _loading = true;
        foreach (var (_, box) in _boxes)
        {
            box.Checked = true;
        }

        _loading = false;
        UpdateState();
    }

    /// <summary>For tests: checks exactly the CPUs in <paramref name="mask"/> (bits the system does not have are ignored).</summary>
    internal void SetMask(ulong mask)
    {
        _loading = true;
        foreach (var (index, box) in _boxes)
        {
            box.Checked = ((mask >> index) & 1UL) != 0;
        }

        _loading = false;
        UpdateState();
    }

    /// <summary>
    /// Asks which CPUs the process may use. Returns null when the user cancels. With <see cref="Dialogs.AutoAnswer"/> set
    /// (smoke and tests) nothing opens and the answer is <see cref="AutoMask"/>, null (cancel) unless a test chose one.
    /// </summary>
    public static ulong? Choose(IWin32Window? owner, ThemeService theme, string target, AffinityInfo info)
    {
        Dialogs.Log.Add(Strings.Get("gui.proc.affinity.title") + " | " + target);
        if (Dialogs.AutoAnswer is not null)
        {
            return AutoMask;
        }

        using var dialog = new AffinityDialog(theme, target, info);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Mask : null;
    }

    /// <summary>What <see cref="Choose"/> answers while dialogs are automatic. Only tests set it.</summary>
    internal static ulong? AutoMask { get; set; }

    private static int AffinityColumns(ulong systemMask) =>
        System.Numerics.BitOperations.PopCount(systemMask) <= 16 ? 4 : 8;

    private void UpdateState()
    {
        if (_loading)
        {
            return;
        }

        var total = _boxes.Count;
        var used = _boxes.Count(b => b.Box.Checked);
        _ok.Enabled = used > 0 && total > 0;
        _count.Text = used == 0
            ? Strings.Get("gui.proc.affinity.none")
            : Strings.Format("gui.proc.affinity.count", Fmt.Dict(("used", used.ToString(CultureInfo.CurrentCulture)), ("total", total.ToString(CultureInfo.CurrentCulture))));
    }
}
