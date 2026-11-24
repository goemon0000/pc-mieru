using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Gui;

/// <summary>
/// The real place a path leads to, for the one question the measured run asks of a folder it was told to write into: is it inside one of the user's own
/// folders? Comparing the words of two paths answers it wrongly whenever the same folder has two spellings: an 8.3 short name
/// (<c>C:\Users\ADMINI~1\AppData\Roaming</c> is <c>C:\Users\Administrator\AppData\Roaming</c>), a junction or a symbolic link (a folder under the scratch
/// directory that is a link into Documents), a redirected Documents (<c>E:\MovedFromC\Documents</c> for <c>C:\Users\Administrator\Documents</c>).
/// The path is put in its one spelling the way Windows does it: the nearest part that exists is opened and asked for its final path
/// (<c>GetFinalPathNameByHandle</c>, which follows every link and expands every short name), and the part that does not exist yet is appended as written.
/// Nothing is created and nothing is written.
/// </summary>
internal static class FolderGuard
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const string LongPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// The path in its real spelling (<paramref name="resolved"/>), true; or false when it cannot be known: a part of it is a link that cannot be followed
    /// (a junction whose target is gone, a link that may not be opened), so no statement about where it leads can be made and the caller must refuse it.
    /// A path nothing of which exists (a drive that is not there) is its own spelling.
    /// </summary>
    public static bool TryResolve(string path, out string resolved)
    {
        resolved = path;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        full = Path.TrimEndingDirectorySeparator(full);
        var tail = new Stack<string>();
        var existing = full;
        while (true)
        {
            if (TryAttributes(existing, out var attributes))
            {
                if (!TryFinalPath(existing, attributes, out var final))
                {
                    return false;
                }

                resolved = tail.Aggregate(final, Path.Combine);
                return true;
            }

            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent))
            {
                // Not even the drive is there: no link can be on the way.
                resolved = full;
                return true;
            }

            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }
    }

    /// <summary>The spellings of a path to compare: the one given (made absolute) and, when it is another, the real one. Empty for a blank path.</summary>
    public static IReadOnlyList<string> Spellings(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        return TryResolve(full, out var real) && !real.Equals(full, StringComparison.OrdinalIgnoreCase) ? [full, real] : [full];
    }

    /// <summary>
    /// Whether the path is a plain local one: a drive letter, a colon and a backslash, and no other colon anywhere after them. Nothing else is a place the guard can
    /// speak for: a device path (<c>\\.\C:\...</c>), a long-path form, a network path, an administrative share of this very machine (<c>\\localhost\C$\...</c>,
    /// <c>\\127.0.0.1\E$\...</c>), a mapped drive that resolves to one of those, and a stream spelling (<c>Documents::$INDEX_ALLOCATION\x</c>) each lead into the
    /// same folders as a letter does but are spelled so that no list of folders matches them. Asked of the path as it was written and of the real one.
    /// </summary>
    public static bool IsPlainLocalPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && path.IndexOf(':', 2) < 0;

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or under it, in any spelling of either.</summary>
    public static bool IsInside(string root, string path)
    {
        var parents = Spellings(root);
        if (parents.Count == 0)
        {
            return false;
        }

        foreach (var child in Spellings(path))
        {
            foreach (var parent in parents)
            {
                if (child.Equals(parent, StringComparison.OrdinalIgnoreCase)
                    || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            // The attributes of the entry itself: a link that leads nowhere is still found here (Directory.Exists says it is not there).
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool TryFinalPath(string existing, FileAttributes attributes, out string final)
    {
        var name = existing.StartsWith(LongPrefix, StringComparison.Ordinal) ? existing
            : existing.StartsWith(@"\\", StringComparison.Ordinal) ? LongUncPrefix + existing[2..]
            : LongPrefix + existing;
        using var handle = CreateFile(name, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid && FinalPath(handle) is { } real)
        {
            final = real;
            return true;
        }

        // The part could not be opened. Without a link anywhere on the way there is nothing to follow, and its own spelling is its place; with one
        // (or one that leads nowhere) the place cannot be told, so it is not guessed.
        final = existing;
        return !(attributes.HasFlag(FileAttributes.ReparsePoint) || HasLinkAbove(existing));
    }

    private static bool HasLinkAbove(string path)
    {
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (TryAttributes(parent, out var attributes) && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Capacity)
            {
                break;
            }

            buffer.EnsureCapacity((int)length + 1);
        }

        var text = buffer.ToString();
        if (text.StartsWith(LongUncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + text[LongUncPrefix.Length..];
        }

        return text.StartsWith(LongPrefix, StringComparison.Ordinal) ? text[LongPrefix.Length..] : text;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
