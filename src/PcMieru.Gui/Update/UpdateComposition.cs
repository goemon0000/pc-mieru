using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.Gui;

/// <summary>The controller of a window and, with <c>--fakes</c>, the fake service behind it (the smoke run drives that one).</summary>
internal sealed record UpdateWiring(UpdateController Controller, FakeUpdateService? Fake);

/// <summary>
/// Puts the window's <see cref="UpdateController"/> together. Making it reads nothing, creates nothing and starts no timer and no HTTP client: the real service is
/// made the first time something asks it (a check, or the settings page asking what state.json says), and a check only ever goes out in Notify or Automatic.
/// </summary>
internal static class UpdateComposition
{
    /// <param name="fakes"><c>--fakes</c>: a fake service that answers from a script and never makes a request (<see cref="FakeUpdateService"/>).</param>
    /// <param name="settings">Holds the mode.</param>
    /// <param name="versionText">The version of this program as the build writes it ("0.4.0"). Not three plain numbers: no check is made (<see cref="UpdateRefusal.OwnVersionUnknown"/>).</param>
    /// <param name="localFolder">L, the per-user data folder (<c>%LOCALAPPDATA%\PCMieru</c>). The state goes to <c>L\update\&lt;id&gt;</c>.</param>
    /// <param name="programFile">PCMieru.exe. The install id is taken from the gate file beside it (<see cref="InstallId.StateFolder"/>); no gate file, no check (<see cref="UpdateRefusal.GateMissing"/>).</param>
    public static UpdateWiring Create(bool fakes, GuiSettings settings, string versionText, string localFolder, string programFile, IUpdateTimers? timers = null)
    {
        timers ??= new WinFormsUpdateTimers();
        var parsed = AppVersion.TryParse(versionText, out var running);
        if (fakes)
        {
            var fake = new FakeUpdateService(running);
            return new UpdateWiring(new UpdateController(fake, settings, timers, () => fake.Placement, running), fake);
        }

        if (!parsed)
        {
            var unknown = new UnavailableUpdateService(UpdateRefusal.OwnVersionUnknown, "the version of this program is not X.Y.Z: " + versionText);
            return new UpdateWiring(new UpdateController(unknown, settings, timers, () => new Placement(PlacementKind.ReadOnly, "no version"), running), null);
        }

        var service = new DeferredUpdateService(
            () => InstallId.StateFolder(localFolder, programFile) is { } folder
                ? new UpdateService(new UpdateServiceOptions { Running = running, StateFolder = folder })
                : GateIsThere(programFile)
                    ? new UnavailableUpdateService(UpdateRefusal.StateUnavailable, "the gate file of this install has no id")
                    : new UnavailableUpdateService(UpdateRefusal.GateMissing, "there is no gate file beside this program"),
            () => settings.UpdateMode);
        return new UpdateWiring(new UpdateController(service, settings, timers, () => PlacementOf(programFile), running), null);
    }

    /// <summary>The gate file is beside the program (it can still fail to open, which is a state problem and not a missing file).</summary>
    private static bool GateIsThere(string programFile) => InstallId.GateFileOf(programFile) is { } gate && File.Exists(gate);

    private static Placement PlacementOf(string programFile)
    {
        var folder = string.IsNullOrEmpty(programFile) ? null : Path.GetDirectoryName(programFile);
        return folder is null ? new Placement(PlacementKind.ReadOnly, "no program folder") : InstallPlacement.Check(folder);
    }
}

/// <summary>
/// Makes the real service the first time it is used (so a start with Ask or Off opens no file for it). Reading the status is not a use in Ask or Off either (M9):
/// the settings page and the notice ask for it, and the owner who has not chosen, or chose "off", must not get a state folder, a lock or a file read for that.
/// </summary>
internal sealed class DeferredUpdateService(Func<IUpdateService> make, Func<UpdateMode>? mode = null) : IUpdateService
{
    private readonly Lazy<IUpdateService> _inner = new(make, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct) =>
        UpdateModes.MayUseNetwork(mode) ? _inner.Value.CheckAsync(mode, manual, ct) : Task.FromResult(UpdateCheck.NotAllowed(mode));

    public UpdateStatus ReadStatus() =>
        !_inner.IsValueCreated && mode is not null && !UpdateModes.MayUseNetwork(mode()) ? UpdateStatus.Empty : _inner.Value.ReadStatus();
}

/// <summary>A service that cannot check (this PC cannot say who it is, or what version it is): it refuses with the reason and asks nobody.</summary>
internal sealed class UnavailableUpdateService(UpdateRefusal reason, string detail) : IUpdateService
{
    public Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct) =>
        Task.FromResult(UpdateModes.MayUseNetwork(mode) ? UpdateCheck.Refuse(reason, detail, fetched: false) : UpdateCheck.NotAllowed(mode));

    public UpdateStatus ReadStatus() => UpdateStatus.Empty;
}

/// <summary>
/// The service of <c>--fakes</c> and of the tests: answers from <see cref="Answer"/>, remembers what it was told in a status of its own, and never
/// makes a request or touches a file. <see cref="Calls"/> counts only the calls that passed the mode gate, so a test can say "nothing was asked".
/// </summary>
internal sealed class FakeUpdateService(AppVersion running, TimeProvider? clock = null) : IUpdateService, IFakeImplementation
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private UpdateStatus _status = UpdateStatus.Empty;
    private int _calls;

    /// <summary>What a check answers: gets the mode and whether it was asked for by hand. Default: nothing newer.</summary>
    public Func<UpdateMode, bool, UpdateCheck> Answer { get; set; } = static (_, _) => UpdateCheck.NoCandidate(fetched: true);

    /// <summary>What the install folder looks like to the settings page (default: writable).</summary>
    public Placement Placement { get; set; } = new(PlacementKind.CanSwap, "sample");

    /// <summary>The checks that passed the mode gate (a call in Ask or Off is not counted).</summary>
    public int Calls => Volatile.Read(ref _calls);

    public Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct)
    {
        if (!UpdateModes.MayUseNetwork(mode))
        {
            return Task.FromResult(UpdateCheck.NotAllowed(mode));
        }

        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        var result = Answer(mode, manual);
        Remember(result);
        return Task.FromResult(result);
    }

    public UpdateStatus ReadStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    /// <summary>Replaces what the state file would say (a candidate that an earlier run left, a wait that is still running).</summary>
    public void SetStatus(UpdateStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }
    }

    /// <summary>A candidate that is newer than the running version: its manifest has no files (nothing is ever downloaded in this step).</summary>
    public UpdateCheck Candidate(int minorsAhead = 1)
    {
        var version = new AppVersion(running.Major, running.Minor + minorsAhead, 0);
        var manifest = new BundleManifest(version.ToString(), "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", []);
        return UpdateCheck.Found(
            version,
            UpdateEndpoints.ReleasePageUrl(version.Tag),
            manifest,
            UpdateEndpoints.AssetUrl(version.Tag, BundleFormat.ZipName(version.ToString())),
            new string('a', 64));
    }

    private void Remember(UpdateCheck result)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            _status = result.Kind switch
            {
                UpdateCheckKind.Candidate => _status with { LastCheckUtc = now, BackoffUntilUtc = null, CandidateVersion = result.Version, CandidatePageUrl = result.PageUrl, LastRefusal = UpdateRefusal.None, RefusedVersion = null },
                // As the real service: a refusal before any request (another check running, no state folder) writes nothing.
                UpdateCheckKind.Refused when !result.Fetched => _status,
                // A failure that judged no release keeps the candidate (UpdateRefusals.IsOperational); a rule of the release itself clears it.
                UpdateCheckKind.Refused when UpdateRefusals.IsOperational(result.Refusal) => _status with { LastCheckUtc = result.Fetched ? now : _status.LastCheckUtc, BackoffUntilUtc = null, LastRefusal = result.Refusal, RefusedVersion = null },
                UpdateCheckKind.Refused => _status with { LastCheckUtc = result.Fetched ? now : _status.LastCheckUtc, BackoffUntilUtc = null, CandidateVersion = null, CandidatePageUrl = null, LastRefusal = result.Refusal, RefusedVersion = result.RefusedVersion },
                UpdateCheckKind.Waiting => _status with { LastCheckUtc = now, BackoffUntilUtc = result.WaitUntil },
                UpdateCheckKind.NoCandidate when result.Fetched => _status with { LastCheckUtc = now, BackoffUntilUtc = null, CandidateVersion = null, CandidatePageUrl = null, LastRefusal = UpdateRefusal.None, RefusedVersion = null },
                _ => _status,
            };
        }
    }
}
