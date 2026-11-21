using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// The system check: it reads this PC's configuration, drives and event logs and judges each item. It puts no load on the PC and has
/// nothing to choose, so the tab is the start button, the stop button and the table.
/// </summary>
internal sealed class SystemCheckTab : RunCheckTab
{
    public SystemCheckTab(CheckController controller, Func<int, int> px)
        : base(controller, px, "checksSystem", "gui.chk.sys.intro")
    {
        StartButton.TabIndex = 0;
        StopButton.TabIndex = 1;
        Grid.TabIndex = 2;
        Compose([StartButton, StopButton], []);
        ApplyLanguage();
    }

    protected override CheckKind CurrentKind => CheckKind.System;

    protected override CheckRequest? BuildRequest() => new(CheckKind.System);
}
