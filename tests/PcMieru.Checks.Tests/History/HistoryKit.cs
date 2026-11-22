using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PcMieru.Checks.Tests;

/// <summary>A fresh empty folder under the system temp folder for one test; deleted at the end. Never the real PCMieru folder.</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcmieru-checks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A test that still holds a file open leaves its folder behind; the next run uses another name.
        }
    }
}

/// <summary>A clock a test sets by hand.</summary>
internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A file system that does the real thing and logs it, and lets a test make one call fail or do something at that moment.
/// Every hook returns the exception to throw (null: carry on).
/// </summary>
internal sealed class FaultFileSystem : ICheckHistoryFileSystem
{
    private readonly ICheckHistoryFileSystem _inner = PhysicalHistoryFileSystem.Instance;
    private readonly object _gate = new();
    private readonly List<string> _log = [];

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public Func<string, Exception?>? ListFault { get; set; }

    public Func<string, Exception?>? ReadFault { get; set; }

    public Func<string, Exception?>? CreateNewFault { get; set; }

    public Func<string, Exception?>? WriteFault { get; set; }

    public Func<string, Exception?>? FlushFault { get; set; }

    /// <summary>Called with (source, destination) just before the real rename; the exception, when given, replaces the rename.</summary>
    public Func<string, string, Exception?>? MoveFault { get; set; }

    public Func<string, Exception?>? DeleteFault { get; set; }

    public int Count(string prefix)
    {
        lock (_gate)
        {
            return _log.Count(entry => entry.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    private void Add(string entry)
    {
        lock (_gate)
        {
            _log.Add(entry);
        }
    }

    public IReadOnlyList<HistoryFileEntry> ListFiles(string directory)
    {
        Add("list");
        if (ListFault?.Invoke(directory) is { } fault)
        {
            throw fault;
        }

        return _inner.ListFiles(directory);
    }

    public byte[] ReadAllBytes(string path, int maxBytes)
    {
        Add("read " + System.IO.Path.GetFileName(path));
        if (ReadFault?.Invoke(path) is { } fault)
        {
            throw fault;
        }

        return _inner.ReadAllBytes(path, maxBytes);
    }

    public bool Exists(string path) => _inner.Exists(path);

    public void CreateDirectory(string directory)
    {
        Add("mkdir");
        _inner.CreateDirectory(directory);
    }

    public ICheckHistoryNewFile CreateNew(string path)
    {
        Add("create " + System.IO.Path.GetFileName(path));
        if (CreateNewFault?.Invoke(path) is { } fault)
        {
            throw fault;
        }

        return new LoggedFile(this, path, _inner.CreateNew(path));
    }

    public void Move(string source, string destination)
    {
        Add($"move {System.IO.Path.GetFileName(source)} -> {System.IO.Path.GetFileName(destination)}");
        if (MoveFault?.Invoke(source, destination) is { } fault)
        {
            throw fault;
        }

        _inner.Move(source, destination);
    }

    public void Delete(string path)
    {
        Add("delete " + System.IO.Path.GetFileName(path));
        if (DeleteFault?.Invoke(path) is { } fault)
        {
            throw fault;
        }

        _inner.Delete(path);
    }

    private sealed class LoggedFile(FaultFileSystem owner, string path, ICheckHistoryNewFile inner) : ICheckHistoryNewFile
    {
        public void Write(ReadOnlySpan<byte> bytes)
        {
            owner.Add("write");
            if (owner.WriteFault?.Invoke(path) is { } fault)
            {
                throw fault;
            }

            inner.Write(bytes);
        }

        public void FlushToDisk()
        {
            owner.Add("flush");
            if (owner.FlushFault?.Invoke(path) is { } fault)
            {
                throw fault;
            }

            inner.FlushToDisk();
        }

        public void Dispose()
        {
            owner.Add("close");
            inner.Dispose();
        }
    }
}

/// <summary>Helpers the history tests share. Expected names are built here with plain .NET formatting, not with the code under test.</summary>
internal static class Kit
{
    /// <summary>
    /// The fake "now" of the stores in these tests: 2026-11-21 04:05:06.789 UTC. It is fixed, while the files the tests write get the real
    /// modification time of the machine running them: before this date they look hours old to a clean-up under this clock, after it they
    /// look as if written in the future. So a test that depends on a file's age sets the file times by hand (<see cref="SetAge"/>) and never
    /// relies on the real ones, and the tests where several saves run at once must not use this clock at all: a write in flight would look
    /// old to another save's clean-up (and be promoted or deleted), so they use <see cref="RealClock"/>.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 21, 4, 5, 6, 789, TimeSpan.Zero);

    public const int Pid = 4242;

    /// <summary>The stamp of <see cref="Now"/> in a .bad name.</summary>
    public const string NowStamp = "20261121T040506789Z";

    /// <summary>
    /// A clock that agrees with the real file times, for the tests where several saves run at once: a write in flight must never
    /// look old to another save's clean-up. (The tests that look at ages set the file times by hand and use <see cref="Now"/>.)
    /// </summary>
    public static FakeTime RealClock() => new(DateTimeOffset.UtcNow);

    public static string KindName(CheckKind kind) => kind.ToString().ToLowerInvariant();

    public static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    public static string NameOf(CheckKind kind, DateTimeOffset end, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{KindName(kind)}-{Stamp(end)}-{id:N}.json");

    public static string NameOf(CheckResult result) => NameOf(result.Kind, result.EndedAtUtc, result.RunId);

    /// <summary>00000000-0000-4000-8000-&lt;n as 12 hex digits&gt;: ordering by n is the ordering of the UUIDs.</summary>
    public static Guid Id(long n) => Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-4000-8000-{n:x12}"));

    /// <summary>The nonce source of a store: 00000001, 00000002, ...</summary>
    public static Func<string> Nonces(int first = 1)
    {
        var next = first - 1;
        return () => string.Create(CultureInfo.InvariantCulture, $"{Interlocked.Increment(ref next):x8}");
    }

    public static string TempNameOf(string finalName, int nonce) => string.Create(CultureInfo.InvariantCulture, $"{finalName}.pcmtmp-{Pid}-{nonce:x8}.tmp");

    /// <summary>A valid result of the kind that ends at <paramref name="end"/> (a sample with another UUID and times).</summary>
    public static CheckResult Make(CheckKind kind, DateTimeOffset end, Guid? id = null, int durationMs = 1000)
    {
        var template = kind switch
        {
            CheckKind.System => Samples.SystemCheck(),
            CheckKind.Cpu => Samples.CpuCheck(),
            CheckKind.Memory => Samples.MemoryCheck(),
            CheckKind.Storage => Samples.StorageCheck(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return template with { RunId = id ?? Guid.NewGuid(), StartedAtUtc = end.AddMilliseconds(-durationMs), EndedAtUtc = end };
    }

    public static byte[] Bytes(CheckResult result) => Encoding.UTF8.GetBytes(CheckJson.Serialize(result, indented: true) + "\n");

    /// <summary>Writes the result straight into the folder under its own name (or <paramref name="asName"/>), bypassing the store.</summary>
    public static string Put(string root, CheckResult result, string? asName = null)
    {
        Directory.CreateDirectory(root);
        var name = asName ?? NameOf(result);
        File.WriteAllBytes(Path.Combine(root, name), Bytes(result));
        return name;
    }

    public static string PutText(string root, string name, string text)
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, name), new UTF8Encoding(false).GetBytes(text));
        return name;
    }

    /// <summary>The JSON text of the result with one piece of it replaced; the piece must be there.</summary>
    public static string Edited(CheckResult result, string from, string to)
    {
        var text = CheckJson.Serialize(result, indented: true) + "\n";
        Assert.Contains(from, text, StringComparison.Ordinal);
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    public static void SetAge(string path, DateTimeOffset lastWrite) => File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);

    /// <summary>The names of the files directly in the folder, in ordinal order.</summary>
    public static string[] Names(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root).Select(Path.GetFileName).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal)]
            : [];

    /// <summary>Every file and folder below the root with its length, last write time and content hash: equal snapshots mean nothing was touched.</summary>
    public static IReadOnlyList<string> Snapshot(string root)
    {
        if (!Directory.Exists(root))
        {
            return ["<no directory>"];
        }

        var lines = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path);
            if (Directory.Exists(path))
            {
                lines.Add("D " + relative);
                continue;
            }

            var info = new FileInfo(path);
            string hash;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (IOException)
            {
                hash = "locked";
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"F {relative} {info.Length} {info.LastWriteTimeUtc.Ticks} {hash}"));
        }

        return lines;
    }

    public static CheckHistoryStore Store(
        string root,
        FakeTime? time = null,
        ICheckHistoryFileSystem? files = null,
        Func<string>? nonce = null,
        TimeSpan? tempMaxAge = null,
        Action<TimeSpan>? sleep = null) =>
        new(root, new CheckHistoryStoreOptions
        {
            Files = files ?? PhysicalHistoryFileSystem.Instance,
            Time = time ?? new FakeTime(Now),
            ProcessId = Pid,
            Nonce = nonce ?? Nonces(),
            TempMaxAge = tempMaxAge ?? CheckHistoryStoreOptions.DefaultTempMaxAge,

            // The tests never wait for a retry; the ones about retries pass a recorder.
            Sleep = sleep ?? (static _ => { }),
        });

    public static ReadOnlyCheckHistory ReadOnly(string root, ICheckHistoryFileReader? files = null) => new(root, files);

    public static CheckHistoryQuery All { get; } = new(null, null);

    public static string[] WarningLines(CheckHistoryPage page) => [.. page.Warnings.Select(w => $"{w.Code}={w.Count}")];

    public static Guid[] RunIds(CheckHistoryPage page) => [.. page.Entries.Select(e => e.RunId)];

    public static IOException DiskFull() => new("disk full", unchecked((int)0x80070070));

    public static IOException SharingViolation() => new("in use", unchecked((int)0x80070020));
}
