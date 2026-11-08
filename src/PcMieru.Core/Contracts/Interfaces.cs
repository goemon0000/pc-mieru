using System.Text.Json.Nodes;
using PcMieru.Core.Hardware.Model;
using PcMieru.Wire;

namespace PcMieru.Core;

// Boundary interfaces between the five work areas. Frozen: to extend one, add a NEW interface in your own
// area (or an optional member with a default implementation) and tell the integration owner.
//
//   A Collection : ISnapshotSource, IProcessDetailsProvider
//   C Hardware   : IHardwareProvider, IStorageHealthProvider, ISensorClient
//   D Management : IProcessControl, IProtectionPolicy, IServiceManager, IStartupManager, ISessionProvider,
//                  IHelperClient, ITaskLauncher
//   E Reporting  : IObservationEngine, IKnownProcessCatalog, IRedactor, IReportBuilder, IReadOnlyInsights

/// <summary>Marker on Fakes. PcMieruServices lists every area still served by a Fake, and the GUI/CLI say so.</summary>
public interface IFakeImplementation
{
}

public interface ISnapshotSource : IDisposable
{
    /// <summary>
    /// Takes one observation. The first call (and the first after <see cref="ResetBaseline"/>) has no baseline:
    /// CPU and rate values are NotCollected. Never blocks the UI thread for long; safe to call from one thread at a time.
    /// </summary>
    Task<Snapshot> SampleAsync(CancellationToken ct = default);

    /// <summary>Drop the baseline (resume from sleep, interval change, counter went backwards).</summary>
    void ResetBaseline();
}

public interface IProcessDetailsProvider
{
    /// <summary>Lazy, cached per ProcessKey. Never throws for a vanished process: the values come back as Gone.</summary>
    Task<ProcessDetails> GetAsync(ProcessKey key, CancellationToken ct = default);
}

public interface IHardwareProvider
{
    Task<HardwareInfo> GetAsync(HardwareCategory category, CancellationToken ct = default);

    /// <summary>Forget cached facts (WM_DEVICECHANGE).</summary>
    void Invalidate();
}

public interface IStorageHealthProvider
{
    /// <summary>
    /// <paramref name="allowElevation"/> is true only for an explicit user action (GUI button, `pcmi health --elevate`).
    /// Anything that is not a user action passes false and gets Denied as-is.
    /// </summary>
    Task<IReadOnlyList<DriveHealth>> ReadAsync(bool allowElevation, CancellationToken ct = default);
}

public interface ISensorClient : IDisposable
{
    /// <summary>Latest GPU sensors from SensorHost. A hung SensorHost is killed and restarted behind this call.</summary>
    Task<Value<IReadOnlyList<GpuSensorReading>>> ReadGpuAsync(CancellationToken ct = default);
}

public interface IProtectionPolicy
{
    /// <summary>Checked immediately before any operation, on the process opened for the operation. A failed query is not "allowed".</summary>
    ProtectionVerdict Evaluate(ProcessKey key);
}

public interface IProcessControl
{
    Task<OperationResult> CloseWindowAsync(ProcessKey key, CancellationToken ct = default);

    Task<OperationResult> TerminateAsync(ProcessKey key, CancellationToken ct = default);

    /// <summary>Fixes the descendants at this moment; later children are not followed.</summary>
    Task<TerminatePlan> PlanTerminateTreeAsync(ProcessKey root, CancellationToken ct = default);

    Task<OperationResult> TerminateTreeAsync(TerminatePlan plan, CancellationToken ct = default);

    Task<OperationResult> SetPriorityAsync(ProcessKey key, SettablePriority priority, CancellationToken ct = default);

    Task<OperationResult> SetEfficiencyModeAsync(ProcessKey key, bool enabled, CancellationToken ct = default);
}

public interface IServiceManager
{
    Task<IReadOnlyList<ServiceInfo>> ListAsync(CancellationToken ct = default);

    Task<OperationResult> StartAsync(string name, CancellationToken ct = default);

    Task<OperationResult> StopAsync(string name, CancellationToken ct = default);

    Task<OperationResult> RestartAsync(string name, CancellationToken ct = default);
}

public interface IStartupManager
{
    Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken ct = default);

    /// <summary>Toggles by <see cref="StartupItem.Id"/>. Never deletes; the previous bytes are remembered so it can be undone.</summary>
    Task<OperationResult> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
}

public interface ISessionProvider
{
    Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct = default);
}

public interface IHelperClient
{
    /// <summary>
    /// Launches PCMieru.Helper.exe through ShellExecuteEx("runas") with the request fixed on the command line.
    /// One UAC prompt, one request. A declined prompt is UacDeclined=true, not an error.
    /// </summary>
    Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default);
}

public interface ITaskLauncher
{
    /// <summary>"Run new task". Elevated uses the runas verb of the caller, never the Helper.</summary>
    OperationResult Run(string commandLine, bool elevated);
}

/// <summary>Everything the rules may look at. History is oldest first and may be empty.</summary>
public sealed record ObservationInput(
    Snapshot Current,
    IReadOnlyList<Snapshot> History,
    IReadOnlyList<DriveHealth>? StorageHealth = null);

public interface IObservationEngine
{
    /// <summary>Evidence-backed facts and candidates only. Pure function of its input.</summary>
    IReadOnlyList<Observation> Evaluate(ObservationInput input);
}

public interface IKnownProcessCatalog
{
    /// <summary>Matches on path and verified signer too, never on the name alone.</summary>
    KnownProcess? Match(string imageName, string? path, SignatureInfo? signature);
}

public interface IRedactor
{
    /// <summary>False only when the process was started with --no-redact. The per-call overloads can only turn redaction off for one call.</summary>
    bool Enabled { get; }

    string RedactText(string text);

    /// <summary>Redacts a whole JSON tree in place by key name, by tree path and by text content. Returns the same node.</summary>
    JsonNode? RedactJson(JsonNode? node);

    /// <summary>
    /// Per-call switch (a "hide personal info" check box). The text is hidden when <paramref name="redact"/> is true AND
    /// <see cref="Enabled"/> is true: a box that is on can never override --no-redact, and a box that is off leaves the text alone.
    /// </summary>
    string RedactText(string text, bool redact) => redact ? RedactText(text) : text;

    /// <summary>The per-call form of <see cref="RedactJson(JsonNode?)"/>; same rule as <see cref="RedactText(string, bool)"/>.</summary>
    JsonNode? RedactJson(JsonNode? node, bool redact) => redact ? RedactJson(node) : node;
}

public interface IReportBuilder
{
    /// <summary>
    /// Builds the data once. <see cref="ReportDocument.Redacted"/> here is only what this process will do when the document
    /// is written out (<see cref="IRedactor.Enabled"/>); the Render methods below stamp what they really did.
    /// </summary>
    Task<ReportDocument> BuildAsync(ReportOptions options, CancellationToken ct = default);

    /// <summary>
    /// The Markdown of a built document. Hidden when <paramref name="redact"/> and the process setting both allow it, and
    /// the "Redacted" line of the header says exactly that. The same document can be rendered both ways without sampling again.
    /// </summary>
    string RenderMarkdown(ReportDocument document, Language language, bool redact, string? appVersion = null) => throw new NotSupportedException();

    /// <summary>The JSON of a built document, same rule as <see cref="RenderMarkdown"/>; "redacted" in it is what was really done.</summary>
    JsonNode RenderJson(ReportDocument document, bool redact) => throw new NotSupportedException();
}
