using System.Runtime.InteropServices;

namespace PcMieru.Wire;

internal static unsafe partial class Native
{
    internal const uint WM_CLOSE = 0x0010;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const uint SMTO_NOTIMEOUTIFNOTHUNG = 0x0008;
    internal const uint GW_OWNER = 4;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static partial nint SendMessageTimeout(
        nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeoutMs, out nuint result);
}
