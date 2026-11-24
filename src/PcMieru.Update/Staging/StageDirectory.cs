using Microsoft.Win32.SafeHandles;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Staging;

/// <summary>
/// Opens a stage directory with its ancestors at and below L pinned against rename and deletion. The parent of L may be reached through junctions or short names.
/// Each opened directory must resolve to the expected child of that parent's final path, and every component starting at L must be an ordinary directory.
/// </summary>
internal sealed class StageDirectory : IDisposable
{
    private readonly List<SafeFileHandle> _held = [];

    public string FullName { get; private set; } = string.Empty;

    public static StageDirectory? Open(string localFolder, string directory)
    {
        var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localFolder));
        var parent = Path.GetDirectoryName(local);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (parent is null || !(full.Equals(local, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var opened = new StageDirectory();
        var complete = false;
        try
        {
            if (!opened.Add(parent, expected: null))
            {
                return null;
            }

            var expected = opened.FullName;
            foreach (var component in Path.GetRelativePath(parent, full).Split(Path.DirectorySeparatorChar))
            {
                expected = Path.Combine(expected, component);
                if (!opened.Add(expected, expected))
                {
                    return null;
                }
            }

            using var spelled = TxNative.OpenStageDirectory(full, allowLink: false);
            if (spelled is null || !string.Equals(TxNative.FinalPath(spelled), opened.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            complete = true;
            return opened;
        }
        finally
        {
            if (!complete)
            {
                opened.Dispose();
            }
        }
    }

    private bool Add(string path, string? expected)
    {
        if (TxNative.OpenStageDirectory(path, allowLink: expected is null) is not { } handle)
        {
            return false;
        }

        _held.Add(handle);
        if (TxNative.FinalPath(handle) is not { } final || (expected is not null && !final.Equals(expected, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        FullName = final;
        return true;
    }

    public void Dispose()
    {
        foreach (var handle in _held)
        {
            handle.Dispose();
        }
    }
}
