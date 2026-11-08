using System.Collections.Concurrent;

namespace PcMieru.Core.Collection;

// IProcessIconSource lives in Core/Contracts (the GUI asks for it for the process list and the detail pane).

/// <summary>Real icon extraction (PrivateExtractIcons + GDI). Work runs on background threads, at most two at a time.</summary>
public sealed class ProcessIconSource : IProcessIconSource
{
    public const string Source = "PrivateExtractIcons";

    private const int MaxCached = 512;
    private const int MinSize = 8;
    private const int MaxSize = 256;
    private const int MaxConcurrent = 2;

    private readonly Func<string, int, Value<byte[]>> _extract;
    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<Key, Value<byte[]>> _cache = new();
    private readonly ConcurrentDictionary<Key, Lazy<Task<Value<byte[]>>>> _inflight = new();
    private readonly Queue<Key> _order = new();
    private int _extractions;

    public ProcessIconSource()
        : this(IconExtractor.ExtractPng)
    {
    }

    internal ProcessIconSource(Func<string, int, Value<byte[]>> extract)
    {
        _extract = extract;
    }

    private readonly record struct Key(FileStamp File, int Size);

    /// <summary>How many icons were actually extracted (not served from the cache).</summary>
    internal int Extractions => Volatile.Read(ref _extractions);

    internal int CachedCount => _cache.Count;

    public async Task<Value<byte[]>> GetPngAsync(string path, int size = 32, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Value.Unsupported<byte[]>(Source, "no file path was given");
        }

        size = Math.Clamp(size, MinSize, MaxSize);
        var stamp = FileStamp.TryRead(path, out var problem);
        if (stamp is not { } file)
        {
            return Value.Failed<byte[]>(Source, problem ?? "the file could not be examined");
        }

        var key = new Key(file, size);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var work = _inflight.GetOrAdd(key, k => new Lazy<Task<Value<byte[]>>>(() => Task.Run(() => RunAsync(k)), LazyThreadSafetyMode.ExecutionAndPublication));
        return await work.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<Value<byte[]>> RunAsync(Key key)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _extractions);
                Value<byte[]> result;
                try
                {
                    result = _extract(key.File.Path, key.Size);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result = Value.Failed<byte[]>(Source, $"the icon could not be extracted: {ex.GetType().Name}");
                }

                // Only what is true of the file is remembered; a transient failure is asked again next time.
                if (result.Status is ValueStatus.Ok or ValueStatus.Unsupported)
                {
                    Remember(key, result);
                }

                return result;
            }
            finally
            {
                _ = _gate.Release();
            }
        }
        finally
        {
            _ = _inflight.TryRemove(key, out _);
        }
    }

    private void Remember(Key key, Value<byte[]> result)
    {
        lock (_order)
        {
            if (_cache.TryAdd(key, result))
            {
                _order.Enqueue(key);
                while (_order.Count > MaxCached)
                {
                    _ = _cache.TryRemove(_order.Dequeue(), out _);
                }
            }
        }
    }
}

/// <summary>Stand-in used when the app runs on fake data: no icons, and it says so.</summary>
public sealed class FakeProcessIconSource : IProcessIconSource, IFakeImplementation
{
    public Task<Value<byte[]>> GetPngAsync(string path, int size = 32, CancellationToken ct = default) =>
        Task.FromResult(Value.Unsupported<byte[]>("fake", "fake data has no program files, so there is no icon"));
}
