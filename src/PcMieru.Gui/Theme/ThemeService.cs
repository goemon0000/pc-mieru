using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PcMieru.Gui;

internal enum ThemeMode
{
    System,
    Light,
    Dark,
}

internal sealed record Palette(
    Color Window,
    Color Surface,
    Color Text,
    Color TextDim,
    Color Accent,
    Color Border,
    Color Selection,
    Color SelectionText);

/// <summary>
/// Light/dark plumbing. Pages never hard-code a colour: they take it from <see cref="Palette"/> and are re-applied
/// through <see cref="Apply"/> whenever <see cref="Changed"/> fires (user choice or, in System mode, the Windows setting).
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private static readonly Palette LightPalette = new(
        Window: Color.FromArgb(243, 243, 243),
        Surface: Color.White,
        Text: Color.FromArgb(28, 28, 28),
        TextDim: Color.FromArgb(96, 96, 96),
        Accent: Color.FromArgb(0, 95, 184),
        Border: Color.FromArgb(214, 214, 214),
        Selection: Color.FromArgb(204, 228, 247),
        SelectionText: Color.FromArgb(0, 0, 0));

    private static readonly Palette DarkPalette = new(
        Window: Color.FromArgb(32, 32, 32),
        Surface: Color.FromArgb(43, 43, 43),
        Text: Color.FromArgb(240, 240, 240),
        TextDim: Color.FromArgb(166, 166, 166),
        Accent: Color.FromArgb(96, 165, 255),
        Border: Color.FromArgb(70, 70, 70),
        Selection: Color.FromArgb(0, 80, 140),
        SelectionText: Color.FromArgb(255, 255, 255));

    private readonly Func<bool> _systemIsDark;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _hooked = new();
    private ThemeMode _mode;

    public ThemeService(ThemeMode mode = ThemeMode.System, Func<bool>? systemIsDark = null)
    {
        _mode = mode;
        _systemIsDark = systemIsDark ?? ReadSystemIsDark;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event Action? Changed;

    public ThemeMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            Changed?.Invoke();
        }
    }

    public bool IsDark => _mode == ThemeMode.Dark || (_mode == ThemeMode.System && _systemIsDark());

    public Palette Palette => IsDark ? DarkPalette : LightPalette;

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>Paints a control tree. Safe to call again after every change; handles that do not exist yet are skipped.</summary>
    public void Apply(Control root)
    {
        var palette = Palette;
        ApplyTo(root, palette, topLevel: true);
        if (root is Form form && form.IsHandleCreated)
        {
            NativeTheme.SetDarkTitleBar(form.Handle, IsDark);
        }
    }

    private void ApplyTo(Control control, Palette palette, bool topLevel)
    {
        switch (control)
        {
            case Form or UserControl:
                control.BackColor = palette.Window;
                control.ForeColor = palette.Text;
                break;
            case ListView or TreeView or TextBoxBase or ListBox:
                control.BackColor = palette.Surface;
                control.ForeColor = palette.Text;
                if (control is TextBoxBase { } box)
                {
                    box.BorderStyle = BorderStyle.FixedSingle;
                }

                if (control is ListView or TreeView)
                {
                    ApplyNativeTheme(control);
                }

                break;
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = palette.Border;
                button.BackColor = palette.Surface;
                button.ForeColor = palette.Text;
                break;
            case NumericUpDown or ComboBox:
                control.BackColor = palette.Surface;
                control.ForeColor = palette.Text;
                if (control is ComboBox)
                {
                    ApplyNativeTheme(control, combo: true);
                }

                break;
            case SplitContainer split:
                // The splitter bar takes the container's colour; the two panels get the window colour back.
                split.BackColor = palette.Border;
                split.ForeColor = palette.Text;
                split.Panel1.BackColor = palette.Window;
                split.Panel2.BackColor = palette.Window;
                split.Panel1.ForeColor = palette.Text;
                split.Panel2.ForeColor = palette.Text;
                break;
            default:
                // Reading BackColor and writing it back would freeze the ambient colour of the first theme that was applied.
                if (topLevel)
                {
                    control.BackColor = palette.Window;
                }
                else
                {
                    control.ResetBackColor();
                }

                control.ForeColor = palette.Text;
                break;
        }

        if (control is IThemed themed)
        {
            themed.ApplyTheme(palette);
        }

        foreach (Control child in control.Controls)
        {
            if (child is SplitterPanel)
            {
                // Painted by the SplitContainer case above; the children of the panel are still ours to paint.
                foreach (Control inner in child.Controls)
                {
                    ApplyTo(inner, palette, topLevel: false);
                }

                continue;
            }

            ApplyTo(child, palette, topLevel: false);
        }
    }

    /// <summary>Colours a context menu or any ToolStrip: a renderer that reads the palette, so dark mode is not half native.</summary>
    public void StyleMenu(ToolStrip strip)
    {
        var palette = Palette;
        strip.Renderer = new ToolStripProfessionalRenderer(new PaletteColors(palette)) { RoundedEdges = false };
        strip.BackColor = palette.Surface;
        strip.ForeColor = palette.Text;
        foreach (ToolStripItem item in strip.Items)
        {
            item.ForeColor = item.Enabled ? palette.Text : palette.TextDim;
            item.BackColor = palette.Surface;
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                StyleMenu(parent.DropDown);
            }
        }
    }

    /// <summary>Native theming needs a window handle; a page that has never been shown gets it when the handle appears.</summary>
    private void ApplyNativeTheme(Control control, bool combo = false)
    {
        if (control.IsHandleCreated)
        {
            NativeTheme.SetControlTheme(control.Handle, IsDark, combo);
            return;
        }

        if (_hooked.TryGetValue(control, out _))
        {
            return;
        }

        _hooked.Add(control, this);
        control.HandleCreated += (_, _) => NativeTheme.SetControlTheme(control.Handle, IsDark, combo);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
        {
            Changed?.Invoke();
        }
    }

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A control that paints something ThemeService cannot reach through BackColor/ForeColor (owner-drawn parts).</summary>
internal interface IThemed
{
    void ApplyTheme(Palette palette);
}

internal static class NativeTheme
{
    private const int DwmUseImmersiveDarkMode = 20;

    public static void SetDarkTitleBar(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    public static void SetControlTheme(IntPtr handle, bool dark, bool combo = false) =>
        _ = SetWindowTheme(handle, dark ? (combo ? "DarkMode_CFD" : "DarkMode_Explorer") : (combo ? "CFD" : "Explorer"), null);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
}

/// <summary>Menu colours taken from the palette (a ToolStrip does not follow DarkMode_Explorer).</summary>
internal sealed class PaletteColors : ProfessionalColorTable
{
    private readonly Palette _p;

    public PaletteColors(Palette palette)
    {
        _p = palette;
        UseSystemColors = false;
    }

    public override Color ToolStripDropDownBackground => _p.Surface;

    public override Color MenuBorder => _p.Border;

    public override Color MenuItemBorder => _p.Selection;

    public override Color MenuItemSelected => _p.Selection;

    public override Color MenuItemSelectedGradientBegin => _p.Selection;

    public override Color MenuItemSelectedGradientEnd => _p.Selection;

    public override Color ImageMarginGradientBegin => _p.Surface;

    public override Color ImageMarginGradientMiddle => _p.Surface;

    public override Color ImageMarginGradientEnd => _p.Surface;

    public override Color SeparatorDark => _p.Border;

    public override Color SeparatorLight => _p.Border;

    public override Color CheckBackground => _p.Selection;

    public override Color CheckSelectedBackground => _p.Selection;

    public override Color CheckPressedBackground => _p.Selection;
}
