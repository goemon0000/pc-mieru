// Provenance: the three wevtapi calls (EvtQuery, EvtNext, EvtClose) and the batch loop around them are those of the owner's parts-life
// EventCounts (src/PartsLife.Checks/SystemInfo/EventCounts.cs), MIT, same owner, here as LibraryImport behind IEventLogApi.

using System.Runtime.InteropServices;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// The real <see cref="IEventLogApi"/>: wevtapi.dll from System32, nothing else. It adds no logic; the rules (deadline, closing,
/// what counts as a count) are in <see cref="WevtEventCounter"/>. The System log is readable without administrator rights.
/// </summary>
internal sealed partial class NativeEventLogApi : IEventLogApi
{
    public int Query(string channel, string xpath, int flags, out nint resultSet)
    {
        resultSet = EvtQuery(0, channel, xpath, flags);
        return resultSet != 0 ? 0 : LastErrorOrFailure();
    }

    public int Next(nint resultSet, int timeoutMs, Span<nint> events, out int returned)
    {
        if (EvtNext(resultSet, events.Length, ref MemoryMarshal.GetReference(events), timeoutMs, 0, out var got))
        {
            returned = got;
            return 0;
        }

        var error = LastErrorOrFailure();
        returned = 0; // the old code looked at the handles only of a call that succeeded
        return error;
    }

    public void Close(nint handle)
    {
        if (handle != 0)
        {
            _ = EvtClose(handle);
        }
    }

    // A failed call with no error code (never seen) must not read as success.
    private static int LastErrorOrFailure()
    {
        var error = Marshal.GetLastPInvokeError();
        return error != 0 ? error : -1;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wevtapi.dll", EntryPoint = "EvtQuery", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint EvtQuery(nint session, string path, string query, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wevtapi.dll", EntryPoint = "EvtNext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(nint resultSet, int eventsSize, ref nint events, int timeout, int flags, out int returned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wevtapi.dll", EntryPoint = "EvtClose")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
