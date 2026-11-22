using PcMieru.Checks.Build;
using PcMieru.Checks.SystemInfo;
using PcMieru.Checks.Tests.SystemInfo;

namespace PcMieru.Checks.Tests.Build;

/// <summary>
/// A WMI that never calls Windows and answers by the exact text of the question (the three memory questions are three different texts, so
/// a script keyed by the class name, like <see cref="FakeWmiReader"/>, could not tell them apart). Each row goes through the mapper of
/// the question, inside the answer, the way the real reader does; a mapper that throws is a failed reading.
/// </summary>
internal sealed class ScriptedWmi : IWmiReader
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<CancellationToken, Task<(IReadOnlyList<IWmiRow>? Rows, CheckReason? Reason)>>> _answers = [];
    private readonly List<(string Scope, string Text)> _asked = [];
    private readonly List<QueryDeadline> _deadlines = [];

    /// <summary>Every question asked, in order, with its namespace.</summary>
    public IReadOnlyList<(string Scope, string Text)> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    /// <summary>The deadline each question was given, in the order they were asked.</summary>
    public IReadOnlyList<QueryDeadline> Deadlines
    {
        get
        {
            lock (_gate)
            {
                return [.. _deadlines];
            }
        }
    }

    public static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    /// <summary>The question gets these rows.</summary>
    public ScriptedWmi Rows(string text, params Dictionary<string, object?>[] rows) => Add(text, _ =>
        Task.FromResult<(IReadOnlyList<IWmiRow>?, CheckReason?)>(([.. rows.Select(r => new FakeRow(r))], null)));

    /// <summary>The question gets no value, for this reason.</summary>
    public ScriptedWmi Reason(string text, CheckReason reason) => Add(text, _ =>
        Task.FromResult<(IReadOnlyList<IWmiRow>?, CheckReason?)>((null, reason)));

    /// <summary>
    /// A real timeout: the question runs the whole of its deadline (<paramref name="elapse"/> moves the test's clock past it) and then gets no
    /// value for <see cref="CheckReason.Timeout"/>. A plain <see cref="Reason"/> of Timeout leaves the clock where it was, which is the
    /// other kind of timeout: a lane that refused at once (no free slot) with the whole deadline still left.
    /// </summary>
    public ScriptedWmi TimesOut(string text, Action elapse) => Add(text, _ =>
    {
        elapse();
        return Task.FromResult<(IReadOnlyList<IWmiRow>?, CheckReason?)>((null, CheckReason.Timeout));
    });

    /// <summary>The reader itself throws (a bug below it; the readers must still give an empty category).</summary>
    public ScriptedWmi Throws(string text, Exception exception) => Add(text, _ => throw exception);

    /// <summary>The question never answers by itself; it ends only when the token is cancelled.</summary>
    public ScriptedWmi Hangs(string text) => Add(text, async ct =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return (null, CheckReason.Error);
    });

    public async Task<CheckReading<IReadOnlyList<T>>> ReadAsync<T>(WmiQuery<T> query, QueryDeadline deadline, CancellationToken ct)
    {
        Func<CancellationToken, Task<(IReadOnlyList<IWmiRow>? Rows, CheckReason? Reason)>>? answer;
        lock (_gate)
        {
            _asked.Add((query.Scope, query.Text));
            _deadlines.Add(deadline);
            _answers.TryGetValue(query.Text, out answer);
        }

        if (answer is null)
        {
            return CheckReading<IReadOnlyList<T>>.Failed();
        }

        var (rows, reason) = await answer(ct);
        if (rows is null)
        {
            return CheckReading<IReadOnlyList<T>>.Missing(reason ?? CheckReason.Error);
        }

        try
        {
            return CheckReading<IReadOnlyList<T>>.Ok([.. rows.Select(query.Map)]);
        }
        catch (Exception)
        {
            return CheckReading<IReadOnlyList<T>>.Failed();
        }
    }

    private ScriptedWmi Add(string text, Func<CancellationToken, Task<(IReadOnlyList<IWmiRow>? Rows, CheckReason? Reason)>> answer)
    {
        lock (_gate)
        {
            _answers[text] = answer;
        }

        return this;
    }
}

/// <summary>A graphics reader that answers what the test says, and records that it was asked.</summary>
internal sealed class ScriptedGpu(Func<QueryDeadline, CancellationToken, Task<CheckReading<IReadOnlyList<RawGpu>>>> answer) : IGpuReader
{
    private int _asked;

    public int Asked => Volatile.Read(ref _asked);

    public static ScriptedGpu Ok(params RawGpu[] gpus) => new((_, _) => Task.FromResult(CheckReading<IReadOnlyList<RawGpu>>.Ok(gpus)));

    public static ScriptedGpu Reason(CheckReason reason) => new((_, _) => Task.FromResult(CheckReading<IReadOnlyList<RawGpu>>.Missing(reason)));

    /// <summary>A real timeout of the graphics reader: it ran the whole deadline (<paramref name="elapse"/> moves the clock past it). See <see cref="ScriptedWmi.TimesOut"/>.</summary>
    public static ScriptedGpu TimesOut(Action elapse) => new((_, _) =>
    {
        elapse();
        return Task.FromResult(CheckReading<IReadOnlyList<RawGpu>>.TimedOut());
    });

    public static ScriptedGpu Throws(Exception exception) => new((_, _) => throw exception);

    public Task<CheckReading<IReadOnlyList<RawGpu>>> ReadAsync(QueryDeadline deadline, CancellationToken ct)
    {
        Interlocked.Increment(ref _asked);
        return answer(deadline, ct);
    }
}
