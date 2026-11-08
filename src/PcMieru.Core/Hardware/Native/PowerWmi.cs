using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace PcMieru.Core.Hardware.Native;

/// <summary>powrprof: the active power plan. Read only; nothing is changed.</summary>
internal static partial class PowrProf
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr setting, byte[]? buffer, ref uint bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>The active plan's GUID and its name as Windows shows it (localised). Name is null when Windows gives none. Null result with the error code on failure.</summary>
    public static (Guid Scheme, string? Name)? ReadActive(out uint error)
    {
        error = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (error != 0 || ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(ptr);
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size < 4 || size > 4096)
            {
                return (scheme, null);
            }

            var buffer = new byte[size];
            var rc = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
            var name = rc == 0 ? Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim() : null;
            return (scheme, string.IsNullOrEmpty(name) ? null : name);
        }
        finally
        {
            LocalFree(ptr);
        }
    }
}

/// <summary>What the Windows security features report as running (Win32_DeviceGuard).</summary>
internal sealed record DeviceGuardState(int VbsStatus, IReadOnlyList<int> ServicesRunning, IReadOnlyList<int> ServicesConfigured);

/// <summary>
/// One WMI read, late bound through the scripting COM object so Core needs no extra package. Win32_DeviceGuard can be read by a standard user.
/// Called at most once per hardware refresh and never in a loop.
/// </summary>
internal static class Wmi
{
    public const int AccessDenied = unchecked((int)0x80041003);
    public const int InvalidNamespace = unchecked((int)0x8004100E);
    public const int InvalidClass = unchecked((int)0x80041010);
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <summary>Null with an HRESULT when the class cannot be read.</summary>
    public static DeviceGuardState? ReadDeviceGuard(out int hresult)
    {
        hresult = 0;
        object? locator = null;
        object? service = null;
        object? set = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null)
            {
                hresult = ClassNotRegistered;
                return null;
            }

            locator = Activator.CreateInstance(type);
            dynamic loc = locator!;
            service = loc.ConnectServer(".", @"root\Microsoft\Windows\DeviceGuard");
            dynamic svc = service!;
            // 0x10 = return immediately. (Forward-only, 0x20, hands back objects with no properties through this late-bound path.)
            set = svc.ExecQuery("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning, SecurityServicesConfigured FROM Win32_DeviceGuard", "WQL", 0x10);
            foreach (dynamic item in (IEnumerable)set!)
            {
                // WMI properties are reached by name through Properties_; they are not visible as members of the COM object.
                object? status = item.Properties_.Item("VirtualizationBasedSecurityStatus").Value;
                object? running = item.Properties_.Item("SecurityServicesRunning").Value;
                object? configured = item.Properties_.Item("SecurityServicesConfigured").Value;
                var state = new DeviceGuardState(status is null ? -1 : Convert.ToInt32(status, System.Globalization.CultureInfo.InvariantCulture), IntList(running), IntList(configured));
                if (item is not null)
                {
                    Marshal.FinalReleaseComObject(item);
                }

                return state;
            }

            hresult = InvalidClass;
            return null;
        }
        catch (COMException ex)
        {
            hresult = ex.HResult;
            return null;
        }
        catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or FormatException)
        {
            // A property the class does not have on this Windows build.
            hresult = InvalidClass;
            return null;
        }
        finally
        {
            foreach (var o in new[] { set, service, locator })
            {
                if (o is not null && Marshal.IsComObject(o))
                {
                    Marshal.FinalReleaseComObject(o);
                }
            }
        }
    }

    private static int[] IntList(object? value) =>
        value is Array array
            ? array.Cast<object>().Select(x => Convert.ToInt32(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : [];
}
