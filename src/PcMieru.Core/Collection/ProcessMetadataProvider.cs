using System.Collections.Concurrent;

namespace PcMieru.Core.Collection;

/// <summary>What never changes for a running process (or stays true for as long as anyone looks): read once and cached per key.</summary>
internal sealed record StableFacts(
    Value<string> Path,
    Value<string> CommandLine,
    Value<string> Owner,
    Value<string> Product,
    Value<string> Description,
    Value<string> Company,
    Value<string> Architecture,
    bool Cacheable)
{
    /// <summary>The process (or its PID) is not there any more: every fact is Gone.</summary>
    public static StableFacts AllGone(string source, string detail) => new(
        Value.Gone<string>(source, detail),
        Value.Gone<string>(source, detail),
        Value.Gone<string>(source, detail),
        Value.Gone<string>(source, detail),
        Value.Gone<string>(source, detail),
        Value.Gone<string>(source, detail),
        Value.Gone<string>(source, detail),
        Cacheable: true);

    public bool IsGone => Path.Status == ValueStatus.Gone;
}

/// <summary>Where the facts come from. The real one reads the process; tests substitute their own to count calls.</summary>
internal interface IFactReader
{
    /// <summary>Blocking. Opens the process, proves identity, reads everything stable. Never throws for a vanished process.</summary>
    StableFacts ReadStable(ProcessKey key);

    /// <summary>Blocking and cheap: efficiency mode can change at any time, so it is read on every request.</summary>
    Value<bool> ReadEfficiency(ProcessKey key);
}

/// <summary>
/// The real <see cref="IProcessDetailsProvider"/>: path, command line, owner, version strings, architecture, services,
/// efficiency mode and an offline signature check, all read only when someone asks about one process (SPEC 4 and 6).
/// Stable facts are cached per <see cref="ProcessKey"/> and shared between concurrent callers; the work runs on background
/// threads, at most three at a time (two for signature checks, which hash whole files). A process that has gone, or whose PID
/// now belongs to another process, answers Gone for everything.
/// </summary>
public sealed class ProcessMetadataProvider : IProcessDetailsProvider, IDisposable
{
    public const int MaxCachedProcesses = 2048;
    public const int MaxConcurrentReads = 3;
    public const int MaxConcurrentSignatureChecks = 2;

    private const string EfficiencySource = "GetProcessInformation(ProcessPowerThrottling)";

    private static readonly TimeSpan DefaultSignatureBudget = TimeSpan.FromSeconds(5);

    private readonly IFactReader _reader;
    private readonly Func<string, Value<SignatureInfo>> _verify;
    private readonly ServiceMap _services;
    private readonly Func<IKnownProcessCatalog?> _catalog;
    private readonly Func<ProcessKey, string?> _nameOf;
    private readonly TimeSpan _signatureBudget;

    private readonly SemaphoreSlim _readGate = new(MaxConcurrentReads, MaxConcurrentReads);
    private readonly SemaphoreSlim _signatureGate = new(MaxConcurrentSignatureChecks, MaxConcurrentSignatureChecks);
    private readonly ConcurrentDictionary<ProcessKey, StableFacts> _cache = new();
    private readonly ConcurrentDictionary<ProcessKey, Lazy<Task<StableFacts>>> _reading = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<Value<SignatureInfo>>>> _signing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ProcessKey> _order = new();
    private int _stableReads;
    private int _running;
    private int _peakRunning;
    private volatile bool _disposed;

    /// <param name="catalog">
    /// Resolves the "what is this?" catalog every time it is needed (the Reporting module registers after this one). Null, or a
    /// null result, means no catalog: <see cref="ProcessDetails.Known"/> stays null.
    /// </param>
    /// <param name="nameOf">
    /// The image name of a process in the latest sample, or null when the key is not in it. Names kernel processes that have no
    /// image file and confirms the identity of a process this user is not allowed to open.
    /// </param>
    public ProcessMetadataProvider(Func<IKnownProcessCatalog?>? catalog = null, Func<ProcessKey, string?>? nameOf = null)
        : this(catalog, nameOf, new SignatureVerifier())
    {
    }

    /// <param name="verifier">Shared with the process grouper so each file is hashed once for both.</param>
    internal ProcessMetadataProvider(Func<IKnownProcessCatalog?>? catalog, Func<ProcessKey, string?>? nameOf, SignatureVerifier verifier)
        : this(new SystemFactReader(nameOf), verifier.Verify, new ServiceMap(), catalog ?? (static () => null), nameOf ?? (static _ => null), DefaultSignatureBudget)
    {
    }

    internal ProcessMetadataProvider(
        IFactReader reader,
        Func<string, Value<SignatureInfo>> verify,
        ServiceMap services,
        Func<IKnownProcessCatalog?> catalog,
        Func<ProcessKey, string?> nameOf,
        TimeSpan signatureBudget)
    {
        _reader = reader;
        _verify = verify;
        _services = services;
        _catalog = catalog;
        _nameOf = nameOf;
        _signatureBudget = signatureBudget;
    }

    /// <summary>How many times the stable facts were really read (not served from the cache or joined to a running read).</summary>
    internal int StableReads => Volatile.Read(ref _stableReads);

    /// <summary>The most reads that ever ran at once; never above <see cref="MaxConcurrentReads"/>.</summary>
    internal int PeakConcurrentReads => Volatile.Read(ref _peakRunning);

    internal int CachedCount => _cache.Count;

    public async Task<ProcessDetails> GetAsync(ProcessKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var stable = await GetStableAsync(key, ct).ConfigureAwait(false);
        if (stable.IsGone)
        {
            return Compose(key, stable, Value.Gone<bool>(EfficiencySource, "the process has exited"), Value.Gone<SignatureInfo>(SignatureVerifier.Source, "the process has exited"), []);
        }

        var efficiency = SafeEfficiency(key);
        var signature = await SignatureAsync(stable, ct).ConfigureAwait(false);
        var services = SafeServices(key);
        return Compose(key, stable, efficiency, signature, services);
    }

    public void Dispose() => _disposed = true;

    private ProcessDetails Compose(ProcessKey key, StableFacts stable, Value<bool> efficiency, Value<SignatureInfo> signature, IReadOnlyList<string> services)
    {
        var known = Match(stable, signature, key);
        return new ProcessDetails(
            key,
            stable.Path,
            stable.CommandLine,
            stable.Owner,
            stable.Product,
            stable.Description,
            stable.Company,
            stable.Architecture,
            efficiency,
            signature,
            services,
            known);
    }

    private KnownProcess? Match(StableFacts stable, Value<SignatureInfo> signature, ProcessKey key)
    {
        var catalog = SafeCatalog();
        if (catalog is null)
        {
            return null;
        }

        var path = stable.Path.IsOk ? stable.Path.Data : null;
        var name = path is null ? _nameOf(key) : System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            return catalog.Match(name, path, signature.IsOk ? signature.Data : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken catalog must not take the details down with it.
            return null;
        }
    }

    private IKnownProcessCatalog? SafeCatalog()
    {
        try
        {
            return _catalog();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return null; // the service registry has no catalog (yet)
        }
    }

    private Value<bool> SafeEfficiency(ProcessKey key)
    {
        try
        {
            return _reader.ReadEfficiency(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Value.Failed<bool>(EfficiencySource, $"the power throttling state could not be read: {ex.GetType().Name}");
        }
    }

    private IReadOnlyList<string> SafeServices(ProcessKey key)
    {
        try
        {
            return _services.NamesOf(key.Pid);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private async Task<StableFacts> GetStableAsync(ProcessKey key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var read = _reading.GetOrAdd(key, k => new Lazy<Task<StableFacts>>(() => Task.Run(() => ReadStableLimitedAsync(k)), LazyThreadSafetyMode.ExecutionAndPublication));

        // Cancelling stops this caller waiting; the read itself is shared and carries on for whoever else asked.
        return await read.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<StableFacts> ReadStableLimitedAsync(ProcessKey key)
    {
        try
        {
            await _readGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var running = Interlocked.Increment(ref _running);
                int peak;
                while (running > (peak = Volatile.Read(ref _peakRunning)) && Interlocked.CompareExchange(ref _peakRunning, running, peak) != peak)
                {
                }

                try
                {
                    Interlocked.Increment(ref _stableReads);
                    StableFacts facts;
                    try
                    {
                        facts = _reader.ReadStable(key);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Not a fact about the process: do not remember it.
                        const string source = "process details";
                        var detail = $"the details could not be read: {ex.GetType().Name}";
                        var failed = Value.Failed<string>(source, detail);
                        facts = new StableFacts(failed, failed, failed, failed, failed, failed, failed, Cacheable: false);
                    }

                    if (facts.Cacheable)
                    {
                        Remember(key, facts);
                    }

                    return facts;
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _running);
                }
            }
            finally
            {
                _ = _readGate.Release();
            }
        }
        finally
        {
            _ = _reading.TryRemove(key, out _);
        }
    }

    private void Remember(ProcessKey key, StableFacts facts)
    {
        lock (_order)
        {
            if (_cache.TryAdd(key, facts))
            {
                _order.Enqueue(key);
                while (_order.Count > MaxCachedProcesses)
                {
                    _ = _cache.TryRemove(_order.Dequeue(), out _);
                }
            }
        }
    }

    /// <summary>
    /// The signature of the process's image. A check that takes longer than the budget (a very large file, a slow disk) is
    /// reported as Timeout while it carries on in the background, so asking again a moment later finds the answer.
    /// </summary>
    private async Task<Value<SignatureInfo>> SignatureAsync(StableFacts stable, CancellationToken ct)
    {
        if (!stable.Path.IsOk || string.IsNullOrEmpty(stable.Path.Data))
        {
            var why = stable.Path.Status switch
            {
                ValueStatus.Unsupported => ValueStatus.Unsupported,
                ValueStatus.Denied => ValueStatus.Denied,
                _ => ValueStatus.Error,
            };
            return Value.Missing<SignatureInfo>(why, SignatureVerifier.Source, "the image file is not known, so it cannot be checked");
        }

        var path = stable.Path.Data;
        var work = _signing.GetOrAdd(path, p => new Lazy<Task<Value<SignatureInfo>>>(() => Task.Run(() => VerifyLimitedAsync(p)), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await work.Value.WaitAsync(_signatureBudget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Value.Missing<SignatureInfo>(ValueStatus.Timeout, SignatureVerifier.Source, "the signature check is still running; ask again in a moment");
        }
    }

    private async Task<Value<SignatureInfo>> VerifyLimitedAsync(string path)
    {
        try
        {
            await _signatureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    return _verify(path);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Value.Failed<SignatureInfo>(SignatureVerifier.Source, $"the signature could not be checked: {ex.GetType().Name}");
                }
            }
            finally
            {
                _ = _signatureGate.Release();
            }
        }
        finally
        {
            // The verifier remembers answers by file version, so the next request is a cache hit.
            _ = _signing.TryRemove(path, out _);
        }
    }
}

/// <summary>Reads the facts from the live process.</summary>
internal sealed class SystemFactReader(Func<ProcessKey, string?>? nameOf) : IFactReader
{
    private const string EfficiencySourceName = "GetProcessInformation(ProcessPowerThrottling)";
    private const string NoPathSource = "image path";
    private const string KernelSource = "NtQuerySystemInformation(SystemProcessIdInformation)";

    public StableFacts ReadStable(ProcessKey key)
    {
        switch (ProcessHandle.Open(key, out var handle, out var detail))
        {
            case OpenOutcome.Gone:
                return StableFacts.AllGone("OpenProcess", detail ?? "the process has exited");
            case OpenOutcome.Error:
                return AllMissing(ValueStatus.Error, "OpenProcess", detail ?? "the process could not be opened", cacheable: false);
            case OpenOutcome.Denied:
                return ReadWithoutHandle(key, detail ?? "access denied opening the process");
            default:
                using (handle)
                {
                    return ReadWithHandle(handle!);
                }
        }
    }

    public Value<bool> ReadEfficiency(ProcessKey key)
    {
        switch (ProcessHandle.Open(key, out var handle, out var detail))
        {
            case OpenOutcome.Gone:
                return Value.Gone<bool>(EfficiencySourceName, detail ?? "the process has exited");
            case OpenOutcome.Denied:
                return Value.Denied<bool>(EfficiencySourceName, detail ?? "access denied opening the process");
            case OpenOutcome.Error:
                return Value.Failed<bool>(EfficiencySourceName, detail ?? "the process could not be opened");
            default:
                using (handle)
                {
                    return handle!.ReadEfficiencyMode();
                }
        }
    }

    private static StableFacts ReadWithHandle(ProcessHandle handle)
    {
        var path = handle.ReadPath();
        var owner = handle.ReadOwner();
        var commandLine = handle.ReadCommandLine();
        var architecture = handle.ReadArchitecture();
        var versions = VersionsOf(path);
        return new StableFacts(path, commandLine, owner, versions.Product, versions.Description, versions.Company, architecture, Cacheable: true);
    }

    /// <summary>
    /// The process could not be opened (another user's, or protected). The kernel still names the image to any caller, but
    /// without a handle nothing proves the PID is still the same process, so the latest sample is asked to vouch for it.
    /// </summary>
    internal StableFacts ReadWithoutHandle(ProcessKey key, string denied)
    {
        const string source = "OpenProcess";
        var deniedValue = Value.Denied<string>(source, denied);
        Value<string> path;
        if (nameOf is null || nameOf(key) is null)
        {
            path = Value.Denied<string>(source, denied + " (and the process is not in the latest sample, so its identity is not confirmed)");
        }
        else
        {
            var resolved = ProcessImagePath.Resolve(key.Pid);
            path = resolved is null
                ? Value.Unsupported<string>(NoPathSource, "this process has no image file, or its device path could not be mapped to a drive letter")
                : new Value<string>(resolved, ValueStatus.Ok, KernelSource, "read by PID: the process could not be opened");
        }

        var versions = VersionsOf(path);
        return new StableFacts(path, deniedValue, deniedValue, versions.Product, versions.Description, versions.Company, deniedValue, Cacheable: true);
    }

    private static FileVersionStrings VersionsOf(Value<string> path)
    {
        if (!path.IsOk || string.IsNullOrEmpty(path.Data))
        {
            return FileVersionStrings.All(
                path.Status is ValueStatus.Ok ? ValueStatus.Error : path.Status,
                VersionInfoReader.Source,
                "the image file is not known, so its version resource cannot be read");
        }

        try
        {
            return VersionInfoReader.Read(path.Data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FileVersionStrings.All(ValueStatus.Error, VersionInfoReader.Source, $"the version resource could not be read: {ex.GetType().Name}");
        }
    }

    private static StableFacts AllMissing(ValueStatus status, string source, string detail, bool cacheable)
    {
        var missing = Value.Missing<string>(status, source, detail);
        return new StableFacts(missing, missing, missing, missing, missing, missing, missing, cacheable);
    }
}
