namespace PcMieru.Core.Collection;

/// <summary>
/// Registers area A. With <see cref="PcMieruOptions.ForceFakes"/> the Fakes are served (tests, demos); otherwise the real
/// sampler and the real metadata provider. A real implementation that cannot measure something says so in its values
/// (Denied, Unsupported, Timeout...); it never falls back to made-up data, because sample numbers mistaken for a
/// measurement are worse than a blank.
/// </summary>
public static class CollectionModule
{
    public static void Register(ServiceRegistry registry, PcMieruOptions options)
    {
        Strings.Register(CollectionStrings.All);

        // Plain settings (interval, pause, minimized, GPU subscription), not a measurement: shared by both modes so a front end
        // can always ask the registry for it.
        var settings = new SamplerSettings();
        registry.Set<ISamplerControl>(settings);

        if (options.ForceFakes)
        {
            registry.Set<ISnapshotSource>(new FakeSnapshotSource(options.Clock));
            registry.Set<IProcessDetailsProvider>(new FakeProcessDetailsProvider());
            registry.Set<IProcessGroupSource>(new FakeProcessGroupSource());
            registry.Set<IProcessIconSource>(new FakeProcessIconSource());
            return;
        }

        // Hardware and Reporting register after this module, so what we need from them is looked up when it is used.
        // A catalog or sensor client that is replaced later (tests, `configure`) is picked up too.
        var verifier = new SignatureVerifier();
        var source = new WindowsSnapshotSource(
            options.Clock,
            settings,
            () => registry.TryGet<ISensorClient>(out var sensors) ? sensors : null,
            verifier);
        var details = new ProcessMetadataProvider(
            () => registry.TryGet<IKnownProcessCatalog>(out var catalog) ? catalog : null,
            source.NameOf,
            verifier);

        registry.Set<ISnapshotSource>(source);
        registry.Set<IProcessGroupSource>(source);
        registry.Set<IProcessDetailsProvider>(details);
        registry.Set<IProcessIconSource>(new ProcessIconSource());
    }
}

public static class CollectionStrings
{
    public static readonly StringEntry[] All =
    [
        new("collection.state.running", "実行中", "Running"),
        new("collection.state.notResponding", "応答なし", "Not responding"),
        new("collection.state.suspended", "中断", "Suspended"),
        new("collection.group.app", "アプリ", "Apps"),
        new("collection.group.windows", "Windows プロセス", "Windows processes"),
        new("collection.group.background", "バックグラウンド プロセス", "Background processes"),
        new("collection.group.pending", "判定中", "Checking"),
        new(
            "collection.group.rule",
            "ウィンドウが表示されているものは「アプリ」、Windows フォルダーにある署名済みのものは「Windows プロセス」、それ以外は「バックグラウンド プロセス」です。タスク マネージャーの分け方とは一致しません。",
            "Processes with a visible window are Apps; signed ones in the Windows folder are Windows processes; the rest are Background processes. This is not Task Manager's grouping."),
    ];
}
