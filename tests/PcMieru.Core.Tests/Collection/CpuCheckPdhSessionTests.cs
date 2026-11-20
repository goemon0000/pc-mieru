using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The set-up of the CPU check's own PDH query (DESIGN-A 6.2): a query that fails halfway through must be closed, not leaked. A scripted query
/// stands in for PDH here: nothing opens a real handle or reads a counter.
/// </summary>
public sealed class CpuCheckPdhSessionTests
{
    private sealed class FakeQuery : IPdhQuery
    {
        public bool IsOpen { get; init; } = true;

        public int Disposed { get; private set; }

        public List<string> Added { get; } = [];

        public int Collects { get; private set; }

        /// <summary>Throws on the n-th call of <see cref="Add"/> (1 is the first). 0: never.</summary>
        public int AddThrowsOn { get; init; }

        public bool CollectThrows { get; init; }

        public uint CollectStatus { get; init; }

        public nint Add(string englishPath, out uint status)
        {
            Added.Add(englishPath);
            if (Added.Count == AddThrowsOn)
            {
                throw new InvalidOperationException("the fake counter could not be added");
            }

            status = 0;
            return Added.Count; // a counter handle that is not zero: the counter exists
        }

        public uint Collect()
        {
            Collects++;
            return CollectThrows ? throw new InvalidOperationException("the fake collection failed") : CollectStatus;
        }

        public void Dispose() => Disposed++;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_counter_that_cannot_be_added_closes_the_query_and_the_exception_goes_on(int failingAdd)
    {
        var query = new FakeQuery { AddThrowsOn = failingAdd };

        var failure = Assert.Throws<InvalidOperationException>(() => new CpuCheckPdhSession(query));

        Assert.Contains("could not be added", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, query.Disposed);
        Assert.Equal(0, query.Collects);
    }

    [Fact]
    public void A_first_collection_that_throws_closes_the_query_and_the_exception_goes_on()
    {
        var query = new FakeQuery { CollectThrows = true };

        var failure = Assert.Throws<InvalidOperationException>(() => new CpuCheckPdhSession(query));

        Assert.Contains("collection failed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(4, query.Added.Count); // every counter was added before the baseline was taken
        Assert.Equal(1, query.Disposed);
    }

    [Fact]
    public void A_set_up_that_works_keeps_the_query_open_and_the_session_closes_it_once()
    {
        var query = new FakeQuery();

        var session = new CpuCheckPdhSession(query);

        Assert.Equal(0, query.Disposed);
        Assert.True(session.IsOpen);
        Assert.Equal(1, query.Collects); // the baseline
        Assert.Equal(
            [
                @"\Processor Information(_Total)\Processor Frequency",
                @"\Processor Information(_Total)\% Processor Performance",
                @"\Processor Information(_Total)\% Processor Time",
                @"\Energy Meter(*)\Power",
            ],
            query.Added);

        session.Dispose();

        Assert.Equal(1, query.Disposed);
    }

    [Fact]
    public void A_query_that_did_not_open_is_not_touched_and_makes_no_exception()
    {
        var query = new FakeQuery { IsOpen = false };

        var session = new CpuCheckPdhSession(query);

        Assert.False(session.IsOpen);
        Assert.Empty(query.Added);
        Assert.Equal(0, query.Collects);
        Assert.Equal(0, query.Disposed); // nothing was opened, so nothing was closed on the failure path; the owner's Dispose closes it
        Assert.Null(session.Sample().EffectiveMhz);
    }

    [Fact]
    public void The_query_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => new CpuCheckPdhSession(null!));
    }
}
