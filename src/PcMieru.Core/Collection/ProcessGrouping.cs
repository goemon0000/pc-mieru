using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core.Collection;

/// <summary>
/// The process list's three groups. The rule is PC Mieru's own (SPEC 5.2) and is not claimed to match Task Manager's:
/// a process with a window the user can see is an App; one whose image is signed and lives in the Windows folder is Windows;
/// everything else is Background.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ProcessGroup>))]
public enum ProcessGroup
{
    App,
    Windows,
    Background,
}

/// <summary>
/// Which group a process belongs to, answered from the latest sample. <c>ProcessInfo</c> carries no group (the contract is
/// frozen), so the list asks this service instead.
/// </summary>
public interface IProcessGroupSource
{
    /// <summary>
    /// Ok with the group and, in Detail, the reason. NotCollected while the image of a new process is still being examined
    /// (a second or two after it first appears; the signature check reads the file). Gone for a key that is not in the latest
    /// sample. Missing (with the status of the failure) when the window enumeration failed, since "has a window" is then unknown.
    /// Cheap and safe from any thread.
    /// </summary>
    Value<ProcessGroup> GroupOf(ProcessKey key);
}

/// <summary>The classification of a process that is not an App: Windows or Background, with the reason that is shown as Detail.</summary>
internal readonly record struct GroupVerdict(ProcessGroup Group, string Why, bool Retry = false);

/// <summary>Decides Windows or Background from the image file: needs the path and, for the Windows folder, the signature.</summary>
internal sealed class WindowsFolderClassifier
{
    private const uint SystemIdle = 0;
    private const uint SystemProcess = 4;

    private readonly Func<string, Value<SignatureInfo>> _verify;
    private readonly string _windowsPrefix;

    public WindowsFolderClassifier(Func<string, Value<SignatureInfo>> verify, string? windowsDirectory = null)
    {
        _verify = verify;
        var folder = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _windowsPrefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
    }

    public GroupVerdict Classify(ProcessInfo process)
    {
        if (process.Key.Pid is SystemIdle or SystemProcess)
        {
            return new GroupVerdict(ProcessGroup.Windows, "the kernel's own process");
        }

        var path = ReadPath(process.Key);
        switch (path.Status)
        {
            case ValueStatus.Ok:
                break;
            case ValueStatus.Unsupported:
                // Registry, Memory Compression and the like: kernel processes started by System, with no image file.
                return process.ParentPid == SystemProcess
                    ? new GroupVerdict(ProcessGroup.Windows, "a kernel process with no image file")
                    : new GroupVerdict(ProcessGroup.Background, "no image file to examine");
            case ValueStatus.Gone:
                return new GroupVerdict(ProcessGroup.Background, "the process ended before it could be examined");
            default:
                return new GroupVerdict(ProcessGroup.Background, "the image path could not be read: " + (path.Detail ?? path.Status.ToString()), Retry: true);
        }

        var file = path.Data!;
        if (!file.StartsWith(_windowsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new GroupVerdict(ProcessGroup.Background, "the image is not in the Windows folder");
        }

        Value<SignatureInfo> signature;
        try
        {
            signature = _verify(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            signature = Value.Failed<SignatureInfo>("signature", ex.GetType().Name);
        }

        if (!signature.IsOk || signature.Data is null)
        {
            return new GroupVerdict(ProcessGroup.Background, "the signature could not be checked: " + (signature.Detail ?? signature.Status.ToString()), Retry: true);
        }

        return signature.Data.State switch
        {
            SignatureState.SignedEmbedded => new GroupVerdict(ProcessGroup.Windows, "signed, and in the Windows folder"),
            SignatureState.SignedCatalog => new GroupVerdict(ProcessGroup.Windows, "signed by a Windows catalog, and in the Windows folder"),
            SignatureState.Invalid => new GroupVerdict(ProcessGroup.Background, "in the Windows folder, but its signature is not valid"),
            _ => new GroupVerdict(ProcessGroup.Background, "in the Windows folder, but not signed"),
        };
    }

    private static Value<string> ReadPath(ProcessKey key)
    {
        switch (ProcessHandle.Open(key, out var handle, out var detail))
        {
            case OpenOutcome.Opened:
                using (handle)
                {
                    return handle!.ReadPath();
                }

            case OpenOutcome.Gone:
                return Value.Gone<string>("OpenProcess", detail);
            case OpenOutcome.Denied:
                // Not ours to open, but the kernel names the image to anyone. The key came from the sample taken a moment ago.
                var byPid = ProcessImagePath.Resolve(key.Pid);
                return byPid is null
                    ? Value.Unsupported<string>("image path", "this process has no image file, or its device path could not be mapped to a drive letter")
                    : Value.Ok(byPid, "NtQuerySystemInformation(SystemProcessIdInformation)");
            default:
                return Value.Failed<string>("OpenProcess", detail ?? "the process could not be opened");
        }
    }
}

/// <summary>
/// Keeps the group of every process in the latest sample. App is recomputed from each tick's window list; Windows or
/// Background is decided once per process (its image does not change) on one background thread, so the sampling tick never
/// waits for a file to be hashed. Entries of processes that left the sample are dropped on the next update.
/// </summary>
internal sealed class ProcessGrouper : IProcessGroupSource, IDisposable
{
    public const string Source = "own rule: visible window = App, signed in the Windows folder = Windows, otherwise Background";
    public const int MaxAttempts = 3;

    private readonly Func<ProcessInfo, GroupVerdict> _classify;
    private readonly object _lock = new();
    private readonly Dictionary<ProcessKey, Entry> _entries = new(512);
    private readonly Queue<ProcessKey> _queue = new();
    private WindowProbeResult? _windows;
    private ValueStatus _windowFailure = ValueStatus.NotCollected;
    private string? _windowFailureDetail;
    private long _generation;
    private bool _workerRunning;
    private volatile bool _disposed;

    public ProcessGrouper(Func<ProcessInfo, GroupVerdict> classify)
    {
        _classify = classify;
    }

    private sealed class Entry(ProcessInfo process)
    {
        public ProcessInfo Process { get; } = process;

        public GroupVerdict? Verdict { get; set; }

        public int Attempts { get; set; }

        public bool Queued { get; set; }

        public long Seen { get; set; }
    }

    /// <summary>Processes whose Windows-or-Background decision is not made yet.</summary>
    internal int Pending
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Count(e => e.Verdict is null);
            }
        }
    }

    internal int EntryCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Called once per tick with the sample's processes and its window probe (null, with the reason, when the probe failed).</summary>
    public void Update(IReadOnlyList<ProcessInfo> processes, WindowProbeResult? windows, ValueStatus failureStatus, string? failureDetail)
    {
        var start = false;
        lock (_lock)
        {
            _windows = windows;
            _windowFailure = failureStatus;
            _windowFailureDetail = failureDetail;
            var generation = ++_generation;
            foreach (var process in processes)
            {
                if (!_entries.TryGetValue(process.Key, out var entry))
                {
                    entry = new Entry(process);
                    _entries[process.Key] = entry;
                }

                entry.Seen = generation;
                if (entry.Verdict is null && !entry.Queued)
                {
                    entry.Queued = true;
                    _queue.Enqueue(process.Key);
                }
            }

            if (_entries.Count > processes.Count)
            {
                foreach (var pair in _entries)
                {
                    if (pair.Value.Seen != generation)
                    {
                        _ = _entries.Remove(pair.Key);
                    }
                }
            }

            if (_queue.Count > 0 && !_workerRunning && !_disposed)
            {
                _workerRunning = true;
                start = true;
            }
        }

        if (start)
        {
            _ = Task.Run(Work);
        }
    }

    public Value<ProcessGroup> GroupOf(ProcessKey key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Seen != _generation)
            {
                return Value.Gone<ProcessGroup>(Source, "this process is not in the latest sample");
            }

            if (_windows is null)
            {
                return Value.Missing<ProcessGroup>(_windowFailure, Source, _windowFailureDetail ?? "the window list is not available");
            }

            if (_windows.WithWindow.Contains(key.Pid))
            {
                return new Value<ProcessGroup>(ProcessGroup.App, ValueStatus.Ok, Source, "has a window the user can see");
            }

            return entry.Verdict is { } verdict
                ? new Value<ProcessGroup>(verdict.Group, ValueStatus.Ok, Source, verdict.Why)
                : Value.NotCollected<ProcessGroup>(Source, "still being examined in the background");
        }
    }

    /// <summary>Waits until the background worker has nothing left to do (for tests and for callers that want a settled answer).</summary>
    internal bool WaitForIdle(TimeSpan timeout)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            lock (_lock)
            {
                if (!_workerRunning && _queue.Count == 0)
                {
                    return true;
                }
            }

            Thread.Sleep(5);
        }

        return false;
    }

    public void Dispose() => _disposed = true;

    private void Work()
    {
        while (!_disposed)
        {
            Entry? entry = null;
            lock (_lock)
            {
                while (_queue.Count > 0)
                {
                    var key = _queue.Dequeue();
                    if (_entries.TryGetValue(key, out var candidate) && candidate.Verdict is null)
                    {
                        entry = candidate;
                        break;
                    }
                }

                if (entry is null)
                {
                    _workerRunning = false;
                    return;
                }
            }

            GroupVerdict verdict;
            try
            {
                verdict = _classify(entry.Process);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                verdict = new GroupVerdict(ProcessGroup.Background, "could not be examined: " + ex.GetType().Name, Retry: true);
            }

            lock (_lock)
            {
                entry.Attempts++;
                if (verdict.Retry && entry.Attempts < MaxAttempts)
                {
                    entry.Queued = false; // asked again on the next tick, not in a tight loop
                }
                else
                {
                    entry.Verdict = verdict with { Retry = false };
                }
            }
        }

        lock (_lock)
        {
            _workerRunning = false;
        }
    }
}

/// <summary>Stand-in used when the app runs on fake data: names decide, and the detail says so.</summary>
public sealed class FakeProcessGroupSource : IProcessGroupSource, IFakeImplementation
{
    public Value<ProcessGroup> GroupOf(ProcessKey key) =>
        new((ProcessGroup)(key.Pid % 3), ValueStatus.Ok, "fake", "fake data: the group is made up from the PID");
}
