namespace PcMieru.Gui;

/// <summary>One entry of a <see cref="SummaryListBox"/>: a name, and the current value under it. The value changes every sample.</summary>
internal sealed class SummaryItem(string key, string title, string value)
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Value { get; set; } = value;

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => Title + ": " + Value;
}

/// <summary>
/// A list of two-line entries (name over current value) with a bar at the left of the selected one. A ListBox underneath,
/// so arrow keys, type-ahead and screen readers work. Only a changed value repaints, and only its own row.
/// </summary>
internal sealed class SummaryListBox : ListBox, IThemed
{
    private Palette? _palette;

    public SummaryListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        TabStop = true;
        UpdateItemHeight();
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? SelectedKey => (SelectedItem as SummaryItem)?.Key;

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Window;
        Invalidate();
    }

    /// <summary>True when the entries are exactly these keys in this order (nothing to rebuild).</summary>
    public bool HasKeys(IReadOnlyList<string> keys)
    {
        if (Items.Count != keys.Count)
        {
            return false;
        }

        for (var i = 0; i < keys.Count; i++)
        {
            if (((SummaryItem)Items[i]).Key != keys[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Replaces the entries and keeps the selection on the same key (or the first entry when it is gone).</summary>
    public void SetItems(IReadOnlyList<SummaryItem> items, string? select)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        var index = select is null ? -1 : items.ToList().FindIndex(i => i.Key == select);
        SelectedIndex = index >= 0 ? index : items.Count > 0 ? 0 : -1;
        EndUpdate();
    }

    public void SelectKey(string key)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (((SummaryItem)Items[i]).Key == key)
            {
                SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Sets one entry's value text. Repaints only that row, and only when the text changed.</summary>
    public void SetValue(int index, string value)
    {
        if (index < 0 || index >= Items.Count)
        {
            return;
        }

        var item = (SummaryItem)Items[index];
        if (item.Value == value)
        {
            return;
        }

        item.Value = value;
        if (IsHandleCreated)
        {
            Invalidate(GetItemRectangle(index));
        }
    }

    // The row height must not wait for a font change: a window whose font equals the default never raises one.
    private void UpdateItemHeight() => ItemHeight = (Font.Height * 2) + LogicalToDeviceUnits(20);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateItemHeight();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        UpdateItemHeight();
        base.OnHandleCreated(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateItemHeight();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var palette = _palette;
        if (e.Index < 0 || palette is null || Items[e.Index] is not SummaryItem item)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) != 0;
        var back = selected ? palette.Selection : palette.Window;
        using (var brush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + LogicalToDeviceUnits(6), LogicalToDeviceUnits(3), e.Bounds.Height - LogicalToDeviceUnits(12));
        }

        var left = e.Bounds.X + LogicalToDeviceUnits(14);
        var width = Math.Max(e.Bounds.Width - LogicalToDeviceUnits(14) - LogicalToDeviceUnits(8), 0);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var line = Font.Height + 2;
        var top = e.Bounds.Y + LogicalToDeviceUnits(9);
        TextRenderer.DrawText(e.Graphics, item.Title, Font, new Rectangle(left, top, width, line), selected ? palette.SelectionText : palette.Text, flags);
        TextRenderer.DrawText(e.Graphics, item.Value, Font, new Rectangle(left, top + line + LogicalToDeviceUnits(2), width, line), selected ? palette.SelectionText : palette.TextDim, flags);

        if ((e.State & DrawItemState.Focus) != 0 && Focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, palette.Text, back);
        }
    }
}
