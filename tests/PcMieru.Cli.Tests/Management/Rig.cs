using PcMieru.Wire;

namespace PcMieru.Cli.Tests.Management;

/// <summary>
/// One CLI run with the recording doubles in place of the process control, Helper and launcher. The Helper result files go to
/// a temp folder that is removed afterwards, so nothing is left in the real <c>%LOCALAPPDATA%</c>.
/// </summary>
internal sealed class Rig : IDisposable
{
    public Rig()
    {
        HelperDirectory = Fx.TempDirectory();
    }

    public string HelperDirectory { get; }

    public RecordingControl Control { get; private set; } = null!;

    public RecordingHelper Helper { get; } = new();

    public RecordingLauncher Launcher { get; } = new();

    /// <summary>Answer of the change calls (terminate, tree, priority, eco) instead of the Fake's, for the refusal paths.</summary>
    public OperationResult? Forced { get; set; }

    public TerminatePlan? PlanOverride { get; set; }

    public ScriptedServices? Services { get; set; }

    public ScriptedStartup? Startup { get; set; }

    public Task<CliResult> RunAsync(params string[] args) => RunWithAsync(Language.En, args);

    public Task<CliResult> RunWithAsync(Language language, params string[] args) =>
        CliRun.RunAsync(
            args,
            language: language,
            configure: registry =>
            {
                Control = new RecordingControl(registry.Get<IProcessControl>()) { Forced = Forced, PlanOverride = PlanOverride };
                registry.Set<IProcessControl>(Control);
                registry.Set<IHelperClient>(Helper);
                registry.Set<ITaskLauncher>(Launcher);
                if (Services is not null)
                {
                    registry.Set<IServiceManager>(Services);
                }

                if (Startup is not null)
                {
                    registry.Set<IStartupManager>(Startup);
                }
            },
            helperDirectory: HelperDirectory);

    /// <summary>No file may be left behind in the Helper folder once a command has ended.</summary>
    public string[] LeftoverFiles() => Directory.Exists(HelperDirectory) ? Directory.GetFiles(HelperDirectory) : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(HelperDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
