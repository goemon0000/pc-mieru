using System.Runtime.InteropServices;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The history file system (DESIGN-A §4: <see cref="ICheckHistoryFileSystem"/>) plus the one thing a baseline needs on top of it:
/// replacing a file that is already there. A baseline file is rewritten in place of its old version (temp file, flush, replace),
/// whereas the history never replaces anything. Reading the old v0.7.4 folder goes through <see cref="ICheckHistoryFileReader"/> only.
/// </summary>
internal interface IBaselineFileSystem : ICheckHistoryFileSystem
{
    /// <summary>
    /// Renames within the same volume. An existing destination is replaced in one step (a reader sees the old file or the new one, never
    /// a half), and the call does not return before the rename is on the disk (write-through).
    /// </summary>
    void Replace(string source, string destination);
}

/// <summary>The real file system for the baseline. Everything but <see cref="Replace"/> is the history's own implementation.</summary>
internal sealed class PhysicalBaselineFileSystem : IBaselineFileSystem
{
    // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH.
    private const uint ReplaceExistingWriteThrough = 0x1 | 0x8;

    private readonly ICheckHistoryFileSystem _inner = PhysicalHistoryFileSystem.Instance;

    public static PhysicalBaselineFileSystem Instance { get; } = new();

    public IReadOnlyList<HistoryFileEntry> ListFiles(string directory) => _inner.ListFiles(directory);

    public byte[] ReadAllBytes(string path, int maxBytes) => _inner.ReadAllBytes(path, maxBytes);

    public bool Exists(string path) => _inner.Exists(path);

    public void CreateDirectory(string directory) => _inner.CreateDirectory(directory);

    public ICheckHistoryNewFile CreateNew(string path) => _inner.CreateNew(path);

    public void Move(string source, string destination) => _inner.Move(source, destination);

    public void Delete(string path) => _inner.Delete(path);

    public void Replace(string source, string destination)
    {
        if (!HistoryNative.MoveFileEx(Path.GetFullPath(source), Path.GetFullPath(destination), ReplaceExistingWriteThrough))
        {
            throw HistoryNative.ToException(Marshal.GetLastPInvokeError());
        }
    }
}
