namespace PcMieru.Checks.Load;

/// <summary>
/// The memory check's rows and verdicts (DESIGN-A §3.2, §14.3) from what the load measured. A pure function: no clock, no thread, no memory.
/// <list type="bullet">
/// <item>One row is judged: <c>memory.mismatches</c>. A mismatch that was found stays a finding whatever happened to the rest of the run, a
/// stopped one too (then with reason partial). Every other row is an observation.</item>
/// <item>A run that was stopped is partial: every row that has a value keeps it and says <see cref="CheckReason.Partial"/>, and a judged row
/// that would have been ok is <see cref="ItemVerdict.Unavailable"/> (a partial row is never ok, §14.0). The overall verdict of a stopped run
/// is therefore never ok.</item>
/// <item>A value that was not measured is a missing value with a reason, never 0: a rate over no bytes, a mismatch count over no verified byte.</item>
/// </list>
/// The notes (<c>memory.borrowed_region_only</c> first, <c>memory.pressure</c> when the machine needed its memory) are the procedure's.
/// </summary>
internal static class MemoryJudge
{
    public static IReadOnlyList<CheckItem> Judge(MemoryLoadResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var stopped = r.Stopped;
        var items = new List<CheckItem>
        {
            Number("memory.duration", r.Seconds, CheckSource.Load, stopped),
        };

        if (r.StatusKnown)
        {
            items.Add(Bytes("memory.total", r.TotalBytes, CheckSource.Win32, stopped));
            items.Add(Bytes("memory.available_at_start", r.AvailableAtStartBytes, CheckSource.Win32, stopped));
        }
        else
        {
            // Windows could not say how much memory there is: not a finding about the memory, but an honest "could not read it".
            items.Add(CheckItem.Create("memory.total", null, ItemVerdict.Info, CheckSource.Win32, CheckReason.Error));
            items.Add(CheckItem.Create("memory.available_at_start", null, ItemVerdict.Info, CheckSource.Win32, CheckReason.Error));
        }

        // Zero is a real answer here: nothing could be borrowed. It is not partial (the run is not "cut short" by it): the rows that rest on
        // the verification say so.
        items.Add(Bytes("memory.borrowed", r.BorrowedBytes, CheckSource.Load, stopped));
        // A count, always, and 0 too: the passes that were finished over the whole borrowed range. Partial when the run was stopped, and when
        // some bytes were verified but not one pass was finished (a count of 0 that stands for "cut short", not for "nothing to do"). Nothing
        // borrowed is a plain 0 with no reason, like memory.borrowed.
        var passesPartial = stopped || (r.Passes == 0 && r.VerifiedBytes > 0);
        items.Add(CheckItem.Create("memory.passes", CheckValue.Int(r.Passes), ItemVerdict.Info, CheckSource.Load, passesPartial ? CheckReason.Partial : null));

        // No verified byte is a value of 0 that says "nothing was checked": partial, whether the run was stopped or not.
        items.Add(Bytes("memory.verified", r.VerifiedBytes, CheckSource.Load, stopped || r.VerifiedBytes <= 0));
        items.Add(Mismatches(r, stopped));
        items.Add(Rate("memory.write_rate", r.WriteRate, stopped));
        items.Add(Rate("memory.verify_rate", r.VerifyRate, stopped));
        return items;
    }

    private static CheckItem Mismatches(MemoryLoadResult r, bool stopped)
    {
        const string id = "memory.mismatches";
        if (r.Mismatches >= 1)
        {
            // Found, so it stays a finding. Nothing that identifies the place is kept: the evidence says what kind of finding it is.
            return CheckItem.Create(id, CheckValue.Int(r.Mismatches), ItemVerdict.Check, CheckSource.Load, stopped ? CheckReason.Partial : null,
                evidence: CheckEvidence.OfCode(CheckEvidenceCode.PatternMismatch));
        }

        if (r.VerifiedBytes <= 0)
        {
            // Nothing was read back, so "no mismatches" says nothing (DESIGN-A 14.3). Partial whether the run was stopped or not: the load did
            // not get to compare, which is not an error of the check.
            return CheckItem.Create(id, null, ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Partial);
        }

        return stopped
            ? CheckItem.Create(id, CheckValue.Int(0), ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Partial)
            : CheckItem.Create(id, CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Load);
    }

    // A rate over no bytes (or no time) is missing, never 0.
    private static CheckItem Rate(string id, double? value, bool stopped) =>
        value is { } v && double.IsFinite(v)
            ? CheckItem.Create(id, CheckValue.Number(v), ItemVerdict.Info, CheckSource.Load, stopped ? CheckReason.Partial : null)
            : CheckItem.Create(id, null, ItemVerdict.Info, CheckSource.Load, CheckReason.Partial);

    private static CheckItem Number(string id, double value, CheckSource source, bool partial) =>
        CheckItem.Create(id, CheckValue.Number(value), ItemVerdict.Info, source, partial ? CheckReason.Partial : null);

    private static CheckItem Bytes(string id, long value, CheckSource source, bool partial) =>
        CheckItem.Create(id, CheckValue.Dec((ulong)Math.Max(0, value)), ItemVerdict.Info, source, partial ? CheckReason.Partial : null);
}
