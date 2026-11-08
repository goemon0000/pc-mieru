using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>What the registry says about the installed Windows. Each field is null when the value is not there.</summary>
internal sealed record RawOsRegistry(string? ProductName, string? EditionId, string? InstallationType, string? DisplayVersion, string? ReleaseId, int? Ubr);

/// <summary>
/// Windows version, edition, build revision, uptime, power plan and whether virtualization-based security is running. The version comes
/// from RtlGetVersion (GetVersionEx lies to apps without a manifest) and the security state from Win32_DeviceGuard, which is read once per refresh.
/// </summary>
internal static class OsCollector
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(10);

    /// <param name="deviceGuard">The one WMI read for this refresh (see <see cref="ReadDeviceGuard"/>).</param>
    public static Value<OsHardware> Collect(Func<(DeviceGuardState? State, int HResult)> deviceGuard, DateTime nowUtc)
    {
        try
        {
            var version = new Ntdll.OsVersionInfo { Size = (uint)Marshal.SizeOf<Ntdll.OsVersionInfo>() };
            if (Ntdll.RtlGetVersion(ref version) != 0)
            {
                return Value.Failed<OsHardware>("ntdll:RtlGetVersion", "Windows version could not be read");
            }

            var registry = ReadRegistry();
            var (vbs, hvci) = OsBuilder.Security(deviceGuard());
            var uptimeSec = (long)(Kernel32.GetTickCount64() / 1000);
            return Value.Ok(OsBuilder.Build(version.Major, version.Minor, version.Build, registry, RuntimeInformation.OSArchitecture, uptimeSec, nowUtc, ReadPowerPlan(), Locale(), vbs, hvci), "composite:os");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<OsHardware>(ex, "composite:os");
        }
    }

    /// <summary>Reads Win32_DeviceGuard with a time limit. A WMI service that hangs becomes a Timeout, not a hang of the whole page.</summary>
    public static (DeviceGuardState? State, int HResult) ReadDeviceGuard()
    {
        var task = Task.Run(() =>
        {
            var state = Wmi.ReadDeviceGuard(out var hr);
            return (state, hr);
        });
        return task.Wait(WmiTimeout) ? task.Result : (null, int.MinValue);
    }

    private static RawOsRegistry ReadRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            if (key is null)
            {
                return new RawOsRegistry(null, null, null, null, null, null);
            }

            return new RawOsRegistry(
                Text(key.GetValue("ProductName")),
                Text(key.GetValue("EditionID")),
                Text(key.GetValue("InstallationType")),
                Text(key.GetValue("DisplayVersion")),
                Text(key.GetValue("ReleaseId")),
                key.GetValue("UBR") is int ubr && ubr >= 0 ? ubr : null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new RawOsRegistry(null, null, null, null, null, null);
        }

        static string? Text(object? value) => value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    }

    private static Value<string> ReadPowerPlan()
    {
        const string source = "powrprof:PowerGetActiveScheme";
        try
        {
            if (PowrProf.ReadActive(out var error) is not { } plan)
            {
                return Probe.FromWin32<string>((int)error, source, "active power plan");
            }

            var name = plan.Name ?? OsBuilder.PowerPlanName(plan.Scheme);
            return name is null
                ? Value.Unsupported<string>(source, "Windows gave this power plan no name")
                : Value.Ok(name, source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<string>(ex, source);
        }
    }

    private static Value<string> Locale()
    {
        const string source = "kernel32:GetUserDefaultLocaleName";
        var buffer = new char[85];
        var n = Kernel32.GetUserDefaultLocaleName(buffer, buffer.Length);
        return n > 1
            ? Probe.Note(new string(buffer, 0, n - 1), source, "regional format of the signed-in user")
            : Value.Unsupported<string>(source, "Windows gave no locale");
    }
}

/// <summary>The pure part of the OS collector. Unit tested with hand-made readings.</summary>
internal static class OsBuilder
{
    private const string VersionSrc = "ntdll:RtlGetVersion";
    private const string RegSrc = "registry:HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    private const string GuardSrc = "wmi:Win32_DeviceGuard";

    public static OsHardware Build(
        uint major,
        uint minor,
        uint build,
        RawOsRegistry reg,
        Architecture architecture,
        long uptimeSec,
        DateTime nowUtc,
        Value<string> powerPlan,
        Value<string> locale,
        Value<bool> vbs,
        Value<bool> hvci)
    {
        var display = !string.IsNullOrWhiteSpace(reg.DisplayVersion) ? reg.DisplayVersion : reg.ReleaseId;
        return new OsHardware(
            Value.Ok(WindowsName(major, minor, build, reg.EditionId, reg.ProductName, reg.InstallationType), VersionSrc + " + registry"),
            Probe.Text(RegSrc + "\\DisplayVersion", display, "Windows does not report a feature-update name"),
            Value.Ok($"{major}.{minor}.{build}", VersionSrc),
            ArchitectureName(architecture) is { } arch
                ? Value.Ok(arch, "runtime:RuntimeInformation.OSArchitecture")
                : Value.Unsupported<string>("runtime:RuntimeInformation.OSArchitecture", "unknown processor architecture"),
            uptimeSec < 0
                ? Value.Unsupported<long>("kernel32:GetTickCount64", "uptime not available")
                : Probe.Note(uptimeSec, "kernel32:GetTickCount64", "since Windows started; with Fast Startup a shutdown does not reset it"),
            powerPlan,
            vbs,
            hvci,
            new OsDetails(
                Probe.Text(RegSrc + "\\EditionID", reg.EditionId, "Windows does not report an edition id"),
                Probe.Text(RegSrc + "\\DisplayVersion", display, "Windows does not report a feature-update name"),
                reg.Ubr is { } ubr ? Value.Ok(ubr, RegSrc + "\\UBR") : Value.Unsupported<int>(RegSrc + "\\UBR", "build revision not reported"),
                uptimeSec < 0
                    ? Value.Unsupported<string>("kernel32:GetTickCount64", "uptime not available")
                    : Value.Ok(BootTime(nowUtc, uptimeSec), "kernel32:GetTickCount64"),
                locale));
    }

    /// <summary>
    /// "Windows 11 Pro". The registry ProductName still says "Windows 10" on Windows 11, so the number comes from the build (22000 and up
    /// is Windows 11) and the edition from EditionID. Servers keep the name Windows gives them.
    /// </summary>
    public static string WindowsName(uint major, uint minor, uint build, string? editionId, string? productName, string? installationType)
    {
        var server = string.Equals(installationType, "Server", StringComparison.OrdinalIgnoreCase)
            || (editionId?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) ?? false);
        if (server && !string.IsNullOrWhiteSpace(productName))
        {
            return productName!;
        }

        var family = major == 10 ? (build >= 22000 ? "Windows 11" : "Windows 10") : $"Windows {major}.{minor}";
        var edition = EditionName(editionId) ?? EditionFromProductName(productName);
        return edition is null ? family : $"{family} {edition}";
    }

    public static string? EditionName(string? editionId) => editionId switch
    {
        "Professional" => "Pro",
        "ProfessionalN" => "Pro N",
        "ProfessionalWorkstation" => "Pro for Workstations",
        "ProfessionalWorkstationN" => "Pro N for Workstations",
        "ProfessionalEducation" => "Pro Education",
        "ProfessionalEducationN" => "Pro Education N",
        "Core" => "Home",
        "CoreN" => "Home N",
        "CoreSingleLanguage" => "Home Single Language",
        "CoreCountrySpecific" => "Home China",
        "Enterprise" => "Enterprise",
        "EnterpriseN" => "Enterprise N",
        "EnterpriseS" => "Enterprise LTSC",
        "IoTEnterprise" => "IoT Enterprise",
        "IoTEnterpriseS" => "IoT Enterprise LTSC",
        "Education" => "Education",
        "EducationN" => "Education N",
        _ => null,
    };

    private static string? EditionFromProductName(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        foreach (var prefix in new[] { "Windows 11 ", "Windows 10 " })
        {
            if (productName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = productName[prefix.Length..].Trim();
                return rest.Length == 0 ? null : rest;
            }
        }

        return null;
    }

    public static string? ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "Arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "Arm",
        _ => null,
    };

    /// <summary>The built-in Windows plans by GUID, used only when Windows returns no name for the active plan.</summary>
    public static string? PowerPlanName(Guid scheme)
    {
        if (scheme == new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"))
        {
            return "Balanced";
        }

        if (scheme == new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"))
        {
            return "High performance";
        }

        if (scheme == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"))
        {
            return "Power saver";
        }

        return scheme == new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61") ? "Ultimate Performance" : null;
    }

    /// <summary>UTC start time to the second, as "2026-11-08T01:02:03Z".</summary>
    public static string BootTime(DateTime nowUtc, long uptimeSec)
    {
        var boot = nowUtc.AddSeconds(-uptimeSec);
        return new DateTime(boot.Year, boot.Month, boot.Day, boot.Hour, boot.Minute, boot.Second, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Virtualization-based security and memory integrity (HVCI) from Win32_DeviceGuard. "Running" is what counts: a feature that is only
    /// switched on in settings but not active is reported as off, with a note.
    /// </summary>
    public static (Value<bool> Vbs, Value<bool> Hvci) Security((DeviceGuardState? State, int HResult) read)
    {
        if (read.State is not { } state)
        {
            var detail = read.HResult switch
            {
                int.MinValue => Value.Missing<bool>(ValueStatus.Timeout, GuardSrc, "Windows management did not answer in time"),
                Wmi.AccessDenied => Value.Denied<bool>(GuardSrc, "Windows does not let this account read the security state"),
                Wmi.InvalidNamespace or Wmi.InvalidClass or Wmi.ClassNotRegistered =>
                    Value.Unsupported<bool>(GuardSrc, "this Windows edition does not report the security state"),
                _ => Value.Failed<bool>(GuardSrc, $"security state could not be read (HRESULT 0x{read.HResult:X8})"),
            };
            return (detail, detail);
        }

        var vbs = state.VbsStatus switch
        {
            2 => Value.Ok(true, GuardSrc),
            0 => Value.Ok(false, GuardSrc),
            1 => Probe.Note(false, GuardSrc, "turned on but not running; a restart or supported hardware is missing"),
            _ => Value.Unsupported<bool>(GuardSrc, "unknown status " + state.VbsStatus.ToString(CultureInfo.InvariantCulture)),
        };

        // Service 2 = hypervisor-protected code integrity (memory integrity).
        var hvci = state.VbsStatus is 0 or 1 or 2
            ? Value.Ok(state.VbsStatus == 2 && state.ServicesRunning.Contains(2), GuardSrc)
            : Value.Unsupported<bool>(GuardSrc, "unknown status " + state.VbsStatus.ToString(CultureInfo.InvariantCulture));
        return (vbs, hvci);
    }
}
