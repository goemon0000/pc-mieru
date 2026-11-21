using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// The load check: it puts a load on the CPU or on the memory (not the GPU) for 30 seconds, 2 minutes or 5 minutes and judges whether the
/// PC ran it steadily. While it runs the tab shows how far it is, the rate it measures right now with its unit, and a stop button.
/// What is chosen is locked while any check runs.
/// </summary>
internal sealed class LoadCheckTab : RunCheckTab
{
    /// <summary>The lengths the request may name, in seconds (the same list as <see cref="CheckRequest.AllowedDurations"/>).</summary>
    internal static readonly int[] DurationSeconds = [30, 120, 300];

    private readonly Label _kindLabel = new() { AutoSize = true, Name = "checksLoadKindLabel" };
    private readonly ToggleButton _cpu = new() { Name = "checksLoadCpu" };
    private readonly ToggleButton _memory = new() { Name = "checksLoadMemory" };
    private readonly FlowLayoutPanel _kinds = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Name = "checksLoadKinds" };
    private readonly Label _durationLabel = new() { AutoSize = true, Name = "checksLoadDurationLabel" };
    private readonly ComboBox _duration = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "checksLoadDuration" };
    private readonly TableLayoutPanel _strip = new() { Name = "checksLoadStrip", Visible = false };
    private readonly CheckProgressBar _bar = new() { Name = "checksLoadBar" };
    private readonly Label _elapsed = new() { AutoSize = true, Name = "checksLoadElapsed" };
    private readonly ToneLabel _rate = new() { AutoSize = true, Name = "checksLoadRate" };
    private CheckKind _kind = CheckKind.Cpu;

    public LoadCheckTab(CheckController controller, Func<int, int> px)
        : base(controller, px, "checksLoad", "gui.chk.load.intro")
    {
        _cpu.Click += (_, _) => SelectKind(CheckKind.Cpu);
        _memory.Click += (_, _) => SelectKind(CheckKind.Memory);
        _duration.Width = Px(110);
        _rate.SetTone(CellTone.Dim);
        foreach (var toggle in new[] { _cpu, _memory })
        {
            toggle.Margin = new Padding(0, 0, Px(4), 0);
            toggle.Padding = new Padding(Px(6), 0, Px(6), 0);
            toggle.MinimumSize = new Size(0, Px(30));
            _kinds.Controls.Add(toggle);
        }

        _kinds.Margin = new Padding(0);
        _kinds.AccessibleRole = AccessibleRole.Grouping;
        _cpu.TabIndex = 0;
        _memory.TabIndex = 1;
        _duration.TabIndex = 2;
        StartButton.TabIndex = 3;
        StopButton.TabIndex = 4;
        Grid.TabIndex = 5;

        var pad = Px(12);
        _bar.Dock = DockStyle.Fill;
        _bar.Margin = new Padding(0, Px(4), pad, Px(4));
        _bar.Height = Px(12);
        _elapsed.Margin = new Padding(0, 0, pad, 0);
        _elapsed.Anchor = AnchorStyles.Left;
        _rate.Anchor = AnchorStyles.Left;
        _strip.Dock = DockStyle.Top;
        _strip.AutoSize = true;
        _strip.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _strip.ColumnCount = 3;
        _strip.RowCount = 1;
        _strip.Padding = new Padding(pad, 0, pad, pad / 2);
        _strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _strip.Controls.Add(_bar, 0, 0);
        _strip.Controls.Add(_elapsed, 1, 0);
        _strip.Controls.Add(_rate, 2, 0);

        Compose([_kindLabel, _kinds, _durationLabel, _duration, StartButton, StopButton], [], _strip);
        FillDurations(DurationSeconds[0]);
        ShowKind();
        ApplyLanguage();
    }

    internal ToggleButton CpuButton => _cpu;

    internal ToggleButton MemoryButton => _memory;

    internal ComboBox Duration => _duration;

    internal Control Strip => _strip;

    /// <summary>True while a load is going and the progress strip is on show (what the tab decided; a hidden window's Visible says nothing).</summary>
    internal bool ProgressShown { get; private set; }

    internal CheckProgressBar Bar => _bar;

    internal Label ElapsedLabel => _elapsed;

    internal ToneLabel RateLabel => _rate;

    /// <summary>A toggle, pressed (a test presses it without a window that could take the click). Refused while a check runs, like the toggle itself.</summary>
    internal void PressKind(CheckKind kind) => SelectKind(kind);

    /// <summary>The kind the toggles are on now (CPU or memory).</summary>
    internal CheckKind SelectedKind => _kind;

    /// <summary>The seconds the selector is on now.</summary>
    internal int Seconds => _duration.SelectedIndex >= 0 ? DurationSeconds[_duration.SelectedIndex] : DurationSeconds[0];

    protected override CheckKind CurrentKind => _kind;

    protected override string StartKey => "gui.chk.load.start";

    protected override CheckRequest? BuildRequest() => new(_kind, TimeSpan.FromSeconds(Seconds));

    protected override void LanguageApplied()
    {
        _kindLabel.Text = Strings.Get("gui.chk.load.kind");
        _durationLabel.Text = Strings.Get("gui.chk.load.duration");
        _cpu.Text = CheckFormat.KindName(CheckKind.Cpu);
        _memory.Text = CheckFormat.KindName(CheckKind.Memory);
        _duration.AccessibleName = Strings.Get("gui.chk.load.duration");
        _kinds.AccessibleName = Strings.Get("gui.chk.load.kind");
        _bar.AccessibleName = Strings.Get("gui.chk.load.duration");
        FillDurations(Seconds);
    }

    protected override void StateRefreshed(CheckKindState state)
    {
        var active = Controller.Active;
        var busy = active is not null;
        var loading = active is { } running && running is CheckKind.Cpu or CheckKind.Memory;
        if (active is { } load && load is CheckKind.Cpu or CheckKind.Memory && load != _kind)
        {
            // The load that is going is the one the tab shows, whatever the toggles said before.
            _kind = load;
            ShowKind();
        }

        _cpu.Enabled = !busy;
        _memory.Enabled = !busy;
        _duration.Enabled = !busy;

        ProgressShown = loading;
        _strip.Visible = loading;
        if (loading)
        {
            var progress = Controller.Progress;
            _bar.SetFraction(CheckFormat.Fraction(progress));
            _elapsed.Text = progress is null ? Strings.Get("gui.chk.load.stage.collecting") : CheckFormat.ProgressText(progress);
            _rate.Text = CheckFormat.RateText(progress);
        }
        else
        {
            _bar.SetFraction(null);
            _elapsed.Text = string.Empty;
            _rate.Text = string.Empty;
        }
    }

    private void SelectKind(CheckKind kind)
    {
        if (Controller.IsBusy || _kind == kind)
        {
            ShowKind();
            return;
        }

        _kind = kind;
        ShowKind();
        RefreshState();
    }

    private void ShowKind()
    {
        _cpu.Checked = _kind == CheckKind.Cpu;
        _memory.Checked = _kind == CheckKind.Memory;
    }

    private void FillDurations(int select)
    {
        _duration.BeginUpdate();
        _duration.Items.Clear();
        foreach (var seconds in DurationSeconds)
        {
            _duration.Items.Add(Strings.Get("gui.chk.load.dur." + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var index = Array.IndexOf(DurationSeconds, select);
        _duration.SelectedIndex = index >= 0 ? index : 0;
        _duration.EndUpdate();
    }
}
