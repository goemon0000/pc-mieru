namespace PcMieru.Checks;

/// <summary>A stretch where a value was known: [<see cref="S"/>, <see cref="E"/>) in microseconds since the run start.</summary>
internal readonly record struct Seg(long S, long E, double V, ulong B)
{
    public long Length => E - S;
}

/// <summary>
/// The observed intervals of one metric (DESIGN-B §7.2): each starts at a new acquisition and lasts until the next point (a value or a "could not be read"),
/// and counts only when that is at most 3P (a gap of exactly 3P is valid, only a longer one makes the whole interval missing). The last value of a
/// normally stopped run gets at most P. Everything is cut to [0, end].
/// </summary>
internal sealed class IntervalSeries
{
    /// <summary>The nominal period used when a frame reported none (the recorder's default sampling period).</summary>
    public const int DefaultPeriodMs = 1000;

    private IntervalSeries(Seg[] items) => Items = items;

    public Seg[] Items { get; }

    public static IntervalSeries Empty { get; } = new([]);

    public long ObservedUs
    {
        get
        {
            long total = 0;
            foreach (var item in Items)
            {
                total += item.Length;
            }

            return total;
        }
    }

    public static IntervalSeries Build(MetricTrack track, long endUs, bool normalEnd)
    {
        var points = track.Points;
        var list = new List<Seg>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Missing)
            {
                continue;
            }

            var start = point.AtUs;
            var period = (point.PeriodMs > 0 ? point.PeriodMs : DefaultPeriodMs) * 1000L;
            long boundary;
            bool valid;
            if (i + 1 < points.Count)
            {
                // A "could not be read" point cuts the interval at the time it was observed; a later value ends it at its own acquire time.
                boundary = points[i + 1].AtUs;
                valid = boundary - start <= 3 * period;
            }
            else if (normalEnd)
            {
                // The terminator of a normal stop: the last value gets at most P, the rest of the run is missing.
                boundary = start + Math.Min(period, Math.Max(0, endUs - start));
                valid = true;
            }
            else
            {
                // An abnormal end closes at the last durable observation.
                boundary = endUs;
                valid = endUs - start <= 3 * period;
            }

            var from = Math.Max(start, 0);
            var to = Math.Min(boundary, endUs);
            if (valid && to > from)
            {
                list.Add(new Seg(from, to, point.Value, point.Bits));
            }
        }

        return new IntervalSeries([.. list]);
    }

    /// <summary>The segments cut to [a, b).</summary>
    public IEnumerable<Seg> Within(long a, long b)
    {
        for (var i = FirstEndingAfter(a); i < Items.Length && Items[i].S < b; i++)
        {
            var item = Items[i];
            var s = Math.Max(item.S, a);
            var e = Math.Min(item.E, b);
            if (e > s)
            {
                yield return item with { S = s, E = e };
            }
        }
    }

    /// <summary>The time-weighted mean over the observed part of [a, b); NaN when nothing was observed there.</summary>
    public double MeanOver(long a, long b)
    {
        double sum = 0;
        long weight = 0;
        foreach (var piece in Within(a, b))
        {
            sum += piece.V * piece.Length;
            weight += piece.Length;
        }

        return weight > 0 ? sum / weight : double.NaN;
    }

    /// <summary>The observed spans (adjacent ones joined), sorted and disjoint.</summary>
    public List<(long S, long E)> Spans()
    {
        var spans = new List<(long S, long E)>();
        foreach (var item in Items)
        {
            if (spans.Count > 0 && spans[^1].E >= item.S)
            {
                spans[^1] = (spans[^1].S, Math.Max(spans[^1].E, item.E));
            }
            else
            {
                spans.Add((item.S, item.E));
            }
        }

        return spans;
    }

    private int FirstEndingAfter(long a)
    {
        var lo = 0;
        var hi = Items.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Items[mid].E > a)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}

/// <summary>Set arithmetic on sorted, disjoint [S, E) lists.</summary>
internal static class SpanSet
{
    public static long Total(List<(long S, long E)> spans)
    {
        long total = 0;
        foreach (var (s, e) in spans)
        {
            total += e - s;
        }

        return total;
    }

    public static List<(long S, long E)> Intersect(List<(long S, long E)> a, List<(long S, long E)> b)
    {
        var result = new List<(long S, long E)>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            var s = Math.Max(a[i].S, b[j].S);
            var e = Math.Min(a[i].E, b[j].E);
            if (e > s)
            {
                result.Add((s, e));
            }

            if (a[i].E < b[j].E)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return result;
    }

    public static List<(long S, long E)> Union(List<(long S, long E)> a, List<(long S, long E)> b)
    {
        var all = new List<(long S, long E)>(a.Count + b.Count);
        all.AddRange(a);
        all.AddRange(b);
        all.Sort(static (x, y) => x.S.CompareTo(y.S));
        var result = new List<(long S, long E)>();
        foreach (var (s, e) in all)
        {
            if (result.Count > 0 && result[^1].E >= s)
            {
                result[^1] = (result[^1].S, Math.Max(result[^1].E, e));
            }
            else
            {
                result.Add((s, e));
            }
        }

        return result;
    }

    /// <summary>The segments of a series cut to a span list (the pieces keep their value).</summary>
    public static IEnumerable<Seg> Clip(Seg[] segments, List<(long S, long E)> spans)
    {
        int i = 0, j = 0;
        while (i < segments.Length && j < spans.Count)
        {
            var s = Math.Max(segments[i].S, spans[j].S);
            var e = Math.Min(segments[i].E, spans[j].E);
            if (e > s)
            {
                yield return segments[i] with { S = s, E = e };
            }

            if (segments[i].E < spans[j].E)
            {
                i++;
            }
            else
            {
                j++;
            }
        }
    }

    /// <summary>
    /// The time-weighted median: the value where the cumulative time reaches half. On an exact tie the two neighbours are averaged, which
    /// for equal weights is the usual median (the middle one when odd, the mean of the two middle ones when even).
    /// </summary>
    public static double WeightedMedian(List<(double V, long W)> items)
    {
        if (items.Count == 0)
        {
            return double.NaN;
        }

        items.Sort(static (x, y) => x.V.CompareTo(y.V));
        long total = 0;
        foreach (var (_, w) in items)
        {
            total += w;
        }

        long cumulative = 0;
        for (var k = 0; k < items.Count; k++)
        {
            cumulative += items[k].W;
            if (cumulative * 2 == total)
            {
                return k + 1 < items.Count ? (items[k].V + items[k + 1].V) / 2 : items[k].V;
            }

            if (cumulative * 2 > total)
            {
                return items[k].V;
            }
        }

        return items[^1].V;
    }
}
