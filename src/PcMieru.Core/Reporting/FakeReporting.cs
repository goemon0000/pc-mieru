using System.Globalization;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>
/// Skeleton rules (two of the section 6 rules) so the vertical slice has something to show. Owner E replaces the body.
/// Findings are evidence-backed and phrased as facts or candidates, never as verdicts.
/// </summary>
public sealed class FakeObservationEngine : IObservationEngine, IFakeImplementation
{
    public IReadOnlyList<Observation> Evaluate(ObservationInput input)
    {
        var found = new List<Observation>();
        var cpu = input.Current.System.Cpu;
        if (cpu.TotalPercent.TryGet(out var total) && total < 30)
        {
            for (var i = 0; i < cpu.PerLogicalPercent.Count; i++)
            {
                if (cpu.PerLogicalPercent[i].TryGet(out var one) && one >= 95)
                {
                    var top = input.Current.Processes
                        .Where(p => p.CpuPercent.IsOk && p.Key.Pid != 0)
                        .OrderByDescending(p => p.CpuPercent.GetOr(0))
                        .Take(3)
                        .ToArray();
                    found.Add(new Observation(
                        "cpu.singleCoreSaturated",
                        ObservationSeverity.Notice,
                        ObservationCertainty.Candidate,
                        "obs.cpu.singleCore",
                        new Dictionary<string, string>
                        {
                            ["cpu"] = i.ToString(CultureInfo.InvariantCulture),
                            ["total"] = total.ToString("0.#", CultureInfo.InvariantCulture),
                            ["top"] = string.Join(", ", top.Select(p => p.Name)),
                        },
                        [
                            new Evidence("cpu.total", total.ToString("0.#", CultureInfo.InvariantCulture), "%"),
                            new Evidence("cpu.logical" + i.ToString(CultureInfo.InvariantCulture), one.ToString("0.#", CultureInfo.InvariantCulture), "%"),
                        ],
                        top.Select(p => p.Key).ToArray()));
                    break;
                }
            }
        }

        var mem = input.Current.System.Memory;
        if (mem.CommitTotalBytes.TryGet(out var commit) && mem.CommitLimitBytes.TryGet(out var limit) && limit > 0 && commit * 10 > limit * 9)
        {
            var percent = 100.0 * commit / limit;
            found.Add(new Observation(
                "memory.commitHigh",
                ObservationSeverity.Warning,
                ObservationCertainty.Fact,
                "obs.memory.commitHigh",
                new Dictionary<string, string> { ["percent"] = percent.ToString("0", CultureInfo.InvariantCulture) },
                [new Evidence("memory.commitPercent", percent.ToString("0.#", CultureInfo.InvariantCulture), "%")],
                []));
        }

        return found;
    }
}
