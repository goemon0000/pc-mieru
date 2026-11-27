using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PcMieru.Core.Siblings;

/// <summary>
/// Where the journal of a switch is kept: one small text. The real one is a file (<see cref="FileRunSwitchJournal"/>); a test puts a fault in front of it. Every method may throw (an
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>): <see cref="RunSwitch"/> turns that into a result.
/// </summary>
public interface IRunSwitchJournal
{
    /// <summary>
    /// The whole text. Throws <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/> when the journal is not there, and ONLY then: any other exception (access denied, a
    /// sharing violation, a disk error) means "could not tell", never "no journal". There is deliberately no <c>Exists()</c>: a file that exists but cannot be opened looks absent to it.
    /// </summary>
    string Read();

    /// <summary>Replaces the whole text in one step: a reader sees the old text or the new text, never a half.</summary>
    void Write(string text);

    /// <summary>Removes the journal; no error when it is not there.</summary>
    void Delete();
}

/// <summary>
/// The journal as a file: written to a temp file next to it (flushed to the disk), then renamed over it. Nothing but the journal path and its <c>.tmp</c> are touched. The folder is created when it
/// is missing (a switch only runs after the save was brought over, so it exists then).
/// </summary>
public sealed class FileRunSwitchJournal(string path) : IRunSwitchJournal
{
    public string Path { get; } = path;

    public string Read() => File.ReadAllText(Path, Encoding.UTF8);

    public void Write(string text)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = Path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(flushToDisk: true);
            }

            Publish(temp, Path);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // the failure that matters is the one being thrown
            }

            throw;
        }
    }

    /// <summary>Renames the flushed temp file over the journal and does not return before the rename is on the disk (MOVEFILE_WRITE_THROUGH), so a power loss cannot undo the journal that the first registry write relies on.</summary>
    private static void Publish(string temp, string target)
    {
        if (!NativeFile.MoveFileExW(temp, target, NativeFile.ReplaceExisting | NativeFile.WriteThrough))
        {
            var error = Marshal.GetLastWin32Error();
            throw error == 5 ? new UnauthorizedAccessException(new Win32Exception(error).Message) : new IOException(new Win32Exception(error).Message, error);
        }
    }

    public void Delete()
    {
        File.Delete(Path);
        try
        {
            File.Delete(Path + ".tmp");
        }
        catch (Exception)
        {
            // a stray temp file is not the journal
        }
    }
}

internal static partial class NativeFile
{
    internal const uint ReplaceExisting = 0x1;
    internal const uint WriteThrough = 0x8;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}
