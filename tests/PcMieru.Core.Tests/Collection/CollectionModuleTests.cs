using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

public sealed class CollectionModuleTests(ITestOutputHelper output)
{
    private sealed class RecordingCatalog : IKnownProcessCatalog
    {
        public List<string> Names { get; } = [];

        public KnownProcess? Match(string imageName, string? path, SignatureInfo? signature)
        {
            lock (Names)
            {
                Names.Add(imageName);
            }

            return new KnownProcess("test.catalog", "テスト", "Test", PathMatches: path is not null, SignerMatches: signature is not null);
        }
    }

    private static readonly string[] CollectionFakes =
    [
        nameof(FakeSnapshotSource),
        nameof(FakeProcessDetailsProvider),
        nameof(FakeProcessGroupSource),
        nameof(FakeProcessIconSource),
    ];

    [Fact]
    public void ForceFakes_serves_the_four_Fakes_and_says_so()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });

        Assert.IsType<FakeSnapshotSource>(services.Snapshots);
        Assert.IsType<FakeProcessDetailsProvider>(services.ProcessDetails);
        Assert.IsType<FakeProcessGroupSource>(services.Get<IProcessGroupSource>());
        Assert.IsType<FakeProcessIconSource>(services.Get<IProcessIconSource>());
        Assert.All(CollectionFakes, name => Assert.Contains(name, services.FakeAreas));
        Assert.NotNull(services.Get<ISamplerControl>());
    }

    [Fact]
    public void Without_ForceFakes_the_real_implementations_are_served_and_are_not_listed_as_fake()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions());

        Assert.IsType<WindowsSnapshotSource>(services.Snapshots);
        Assert.IsType<ProcessMetadataProvider>(services.ProcessDetails);
        Assert.IsType<ProcessIconSource>(services.Get<IProcessIconSource>());
        Assert.Same(services.Snapshots, services.Get<IProcessGroupSource>()); // one object answers both, so grouping follows the sample
        Assert.IsType<SamplerSettings>(services.Get<ISamplerControl>());
        Assert.Same(services.Get<ISamplerControl>(), ((WindowsSnapshotSource)services.Snapshots).Control);
        Assert.All(CollectionFakes, name => Assert.DoesNotContain(name, services.FakeAreas));
    }

    [Fact]
    public void The_group_and_state_strings_exist_in_both_languages()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });

        foreach (var key in new[] { "app", "windows", "background", "pending", "rule" })
        {
            var entry = Assert.Single(Strings.All, e => e.Key == "collection.group." + key);
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja));
            Assert.False(string.IsNullOrWhiteSpace(entry.En));
        }
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task Through_the_registry_the_real_sampler_sees_a_spawned_process_and_the_provider_asks_the_catalog_that_is_registered_later()
    {
        var catalog = new RecordingCatalog();
        using var services = PcMieruServices.Create(new PcMieruOptions(), registry => registry.Set<IKnownProcessCatalog>(catalog));
        using var ping = new SpawnedPing();

        _ = await services.Snapshots.SampleAsync();
        var snapshot = await services.Snapshots.SampleAsync();

        var spawned = snapshot.Processes.Single(p => p.Key == ping.Key);
        Assert.Equal("PING.EXE", spawned.Name.ToUpperInvariant());
        Assert.True(snapshot.System.Cpu.TotalPercent.IsOk);

        var details = await services.ProcessDetails.GetAsync(ping.Key);
        output.WriteLine($"{details.Path.Data} | {details.Description.Data} | {details.Signature.Data?.State} {details.Signature.Data?.Signer} | known={details.Known?.Id}");
        Assert.True(details.Path.IsOk);
        Assert.EndsWith("ping.exe", details.Path.Data, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("test.catalog", details.Known?.Id);
        lock (catalog.Names)
        {
            Assert.Contains("PING.EXE", catalog.Names.Select(n => n.ToUpperInvariant()));
        }

        var group = services.Get<IProcessGroupSource>();
        Assert.True(((WindowsSnapshotSource)services.Snapshots).WaitForGroups(TimeSpan.FromSeconds(60)));
        _ = await services.Snapshots.SampleAsync();
        Assert.Equal(ProcessGroup.Windows, group.GroupOf(ping.Key).Data);

        var icon = await services.Get<IProcessIconSource>().GetPngAsync(details.Path.Data!, 16);
        output.WriteLine($"icon: {icon.Status} {icon.Data?.Length} bytes {icon.Detail}");
        Assert.True(icon.IsOk || icon.Status == ValueStatus.Unsupported, icon.Detail); // a console tool may have no icon; anything else is a fault
    }
}
