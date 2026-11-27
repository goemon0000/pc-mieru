using PcMieru.Core.Siblings;

namespace PcMieru.Party.Import;

/// <summary>What the first start learned for the steps after it: set by <see cref="FirstStartFlow.Run"/> when a write of its own brought the old save over.</summary>
public sealed class FirstStartReport
{
    /// <summary>True when this run's Commit copied the old save into the new place (<see cref="ImportOutcome.Imported"/>). A fresh start, or a save that was already there, is not an import.</summary>
    public bool Imported { get; set; }
}

/// <summary>The two things the sign-in switch says to a person. The app answers them with windows; tests answer them with a script.</summary>
public interface IRunSwitchPrompts
{
    /// <summary>"Switch the sign-in start to the new app too?" (the question says that the save copy already succeeded). True = switch.</summary>
    bool AskSwitch();

    /// <summary>"Please check the sign-in setting": the switch or its recovery could not be settled (a restore failed, or the user changed an entry meanwhile).</summary>
    void ShowCheckNotice();

    /// <summary>"Could not switch; the old Parts Life still starts at sign-in": the owner said Switch, and the switch was rolled back (every value is as before) or could not start (the lock was busy).</summary>
    void ShowNotSwitchedNotice();
}

/// <summary>How <see cref="RunSwitchFlow.AfterImport"/> ended.</summary>
public enum RunSwitchFlowEnd
{
    /// <summary>Nothing asked: nothing was imported in this run, or the offer did not apply.</summary>
    NotAsked,

    /// <summary>The user chose "Not now". Nothing was written.</summary>
    Declined,

    /// <summary>The sign-in start now runs the new exe.</summary>
    Switched,

    /// <summary>The values changed between the question and the switch (the offer no longer applied). Nothing was written and nothing is shown.</summary>
    NotSwitched,

    /// <summary>The owner said Switch, but it was rolled back (every value is as it was) or the lock was busy: the notice "could not switch" was shown (the checkbox on the PCMieru settings page is the retry).</summary>
    NotSwitchedNotified,

    /// <summary>The notice "please check the sign-in setting" was shown.</summary>
    Notified,
}

/// <summary>
/// DESIGN-C 4.3 on the Party side: the question after a successful import and the recovery at a real start. The decisions are here, the registry is not (a store is handed in), the
/// windows are not (prompts are handed in). The journal is <c>run-switch.json</c> in the new save folder.
/// </summary>
public static class RunSwitchFlow
{
    /// <summary>The journal of the switch: next to the new save (<see cref="ImportPaths.DestinationDir"/>).</summary>
    public static string JournalPath(ImportPaths paths) => RunSwitch.JournalPathFor(paths.DestinationDir);

    /// <summary>
    /// A real start, before the first-start flow: puts back what an unfinished switch left. A clean recovery says nothing; one that could not be settled shows the notice. Returns true when it did.
    /// </summary>
    public static bool RecoverAtStart(IRunStore store, string saveFolder, IRunSwitchPrompts prompts)
    {
        var result = RunSwitch.Recover(store, RunSwitch.JournalPathFor(saveFolder));
        if (!result.NeedsAttention)
        {
            return false;
        }

        prompts.ShowCheckNotice();
        return true;
    }

    /// <summary>
    /// After the first-start flow ended with a load: when this run imported the old save and the old sign-in entry is on and points at the old exe, asks once; on yes, switches. A success is quiet; a result
    /// that needs a person (a restore failed, or an entry was changed meanwhile) shows the check notice, and a clean rollback or a busy lock shows the "could not switch" notice (the owner said yes, so
    /// it is told). The copy of the save is already done and stays done whatever happens here.
    /// </summary>
    public static RunSwitchFlowEnd AfterImport(FirstStartReport report, IRunStore store, string saveFolder, string newExePath, IRunSwitchPrompts prompts)
    {
        if (!report.Imported || !RunSwitch.Offer(store, newExePath).Ask)
        {
            return RunSwitchFlowEnd.NotAsked;
        }

        if (!prompts.AskSwitch())
        {
            return RunSwitchFlowEnd.Declined;
        }

        var result = RunSwitch.Switch(store, RunSwitch.JournalPathFor(saveFolder), newExePath);
        if (result.Status == RunSwitchStatus.Switched)
        {
            return RunSwitchFlowEnd.Switched;
        }

        if (result.NeedsAttention)
        {
            prompts.ShowCheckNotice();
            return RunSwitchFlowEnd.Notified;
        }

        // The owner agreed, so a clean rollback or a busy lock is told (the old start stays as it was; the setting on the PCMieru page is the retry). Only "no longer offered" stays quiet.
        if (result.Status is RunSwitchStatus.RolledBack or RunSwitchStatus.Busy)
        {
            prompts.ShowNotSwitchedNotice();
            return RunSwitchFlowEnd.NotSwitchedNotified;
        }

        return RunSwitchFlowEnd.NotSwitched;
    }
}
