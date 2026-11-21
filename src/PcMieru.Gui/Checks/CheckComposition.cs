using PcMieru.Checks;
using PcMieru.Checks.Build;
using PcMieru.Core;

namespace PcMieru.Gui;

/// <summary>
/// Puts the window's <see cref="CheckController"/> together. The real services are made late: building the controller reads nothing and creates
/// nothing, and neither does opening the history (a folder that is not there is an empty history). Only the first start of a check builds the
/// real services, and a check is what writes its baseline and its result under <c>%LOCALAPPDATA%\PCMieru\checks</c>.
/// </summary>
internal static class CheckComposition
{
    /// <param name="services">The app's services (the storage check reads the drives through them, so the administrator's permission goes the way every other helper call goes).</param>
    /// <param name="dataRoot">The data folder. Null: <c>%LOCALAPPDATA%\PCMieru</c>. Tests give a temporary one.</param>
    /// <param name="fake">True: the fake runner and the in-memory history, touching nothing. Null: follows <c>--fakes</c>.</param>
    /// <param name="legacyDirectory">The checks folder of v0.7.4, only read, once, by the baseline. With the default data folder it is <see cref="CheckHistoryStore.DefaultLegacyDirectory"/>; with a given one, null unless given.</param>
    /// <param name="hold">With the fake runner only: the smoke run's way to hold one load run still. Null: none.</param>
    public static CheckController Create(PcMieruServices services, string? dataRoot = null, bool? fake = null, string? legacyDirectory = null, FakeCheckHold? hold = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (fake ?? services.Options.ForceFakes)
        {
            var history = new InMemoryCheckHistory();
            return new CheckController(() => new FakeCheckRunner(saved: history.Add, hold: hold), history);
        }

        // The defaults are the Checks library's to name (its one place for the folder names); only a root a test gives is joined here.
        var checksFolder = dataRoot is null ? CheckHistoryStore.DefaultRoot() : Path.Combine(dataRoot, "checks");
        var root = dataRoot ?? Path.GetDirectoryName(checksFolder)!;
        var legacy = dataRoot is null ? legacyDirectory ?? CheckHistoryStore.DefaultLegacyDirectory() : legacyDirectory;
        var live = new LiveRunner(services, root, legacy, checksFolder);
        return new CheckController(live.Get, CheckHistory.OpenReadOnly(checksFolder));
    }

    /// <summary>
    /// The maker of the configuration code (PCB1). With <c>--fakes</c> it is the sample one (a fixed, made-up PC: it reads nothing; see
    /// <see cref="FakeBuildCode"/>); otherwise the Windows one, which only reads when a button asks it to (making it reads nothing).
    /// </summary>
    public static IBuildCodeProvider CreateBuildCode(PcMieruServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Options.ForceFakes ? new FakeBuildCode() : BuildCodeProviders.Windows();
    }

    /// <summary>The real runner, made the first time a check starts (on the worker the controller starts it on) and kept for the next ones.</summary>
    private sealed class LiveRunner(PcMieruServices services, string root, string? legacy, string checksFolder)
    {
        private readonly object _gate = new();
        private ICheckRunner? _runner;

        public ICheckRunner Get()
        {
            lock (_gate)
            {
                if (_runner is null)
                {
                    var checkServices = new CheckServices(TimeProvider.System, root, services.StorageInspection, legacy, new CheckHistoryStore(checksFolder), app: null);
                    _runner = new CheckRunner(checkServices);
                }

                return _runner;
            }
        }
    }
}
