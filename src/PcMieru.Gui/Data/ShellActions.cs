using System.Diagnostics;

namespace PcMieru.Gui;

/// <summary>
/// The few things that hand something to Windows (open Explorer on a file, open the browser, show file properties).
/// One place so tests and the smoke run can replace the launcher and never open a real window.
/// </summary>
internal static class ShellActions
{
    /// <summary>Replaced in tests. The default starts the shell for real.</summary>
    internal static Action<ProcessStartInfo> Start { get; set; } = static info => Process.Start(info)?.Dispose();

    public static void OpenFileLocation(string path) =>
        Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = true });

    public static void ShowProperties(string path) =>
        Start(new ProcessStartInfo(path) { Verb = "properties", UseShellExecute = true });

    /// <summary>Search in the default browser. Only the process name is passed on, nothing else about this PC.</summary>
    public static void SearchWeb(string processName) =>
        Start(new ProcessStartInfo("https://www.bing.com/search?q=" + Uri.EscapeDataString(processName)) { UseShellExecute = true });
}
