namespace PcMieru.Wire;

/// <summary>
/// Keys of the messages the management operations can produce. Wire has no texts (it has no language): Core registers
/// ja and en for every constant here (<c>ManagementStrings</c>; a test fails if one is missing) and fills the
/// placeholders from <see cref="ActionResult.Args"/>.
/// Placeholders per key are written next to it.
/// </summary>
public static class MessageKeys
{
    // --- protection (the verdict reasons) ---
    public const string ProtectedSystem = "mgmt.protected.system";
    public const string ProtectedSelf = "mgmt.protected.self";
    public const string ProtectedCritical = "mgmt.protected.critical";
    public const string ProtectedProcess = "mgmt.protected.process";
    public const string ProtectedCore = "mgmt.protected.core";

    /// <summary>A question could not be answered, so nothing was done (registered by the skeleton).</summary>
    public const string ProtectedUnknown = "mgmt.protected.unknown";

    // --- processes ---
    /// <summary>{pid}</summary>
    public const string ProcessTerminated = "mgmt.process.terminated";

    /// <summary>{pid}: TerminateProcess succeeded but the process was still there after the wait.</summary>
    public const string ProcessTerminatePending = "mgmt.process.terminate-pending";

    public const string ProcessGone = "mgmt.process.gone";
    public const string ProcessReplaced = "mgmt.process.replaced";
    public const string ProcessDenied = "mgmt.process.denied";

    /// <summary>{code}</summary>
    public const string ProcessError = "mgmt.process.error";

    /// <summary>{priority}</summary>
    public const string ProcessPrioritySet = "mgmt.process.priority-set";

    public const string EfficiencyOn = "mgmt.process.efficiency-on";
    public const string EfficiencyOff = "mgmt.process.efficiency-off";
    public const string EfficiencyUnsupported = "mgmt.process.efficiency-unsupported";
    public const string EfficiencyRolledBack = "mgmt.process.efficiency-rolled-back";
    public const string EfficiencyRollbackFailed = "mgmt.process.efficiency-rollback-failed";

    /// <summary>The window got WM_CLOSE and the process ended.</summary>
    public const string CloseDone = "mgmt.process.closed";

    /// <summary>WM_CLOSE was delivered; the process is still running (it may be asking to save).</summary>
    public const string CloseSent = "mgmt.process.close-sent";

    public const string CloseNoWindow = "mgmt.process.close-no-window";
    public const string CloseNoReply = "mgmt.process.close-no-reply";

    /// <summary>{count}</summary>
    public const string TreeDone = "mgmt.process.tree-done";

    /// <summary>{done} {total}</summary>
    public const string TreePartial = "mgmt.process.tree-partial";

    public const string TreeNothing = "mgmt.process.tree-nothing";

    public const string AffinitySet = "mgmt.process.affinity-set";
    public const string AffinityInvalid = "mgmt.process.affinity-invalid";
    public const string AffinityMultiGroup = "mgmt.process.affinity-multigroup";

    // --- services ---
    /// <summary>{name}</summary>
    public const string ServiceStarted = "mgmt.service.started";

    /// <summary>{name}</summary>
    public const string ServiceStopped = "mgmt.service.stopped";

    /// <summary>{name}</summary>
    public const string ServiceRestarted = "mgmt.service.restarted";

    /// <summary>{name}</summary>
    public const string ServiceAlreadyRunning = "mgmt.service.already-running";

    /// <summary>{name}</summary>
    public const string ServiceAlreadyStopped = "mgmt.service.already-stopped";

    /// <summary>{name}: the request was accepted but RUNNING was not reached in time.</summary>
    public const string ServiceStartPending = "mgmt.service.start-pending";

    /// <summary>{name}: the request was accepted but STOPPED was not reached in time.</summary>
    public const string ServiceStopPending = "mgmt.service.stop-pending";

    /// <summary>{name} {exitCode}</summary>
    public const string ServiceStartFailed = "mgmt.service.start-failed";

    /// <summary>{name}</summary>
    public const string ServiceNotFound = "mgmt.service.not-found";

    /// <summary>{name}</summary>
    public const string ServiceDenied = "mgmt.service.denied";

    /// <summary>{name}</summary>
    public const string ServiceDisabled = "mgmt.service.disabled";

    /// <summary>{name} {dependents}: running services that need it; v1 does not stop them for you.</summary>
    public const string ServiceDependentsRunning = "mgmt.service.dependents-running";

    /// <summary>{name}</summary>
    public const string ServiceCannotStop = "mgmt.service.cannot-stop";

    /// <summary>{name}: restart stopped it but it did not come back.</summary>
    public const string ServiceRestartStoppedOnly = "mgmt.service.restart-stopped-only";

    /// <summary>{name} {code}</summary>
    public const string ServiceError = "mgmt.service.error";

    public const string ServiceBadName = "mgmt.service.bad-name";

    // --- startup entries ---
    /// <summary>{name}</summary>
    public const string StartupEnabled = "mgmt.startup.enabled";

    /// <summary>{name}</summary>
    public const string StartupDisabled = "mgmt.startup.disabled";

    /// <summary>{name}</summary>
    public const string StartupAlreadyEnabled = "mgmt.startup.already-enabled";

    /// <summary>{name}</summary>
    public const string StartupAlreadyDisabled = "mgmt.startup.already-disabled";

    /// <summary>{id}</summary>
    public const string StartupNotFound = "mgmt.startup.not-found";

    public const string StartupListOnly = "mgmt.startup.list-only";

    /// <summary>The approval value has a type, length or flag we do not know; it is shown and never rewritten.</summary>
    public const string StartupUnknownState = "mgmt.startup.unknown-state";

    public const string StartupDenied = "mgmt.startup.denied";

    /// <summary>{code}</summary>
    public const string StartupError = "mgmt.startup.error";

    /// <summary>{name}: restored to the state before the first change.</summary>
    public const string StartupRestored = "mgmt.startup.restored";

    /// <summary>{id}: nothing is recorded to undo.</summary>
    public const string StartupNothingToUndo = "mgmt.startup.nothing-to-undo";

    // --- drives ---
    public const string DiskRead = "mgmt.disk.read";

    /// <summary>{drive}</summary>
    public const string DiskNotFound = "mgmt.disk.not-found";

    /// <summary>{bus}: nothing is sent to USB bridges, RAID or virtual disks.</summary>
    public const string DiskUnsupportedBus = "mgmt.disk.unsupported-bus";

    public const string DiskDenied = "mgmt.disk.denied";
    public const string DiskNoData = "mgmt.disk.no-data";

    /// <summary>{code}</summary>
    public const string DiskError = "mgmt.disk.error";

    // --- helper client ---
    public const string HelperMissing = "mgmt.helper.missing";

    /// <summary>{code}</summary>
    public const string HelperStartFailed = "mgmt.helper.start-failed";

    public const string HelperNoResult = "mgmt.helper.no-result";

    /// <summary>{code}</summary>
    public const string HelperUnexpectedExit = "mgmt.helper.unexpected-exit";

    // --- run new task ---
    public const string TaskEmpty = "mgmt.task.empty";

    /// <summary>{command}</summary>
    public const string TaskStarted = "mgmt.task.started";

    /// <summary>{command}</summary>
    public const string TaskNotFound = "mgmt.task.not-found";

    /// <summary>{code}</summary>
    public const string TaskFailed = "mgmt.task.failed";

    // --- explorer restart ---
    public const string ShellRestarted = "mgmt.shell.restarted";
    public const string ShellElevatedRefused = "mgmt.shell.elevated-refused";
    public const string ShellFailed = "mgmt.shell.failed";
}
