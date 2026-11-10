namespace PcMieru.Gui;

/// <summary>What the "Measure network" button shows for one state of the meter: its text, its tip, whether it is there at all, whether it can be pressed, whether it carries the UAC shield.</summary>
internal readonly record struct NetMeterButton(string TextKey, string TipKey, bool Visible, bool Enabled, bool Shield);

/// <summary>
/// The words and states the network meter needs on screen, as plain functions of the meter's state, so the button and the
/// tips of a missing value are tested without a window. The meter itself is only asked for its state.
/// </summary>
internal static class NetMeterUi
{
    /// <summary>
    /// Off, Declined and Failed offer to start (again); the shield says a prompt follows. Starting is greyed out while the
    /// prompt is up. Running offers to stop. Unsupported hides it: there is nothing to press, and the dash of the Network column says why.
    /// </summary>
    public static NetMeterButton Button(NetMeterStatus status) => status switch
    {
        NetMeterStatus.Starting => new("gui.proc.net.starting", "gui.proc.net.starting.tip", Visible: true, Enabled: false, Shield: false),
        NetMeterStatus.Running => new("gui.proc.net.stop", "gui.proc.net.stop.tip", Visible: true, Enabled: true, Shield: false),
        NetMeterStatus.Unsupported => new("gui.proc.net.start", "gui.proc.net.tip.unsupported", Visible: false, Enabled: false, Shield: true),
        NetMeterStatus.Declined => new("gui.proc.net.start", "gui.proc.net.tip.declined", Visible: true, Enabled: true, Shield: true),
        NetMeterStatus.Failed => new("gui.proc.net.start", "gui.proc.net.tip.failed", Visible: true, Enabled: true, Shield: true),
        _ => new("gui.proc.net.start", "gui.proc.net.start.tip", Visible: true, Enabled: true, Shield: true),
    };

    /// <summary>The tip of the button. When the meter gave a reason for a failure, it follows the fixed words.</summary>
    public static string ButtonTip(NetMeterState state)
    {
        var tip = Strings.Get(Button(state.Status).TipKey);
        return state.Status == NetMeterStatus.Failed ? WithDetail(tip, state.Detail) : tip;
    }

    /// <summary>
    /// Why a network cell is a dash. While the meter is not running the reason is the meter's state, in words that say what to
    /// do; when it runs and one process still has no value, the ordinary reason of the value applies.
    /// </summary>
    public static string MissingTip(NetMeterState state, Value<double> value) => state.Status switch
    {
        NetMeterStatus.Off => Strings.Get("gui.proc.net.tip.off"),
        NetMeterStatus.Starting => Strings.Get("gui.proc.net.tip.starting"),
        NetMeterStatus.Declined => Strings.Get("gui.proc.net.tip.declined"),
        NetMeterStatus.Failed => WithDetail(Strings.Get("gui.proc.net.tip.failed"), state.Detail),
        NetMeterStatus.Unsupported => WithDetail(Strings.Get("gui.proc.net.tip.unsupported"), state.Detail),
        _ => Fmt.MissingTip(value),
    };

    private static string WithDetail(string text, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? text : text + Environment.NewLine + detail;
}
