namespace PcMieru.Gui;

/// <summary>
/// One side of a small either/or switch ("CPU order" / "memory order", "Preview" / "Markdown"): a flat button that
/// stays pressed while it is the chosen one. The chosen side takes the selection colours, and an accent border, so it reads as
/// chosen in both themes. Screen readers hear a radio button that is checked or not.
/// </summary>
internal sealed class ToggleButton : Button, IThemed
{
    private Palette? _palette;
    private bool _checked;

    public ToggleButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        UseMnemonic = false;
        AccessibleRole = AccessibleRole.RadioButton;
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            Recolor();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Recolor();
    }

    /// <summary>The background this button must have in a palette (the smoke run's colour check can ask).</summary>
    public Color ExpectedBack(Palette palette) => _checked ? palette.Selection : palette.Surface;

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessible(this);

    private void Recolor()
    {
        if (_palette is not { } palette)
        {
            return;
        }

        BackColor = _checked ? palette.Selection : palette.Surface;
        ForeColor = _checked ? palette.SelectionText : palette.Text;
        FlatAppearance.BorderColor = _checked ? palette.Accent : palette.Border;
        FlatAppearance.MouseOverBackColor = _checked ? palette.Selection : palette.Border;
    }

    private sealed class ToggleAccessible(ToggleButton owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => owner._checked ? base.State | AccessibleStates.Checked : base.State;

        public override string DefaultAction => "Select";

        public override void DoDefaultAction() => owner.PerformClick();
    }
}
