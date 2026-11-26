using System.Text;

namespace PcMieru.Core.Siblings;

/// <summary>
/// Where the journal of a switch is kept: one small text. The real one is a file (<see cref="FileRunSwitchJournal"/>); a test puts a fault in front of it. Every method may throw (an
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>): <see cref="RunSwitch"/> turns that into a result.
/// </summary>
public interface IRunSwitchJournal
{
    bool Exists();

    /// <summary>The whole text. Throws when the journal is not there.</summary>
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

    public bool Exists() => File.Exists(Path);

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

            File.Move(temp, Path, overwrite: true);
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
