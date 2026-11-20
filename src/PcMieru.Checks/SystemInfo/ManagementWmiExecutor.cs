using System.Management;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// The real <see cref="IWmiExecutor"/>: System.Management with the time that is left as the timeout of the connection and of the
/// enumeration. It adds no logic (the deadline between rows, the mapping and the reasons are in <see cref="ManagementWmiReader"/>); it
/// only makes sure every WMI object it touches is disposed, also when the visitor stops early.
/// </summary>
internal sealed class ManagementWmiExecutor : IWmiExecutor
{
    public void Run(string scope, string query, TimeSpan timeout, Func<IWmiRow, bool> visit)
    {
        var connection = new ConnectionOptions { Timeout = timeout };
        var managementScope = new ManagementScope(scope, connection);
        managementScope.Connect();

        var options = new System.Management.EnumerationOptions
        {
            Timeout = timeout,
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
