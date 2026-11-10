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
            var fakeNet = new FakeNetMeterControl();
            registry.Set<INetMeterControl>(fakeNet);
            var fake = new FakeSnapshotSource(options.Clock, fakeNet);
            registry.Set<ISnapshotSource>(fake);
            registry.Set(new SamplerLoop(fake, settings));
            registry.Set<IProcessDetailsProvider>(new FakeProcessDetailsProvider());
            registry.Set<IProcessGroupSource>(new FakeProcessGroupSource());
            registry.Set<IProcessIconSource>(new FakeProcessIconSource());
            return;
        }

        // Hardware and Reporting register after this module, so what we need from them is looked up when it is used.
        // A catalog or sensor client that is replaced later (tests, `configure`) is picked up too.
        // The opt-in network meter: nothing starts (and nothing is asked of the user) until a front end calls StartAsync.
        // The registry disposes it with the services, which stops the meter if it is running.
        registry.Set<INetMeterControl>(new NetMeterControl());

        var verifier = new SignatureVerifier();
        var source = new WindowsSnapshotSource(
            options.Clock,
            settings,
            () => registry.TryGet<ISensorClient>(out var sensors) ? sensors : null,
            verifier,
            () => registry.TryGet<INetMeterControl>(out var control) ? control : null);
        var details = new ProcessMetadataProvider(
            () => registry.TryGet<IKnownProcessCatalog>(out var catalog) ? catalog : null,
            source.NameOf,
            verifier);

        registry.Set<ISnapshotSource>(source);
        registry.Set(new SamplerLoop(source, settings));
        registry.Set<IProcessGroupSource>(source);
        registry.Set<IProcessDetailsProvider>(details);
        registry.Set<IProcessIconSource>(new ProcessIconSource());
    }
}

public static class CollectionStrings
{
    // What the network meter's state says in plain words (NetMeterState.Detail).
    internal const string NetMissing = "collection.net.missing";
    internal const string NetNoIdentity = "collection.net.no-identity";
    internal const string NetLaunchFailed = "collection.net.launch-failed";
    internal const string NetNoAnswer = "collection.net.no-answer";
    internal const string NetExited = "collection.net.exited";
    internal const string NetPipeLost = "collection.net.pipe-lost";
    internal const string NetBadReply = "collection.net.bad-reply";
    internal const string NetNotOurPipe = "collection.net.not-our-pipe";
    internal const string NetSessionDenied = "collection.net.session-denied";
    internal const string NetSessionStopped = "collection.net.session-stopped";
    internal const string NetSessionResources = "collection.net.session-resources";
    internal const string NetSessionError = "collection.net.session-error";

    public static readonly StringEntry[] All =
    [
        new(NetMissing, "ネットワーク メーター (PCMieru.NetMeter.exe) が、アプリと同じフォルダーにありません。", "The network meter (PCMieru.NetMeter.exe) is not in the app folder."),
        new(NetNoIdentity, "ネットワーク メーターに渡す、このアプリの情報を読み取れませんでした。", "The details of this app that the network meter needs could not be read."),
        new(NetLaunchFailed, "ネットワーク メーターを起動できませんでした (Windows のエラー {code})。", "The network meter could not be started (Windows error {code})."),
        new(NetNoAnswer, "ネットワーク メーターが時間内に応答しませんでした。", "The network meter did not answer in time."),
        new(NetExited, "ネットワーク メーターが終了しました (終了コード {code})。", "The network meter has ended (exit code {code})."),
        new(NetPipeLost, "ネットワーク メーターとの接続が切れました。", "The connection to the network meter was lost."),
        new(NetBadReply, "ネットワーク メーターから想定外の応答が返りました。", "The network meter sent a reply that was not expected."),
        new(NetNotOurPipe, "ネットワーク メーターの通信路が、起動したメーターのものではなかったため、使いませんでした。", "The network meter's pipe did not belong to the meter that was started, so it was not used."),
        new(NetSessionDenied, "ネットワークの計測セッションを開始する権限がありません (管理者として実行されていません)。", "The network trace session could not be started: administrator rights are missing."),
        new(NetSessionStopped, "ネットワークの計測セッションが、ほかのツールなどによって止められました。", "The network trace session was stopped by something else."),
        new(NetSessionResources, "ネットワークの計測セッションに必要な資源が足りません。ほかのトレース セッションが多すぎる可能性があります。", "Windows has too few resources for another trace session; too many trace sessions may be running."),
        new(NetSessionError, "ネットワークの計測セッションを使えません (Windows のエラー {code})。", "The network trace session cannot be used (Windows error {code})."),
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
