namespace PcMieru.Party.Import;

/// <summary>What the first start of Parts Life does next, given what <see cref="ImportEngine.Prepare"/> found (DESIGN-C 4.1 items 1 and 2).</summary>
public enum FirstStartStep
{
    /// <summary>Load the save as it is (there is one, or it cannot be read and <c>PartyState.Load</c> keeps it and blocks saving).</summary>
    Load,

    /// <summary>There is no old save: <see cref="ImportEngine.StartFresh"/> writes the first file, then load.</summary>
    StartFreshThenLoad,

    /// <summary>An old save can be brought over: ask the three-choice question.</summary>
    Ask,

    /// <summary>Say why it cannot go on yet, and offer Retry and Close (and, for a hold, Start fresh).</summary>
    Problem,
}

/// <summary>What to do with the result of a <see cref="ImportEngine.Commit"/> or <see cref="ImportEngine.StartFresh"/> call.</summary>
public enum FirstStartAfter
{
    /// <summary>A save is there now: load it and run.</summary>
    Load,

    /// <summary>The world changed under the call (the old product started, the source changed, the list of repairs changed): look again from the start.</summary>
    PrepareAgain,

    /// <summary>It failed or is held: say why.</summary>
    Problem,
}

/// <summary>The decisions of the first start as pure functions of an <see cref="ImportOutcome"/>. No file, no window.</summary>
public static class FirstStartDecision
{
    /// <summary>The step for the status of a plan.</summary>
    public static FirstStartStep Next(ImportOutcome status) => status switch
    {
        ImportOutcome.AlreadyHave => FirstStartStep.Load,

        // Nothing to continue from: the first file comes from the engine, under its lock, never from the save writer of the window.
        ImportOutcome.NoCandidate => FirstStartStep.StartFreshThenLoad,

        ImportOutcome.Ready or ImportOutcome.NeedsRepair => FirstStartStep.Ask,

        // The destination is there but cannot be read: starting fresh would hide it. Load it as it is; PartyState.Load keeps the file and blocks saving.
        ImportOutcome.DestinationBlocked => FirstStartStep.Load,

        ImportOutcome.OldAppRunning or ImportOutcome.SourceUnreadable or ImportOutcome.SourceChanged
            or ImportOutcome.Busy or ImportOutcome.Hold or ImportOutcome.Failed => FirstStartStep.Problem,

        // Not answers of Prepare. Nothing was proven about the old save, so they never start anything.
        _ => FirstStartStep.Problem,
    };

    /// <summary>What follows a write call (<see cref="ImportEngine.Commit"/> or <see cref="ImportEngine.StartFresh"/>).</summary>
    public static FirstStartAfter AfterWrite(ImportOutcome outcome) => outcome switch
    {
        ImportOutcome.Imported or ImportOutcome.StartedFresh or ImportOutcome.AlreadyHave or ImportOutcome.DestinationBlocked => FirstStartAfter.Load,
        ImportOutcome.Busy or ImportOutcome.Failed or ImportOutcome.Hold or ImportOutcome.SourceUnreadable => FirstStartAfter.Problem,
        _ => FirstStartAfter.PrepareAgain,
    };

    /// <summary>Only a hold offers Start fresh next to Retry and Close: the old save cannot be brought over, so starting anew is a real way on.</summary>
    public static bool OffersStartFresh(ImportOutcome outcome) => outcome == ImportOutcome.Hold;
}

/// <summary>The answer to the three-choice question.</summary>
public enum ContinueChoice
{
    Continue,
    Later,
    StartFresh,
}

/// <summary>The answer to a message with Retry and Close.</summary>
public enum ProblemChoice
{
    Retry,
    Close,
    StartFresh,
}

/// <summary>What the first start could not do, for the message. <see cref="Detail"/> is for logs; it is never shown.</summary>
public sealed record ProblemView(ImportOutcome Outcome, string Reason, string? Detail)
{
    public bool CanStartFresh => FirstStartDecision.OffersStartFresh(Outcome);
}

/// <summary>The questions the first start asks a person. The app answers them with windows; tests answer them with a script.</summary>
public interface IFirstStartPrompts
{
    /// <summary>For a plan that is <see cref="ImportOutcome.Ready"/> or <see cref="ImportOutcome.NeedsRepair"/>. For a repair the list is shown before the button, so Continue is the consent.</summary>
    ContinueChoice AskContinue(ImportPlan plan);

    /// <summary>"The old progress cannot be brought over afterwards." Yes or No (the default is No).</summary>
    bool ConfirmStartFresh();

    ProblemChoice ShowProblem(ProblemView problem);
}

/// <summary>How the first start ended.</summary>
public enum FirstStartEnd
{
    /// <summary>A save is in place (or is kept as it is): load it and run.</summary>
    Proceed,

    /// <summary>The user chose Later or Close. Nothing was written; the next start asks again.</summary>
    Exit,
}

/// <summary>
/// The first start of Parts Life (DESIGN-C 4.1 items 1 and 2): looks at the old save through the engine and does what the user decides.
/// The engine does every file operation; this adds none, and never touches the old folders. The engine calls run off the calling thread
/// (the caller waits: no window exists yet), each one taking and giving back the import lock inside its own call.
/// </summary>
public static class FirstStartFlow
{
    public static FirstStartEnd Run(ImportEngine engine, IFirstStartPrompts prompts)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prompts);

        var plan = Prepare(engine);
        while (true)
        {
            switch (FirstStartDecision.Next(plan.Status))
            {
                case FirstStartStep.Load:
                    return FirstStartEnd.Proceed;

                case FirstStartStep.StartFreshThenLoad:
                    if (Settle(StartFresh(engine), engine, ref plan)) return FirstStartEnd.Proceed;
                    continue;

                case FirstStartStep.Ask:
                {
                    var choice = prompts.AskContinue(plan);
                    if (choice == ContinueChoice.Later) return FirstStartEnd.Exit;
                    if (choice == ContinueChoice.StartFresh)
                    {
                        if (!prompts.ConfirmStartFresh()) continue;   // No: the same question again, nothing was done
                        if (Settle(StartFresh(engine), engine, ref plan)) return FirstStartEnd.Proceed;
                        continue;
                    }

                    // Continue. A repair has shown its list before this button, so pressing it is the consent.
                    var current = plan;
                    if (Settle(Off(() => engine.Commit(current, consentToRepair: current.Status == ImportOutcome.NeedsRepair), "commit", current.Candidate), engine, ref plan))
                        return FirstStartEnd.Proceed;
                    continue;
                }

                default:
                {
                    var problem = new ProblemView(plan.Status, plan.Reason, plan.Detail);
                    switch (prompts.ShowProblem(problem))
                    {
                        case ProblemChoice.Close:
                            return FirstStartEnd.Exit;
                        case ProblemChoice.StartFresh when problem.CanStartFresh:
                            if (!prompts.ConfirmStartFresh()) continue;
                            if (Settle(StartFresh(engine), engine, ref plan)) return FirstStartEnd.Proceed;
                            continue;
                        default:
                            plan = Prepare(engine);   // Retry (and a Start fresh that this message did not offer)
                            continue;
                    }
                }
            }
        }
    }

    /// <summary>True when a save is in place. Otherwise <paramref name="plan"/> is what the loop looks at next.</summary>
    private static bool Settle(ImportResult result, ImportEngine engine, ref ImportPlan plan)
    {
        switch (FirstStartDecision.AfterWrite(result.Outcome))
        {
            case FirstStartAfter.Load:
                return true;
            case FirstStartAfter.PrepareAgain:
                plan = Prepare(engine);
                return false;
            default:
                // A failure or a hold: shown through the same path as a plan that says so.
                plan = new ImportPlan { Status = result.Outcome, Reason = result.Reason, Detail = result.Detail, Candidate = result.Candidate };
                return false;
        }
    }

    private static ImportPlan Prepare(ImportEngine engine)
    {
        try
        {
            return Task.Run(engine.Prepare).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ImportPlan { Status = ImportOutcome.Failed, Reason = ImportReason.InternalError, Detail = e.GetType().Name };
        }
    }

    private static ImportResult StartFresh(ImportEngine engine) => Off(engine.StartFresh, "start-fresh", null);

    private static ImportResult Off(Func<ImportResult> call, string what, ImportCandidate? candidate)
    {
        try
        {
            return Task.Run(call).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ImportResult { Outcome = ImportOutcome.Failed, Reason = ImportReason.InternalError, Detail = what + ": " + e.GetType().Name, Candidate = candidate };
        }
    }
}
