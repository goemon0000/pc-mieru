using System.Runtime.InteropServices;

namespace PcMieru.Core.Management;

/// <summary>One session as the Terminal Services API reports it, before it is turned into <see cref="SessionInfo"/>.</summary>
internal readonly record struct RawSession(uint SessionId, string WindowStation, int State, string User);

/// <summary>The real <see cref="ISessionProvider"/>: WTSEnumerateSessions plus the user name of each session. Read-only.</summary>
public sealed class SessionProvider : ISessionProvider
{
    private const int WTSUserName = 5;

    private readonly Func<IReadOnlyList<RawSession>> _source;

    public SessionProvider()
        : this(ReadFromSystem)
    {
    }

    /// <summary>For tests: the raw sessions come from <paramref name="source"/>.</summary>
    internal SessionProvider(Func<IReadOnlyList<RawSession>> source) => _source = source;

    public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct = default) =>
        Task.Run(
            () => (IReadOnlyList<SessionInfo>)_source()
                .Select(Convert)
                .OrderBy(s => s.SessionId)
                .ToArray(),
            ct);

    /// <summary>WTS_CONNECTSTATE_CLASS: 0 Active, 1 Connected, 4 Disconnected, 5 Idle, anything else is Other.</summary>
    internal static SessionConnectState MapState(int state) => state switch
    {
        0 => SessionConnectState.Active,
        1 => SessionConnectState.Connected,
        4 => SessionConnectState.Disconnected,
        5 => SessionConnectState.Idle,
        _ => SessionConnectState.Other,
    };

    internal static SessionInfo Convert(RawSession raw) =>
        new(raw.SessionId, raw.User, MapState(raw.State), string.IsNullOrEmpty(raw.WindowStation) ? null : raw.WindowStation);

    private static IReadOnlyList<RawSession> ReadFromSystem()
    {
        var result = new List<RawSession>();
        if (!ManagementNative.WTSEnumerateSessions(0, 0, 1, out var buffer, out var count))
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<MgmtWtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<MgmtWtsSessionInfo>(buffer + (i * size));
                var user = Query(info.SessionId, WTSUserName);
                result.Add(new RawSession(info.SessionId, ManagementNative.ReadString(info.WinStationName), info.State, user));
            }
        }
        finally
        {
            ManagementNative.WTSFreeMemory(buffer);
        }

        return result;
    }

    private static string Query(uint sessionId, int infoClass)
    {
        if (!ManagementNative.WTSQuerySessionInformation(0, sessionId, infoClass, out var buffer, out _))
        {
            return "";
        }

        try
        {
            return ManagementNative.ReadString(buffer);
        }
        finally
        {
            ManagementNative.WTSFreeMemory(buffer);
        }
    }
}
