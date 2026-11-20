using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.SystemInfo;

/// <summary>Core's <see cref="Value{T}"/> as the readings the checks use: a value that is there, or the reason it is not.</summary>
internal static class CoreReadings
{
    /// <summary>The value when Core read it; otherwise the reason (only "the machine says it does not have it" is unsupported; see <see cref="StorageJudge.ReasonOf"/>).</summary>
    public static CheckReading<T> Of<T>(Value<T> value) =>
        value.TryGet(out var data) ? CheckReading<T>.Ok(data) : CheckReading<T>.Missing(StorageJudge.ReasonOf(value.Status));

    /// <summary>A text of Core: trimmed, and a text that is blank is not a name (unsupported).</summary>
    public static CheckReading<string> Text(Value<string> value)
    {
        var reading = Of(value);
        if (!reading.HasValue)
        {
            return reading;
        }

        var trimmed = reading.Value.Trim();
        return trimmed.Length == 0 ? CheckReading<string>.Missing(CheckReason.Unsupported) : CheckReading<string>.Ok(trimmed);
    }

    public static CheckReading<TOut> Map<TIn, TOut>(this CheckReading<TIn> reading, Func<TIn, TOut> map) =>
        reading.HasValue ? CheckReading<TOut>.Ok(map(reading.Value)) : CheckReading<TOut>.Missing(reading.Reason!.Value);
}

/// <summary>
/// What the system check takes from Core's <see cref="IHardwareProvider"/>: one reading per category (the OS, the CPU, the memory, the GPUs
/// and the drives). A category that did not come is a missing reading with its reason, and the rows that rest on it are unavailable.
/// </summary>
internal sealed record HardwareFacts(
    CheckReading<OsHardware> Os,
    CheckReading<CpuHardware> Cpu,
    CheckReading<MemoryHardware> Memory,
    CheckReading<IReadOnlyList<GpuHardware>> Gpus,
    CheckReading<IReadOnlyList<StorageDevice>> Storage)
{
    /// <summary>How long the whole hardware collection may take (all categories together).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Nothing came, for one reason (the run was stopped, or the collection could not start).</summary>
    public static HardwareFacts Missing(CheckReason reason) => new(
        CheckReading<OsHardware>.Missing(reason),
        CheckReading<CpuHardware>.Missing(reason),
        CheckReading<MemoryHardware>.Missing(reason),
        CheckReading<IReadOnlyList<GpuHardware>>.Missing(reason),
        CheckReading<IReadOnlyList<StorageDevice>>.Missing(reason));

    /// <summary>
    /// Asks the provider for the five categories at once, and waits for all of them for at most <see cref="Budget"/> in all (one deadline,
    /// on the check's own clock). A category that has not answered by then is a timeout and its late answer is thrown away: the provider
    /// is asked to stop, and a call that cannot be stopped goes on in the background with nobody listening. It never throws for a
    /// category that failed (a stop by the caller is the only exception, and it is passed on).
    /// </summary>
    public static async Task<HardwareFacts> ReadAsync(IHardwareProvider hardware, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(time);
        var deadline = QueryDeadline.After(time, Budget);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var os = AskAsync(hardware, HardwareCategory.Os, deadline, cancel.Token, ct);
        var cpu = AskAsync(hardware, HardwareCategory.Cpu, deadline, cancel.Token, ct);
        var memory = AskAsync(hardware, HardwareCategory.Memory, deadline, cancel.Token, ct);
        var gpus = AskAsync(hardware, HardwareCategory.Gpu, deadline, cancel.Token, ct);
        var storage = AskAsync(hardware, HardwareCategory.Storage, deadline, cancel.Token, ct);
        try
        {
            await Task.WhenAll(os, cpu, memory, gpus, storage).ConfigureAwait(false);
        }
        finally
        {
            // Whatever has not answered by now is not wanted any more.
            await cancel.CancelAsync().ConfigureAwait(false);
        }

        return new HardwareFacts(
            Pick(os.Result, static h => h.Os),
            Pick(cpu.Result, static h => h.Cpu),
            Pick(memory.Result, static h => h.Memory),
            Pick(gpus.Result, static h => h.Gpus),
            Pick(storage.Result, static h => h.Storage));
    }

    private static CheckReading<T> Pick<T>(CheckReading<HardwareInfo> info, Func<HardwareInfo, Value<T>> category) =>
        info.HasValue ? CoreReadings.Of(category(info.Value)) : CheckReading<T>.Missing(info.Reason!.Value);

    private static async Task<CheckReading<HardwareInfo>> AskAsync(
        IHardwareProvider hardware, HardwareCategory category, QueryDeadline deadline, CancellationToken stopCall, CancellationToken ct)
    {
        // Task.Run: a provider whose call blocks before it returns a task must not hold the caller.
        var call = Task.Run(() => hardware.GetAsync(category, stopCall), CancellationToken.None);
        try
        {
            var info = await call.WaitAsync(deadline.RemainingAtLeastOneMillisecond(), deadline.Time, ct).ConfigureAwait(false);
            return CheckReading<HardwareInfo>.Ok(info);
        }
        catch (TimeoutException)
        {
            Abandon(call);
            return CheckReading<HardwareInfo>.TimedOut();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Abandon(call);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The provider's own failure (or its own cancel): this category did not come. The exception is observed by the await.
            return CheckReading<HardwareInfo>.Failed();
        }
    }

    // A call that is left running: its answer is not used, and its failure, if it fails, is not an unobserved exception.
    private static void Abandon(Task call) =>
        call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
