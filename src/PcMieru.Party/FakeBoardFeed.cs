using PcMieru.Core;
using PcMieru.Party.Hardware;

namespace PcMieru.Party;

/// <summary>
/// Fixed frames that move a little each second: one CPU, memory, 2 GPUs, an NVMe and an HDD. The second GPU has no value on every 5th second
/// and the HDD on every 7th, so the "missing stays missing" path is drawn too. For --smoke and for step 4a only; the real feed replaces it.
/// </summary>
internal sealed class FakeBoardFeed : IBoardFeed
{
    private const long GiB = 1024L * 1024 * 1024;
    private const double MiB = 1024.0 * 1024;
    private readonly DateTime _start = DateTime.UtcNow;

    public (BoardFrame Frame, DateTime SampledUtc)? Latest
    {
        get
        {
            var now = DateTime.UtcNow;
            return (FrameAt((int)(now - _start).TotalSeconds), now);
        }
    }

    /// <summary>The frame for second <paramref name="t"/> (a pure function, so a test or a screenshot can ask for any second).</summary>
    internal static BoardFrame FrameAt(int t)
    {
        double Wave(double lo, double hi, double period, double phase) =>
            lo + (hi - lo) * (0.5 + 0.5 * Math.Sin((t + phase) * 2 * Math.PI / period));

        double cpu = Wave(15, 70, 23, 0);
        double mem = Wave(40, 55, 61, 5);
        BoardGpu[] gpus =
        [
            new("fake-gpu-0", "NVIDIA GeForce RTX 5070", Wave(10, 85, 17, 3), (long)(6 * GiB), 12 * GiB, 1),
            new("fake-gpu-1", "NVIDIA GeForce RTX 3080 Ti", t % 5 == 4 ? null : Wave(0, 40, 29, 11), (long)(2 * GiB), 12 * GiB, 2),
        ];
        BoardDiskKind[] kinds =
        [
            new(DiskKind.Nvme, 1, Wave(5, 600, 13, 2) * MiB, false, 1000 * GiB, 420 * GiB, 930 * GiB),
            new(DiskKind.Hdd, 1, t % 7 == 6 ? null : Wave(0, 90, 19, 7) * MiB, t % 7 == 6, 4000 * GiB, 1800 * GiB, 3700 * GiB),
        ];
        double bytes = kinds.Sum(k => k.BytesPerSec ?? 0);
        return new BoardFrame(
            16, cpu,
            new BoardMemory((long)(32 * GiB * mem / 100), 32 * GiB, mem),
            gpus, gpus.Max(g => g.Percent),
            new BoardDisk(bytes, kinds.Any(k => k.Partial)),
            new BoardNet(1 * MiB, 3 * MiB, false))
        {
            AllGpus = gpus,
            DiskKinds = kinds,
            FullestVolumePercent = 70,
        };
    }
}
