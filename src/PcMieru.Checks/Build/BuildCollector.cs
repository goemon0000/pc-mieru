// Provenance: PartsLife.Checks.Build.BuildSources and BuildCollector (pl-cloud 25e4571, src/PartsLife.Checks/Build/BuildCollector.cs), MIT, same owner.
// The sources, their order (CPU, board, memory, GPU, storage, monitor, optical drive) and "a category that cannot be read stays empty, it is
// never an exception" are the old ones. What is new: the sources are asynchronous (the Windows ones are bounded by a deadline, see
// WindowsBuildReaders.cs), the collector is a Task, and a source that throws leaves a Trace line with the exception type only (a Windows
// message could name a machine or a path).
using System.Diagnostics;

namespace PcMieru.Checks.Build;

/// <summary>
/// Where the parts are read from, one source per category. **Tests replace them** (a source that reads nothing, or throws, must give an
/// empty category). The default is empty; <see cref="Windows"/> is this machine (WMI with a deadline, and DXGI; no administrator needed).
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
/// Collects the parts of this machine. **A category that could not be read is silently left empty** (no exception comes out; a
/// Windows reader that ran out of time is the same). Only the caller's cancellation is passed on. It is slow (WMI): not on the UI thread.
/// </summary>
public static class BuildCollector
{
    /// <summary>Reads this machine (WMI with a 5 s deadline per category, DXGI; no administrator needed).</summary>
    public static Task<BuildConfig> CollectAsync(CancellationToken ct = default) => CollectAsync(BuildSources.Windows(), ct);

    internal static async Task<BuildConfig> CollectAsync(BuildSources sources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var c = new BuildConfig();

        async Task Try(string what, Func<Task> read)
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.TraceWarning($"PCMieru: build code: could not read {what}: {ex.GetType().Name}");
            }
        }

        await Try("cpu", async () => c.Set("cpu", BuildRules.Cpus(await sources.Cpus(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("motherboard", async () =>
        {
            var (m, p) = await sources.Board(ct).ConfigureAwait(false);
            c.Set("motherboard", BuildRules.Motherboard(m, p));
        }).ConfigureAwait(false);
        await Try("ram", async () => c.Set("ram", BuildRules.Ram(await sources.Memory(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("gpu", async () => c.Set("gpu", BuildRules.Gpus(await sources.Gpus(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("storage", async () =>
        {
            var (ssd, hdd) = BuildRules.Disks(await sources.Disks(ct).ConfigureAwait(false));
            c.Set("ssd", ssd);
            c.Set("hdd", hdd);
        }).ConfigureAwait(false);
        await Try("monitor", async () => c.Set("monitor", BuildRules.Monitors(await sources.Monitors(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        await Try("optical-drive", async () => c.Set("optical-drive", BuildRules.Opticals(await sources.Opticals(ct).ConfigureAwait(false)))).ConfigureAwait(false);
        return c;
    }
}
