using PcMieru.Wire;

namespace PcMieru.NetMeter;

/// <summary>
/// Opens a process with the least right that answers "when was it created, and has it exited" and keeps that handle for as
/// long as the counters hold the entry: while a handle to a process is open its pid cannot be given to a new process, so the
/// (pid, creation time) key stays true for the whole life of the entry.
/// </summary>
internal sealed unsafe class ProcessResolver : INetMeterProcessResolver
{
    public INetMeterProcessPin? Pin(uint pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        if (!TryTimes(handle, out var created, out var exited) || created == 0 || exited != 0)
        {
            handle.Dispose();
            return null;
        }

        return new OpenProcessPin(handle, created);
    }

    internal static bool TryTimes(SafeKernelHandle handle, out long created, out long exited)
    {
        long creation = 0, exit = 0, kernel = 0, user = 0;
        var ok = Native.GetProcessTimes(handle, &creation, &exit, &kernel, &user);
        created = creation;
        exited = exit;
        return ok;
    }

    private sealed class OpenProcessPin(SafeKernelHandle handle, long created) : INetMeterProcessPin
    {
        public long CreationFileTime { get; } = created;

        // GetProcessTimes reports a non-zero exit time once the process has ended. (The exit code would be ambiguous: a
        // process may exit with 259, the value STILL_ACTIVE.)
        public bool HasExited => !TryTimes(handle, out _, out var exited) || exited != 0;

        public void Dispose() => handle.Dispose();
    }
}
