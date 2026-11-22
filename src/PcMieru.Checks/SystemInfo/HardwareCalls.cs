using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// The calls into the hardware provider that the system check has started and that may still be running (DESIGN-A 6.1: an abandoned
/// call must not pile up workers). One slot per category, kept by the services (<see cref="CheckServices"/>) and so shared by every run of
/// them: a category whose call of an earlier run has not returned is not asked again, because a call that cannot be stopped holds a worker
/// until Windows lets it go, and one more call of the same kind would only wait behind it with one more worker. The run that finds the
/// slot taken reports a timeout for that category at once.
/// </summary>
internal sealed class HardwareCalls
{
    private readonly object _gate = new();
    private readonly Dictionary<HardwareCategory, Task> _calls = [];

    /// <summary>
    /// Starts a call for <paramref name="category"/> with <paramref name="start"/> and remembers it, unless the call of an earlier run for the
    /// same category is still running: then nothing is started and the result is null. <paramref name="start"/> runs under a lock and must
    /// return at once (the caller gives it a <c>Task.Run</c>).
    /// </summary>
    public Task<HardwareInfo>? TryStart(HardwareCategory category, Func<Task<HardwareInfo>> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_gate)
        {
            if (_calls.TryGetValue(category, out var previous) && !previous.IsCompleted)
            {
                return null;
            }

            var call = start();
            _calls[category] = call;
            return call;
        }
    }

    /// <summary>Whether a call for this category has been started and has not finished (a test looks at it).</summary>
    public bool IsRunning(HardwareCategory category)
    {
        lock (_gate)
        {
            return _calls.TryGetValue(category, out var call) && !call.IsCompleted;
        }
    }
}
