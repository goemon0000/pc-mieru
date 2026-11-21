// Provenance: PartsLife.Checks.Build.BuildSources and BuildCollector (pl-cloud 25e4571, src/PartsLife.Checks/Build/BuildCollector.cs), MIT, same owner.
// The sources, their order (CPU, board, memory, GPU, storage, monitor, optical drive) and "a category that cannot be read stays empty, it is
// never an exception" are the old ones. What is new: the sources are asynchronous (the Windows ones are bounded by a deadline, see
// WindowsBuildReaders.cs), the collector is a Task, a source that throws leaves a Trace line with the exception type only (a Windows
// message could name a machine or a path), and the collector says which categories it could not read and why (BuildUnread).
using System.Diagnostics;

namespace PcMieru.Checks.Build;

/// <summary>
/// Where the parts are read from, one source per category. **Tests replace them** (a source that reads nothing, or throws, must give an
/// empty category). The default is empty; <see cref="Windows"/> is this machine (WMI with a deadline, and DXGI; no administrator needed).
/// A source that could not read says so by throwing <see cref="BuildCategoryUnreadException"/> with the reason; an empty list means "read, and
/// there is none". Any other exception is a failure (<see cref="CheckReason.Error"/>).
/// </summary>
internal sealed class BuildSources
{
    public Func<CancellationToken, Task<IReadOnlyList<string>>> Cpus { get; init; } = static _ => Nothing<string>();

    public Func<CancellationToken, Task<(string? Manufacturer, string? Product)>> Board { get; init; } = static _ => Task.FromResult<(string?, string?)>((null, null));

    public Func<CancellationToken, Task<IReadOnlyList<RawMemory>>> Memory { get; init; } = static _ => Nothing<RawMemory>();

    public Func<CancellationToken, Task<IReadOnlyList<RawGpu>>> Gpus { get; init; } = static _ => Nothing<RawGpu>();

    public Func<CancellationToken, Task<IReadOnlyList<RawDisk>>> Disks { get; init; } = static _ => Nothing<RawDisk>();

    public Func<CancellationToken, Task<IReadOnlyList<RawMonitor>>> Monitors { get; init; } = static _ => Nothing<RawMonitor>();

    public Func<CancellationToken, Task<IReadOnlyList<RawOptical>>> Opticals { get; init; } = static _ => Nothing<RawOptical>();

    /// <summary>This machine: WMI (each question bounded by a deadline) and DXGI.</summary>
    public static BuildSources Windows(WindowsBuildReaders? readers = null)
    {
        var r = readers ?? new WindowsBuildReaders();
        return new BuildSources
        {
            Cpus = r.CpusAsync,
            Board = r.BoardAsync,
            Memory = r.MemoryAsync,
            Gpus = r.GpusAsync,
            Disks = r.DisksAsync,
            Monitors = r.MonitorsAsync,
            Opticals = r.OpticalsAsync,
        };
    }

    private static Task<IReadOnlyList<T>> Nothing<T>() => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
}

/// <summary>
/// A source could not read its category, for this reason (the readers of Windows throw it when a question was out of time, denied or
/// broken). The collector leaves the category empty and keeps the reason. It carries nothing of the machine.
/// </summary>
internal sealed class BuildCategoryUnreadException(CheckReason reason) : Exception("The category could not be read: " + reason)
{
    public CheckReason Reason { get; } = reason;
}

/// <summary>What the collector read, and the categories it could not read (in reading order).</summary>
internal sealed record BuildCollection(BuildConfig Config, IReadOnlyList<BuildUnread> Unread);

/// <summary>
/// Collects the parts of a machine. **A category that could not be read is silently left empty** (no exception comes out; a Windows reader
/// that ran out of time is the same) and is listed as unread, with the reason. Only the caller's cancellation is passed on. It is slow (WMI):
/// not on the UI thread. Internal: the way in from outside is <see cref="BuildCodeProviders"/>.
/// </summary>
internal static class BuildCollector
{
    /// <summary>The parts only (the tests that do not care about the unread categories).</summary>
    internal static async Task<BuildConfig> CollectAsync(BuildSources sources, CancellationToken ct = default) =>
        (await CollectWithUnreadAsync(sources, ct).ConfigureAwait(false)).Config;

    /// <summary>The parts, and the categories that could not be read.</summary>
    internal static async Task<BuildCollection> CollectWithUnreadAsync(BuildSources sources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var c = new BuildConfig();
        var unread = new List<BuildUnread>();

        async Task Try(string what, string[] categories, Func<Task> read)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await read().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BuildCategoryUnreadException ex)
            {
                Trace.TraceWarning($"PCMieru: build code: could not read {what}: {ex.Reason}");
                Left(categories, ex.Reason);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.TraceWarning($"PCMieru: build code: could not read {what}: {ex.GetType().Name}");
                Left(categories, CheckReason.Error); // a source that throws something else: it failed
            }
        }

        void Left(string[] categories, CheckReason reason)
        {
            foreach (var category in categories) unread.Add(new BuildUnread(category, reason));
        }

        await Try("cpu", ["cpu"], async () => c.Set("cpu", BuildRules.Cpus(await sources.Cpus(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("motherboard", ["motherboard"], async () =>
        {
            var (m, p) = await sources.Board(ct).ConfigureAwait(false);
            c.Set("motherboard", BuildRules.Motherboard(m, p));
        }).ConfigureAwait(false);
        await Try("ram", ["ram"], async () => c.Set("ram", BuildRules.Ram(await sources.Memory(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("gpu", ["gpu"], async () => c.Set("gpu", BuildRules.Gpus(await sources.Gpus(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("storage", ["ssd", "hdd"], async () =>
        {
            var (ssd, hdd) = BuildRules.Disks(await sources.Disks(ct).ConfigureAwait(false));
            c.Set("ssd", ssd);
            c.Set("hdd", hdd);
        }).ConfigureAwait(false);
        await Try("monitor", ["monitor"], async () => c.Set("monitor", BuildRules.Monitors(await sources.Monitors(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("optical-drive", ["optical-drive"], async () => c.Set("optical-drive", BuildRules.Opticals(await sources.Opticals(ct).ConfigureAwait(false)))).ConfigureAwait(false);

        // A cancel that came while the last category was read must not be lost: the caller asked to stop, so it gets no result.
        ct.ThrowIfCancellationRequested();
        return new BuildCollection(c, unread);
    }
}
