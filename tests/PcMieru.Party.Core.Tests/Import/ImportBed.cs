using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>A probe that says what the test tells it to, and counts how often it was asked.</summary>
internal sealed class FakeProbe(LegacyRunningState state = LegacyRunningState.NotRunning) : ILegacyRunningProbe
{
    public LegacyRunningState State { get; set; } = state;

    public int Calls { get; private set; }

    public LegacyRunningState Check()
    {
        Calls++;
        return State;
    }
}

/// <summary>
/// One test's world: a temp folder that stands for %APPDATA% (never the real one), the engine on it with a fake probe and a lock of its own.
/// Every source the test puts in is remembered with its bytes, its time and the listing of its folder; on dispose the bed checks that the
/// engine left all of it as it was (it never writes, moves or deletes the old save, and leaves nothing beside it), then removes the folder.
/// A test that changes a source itself (the old program rewriting it) says so with the Rewrite/Replace methods, which update what is expected.
/// </summary>
internal sealed class ImportBed : IDisposable
{
    private readonly Dictionary<string, (byte[] Bytes, DateTime Written, string[] Listing)> _sources = new(StringComparer.OrdinalIgnoreCase);

    public ImportBed()
    {
        Root = Path.Combine(Path.GetTempPath(), "party-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new ImportPaths(Path.Combine(Root, "Roaming"));
        Probe = new FakeProbe();
        LockName = @"Local\PCMieru.Party.Import.test." + Guid.NewGuid().ToString("N");
        Engine = new ImportEngine(Paths, Probe, LockName, TimeSpan.FromMilliseconds(500));
    }

    public string Root { get; }

    public ImportPaths Paths { get; }

    public FakeProbe Probe { get; }

    public string LockName { get; }

    public ImportEngine Engine { get; }

    public string DestinationDir => Paths.DestinationDir;

    public string Destination => Paths.Destination;

    public string SourcePath(ImportOrigin origin) => Paths.Candidates.Single(c => c.Origin == origin).Path;

    /// <summary>Puts a source where the old product keeps its save.</summary>
    public string WriteSource(ImportOrigin origin, byte[] bytes)
    {
        string path = SourcePath(origin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        Remember(path);
        return path;
    }

    /// <summary>The old program rewrites the file in place: the same size or not, the same file.</summary>
    public void RewriteInPlace(ImportOrigin origin, byte[] bytes)
    {
        string path = SourcePath(origin);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.SetLength(0);
            fs.Write(bytes);
        }

        Remember(path);
    }

    /// <summary>The old program's way (DESIGN-C 4.1 item 4): a new file next to it, then a rename over the old one.</summary>
    public void ReplaceByRename(ImportOrigin origin, byte[] bytes)
    {
        string path = SourcePath(origin);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        Remember(path);
    }

    public void DeleteSource(ImportOrigin origin)
    {
        string path = SourcePath(origin);
        File.Delete(path);
        _sources.Remove(path);
    }

    private void Remember(string path) =>
        _sources[path] = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path), Listing(Path.GetDirectoryName(path)!));

    private static string[] Listing(string dir) =>
        Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    /// <summary>The names in the destination folder (empty when the folder is not there).</summary>
    public string[] DestinationFiles() => Directory.Exists(DestinationDir) ? Listing(DestinationDir) : [];

    public byte[] DestinationBytes() => File.ReadAllBytes(Destination);

    public void WriteDestination(byte[] bytes)
    {
        Directory.CreateDirectory(DestinationDir);
        File.WriteAllBytes(Destination, bytes);
    }

    public void Dispose()
    {
        try
        {
            foreach (var (path, expected) in _sources)
            {
                if (!File.Exists(path)) Assert.Fail($"the old save was removed: {path}");
                Assert.Equal(expected.Bytes, File.ReadAllBytes(path));
                Assert.Equal(expected.Written, File.GetLastWriteTimeUtc(path));
                Assert.Equal(expected.Listing, Listing(Path.GetDirectoryName(path)!));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
