using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>
/// The real <see cref="IProtectionPolicy"/>: the verdict of <see cref="ProcessGuard.Inspect"/> (query rights only, the
/// same rules the operations apply on their own handle), in the user's language. Only a fully answered "nothing
/// protects it" is <see cref="ProtectionKind.None"/>; a question that failed is <see cref="ProtectionKind.Unknown"/>,
/// which is treated exactly like Protected by everything that calls this.
/// </summary>
public sealed class ProtectionPolicy : IProtectionPolicy
{
    private readonly Func<ProcessKey, ProtectionAssessment> _inspect;

    public ProtectionPolicy()
    {
        var self = ProcessOperations.CurrentSelf();
        var systemDirectory = Environment.SystemDirectory;
        _inspect = key => ProcessGuard.Inspect(key.Pid, key.CreateTime, self, systemDirectory);
    }

    /// <summary>For tests: the answer comes from <paramref name="inspect"/> instead of the system.</summary>
    internal ProtectionPolicy(Func<ProcessKey, ProtectionAssessment> inspect) => _inspect = inspect;

    public ProtectionVerdict Evaluate(ProcessKey key)
    {
        ProtectionAssessment assessment;
        try
        {
            assessment = _inspect(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong, "allowed" is never the answer.
            assessment = new ProtectionAssessment(ProtectionOutcome.Unknown, ProtectionReason.CannotConfirm);
        }

        return assessment.Outcome switch
        {
            ProtectionOutcome.Allowed => new ProtectionVerdict(ProtectionKind.None, null),
            ProtectionOutcome.Protected => new ProtectionVerdict(ProtectionKind.Protected, Strings.Get(ProcessGuard.KeyFor(assessment.Reason))),
            _ => new ProtectionVerdict(ProtectionKind.Unknown, Strings.Get(MessageKeys.ProtectedUnknown)),
        };
    }
}
