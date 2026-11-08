using System.Runtime.InteropServices;

namespace PcMieru.Wire;

/// <summary>
/// The local service control manager, service names only. It never changes a start type, never deletes, never
/// pauses: <see cref="IServiceControlApi"/> has no such member.
/// </summary>
public sealed unsafe class ScmServiceApi : IServiceControlApi
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceEnumerateDependents = 0x0008;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceControlStop = 1;
    private const uint ServiceActive = 1;

    public int Query(string name, out ServiceSnapshot snapshot)
    {
        snapshot = default;
        var error = OpenService(name, ServiceQueryStatus, out var manager, out var service);
        if (error != 0)
        {
            return error;
        }

        using (manager)
        using (service)
        {
            if (!Native.QueryServiceStatusEx(service!, ScStatusProcessInfo, out var status, (uint)sizeof(ServiceStatusProcess), out _))
            {
                return Marshal.GetLastPInvokeError();
            }

            snapshot = new ServiceSnapshot(
                status.CurrentState, status.CheckPoint, status.WaitHint, status.ProcessId, status.ControlsAccepted, status.Win32ExitCode);
            return 0;
        }
    }

    public int Start(string name)
    {
        var error = OpenService(name, ServiceStart | ServiceQueryStatus, out var manager, out var service);
        if (error != 0)
        {
            return error;
        }

        using (manager)
        using (service)
        {
            return Native.StartService(service!, 0, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public int Stop(string name)
    {
        var error = OpenService(name, ServiceStop | ServiceQueryStatus, out var manager, out var service);
        if (error != 0)
        {
            return error;
        }

        using (manager)
        using (service)
        {
            return Native.ControlService(service!, ServiceControlStop, out _) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public int RunningDependents(string name, out IReadOnlyList<string> dependents)
    {
        dependents = [];
        var error = OpenService(name, ServiceEnumerateDependents, out var manager, out var service);
        if (error != 0)
        {
            return error;
        }

        using (manager)
        using (service)
        {
            if (Native.EnumDependentServices(service!, ServiceActive, null, 0, out var needed, out _))
            {
                return 0;
            }

            error = Marshal.GetLastPInvokeError();
            if (error != Native.ERROR_MORE_DATA)
            {
                return error;
            }

            var buffer = new byte[needed + 256];
            fixed (byte* p = buffer)
            {
                if (!Native.EnumDependentServices(service!, ServiceActive, p, (uint)buffer.Length, out _, out var count))
                {
                    return Marshal.GetLastPInvokeError();
                }

                var names = new List<string>((int)count);
                var entries = (EnumServiceStatus*)p;
                for (var i = 0; i < count; i++)
                {
                    var text = Marshal.PtrToStringUni(entries[i].ServiceName);
                    if (!string.IsNullOrEmpty(text))
                    {
                        names.Add(text);
                    }
                }

                dependents = names;
                return 0;
            }
        }
    }

    private static int OpenService(string name, uint access, out SafeScmHandle manager, out SafeScmHandle? service)
    {
        service = null;
        manager = Native.OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        service = Native.OpenService(manager, name, access);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            service.Dispose();
            service = null;
            manager.Dispose();
            return error;
        }

        return 0;
    }
}
