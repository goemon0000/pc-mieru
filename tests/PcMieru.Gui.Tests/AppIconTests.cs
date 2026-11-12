namespace PcMieru.Gui.Tests;

public class AppIconTests
{
    // The window must show the app's icon, not the WinForms default one, with the sizes a title bar, taskbar and Alt+Tab use.
    [Fact]
    public void The_window_icon_is_embedded_with_the_window_sizes()
    {
        using var icon = AppIcon.Load();
        Assert.NotNull(icon);
        foreach (var size in new[] { 16, 24, 32, 48 })
        {
            using var exact = new Icon(icon, size, size);
            Assert.Equal(size, exact.Width);
        }
    }

    // Explorer shows the exe's 256 px frame (PNG-compressed, which System.Drawing.Icon skips, so read the directory itself).
    [Fact]
    public void The_icon_file_has_every_size_from_16_to_256()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Harness.Root, "assets", "PCMieru.ico"));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + (16 * i)] == 0 ? 256 : bytes[6 + (16 * i)]).ToHashSet();
        Assert.Superset(new HashSet<int> { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, sizes);
    }

    [Fact]
    public void The_main_window_uses_it()
    {
        var source = File.ReadAllText(Path.Combine(Harness.GuiSource, "Shell", "MainForm.cs"));
        Assert.Contains("Icon = AppIcon.Load()", source, StringComparison.Ordinal);
    }
}
