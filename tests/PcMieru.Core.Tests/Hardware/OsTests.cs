using System.Runtime.InteropServices;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Tests.Hardware;

public class OsTests
{
    private static readonly RawOsRegistry Reg = new("Windows 10 Pro", "Professional", "Client", "25H2", "2009", 1234);
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 30, DateTimeKind.Utc);

    private static OsHardware Build(RawOsRegistry? reg = null, long uptime = 93_784, Architecture arch = Architecture.X64, Value<bool>? vbs = null, Value<bool>? hvci = null) =>
        OsBuilder.Build(10, 0, 26300, reg ?? Reg, arch, uptime, Now, Value.Ok("Balanced", "t"), Value.Ok("ja-JP", "t"), vbs ?? Value.Ok(true, "t"), hvci ?? Value.Ok(true, "t"));

    [Theory]
    [InlineData(10u, 0u, 26300u, "Professional", "Windows 10 Pro", "Client", "Windows 11 Pro")]
    [InlineData(10u, 0u, 22000u, "Core", "Windows 10 Home", "Client", "Windows 11 Home")]
    [InlineData(10u, 0u, 19045u, "Professional", "Windows 10 Pro", "Client", "Windows 10 Pro")]
    [InlineData(10u, 0u, 26100u, "IoTEnterpriseS", "Windows 10 IoT Enterprise LTSC", "Client", "Windows 11 IoT Enterprise LTSC")]
    [InlineData(10u, 0u, 26100u, "ProfessionalWorkstation", null, null, "Windows 11 Pro for Workstations")]
    public void NameUsesTheBuildForTheNumberAndEditionIdForTheEdition(uint major, uint minor, uint build, string? editionId, string? product, string? install, string expected) =>
        Assert.Equal(expected, OsBuilder.WindowsName(major, minor, build, editionId, product, install));

    [Fact]
    public void ServerKeepsTheNameWindowsGivesIt() =>
        Assert.Equal("Windows Server 2025 Standard", OsBuilder.WindowsName(10, 0, 26100, "ServerStandard", "Windows Server 2025 Standard", "Server"));

    [Fact]
    public void UnknownEditionIdFallsBackToTheProductNameSuffix() =>
        Assert.Equal("Windows 11 Cloud Special", OsBuilder.WindowsName(10, 0, 26100, "SomethingNew", "Windows 10 Cloud Special", "Client"));

    [Fact]
    public void NoEditionAtAllGivesJustTheFamily() =>
        Assert.Equal("Windows 11", OsBuilder.WindowsName(10, 0, 26100, null, null, null));

    [Fact]
    public void OtherMajorVersionsAreNotCalledWindows11() =>
        Assert.Equal("Windows 6.3 Pro", OsBuilder.WindowsName(6, 3, 9600, "Professional", null, null));

    [Fact]
    public void VersionBuildArchitectureAndDetailsAreFilled()
    {
        var os = Build();
        Assert.Equal("Windows 11 Pro", os.Name.Data);
        Assert.Equal("25H2", os.Version.Data);
        Assert.Equal("10.0.26300", os.Build.Data);
        Assert.Equal("x64", os.Architecture.Data);
        Assert.Equal(93_784L, os.UptimeSec.Data);
        Assert.Equal("Balanced", os.PowerPlan.Data);
        Assert.Equal("Professional", os.Details!.Edition.Data);
        Assert.Equal(1234, os.Details.Ubr.Data);
        Assert.Equal("ja-JP", os.Details.Locale.Data);
    }

    [Fact]
    public void ReleaseIdIsUsedWhenThereIsNoDisplayVersion()
    {
        var os = Build(new RawOsRegistry("Windows 10 Pro", "Professional", "Client", null, "2009", null));
        Assert.Equal("2009", os.Version.Data);
        Assert.Equal(ValueStatus.Unsupported, os.Details!.Ubr.Status);
    }

    [Fact]
    public void MissingRegistryValuesAreUnsupportedNotEmpty()
    {
        var os = Build(new RawOsRegistry(null, null, null, null, null, null));
        Assert.Equal("Windows 11", os.Name.Data);
        Assert.Equal(ValueStatus.Unsupported, os.Version.Status);
        Assert.Equal(ValueStatus.Unsupported, os.Details!.Edition.Status);
        Assert.Equal(ValueStatus.Unsupported, os.Details.DisplayVersion.Status);
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "Arm64")]
    [InlineData(Architecture.X86, "x86")]
    public void ArchitectureNamesAreReadable(Architecture a, string expected) => Assert.Equal(expected, Build(arch: a).Architecture.Data);

    [Fact]
    public void UnknownArchitectureIsUnsupported() => Assert.Equal(ValueStatus.Unsupported, Build(arch: Architecture.Wasm).Architecture.Status);

    [Fact]
    public void BootTimeIsNowMinusUptimeToTheSecondInUtc()
    {
        Assert.Equal("2026-11-07T09:57:26Z", OsBuilder.BootTime(Now, 93_784));
        Assert.Equal("2026-11-08T12:00:30Z", OsBuilder.BootTime(Now.AddMilliseconds(400), 0));
        Assert.Equal("2026-11-07T09:57:26Z", Build().Details!.BootTimeUtc.Data);
    }

    [Fact]
    public void NegativeUptimeIsUnsupportedNotAFakeZero()
    {
        var os = Build(uptime: -1);
        Assert.Equal(ValueStatus.Unsupported, os.UptimeSec.Status);
        Assert.Equal(ValueStatus.Unsupported, os.Details!.BootTimeUtc.Status);
    }

    [Fact]
    public void UptimeNotesFastStartup() => Assert.Contains("Fast Startup", Build().UptimeSec.Detail);

    [Fact]
    public void RunningVbsAndHvciAreTrue()
    {
        var (vbs, hvci) = OsBuilder.Security((new DeviceGuardState(2, [2, 5], [2, 5]), 0));
        Assert.True(vbs.Data);
        Assert.True(hvci.Data);
    }

    [Fact]
    public void VbsRunningWithoutHvciIsHvciFalse()
    {
        var (vbs, hvci) = OsBuilder.Security((new DeviceGuardState(2, [1], [1]), 0));
        Assert.True(vbs.Data);
        Assert.Equal(ValueStatus.Ok, hvci.Status);
        Assert.False(hvci.Data);
    }

    [Fact]
    public void ConfiguredButNotRunningIsOffWithANote()
    {
        var (vbs, hvci) = OsBuilder.Security((new DeviceGuardState(1, [], [2]), 0));
        Assert.Equal(ValueStatus.Ok, vbs.Status);
        Assert.False(vbs.Data);
        Assert.Contains("not running", vbs.Detail);
        Assert.False(hvci.Data);
    }

    [Fact]
    public void OffIsOff()
    {
        var (vbs, hvci) = OsBuilder.Security((new DeviceGuardState(0, [], []), 0));
        Assert.False(vbs.Data);
        Assert.False(hvci.Data);
    }

    [Fact]
    public void UnknownStatusCodeIsUnsupported()
    {
        var (vbs, hvci) = OsBuilder.Security((new DeviceGuardState(9, [2], [2]), 0));
        Assert.Equal(ValueStatus.Unsupported, vbs.Status);
        Assert.Equal(ValueStatus.Unsupported, hvci.Status);
    }

    [Theory]
    [InlineData(Wmi.AccessDenied, ValueStatus.Denied)]
    [InlineData(Wmi.InvalidNamespace, ValueStatus.Unsupported)]
    [InlineData(Wmi.InvalidClass, ValueStatus.Unsupported)]
    [InlineData(Wmi.ClassNotRegistered, ValueStatus.Unsupported)]
    [InlineData(int.MinValue, ValueStatus.Timeout)]
    [InlineData(unchecked((int)0x80020009), ValueStatus.Error)]
    public void WmiFailuresMapToStatusesNeverFalse(int hresult, ValueStatus expected)
    {
        var (vbs, hvci) = OsBuilder.Security((null, hresult));
        Assert.Equal(expected, vbs.Status);
        Assert.Equal(expected, hvci.Status);
        Assert.False(vbs.Status == ValueStatus.Ok);
    }

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced")]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "High performance")]
    [InlineData("a1841308-3541-4fab-bc81-f71556f20b4a", "Power saver")]
    [InlineData("e9a42b02-d5df-448d-aa00-03f14749eb61", "Ultimate Performance")]
    public void BuiltInPowerPlansHaveFallbackNames(string guid, string expected) => Assert.Equal(expected, OsBuilder.PowerPlanName(new Guid(guid)));

    [Fact]
    public void CustomPowerPlanHasNoFallbackName() => Assert.Null(OsBuilder.PowerPlanName(Guid.Parse("11111111-2222-3333-4444-555555555555")));

    [Trait("Category", "RealMachine")]
    [Fact]
    public void RealCollectionRunsOnThisMachine()
    {
        var v = OsCollector.Collect(() => (null, int.MinValue), DateTime.UtcNow);
        Assert.Equal(ValueStatus.Ok, v.Status);
        var os = v.Data!;
        Assert.StartsWith("Windows", os.Name.Data);
        Assert.Matches(@"^10\.0\.\d+$", os.Build.Data!);
        Assert.True(os.UptimeSec.Data > 0);
        Assert.Equal(ValueStatus.Timeout, os.VirtualizationBasedSecurity.Status);
        Assert.Matches(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]+)*$", os.Details!.Locale.Data!);
    }
}
