namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// What a deadline-bounded query gives back (DESIGN-A §6.1): a complete value, or no value and the reason it is missing. Never both, and
/// never a partial value: a count or a row list that was cut short (deadline, error, stop, truncation) is thrown away, so nothing
/// that comes out of a query can be a number that merely looks complete.
/// <para>
/// The reasons are the ones of <see cref="CheckReason"/>: <see cref="CheckReason.Timeout"/> (deadline passed, or every slot of the
/// lane was held by a call that has not returned), <see cref="CheckReason.Error"/>, <see cref="CheckReason.Denied"/>,
/// <see cref="CheckReason.Unsupported"/> and <see cref="CheckReason.Partial"/> (the run was stopped, or the count passed its cap). The
/// non-generic <see cref="CheckReading"/> of the history reader is unrelated: it is a file that was read.
/// </para>
/// </summary>
internal sealed class CheckReading<T>
{
    private readonly T? _value;

    private CheckReading(T? value, CheckReason? reason)
    {
        _value = value;
        Reason = reason;
    }

    /// <summary>True when the query ended normally and <see cref="Value"/> is the complete answer.</summary>
    public bool HasValue => Reason is null;

    /// <summary>Why there is no value; null exactly when there is one.</summary>
    public CheckReason? Reason { get; }

    public T Value => Reason is null ? _value! : throw new InvalidOperationException($"There is no value: {Reason}.");

    public static CheckReading<T> Ok(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);

    public static CheckReading<T> Missing(CheckReason reason) => new(default, reason);

    /// <summary>The deadline passed, or the lane had no free slot.</summary>
    public static CheckReading<T> TimedOut() => Missing(CheckReason.Timeout);

    /// <summary>The query could not be done or broke off: a Windows error, a mapper that threw.</summary>
    public static CheckReading<T> Failed() => Missing(CheckReason.Error);

    /// <summary>The run was stopped, or the count passed its cap (a count that was cut short is not a count).</summary>
    public static CheckReading<T> Stopped() => Missing(CheckReason.Partial);

    public override string ToString() => Reason is null ? $"Ok({_value})" : $"Missing({Reason})";
}
