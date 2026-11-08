using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;
using PcMieru.Core.Reporting;

namespace PcMieru.Core.Tests.Hardware;

public class HardwareModuleTests
{
    [Fact]
    public void WithoutForceFakesTheRealImplementationsAreRegistered()
    {
        var registry = new ServiceRegistry();

        HardwareModule.Register(registry, new PcMieruOptions());

        Assert.IsType<RealHardwareProvider>(registry.Get<IHardwareProvider>());
        Assert.IsType<RealStorageHealthProvider>(registry.Get<IStorageHealthProvider>());
        Assert.IsType<SensorHostClient>(registry.Get<ISensorClient>());
        Assert.Empty(registry.All.OfType<IFakeImplementation>());
        Dispose(registry);
    }

    [Fact]
    public void WithForceFakesEveryHardwareSlotIsAFakeAndSaysSo()
    {
        var registry = new ServiceRegistry();

        HardwareModule.Register(registry, new PcMieruOptions { ForceFakes = true });

        Assert.IsType<FakeHardwareProvider>(registry.Get<IHardwareProvider>());
        Assert.IsType<FakeStorageHealthProvider>(registry.Get<IStorageHealthProvider>());
        Assert.IsType<FakeSensorClient>(registry.Get<ISensorClient>());
        Assert.Equal(3, registry.All.OfType<IFakeImplementation>().Count());
        Dispose(registry);
    }

    [Fact]
    public async Task TheRealSensorClientIsDisposedWithTheServicesAndAnswersGoneAfterwards()
    {
        var registry = new ServiceRegistry();
        HardwareModule.Register(registry, new PcMieruOptions());
        var sensors = registry.Get<ISensorClient>();

        Dispose(registry);

        Assert.Equal(ValueStatus.Gone, (await sensors.ReadGpuAsync()).Status);
    }

    [Fact]
    public void BuildingTheRealWiringDoesNotLaunchTheSensorHost()
    {
        // Construction has no side effects: the host is launched by the first GPU read, never before.
        var registry = new ServiceRegistry();
        HardwareModule.Register(registry, new PcMieruOptions());

        var client = (SensorHostClient)registry.Get<ISensorClient>();

        Assert.False(client.IsRunning);
        Assert.Equal(0, client.ConsecutiveFailures);
        Dispose(registry);
    }

    [Fact]
    public async Task OtherCategoriesNeverAskTheSensorHost()
    {
        var sensors = new CountingSensors(_ => throw new InvalidOperationException("must not be called"));
        var provider = new RealHardwareProvider(sensors: sensors);

        var info = await provider.GetAsync(HardwareCategory.Os);

        Assert.Equal(0, sensors.Calls);
        Assert.Equal(ValueStatus.NotCollected, info.Gpus.Status);
        Assert.True(info.Os.IsOk);
    }

    [Fact]
    public async Task AFailingSensorClientTurnsIntoPerGpuErrorsNotAnException()
    {
        var sensors = new CountingSensors(_ => throw new InvalidOperationException("boom"));
        var provider = new RealHardwareProvider(sensors: sensors);

        var info = await provider.GetAsync(HardwareCategory.Gpu);

        Assert.Equal(1, sensors.Calls);
        Assert.NotEqual(ValueStatus.NotCollected, info.Gpus.Status);
        foreach (var gpu in info.Gpus.Data ?? [])
        {
            Assert.Equal(ValueStatus.Error, gpu.Vbios.Status);
            Assert.Equal(ValueStatus.Error, gpu.PowerLimitWatts.Status);
            Assert.Contains("InvalidOperationException", gpu.Vbios.Detail);
        }
    }

    [Fact]
    public async Task ASensorClientThatNeverAnswersCostsAtMostTheFiveSecondLimit()
    {
        var sensors = new CountingSensors(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default;
        });
        var provider = new RealHardwareProvider(sensors: sensors);
        var started = DateTime.UtcNow;

        var info = await provider.GetAsync(HardwareCategory.Gpu);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        foreach (var gpu in info.Gpus.Data ?? [])
        {
            Assert.Equal(ValueStatus.Timeout, gpu.Vbios.Status);
        }
    }

    [Fact]
    public async Task ACallerWhoCancelsGetsACancellationNotATimeoutValue()
    {
        var sensors = new CountingSensors(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default;
        });
        var provider = new RealHardwareProvider(sensors: sensors);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Environments with no GPU never reach the sensor call, so only a call that did can be cancelled.
        try
        {
            await provider.GetAsync(HardwareCategory.Gpu, cts.Token);
            Assert.Equal(0, sensors.Calls);
        }
        catch (OperationCanceledException)
        {
            Assert.Equal(1, sensors.Calls);
        }
    }

    private static void Dispose(ServiceRegistry registry)
    {
        foreach (var d in registry.All.OfType<IDisposable>())
        {
            d.Dispose();
        }
    }

    private sealed class CountingSensors(Func<CancellationToken, Task<Value<IReadOnlyList<GpuSensorReading>>>> read) : ISensorClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<Value<IReadOnlyList<GpuSensorReading>>> ReadGpuAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return read(ct);
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The redactor blanks by JSON key name. These tests keep the hardware model and that rule in step: a new field that holds a serial number,
/// address or network name must be one the redactor already knows, or the report would leak it.
/// </summary>
public class HardwareRedactionTests
{
    private static readonly string[] BlankedKeys = ["serialNumber", "macAddress", "ipAddresses", "dnsServers", "ssid"];

    private static readonly Regex SensitiveLooking = new(@"serial|mac(address)?$|ssid|uuid|guid|^ip|dns|hostname|address$|instanceid|owner", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static SimpleRedactor Redactor() => new(new RedactionContext("tatsuki", "DESKTOP-TEST", @"C:\Users\tatsuki"), enabled: true);

    [Fact]
    public void EveryPropertyThatLooksSensitiveIsOneTheRedactorBlanks()
    {
        var names = typeof(HardwareInfo).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(HardwareInfo).Namespace && t.IsClass)
            .SelectMany(t => t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .Distinct()
            .Where(n => SensitiveLooking.IsMatch(n))
            .ToList();

        var uncovered = names.Where(n => !BlankedKeys.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(
            uncovered.Count == 0,
            "These hardware fields look sensitive but the redactor does not blank them by key: " + string.Join(", ", uncovered)
            + ". Add the key to the redactor or rename the field.");
        Assert.Equal(BlankedKeys.Length, names.Count(n => BlankedKeys.Contains(n, StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SerialsMacsAddressesAndSsidAreBlankedInAWholeHardwareReport()
    {
        var info = await new FakeHardwareProvider(static () => Fixture.FixedTime).GetAsync(HardwareCategory.All);
        var node = ToNode(info);
        var injected = new Dictionary<string, int>();
        Inject(node, injected);

        // The fake data does not fill every field; make sure the walk really found each kind of key before trusting the result.
        foreach (var key in BlankedKeys)
        {
            Assert.True(injected.GetValueOrDefault(key) > 0, $"the sample report has no '{key}' field to test with");
        }

        Redactor().RedactJson(node);
        var text = node.ToJsonString();

        Assert.DoesNotContain("SENTINEL-", text, StringComparison.Ordinal);
        // ToJsonString escapes the angle brackets of the placeholder, so look for the word itself.
        Assert.Contains("redacted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatFailedToReadIsLeftAsItIsSoTheReasonStaysVisible()
    {
        var node = JsonNode.Parse("""{"serialNumber":{"status":"denied","source":"ioctl","detail":"access denied"}}""")!;

        Redactor().RedactJson(node);

        Assert.Equal("denied", node["serialNumber"]!["status"]!.GetValue<string>());
        Assert.Null(node["serialNumber"]!["value"]);
    }

    [Fact]
    public void ADeviceNameWithTheUsersNameIsRedactedByText()
    {
        var text = Redactor().RedactText("tatsuki's USB stick on DESKTOP-TEST");

        Assert.DoesNotContain("tatsuki", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DESKTOP-TEST", text, StringComparison.Ordinal);
    }

    private static JsonNode ToNode(HardwareInfo info) =>
        System.Text.Json.JsonSerializer.SerializeToNode(info, PcMieruJson.Compact)!;

    private static void Inject(JsonNode? node, Dictionary<string, int> found)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (BlankedKeys.Contains(name, StringComparer.OrdinalIgnoreCase) && child is JsonObject leaf && leaf.ContainsKey("status"))
                    {
                        leaf["status"] = "ok";
                        leaf["value"] = name is "ipAddresses" or "dnsServers"
                            ? new JsonArray("SENTINEL-" + name)
                            : JsonValue.Create("SENTINEL-" + name);
                        found[name] = found.GetValueOrDefault(name) + 1;
                    }
                    else
                    {
                        Inject(child, found);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Inject(item, found);
                }

                break;
        }
    }
}
