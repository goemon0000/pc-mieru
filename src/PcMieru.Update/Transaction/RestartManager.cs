using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PcMieru.Update.Transaction;

/// <summary>Only queries the gate file's users. Restart Manager is never asked to shut down or restart anything.</summary>
public static class RestartManager
{
    private const int MoreData = 234;

    public readonly record struct Holder(int Pid, string Name);

    public static IReadOnlyList<Holder>? ReadHolders(string gateFile)
    {
        if (RmStartSession(out var session, 0, new StringBuilder(33)) != 0)
        {
            return null;
        }

        try
        {
            if (RmRegisterResources(session, 1, [gateFile], 0, 0, 0, 0) != 0)
            {
                return null;
            }

            uint count = 0;
            uint reasons = 0;
            ProcessInfo[]? rows = null;
            // A process can enter or leave between sizing the array and filling it.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var error = RmGetList(session, out var needed, ref count, rows, ref reasons);
                if (error == 0)
                {
                    return rows is null ? [] : rows.Take((int)count).Select(row => new Holder(row.Process.Pid, NameOf(row))).ToArray();
                }

                if (error != MoreData || needed is 0 or > 4096)
                {
                    return null;
                }

                count = needed;
                rows = new ProcessInfo[count];
            }

            return null;
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static string NameOf(ProcessInfo row)
    {
        try
        {
            using var process = Process.GetProcessById(row.Process.Pid);
            var started = ((long)(uint)row.Process.Start.dwHighDateTime << 32) | (uint)row.Process.Start.dwLowDateTime;
            if (process.StartTime.ToFileTimeUtc() == started)
            {
                return process.ProcessName;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The holder exited or its image could not be read. Use the name Restart Manager supplied.
        }

        return Path.GetFileNameWithoutExtension(row.Application);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int Pid;
        public FILETIME Start;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Application;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint files, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] paths,
        uint applications, nint processes, uint services, nint names);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? rows, ref uint reasons);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
