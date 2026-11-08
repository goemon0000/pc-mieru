using PcMieru.Core.Hardware.Model;

namespace PcMieru.Gui.Tests;

/// <summary>WM_DEVICECHANGE makes the hardware provider forget its cache (B.md 6), for the three "device list changed" notifications only.</summary>
public class DeviceChangeTests
{
    private sealed class CountingHardware : IHardwareProvider
    {
        public int Invalidations;

        public Task<HardwareInfo> GetAsync(HardwareCategory category, CancellationToken ct = default) => throw new InvalidOperationException("a plug event must not read hardware");

        public void Invalidate() => Invalidations++;
    }

    private static Message Device(int message, long wParam) => Message.Create(0, message, (nint)wParam, 0);

    [Theory]
    [InlineData(0x0007)] // DBT_DEVNODES_CHANGED
    [InlineData(0x8000)] // DBT_DEVICEARRIVAL
    [InlineData(0x8004)] // DBT_DEVICEREMOVECOMPLETE
    public void A_device_list_change_drops_the_cache_and_reads_nothing(int wParam)
    {
        var hardware = new CountingHardware();

        var handled = DeviceChange.Handle(Device(DeviceChange.WmDeviceChange, wParam), hardware);

        Assert.True(handled);
        Assert.Equal(1, hardware.Invalidations);
    }

    [Theory]
    [InlineData(0x0017)] // DBT_QUERYCHANGECONFIG
    [InlineData(0x0018)] // DBT_CONFIGCHANGED
    [InlineData(0x8001)] // DBT_DEVICEQUERYREMOVE
    [InlineData(0x8003)] // DBT_DEVICEREMOVEPENDING
    [InlineData(0x0000)]
    public void Other_device_notifications_leave_the_cache_alone(int wParam)
    {
        var hardware = new CountingHardware();

        Assert.False(DeviceChange.Handle(Device(DeviceChange.WmDeviceChange, wParam), hardware));
        Assert.Equal(0, hardware.Invalidations);
    }

    [Fact]
    public void Another_window_message_with_the_same_number_in_wParam_is_not_a_device_change()
    {
        var hardware = new CountingHardware();

        Assert.False(DeviceChange.Handle(Device(0x0113 /* WM_TIMER */, 0x0007), hardware));
        Assert.Equal(0, hardware.Invalidations);
    }

    [Fact]
    public void The_main_form_routes_the_message_through_it()
    {
        var source = File.ReadAllText(Path.Combine(Harness.GuiSource, "Shell", "MainForm.cs"));
        Assert.Contains("WndProc(ref Message m)", source, StringComparison.Ordinal);
        Assert.Contains("DeviceChange.Handle(m, _context.Services.Hardware)", source, StringComparison.Ordinal);
    }
}
