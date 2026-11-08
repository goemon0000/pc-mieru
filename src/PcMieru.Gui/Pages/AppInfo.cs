namespace PcMieru.Gui;

/// <summary>The version shown in Settings and written into the smoke result.</summary>
internal static class AppInfo
{
    public static string Version { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
