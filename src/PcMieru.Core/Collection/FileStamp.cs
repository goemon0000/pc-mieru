namespace PcMieru.Core.Collection;

/// <summary>
/// Which version of which file: path, size and last write time. Caches of facts about a file (signature, icon) are keyed
/// by this, so a file that is replaced while the app runs is looked at again (a PID alone says nothing about the file).
/// </summary>
internal readonly record struct FileStamp(string Path, long Length, long WriteTicks)
{
    /// <summary>Null when the file is missing or cannot be examined; <paramref name="problem"/> then says why.</summary>
    public static FileStamp? TryRead(string path, out string? problem)
    {
        problem = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                problem = "the image file does not exist (deleted or replaced after the process started)";
                return null;
            }

            return new FileStamp(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"the image file could not be examined: {ex.GetType().Name}";
            return null;
        }
    }
}
