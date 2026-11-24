namespace PcMieru.Update.Feed;

/// <summary>How <see cref="IUpdateService.StageAsync"/> ended.</summary>
public enum UpdateStageKind
{
    /// <summary>Nothing was done and nothing was asked: the mode is not Automatic, the candidate is not one a check validated, or this install cannot take a swap.</summary>
    NotAttempted,

    /// <summary>The update is on this PC, unpacked and checked (<see cref="UpdateStageResult.Staged"/>).</summary>
    Staged,

    /// <summary>The stage was tried (or was known to be refused) and did not end in a stage (<see cref="UpdateStageResult.Reason"/>).</summary>
    Refused,
}

/// <summary>What <see cref="IUpdateService.StageAsync"/> did (DESIGN-B 3.3).</summary>
public sealed record UpdateStageResult
{
    private UpdateStageResult(UpdateStageKind kind) => Kind = kind;

    public UpdateStageKind Kind { get; }

    /// <summary>Refused: which rule held (the same enum as a check's, so the settings page says it in the same words).</summary>
    public UpdateRefusal Reason { get; private init; }

    /// <summary>A short text for a log (never shown as the reason: the reason is the enum).</summary>
    public string Detail { get; private init; } = string.Empty;

    /// <summary>Staged: the stage.</summary>
    public StagedUpdate? Staged { get; private init; }

    /// <summary>Staged: true when a stage that was already on this PC (and was checked again in full) was kept, so nothing was downloaded.</summary>
    public bool Reused { get; private init; }

    public static UpdateStageResult NotAttempted(string detail) => new(UpdateStageKind.NotAttempted) { Detail = detail };

    public static UpdateStageResult Done(StagedUpdate staged, bool reused) => new(UpdateStageKind.Staged) { Staged = staged, Reused = reused };

    public static UpdateStageResult Refuse(UpdateRefusal reason, string detail) => new(UpdateStageKind.Refused) { Reason = reason, Detail = detail };

    public override string ToString() => Kind switch
    {
        UpdateStageKind.Staged => $"staged {Staged!.Version}{(Reused ? " (kept)" : string.Empty)}",
        UpdateStageKind.Refused => $"refused: {Reason} ({Detail})",
        _ => $"not attempted ({Detail})",
    };
}
