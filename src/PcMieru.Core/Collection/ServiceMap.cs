using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>
/// Which Windows services run inside which process (svchost.exe hosts many). One EnumServicesStatusEx call lists every
/// running service with its PID, so the answer for any process comes from one cached table, refreshed at most every few
/// seconds. Needs no elevation: the service manager lets any user enumerate.
/// </summary>
internal sealed unsafe class ServiceMap
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceActive = 0x1;
    private const int ScEnumProcessInfo = 0;
    private const uint ErrorMoreData = 234;
    private const int MaxServices = 4096;
    private const int MaxBuffer = 4 << 20;

    // ENUM_SERVICE_STATUS_PROCESSW on x64: two string pointers, then SERVICE_STATUS_PROCESS (nine DWORDs, PID is the eighth).
    private const int EntrySize = 56;
    private const int PidOffset = 16 + 28;

    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(5);

    private readonly Func<IReadOnlyDictionary<uint, string[]>?> _load;
    private readonly TimeSpan _maxAge;
    private readonly object _lock = new();
    private IReadOnlyDictionary<uint, string[]> _table = new Dictionary<uint, string[]>();
    private long _loadedAt = long.MinValue;
    private int _loads;

    public ServiceMap()
        : this(LoadFromServiceManager, DefaultMaxAge)
    {
    }

    internal ServiceMap(Func<IReadOnlyDictionary<uint, string[]>?> load, TimeSpan maxAge)
    {
        _load = load;
        _maxAge = maxAge;
    }

    /// <summary>How many times the service manager was asked (for tests and the cost report).</summary>
    public int Loads => Volatile.Read(ref _loads);

    /// <summary>Service names hosted by this PID; empty when none, or when the service manager cannot be read.</summary>
    public IReadOnlyList<string> NamesOf(uint pid)
    {
        if (pid == 0)
        {
            return [];
        }

        IReadOnlyDictionary<uint, string[]> table;
        lock (_lock)
        {
            if (_loadedAt == long.MinValue || Stopwatch.GetElapsedTime(_loadedAt) > _maxAge)
            {
                var fresh = _load();
                Interlocked.Increment(ref _loads);

                // A failed refresh keeps the previous table (and tries again next time) rather than saying "no services".
                if (fresh is not null)
                {
                    _table = fresh;
                }

                _loadedAt = Stopwatch.GetTimestamp();
            }

            table = _table;
        }

        return table.TryGetValue(pid, out var names) ? names : [];
    }

    private static IReadOnlyDictionary<uint, string[]>? LoadFromServiceManager()
    {
        var manager = Win32.OpenSCManager(0, 0, ScManagerEnumerateService);
        if (manager == 0)
        {
            return null;
        }

        try
        {
            var byPid = new Dictionary<uint, List<string>>();
            var buffer = new byte[64 * 1024];
            uint resume = 0;
            var total = 0;
            for (var round = 0; round < 32; round++)
            {
                bool ok;
                uint needed;
                uint returned;
                fixed (byte* p = buffer)
                {
                    ok = Win32.EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive, p, (uint)buffer.Length, out needed, out returned, ref resume, 0);
                    var error = ok ? 0u : (uint)Marshal.GetLastPInvokeError();
                    if (!ok && error != ErrorMoreData)
                    {
                        return null;
                    }

                    var limit = Math.Min(returned, (uint)((buffer.Length) / EntrySize));
                    for (var i = 0u; i < limit && total < MaxServices; i++, total++)
                    {
                        var entry = p + (int)i * EntrySize;
                        var name = *(char**)entry;
                        var pid = *(uint*)(entry + PidOffset);
                        if (pid == 0 || name is null || (byte*)name < p || (byte*)name >= p + buffer.Length)
                        {
                            continue;
                        }

                        var text = ReadString(name, p + buffer.Length);
                        if (text is null)
                        {
                            continue;
                        }

                        if (!byPid.TryGetValue(pid, out var list))
                        {
                            list = [];
                            byPid[pid] = list;
                        }

                        list.Add(text);
                    }
                }

                if (ok || total >= MaxServices)
                {
                    break;
                }

                // More entries than fit: continue from the resume handle, in a buffer big enough for the rest when the API said how much.
                if (needed > buffer.Length && buffer.Length < MaxBuffer)
                {
                    buffer = new byte[Math.Min((int)needed + 4096, MaxBuffer)];
                }
            }

            var result = new Dictionary<uint, string[]>(byPid.Count);
            foreach (var (pid, names) in byPid)
            {
                names.Sort(StringComparer.OrdinalIgnoreCase);
                result[pid] = [.. names];
            }

            return result;
        }
        finally
        {
            _ = Win32.CloseServiceHandle(manager);
        }
    }

    /// <summary>A NUL-terminated name that must end inside the buffer (service names are at most 256 characters).</summary>
    private static string? ReadString(char* start, byte* end)
    {
        var max = (int)Math.Min((end - (byte*)start) / 2, 300);
        for (var i = 0; i < max; i++)
        {
            if (start[i] == '\0')
            {
                return i == 0 ? null : new string(start, 0, i);
            }
        }

        return null;
    }
}
