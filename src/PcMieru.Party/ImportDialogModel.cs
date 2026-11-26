using PcMieru.Party.I18n;
using PcMieru.Party.Import;

namespace PcMieru.Party;

/// <summary>What a button of the import dialogs means.</summary>
internal enum ImportDialogResult
{
    Continue,
    Later,
    StartFresh,
    Retry,
    Close,
}

internal sealed record ImportDialogButton(string Text, ImportDialogResult Result, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// What an import dialog says and offers, as plain data (no WPF): the window only draws it and the tests read it.
/// <see cref="List"/> is the fields that could not be read (a repair), shown before the buttons so the first button is the consent.
/// </summary>
internal sealed record ImportDialogModel(
    string Title,
    string Body,
    string? ListHeader,
    IReadOnlyList<string> List,
    string? Note,
    IReadOnlyList<ImportDialogButton> Buttons)
{
    /// <summary>What closing the window by the cross or Escape means: Later (the question) or Close (the message). Never a write.</summary>
    public ImportDialogResult CancelResult => Buttons.First(b => b.IsCancel).Result;
}

internal static class ImportDialogModels
{
    /// <summary>The three-choice question for a plan that is Ready or NeedsRepair.</summary>
    public static ImportDialogModel Ask(ImportPlan plan)
    {
        var repair = plan.Status == ImportOutcome.NeedsRepair;
        var body = plan.Candidate?.Origin == ImportOrigin.PartsParty ? Strings.ImportBodyParty : Strings.ImportBodyLife;
        var note = repair ? Strings.ImportRepairNote + " " + Strings.ImportKept : Strings.ImportKept;
        return new ImportDialogModel(
            Strings.ImportTitle,
            body,
            repair ? Strings.ImportRepairHeader : null,
            repair ? plan.RepairedFields : [],
            note,
            [
                new ImportDialogButton(Strings.ImportContinue, ImportDialogResult.Continue, IsDefault: true),
                new ImportDialogButton(Strings.ImportLater, ImportDialogResult.Later, IsCancel: true),
                new ImportDialogButton(Strings.ImportStartFresh, ImportDialogResult.StartFresh),
            ]);
    }

    /// <summary>The message for something that stops the first start: the reason, Retry and Close (and Start fresh for a hold).</summary>
    public static ImportDialogModel Problem(ProblemView problem)
    {
        var buttons = new List<ImportDialogButton> { new(Strings.ImportRetry, ImportDialogResult.Retry, IsDefault: true) };
        if (problem.CanStartFresh) buttons.Add(new ImportDialogButton(Strings.ImportStartFresh, ImportDialogResult.StartFresh));
        buttons.Add(new ImportDialogButton(Strings.ImportCloseButton, ImportDialogResult.Close, IsCancel: true));
        return new ImportDialogModel(Strings.ImportProblemTitle, Strings.ImportMessage(problem.Outcome, problem.Reason), null, [], null, buttons);
    }

    /// <summary>The dialog of a <c>--smoke --import-preview</c> case. A made-up plan: no engine, no path is opened (the candidate path is only a label).</summary>
    public static ImportDialogModel Preview(PartyImportPreview which) => which switch
    {
        PartyImportPreview.Ready => Ask(new ImportPlan
        {
            Status = ImportOutcome.Ready,
            Reason = ImportReason.Accept,
            Candidate = new ImportCandidate(ImportOrigin.PartsLife, "preview"),
        }),
        PartyImportPreview.Repair => Ask(new ImportPlan
        {
            Status = ImportOutcome.NeedsRepair,
            Reason = ImportReason.NeedsRepair,
            Candidate = new ImportCandidate(ImportOrigin.PartsParty, "preview"),
            RepairedFields = ["Adventure.Gold", "Levels.Cpu"],
        }),
        PartyImportPreview.OldApp => Problem(new ProblemView(ImportOutcome.OldAppRunning, ImportReason.OldAppRunning, null)),
        PartyImportPreview.Hold => Problem(new ProblemView(ImportOutcome.Hold, ImportReason.VersionFuture, null)),
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };
}
