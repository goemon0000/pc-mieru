using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PcMieru.Party.Import;

/// <summary>What the probe could tell about the old Parts Life.</summary>
public enum LegacyRunningState
{
    NotRunning,
    Running,

    /// <summary>It could not be told (the owner of a candidate process cannot be read, the mutex cannot be looked at). Treated like <see cref="Running"/>: hold, never import.</summary>
    Unknown,
}

/// <summary>Is the old Parts Life of this user running? Asked before the consent and again just before the commit (DESIGN-C 4.1 item 3).</summary>
public interface ILegacyRunningProbe
{
    LegacyRunningState Check();
}

/// <summary>A process the probe looks at. <paramref name="OwnerSid"/> is null when the owner could not be read.</summary>
public readonly record struct LegacyProcess(int Pid, string Name, string? OwnerSid);

/// <summary>What was found of the shared resident mutex (looked at, never taken).</summary>
public enum ResidentMutexState
{
    Absent,
    Held,
    Unknown,
}

/// <summary>
/// The real probe (DESIGN-C 4.1 item 3). It never ends a process and never takes a mutex.
///
/// Running when either of these is true:
///  - a process named <c>PartsLife</c> or <c>PartsParty</c> (v0.7.4 and older; v0.8.0 too) runs as the SAME user (SID), in any session;
///  - the resident mutex that v0.8.0 shares with Party is held AND no <c>PCMieru.Party</c> of this user runs (so the holder is the old
///    product, not our own Party, which holds the same mutex).
/// Unknown when no Running is found but the owner of a candidate process cannot be read (another user's process, an elevated one), or the
/// mutex cannot be looked at. A process of another user whose owner CAN be read is not ours: ignored.
///
/// "Held" is read as "the mutex object exists": looking at it must not take it even for an instant, or a Party that starts at that moment
/// would find a rival. The object lives as long as any process keeps a handle, and the resident keeps its handle for its whole life; the only
/// exceptions are the few microseconds of its own take and give-back.
///
/// An old product that starts and ends between two checks cannot be seen: the file-side checks of the engine (identity, size, SHA-256) are the
/// other half.
/// </summary>
public sealed class WindowsLegacyRunningProbe : ILegacyRunningProbe
{
    /// <summary>The names (without .exe) of the old product: Parts Life, and its older name.</summary>
    internal static readonly string[] LegacyNames = ["PartsLife", "PartsParty"];

    /// <summary>The new window (without .exe).</summary>
    internal const string PartyName = "PCMieru.Party";

    private readonly Func<IReadOnlyList<LegacyProcess>> _list;
    private readonly Func<ResidentMutexState> _mutex;
    private readonly string _userSid;

    public WindowsLegacyRunningProbe() : this(PartyMutexNames.CurrentSid())
    {
    }

    private WindowsLegacyRunningProbe(string userSid)
        : this(ListProcesses, () => MutexPresence(PartyMutexNames.ResidentNameFor(userSid)), userSid)
    {
    }

    /// <summary>Tests: the process list and the mutex look-up are given.</summary>
    internal WindowsLegacyRunningProbe(Func<IReadOnlyList<LegacyProcess>> list, Func<ResidentMutexState> mutex, string userSid)
    {
        _list = list;
        _mutex = mutex;
        _userSid = userSid;
    }

    public LegacyRunningState Check()
    {
        try
        {
            return Decide(_list(), _mutex(), _userSid);
        }
        catch (Exception)
        {
            return LegacyRunningState.Unknown;
        }
    }

    /// <summary>The decision itself: pure, so that every case is a test.</summary>
    internal static LegacyRunningState Decide(IReadOnlyList<LegacyProcess> processes, ResidentMutexState mutex, string userSid)
    {
        bool unknown = false;

        foreach (var p in processes)
        {
            if (!IsLegacy(p.Name)) continue;
            if (p.OwnerSid is null) unknown = true;
            else if (SameSid(p.OwnerSid, userSid)) return LegacyRunningState.Running;
        }

        switch (mutex)
        {
            case ResidentMutexState.Unknown:
                unknown = true;
                break;
            case ResidentMutexState.Held:
                bool partyRunning = false, partyUnknown = false;
                foreach (var p in processes)
                {
                    if (!string.Equals(p.Name, PartyName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.OwnerSid is null) partyUnknown = true;
                    else if (SameSid(p.OwnerSid, userSid)) partyRunning = true;
                }

                if (!partyRunning)
                {
                    // Held, and the holder is not a Party of this user. If a Party's owner could not be read, it may be the holder: say so.
                    if (partyUnknown) unknown = true;
                    else return LegacyRunningState.Running;
                }

                break;
        }

        return unknown ? LegacyRunningState.Unknown : LegacyRunningState.NotRunning;
    }

    private static bool IsLegacy(string name)
    {
        foreach (string legacy in LegacyNames)
            if (string.Equals(name, legacy, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool SameSid(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every process of the three names, in every session, with the SID of its owner (null when it cannot be read).</summary>
    internal static IReadOnlyList<LegacyProcess> ListProcesses()
    {
        var found = new List<LegacyProcess>();
        string[] names = [.. LegacyNames, PartyName];
        foreach (string name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                    found.Add(new LegacyProcess(process.Id, name, ProcessOwner.TrySid(process.Id)));
            }
        }

        return found;
    }

    /// <summary>Looks at the mutex without taking it. Not openable (another owner, another kind of object) is Unknown.</summary>
    internal static ResidentMutexState MutexPresence(string name)
    {
        try
        {
            if (MutexAcl.TryOpenExisting(name, MutexRights.Synchronize, out Mutex? mutex))
            {
                mutex!.Dispose();
                return ResidentMutexState.Held;
            }

            return ResidentMutexState.Absent;
        }
        catch (Exception)
        {
            return ResidentMutexState.Unknown;
        }
    }
}

/// <summary>The SID of the user a process runs as (OpenProcess, OpenProcessToken, GetTokenInformation).</summary>
internal static partial class ProcessOwner
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    /// <summary>Null when the owner cannot be read (access denied, the process ended, anything else).</summary>
    internal static string? TrySid(int pid)
    {
        nint process = 0, token = 0;
        try
        {
            process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (process == 0) return null;
            if (!OpenProcessToken(process, TokenQuery, out token)) return null;

            _ = GetTokenInformation(token, TokenUser, 0, 0, out uint needed);   // fails with "buffer too small" and says how much
            if (needed == 0 || needed > 4096) return null;

            nint buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;   // TOKEN_USER starts with the SID pointer
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (token != 0) CloseHandle(token);
            if (process != 0) CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, nint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
