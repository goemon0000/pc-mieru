using System.Management;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// The real <see cref="IWmiExecutor"/>: System.Management, forward-only, with the time that is left as the timeout of the enumeration.
/// It adds no logic (the deadline between rows, the mapping and the reasons are in <see cref="ManagementWmiReader"/>); it only makes
/// sure every WMI object it touches is disposed, also when the visitor stops early.
/// <para>What the timeout does and does not bound. <c>ConnectionOptions.Timeout</c> is reserved and unused by System.Management, so it
/// is not set: the connect has no timeout of its own and runs until WMI's own maximum wait (about two minutes,
/// WBEM_FLAG_CONNECT_USE_MAX_WAIT), or longer if winmgmt hangs. The CALLER is not held by that: the lane gives it its answer at the
/// deadline. The SLOT of the lane is held until the connect returns. <c>EnumerationOptions.Timeout</c> applies to each Next call, not
/// to the whole enumeration; a Next that times out throws and cannot be resumed, so the loop is not restructured. With the deadline
/// checked between rows (in the reader), the slot can therefore be held up to "deadline + one per-Next timeout" once the connect is
/// done.</para>
/// </summary>
internal sealed class ManagementWmiExecutor : IWmiExecutor
{
    public void Run(string scope, string query, TimeSpan timeout, Func<IWmiRow, bool> visit)
    {
        // No ConnectionOptions.Timeout: System.Management reserves it and ignores it (see the class remarks).
        var managementScope = new ManagementScope(scope);
        managementScope.Connect();

        var options = new System.Management.EnumerationOptions
        {
            Timeout = timeout, // per Next call, not for the whole enumeration (see the class remarks)
            Rewindable = false, // forward only: the rows are read once, in order (this and the default ReturnImmediately keep the wait inside the enumeration, where Timeout applies)
        };

        using var searcher = new ManagementObjectSearcher(managementScope, new ObjectQuery(query), options);
        using var rows = searcher.Get();
        foreach (var item in rows)
        {
            using var row = (ManagementBaseObject)item;
            if (!visit(new ManagementRow(row)))
            {
                return;
            }
        }
    }

    private sealed class ManagementRow(ManagementBaseObject row) : IWmiRow
    {
        public object? this[string name]
        {
            get
            {
                try
                {
                    return row[name];
                }
                catch (ManagementException)
                {
                    return null; // no such property in this row
                }
            }
        }
    }
}
