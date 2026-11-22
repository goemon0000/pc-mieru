using System.Globalization;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The ".delta" rows of one disk, exactly as DESIGN-A §3.3, §3.5-5 to 7 and §14.5 write them. The storage check calls this with the
/// outcome of <see cref="BaselineEngine.Complete"/>; the rows pass <see cref="CheckResultValidator"/>.
/// </summary>
public static class BaselineDeltaRows
{
    /// <summary>One ".delta" row for each counter the disk's outcome lists, in the order of the item registry. Empty for a disk with no counters.</summary>
    public static IReadOnlyList<CheckItem> Build(BaselineDiskOutcome disk, CheckSubject subject)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(subject);
        var rows = new List<CheckItem>(disk.Counters.Count);
        foreach (var counter in disk.Counters)
        {
            rows.Add(Row(counter, subject));
        }

        return rows;
    }

    /// <summary>The row of one counter. Not comparable: no value, info, partial, the code only. Compared: the signed change and what it rests on.</summary>
    public static CheckItem Row(BaselineCounterOutcome counter, CheckSubject subject)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(subject);
        if (!counter.IsCompared)
        {
            return CheckItem.Create(
                counter.DeltaId,
                value: null,
                ItemVerdict.Info,
                CheckSource.Baseline,
                CheckReason.Partial,
                subject,
                CheckEvidence.OfCode(counter.Code!.Value));
        }

        var name = counter.CounterId["storage.".Length..];
        var judged = BaselineCounters.GrowthVerdict(name);
        var verdict = ItemVerdict.Info;
        CheckEvidenceCode? code = null;
        if (judged is { } whenGrown)
        {
            if (counter.Grew)
            {
                verdict = whenGrown;
                code = name == BaselineCounters.CriticalWarning ? CheckEvidenceCode.NewBits
                    : name == BaselineCounters.Ata(199) ? CheckEvidenceCode.LikelyCable
                    : CheckEvidenceCode.CounterIncreased;
            }
            else
            {
                verdict = ItemVerdict.Ok;
                code = counter.After < counter.Before ? CheckEvidenceCode.CounterDecreased : null;
            }
        }

        var evidence = new CheckEvidence
        {
            Code = code,
            Before = counter.Before!.Value.ToString(CultureInfo.InvariantCulture),
            After = counter.After!.Value.ToString(CultureInfo.InvariantCulture),
            SinceUtc = counter.SinceUtc,
            NewBits = name == BaselineCounters.CriticalWarning && counter.Grew ? counter.NewBits : null,
        };
        return CheckItem.Create(counter.DeltaId, CheckValue.SignedDec(counter.Change), verdict, CheckSource.Baseline, reason: null, subject, evidence);
    }
}
