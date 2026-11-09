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

    [Trait("Category", "RealMachine")]
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

    [Trait("Category", "RealMachine")]
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

    [Trait("Category", "RealMachine")]
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

    [Trait("Category", "RealMachine")]
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
    public void EveryFreeTextFieldThatAPersonTypedIsBlankedAtItsPlace()
    {
        // "label" and "name" mean other things in other places, so these are blanked by path, not by key. A new field
        // called "label" anywhere in the hardware model must be decided on here (add its path, or say it is fine).
        var labels = typeof(HardwareInfo).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(HardwareInfo).Namespace && t.IsClass)
            .Where(t => t.GetProperty("Label") is not null)
            .Select(t => t.Name)
            .ToList();
        Assert.Equal(["StorageVolume"], labels);

        // Each blanked path ends in a property that really exists on the type that sits at that place in the model.
        Assert.Equal(
            ["audio[].details.adapter", "audio[].name", "displays[].details.monitorName", "network[].name", "usb[].name", "volumes[].label"],
            SimpleRedactor.BlankedPaths.Order().ToArray());
        Assert.NotNull(typeof(StorageVolume).GetProperty("Label"));
        Assert.NotNull(typeof(AudioEndpoint).GetProperty("Name"));
        Assert.NotNull(typeof(UsbDevice).GetProperty("Name"));
        Assert.NotNull(typeof(NetworkAdapter).GetProperty("Name"));
        Assert.NotNull(typeof(DisplayDetails).GetProperty("MonitorName"));
        Assert.NotNull(typeof(AudioDetails).GetProperty("Adapter"));
    }

    [Fact]
    public async Task VolumeLabelsAudioNamesAndUsbNamesAreBlankedInAWholeHardwareReport()
    {
        var info = await new FakeHardwareProvider(static () => Fixture.FixedTime).GetAsync(HardwareCategory.All);
        // The fake leaves every Details null (the skeleton shows the top-level fields only). The monitor's OSD name and the
        // audio adapter name live under details, so give the sample display/audio a details record to reach those blanked paths.
        info = WithDisplayAndAudioDetails(info);
        var report = new JsonObject { ["hardware"] = ToNode(info) };

        var placed = new Dictionary<string, int>();
        foreach (var (path, owner, name, child) in Properties(report, string.Empty).ToList())
        {
            var blanked = BlankedPathOf(path);
            if (blanked is null)
            {
                continue;
            }

            if (child is JsonObject leaf && leaf.ContainsKey("status"))
            {
                leaf["status"] = "ok";
                leaf["value"] = "PERSON-" + blanked;
            }
            else
            {
                owner[name] = "PERSON-" + blanked;
            }

            placed[blanked] = placed.GetValueOrDefault(blanked) + 1;
        }

        // The fake data does not fill every place; make sure the walk really found each one before trusting the result.
        foreach (var path in SimpleRedactor.BlankedPaths)
        {
            Assert.True(placed.GetValueOrDefault(path) > 0, $"the sample report has nothing at '{path}' to test with");
        }

        Assert.Contains("PERSON-", report.ToJsonString(), StringComparison.Ordinal);
        Redactor().RedactJson(report);
        Assert.DoesNotContain("PERSON-", report.ToJsonString(), StringComparison.Ordinal);

        // Every place was blanked in the Value (status kept), not removed.
        foreach (var (path, _, _, child) in Properties(report, string.Empty).ToList())
        {
            if (BlankedPathOf(path) is not null)
            {
                var text = child is JsonObject { } leaf ? leaf["value"]?.GetValue<string>() : child.GetValue<string>();
                Assert.Equal(SimpleRedactor.Placeholder, text);
            }
        }
    }

    [Fact]
    public async Task OtherFieldsOfTheSameRecordsAreNotBlanked()
    {
        var info = await new FakeHardwareProvider(static () => Fixture.FixedTime).GetAsync(HardwareCategory.All);
        var node = ToNode(info);
        Redactor().RedactJson(node);

        var text = node.ToJsonString();
        Assert.Contains("NTFS", text, StringComparison.Ordinal);
        Assert.Contains(info.Cpu.Data!.Name.Data!, text, StringComparison.Ordinal);
        Assert.Contains(info.Usb.Data![0].Manufacturer.Data!, text, StringComparison.Ordinal);
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

    /// <summary>Every property of every object, with its place in the tree (arrays are "[]", the "value" of a Value is not a step).</summary>
    private static IEnumerable<(string Path, JsonObject Owner, string Name, JsonNode Child)> Properties(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                var isValue = obj.ContainsKey("status");
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is null)
                    {
                        continue;
                    }

                    var childPath = isValue && name == "value" ? path : path.Length == 0 ? name : path + "." + name;
                    yield return (childPath, obj, name, child);
                    foreach (var inner in Properties(child, childPath))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in Properties(item, path + "[]"))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    private static string? BlankedPathOf(string path) =>
        SimpleRedactor.BlankedPaths.FirstOrDefault(p => path == p || path.EndsWith("." + p, StringComparison.Ordinal));

    private static JsonNode ToNode(HardwareInfo info) =>
        System.Text.Json.JsonSerializer.SerializeToNode(info, PcMieruJson.Compact)!;

    /// <summary>
    /// Attach a details record to the sample display and audio endpoints so a report walk can reach the two blanked paths that
    /// live under details: <c>displays[].details.monitorName</c> (the monitor's OSD name) and <c>audio[].details.adapter</c>.
    /// </summary>
    private static HardwareInfo WithDisplayAndAudioDetails(HardwareInfo info)
    {
        static Value<T> Ok<T>(T value) => Value.Ok(value, "test");

        var displays = info.Displays.Data!
            .Select(d => d with
            {
                Details = new DisplayDetails(
                    Ok("MONITOR\\FAKE1234"), Ok(0x1234), Ok("FAKE-DISPLAY-SERIAL"), Ok("1.4"), Ok(true), Ok(1), Ok(27.0),
                    Ok("Digital"), Ok(8), Ok("tatsuki's monitor"), Ok(48.0), Ok(165.0), Ok(false), Ok(false), Ok(0)),
            })
            .ToList();
        var audio = info.Audio.Data!
            .Select(a => a with { Details = new AudioDetails(Ok("active"), Ok("Speakers"), Ok("Realtek(R) Audio"), Ok(2)) })
            .ToList();
        return info with
        {
            Displays = Value.Ok<IReadOnlyList<DisplayDevice>>(displays, "test"),
            Audio = Value.Ok<IReadOnlyList<AudioEndpoint>>(audio, "test"),
        };
    }

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
