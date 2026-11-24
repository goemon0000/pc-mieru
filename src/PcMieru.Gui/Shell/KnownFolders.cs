using System.Runtime.InteropServices;

namespace PcMieru.Gui;

/// <summary>
/// The folders Windows keeps for the user that <see cref="Environment.SpecialFolder"/> has no name for. Downloads is the one the measured run needs: it is
/// redirected like Documents (<c>E:\MovedFromC\Downloads</c> on a machine that moved it), and only Windows knows where it went.
/// </summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder where Windows has it, or null when it cannot be told.</summary>
    public static string? Downloads()
    {
        try
        {
            var id = DownloadsId;
            var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var text);
            try
            {
                return result == 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text) : null;
            }
            finally
            {
                if (text != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(text);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
