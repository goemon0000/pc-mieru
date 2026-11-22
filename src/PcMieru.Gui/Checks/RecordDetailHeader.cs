namespace PcMieru.Gui;

/// <summary>
/// The line above the summary: what the selected recording is (when it started, how it ended, a word about it) on the left, wrapping when it is long,
/// and the "open CSV" and "delete" buttons on the right. One block instead of a line and a bar, so the summary keeps the height; when the window is too
/// narrow for both side by side the buttons go under the text. The block is as tall as its content needs and sets that itself.
/// </summary>
internal sealed class RecordDetailHeader : Panel
{
    private readonly WrapLabel _head;
    private readonly Button[] _buttons;
    private readonly int _pad;
    private readonly HashSet<Button> _off = [];
    private bool _fitting;

    public RecordDetailHeader(WrapLabel head, int pad, params Button[] buttons)
    {
        _head = head;
        _buttons = buttons;
        _pad = pad;
        Dock = DockStyle.Top;
        Name = "recordDetailHeader";
        head.Dock = DockStyle.None;
        Controls.Add(head);
        foreach (var button in buttons)
        {
            Controls.Add(button);
        }
    }

    /// <summary>True when the buttons sit under the text (the window is too narrow for them beside it).</summary>
    public bool ButtonsBelow { get; private set; }

    /// <summary>Shows or hides one of the buttons (one that only means something for some recordings). A hidden button takes no room and is not reached by Tab.</summary>
    public void SetShown(Button button, bool shown)
    {
        if (shown ? !_off.Remove(button) : !_off.Add(button))
        {
            return;
        }

        button.Visible = shown;
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }

    private void Arrange()
    {
        if (_fitting || Width <= 0)
        {
            return;
        }

        _fitting = true;
        try
        {
            var gap = _pad * 2 / 3;
            var buttonsWidth = 0;
            var buttonsHeight = 0;
            var shown = _buttons.Where(button => !_off.Contains(button)).ToArray();
            foreach (var button in shown)
            {
                button.Size = button.GetPreferredSize(Size.Empty);
                buttonsWidth += button.Width + gap;
                buttonsHeight = Math.Max(buttonsHeight, button.Height);
            }

            var top = _pad / 3;
            var room = Width - (_pad * 2);
            var minHead = Math.Max(_head.Font.Height * 12, LogicalToDeviceUnits(200));
            ButtonsBelow = room - buttonsWidth < minHead;
            var headWidth = ButtonsBelow ? room : room - buttonsWidth;
            _head.Padding = new Padding(0, LogicalToDeviceUnits(4), 0, 0);
            var headHeight = _head.HeightAt(headWidth);
            _head.SetBounds(_pad, top, headWidth, headHeight);
            _head.Visible = headHeight > 0;

            var height = top;
            if (ButtonsBelow)
            {
                var y = top + headHeight + (headHeight > 0 ? _pad / 3 : 0);
                var x = _pad;
                foreach (var button in shown)
                {
                    button.Location = new Point(x, y);
                    x += button.Width + gap;
                }

                height = y + buttonsHeight;
            }
            else
            {
                var x = Width - _pad;
                for (var i = shown.Length - 1; i >= 0; i--)
                {
                    var button = shown[i];
                    x -= button.Width;
                    button.Location = new Point(x, top);
                    x -= gap;
                }

                height = top + Math.Max(headHeight, buttonsHeight);
            }

            height += _pad / 3;
            if (Height != height)
            {
                Height = height;
            }
        }
        finally
        {
            _fitting = false;
        }
    }
}
