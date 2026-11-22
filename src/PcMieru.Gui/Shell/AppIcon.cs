namespace PcMieru.Gui;

/// <summary>The app's own icon (assets/PCMieru.ico, also the exe's icon), for the window's title bar and taskbar button.</summary>
internal static class AppIcon
{
    /// <summary>The icon with all its sizes, so Windows picks the 16 px one for the title bar and a larger one for Alt+Tab.</summary>
    public static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("PCMieru.ico");
        return stream is null ? null : new Icon(stream);
    }
}
