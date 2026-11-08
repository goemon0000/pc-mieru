namespace PcMieru.Gui;

/// <summary>
/// WM_DEVICECHANGE: the list of devices changed (a USB device plugged or removed, a disk, a monitor). The hardware provider
/// keeps what it collected, so it is told to forget it; the next read of the hardware page then collects again.
/// Only the cache is dropped here. Nothing is read, and no helper process is started, at a plug event.
/// </summary>
internal static class DeviceChange
{
    public const int WmDeviceChange = 0x0219;

    private const long DevNodesChanged = 0x0007;
    private const long DeviceArrival = 0x8000;
    private const long DeviceRemoveComplete = 0x8004;

    /// <summary>True for the three notifications that mean "the set of devices is different now" (not a query, not a config change).</summary>
    public static bool IsListChange(int message, nint wParam) =>
        message == WmDeviceChange && (long)wParam is DevNodesChanged or DeviceArrival or DeviceRemoveComplete;

    /// <summary>Forgets the cached hardware facts when <paramref name="message"/> reports a device list change. Returns whether it did.</summary>
    public static bool Handle(in Message message, IHardwareProvider hardware)
    {
        if (!IsListChange(message.Msg, message.WParam))
        {
            return false;
        }

        hardware.Invalidate();
        return true;
    }
}
