using PcMieru.Party.Hardware;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Game;

/// <summary>
/// 実機に載っている物から、並べる顔ぶれを決める（旧 Parts Life の Composition を <see cref="BoardFrame"/> から作る形に移した物）。
///
/// **有るものは必ず出し、無いものは出さない。** SSD しか無い機械に HDD くんは立たない。GPU が 2 枚なら 2 体立つ。
/// 鍵と目盛りは旧版のまま（cpu#i・cpu-cooler#i・gpu#i・memory・m2/ssd/hdd・psu）。ディスクの目盛りは <see cref="DiskSlots"/>。
///
/// **欠けは欠けのまま（DESIGN-C 2.3）。** 値が取れなかった席は Load 0・<see cref="PartySlot.Measured"/> が偽・Detail "—" にする。
/// 0 にして渡すと「測れて 0%」と区別がつかず、<see cref="Director.Tick"/> が経験値を足してしまうため、
/// Director は Measured が偽の席を飛ばす。
/// </summary>
public static class PartyComposition
{
    /// <summary>値が無いときの Detail。</summary>
    public const string Missing = "—";

    /// <summary>frame から盤に立つ席を組み立てる。lang は "en" 以外なら日本語。</summary>
    public static List<PartySlot> Build(BoardFrame frame, string lang)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var slots = new List<PartySlot>();

        // --- CPU（ソケットごと。ソケットの割り当てが無ければ全体で 1 体） ---
        var sockets = frame.Sockets is { Count: > 0 }
            ? frame.Sockets
            : [new BoardCpuSocket(frame.CpuPercent, frame.LogicalProcessors)];
        bool many = sockets.Count > 1;
        for (int i = 0; i < sockets.Count; i++)
        {
            double? load = Ratio(sockets[i].Percent);
            string n = many ? $"CPU{i}" : "CPU";
            slots.Add(Slot($"cpu#{i}", "cpu", SlotKind.Socket, n,
                load, l => $"{l * 100:0}%  {Cores(sockets[i].LogicalProcessors, lang)}"));
            // クーラーが冷やす相手は、その CPU ただ一人。必ず同じ数だけ立つ。
            slots.Add(Slot($"cpu-cooler#{i}", "cpu-cooler", SlotKind.Socket, many ? $"FAN{i}" : "FAN", load, _ => ""));
        }

        // --- メモリ ---
        var mem = frame.Memory;
        slots.Add(Slot("memory", "memory", SlotKind.Dimm, "MEM",
            mem is null ? null : Ratio(mem.Percent),
            _ => $"{Gb(mem!.UsedBytes):0.0} / {Gb(mem.TotalBytes):0.0} GB"));

        // --- GPU（取れた順。Gpus が null は「わからない」なので 1 体も立てない） ---
        var gpus = frame.Gpus ?? [];
        for (int i = 0; i < Math.Min(gpus.Count, BoardFrame.MaxGpus); i++)
        {
            var g = gpus[i];
            slots.Add(Slot($"gpu#{i}", "gpu", SlotKind.Pcie, gpus.Count > 1 ? $"GPU{i}" : "GPU",
                Ratio(g.Percent), l => $"{l * 100:0}%  {g.Name}"));
        }

        // --- 記憶装置（有る種類だけ） ---
        bool anyKind = false;
        foreach (var kind in frame.DiskKinds ?? [])
        {
            if (DiskSlots.For(kind.Kind) is not { } spec) continue;
            anyKind = true;
            double? load = kind.BytesPerSec is { } b ? spec.Load(b) : null;
            var slotKind = spec.Key == "m2" ? SlotKind.M2 : SlotKind.Sata;
            slots.Add(new PartySlot(spec.Key, spec.CharacterId, slotKind, spec.Title,
                load is null ? Missing : Capacity(kind.UsedBytes, kind.TotalBytes, kind.DeviceCount, lang),
                load ?? 0, load is not null));
        }

        // 一台も種別を判別できなかったとき（WMI が引けない等）は、まとめて 1 体だけ出す
        if (!anyKind)
        {
            var plain = DiskSlots.Plain;
            double? load = frame.Disk.BytesPerSec is { } b ? plain.Load(b) : null;
            slots.Add(new PartySlot(plain.Key, plain.CharacterId, SlotKind.M2, plain.Title,
                load is null ? Missing : "", load ?? 0, load is not null));
        }

        // --- 電源（全員に配る側。誰かが働けば、その分だけ働いている。入力が 1 つでも測れていれば測れている） ---
        double? cpu = Ratio(frame.CpuPercent);
        double? gpu = Ratio(frame.GpuMaxPercent);
        double? disk = DiskWork(frame);
        if (cpu is null && gpu is null && disk is null)
        {
            slots.Add(new PartySlot("psu", "psu", SlotKind.Atx, "PSU", Missing, 0, false));
        }
        else
        {
            double load = Math.Clamp(((cpu ?? 0) + (gpu ?? 0)) * 0.5 + (disk ?? 0) * 0.2, 0, 1);
            string detail = frame.Disk.BytesPerSec is { } bps ? $"{bps / 1024.0 / 1024.0:0.0} MB/s" : Missing;
            slots.Add(new PartySlot("psu", "psu", SlotKind.Atx, "PSU", detail, load));
        }

        return slots;
    }

    /// <summary>
    /// 物語（旅程）へ渡す仕事量。値が無いチャンネルは今は 0 にしている。
    /// </summary>
    public static Work WorkOf(BoardFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // TODO(step 4b): leave missing channels out of JourneyRate
        return new Work(
            Cpu: Ratio(frame.CpuPercent) ?? 0,
            Gpu: Ratio(frame.GpuMaxPercent) ?? 0,
            Memory: Ratio(frame.Memory?.Percent) ?? 0,
            Disk: DiskWork(frame) ?? 0,
            DiskFullest: Ratio(frame.FullestVolumePercent) ?? 0);
    }

    private static double? DiskWork(BoardFrame frame) =>
        frame.Disk.BytesPerSec is { } b && double.IsFinite(b) && b >= 0
            ? Math.Clamp(b / DiskSlots.WorkScaleBytesPerSec, 0, 1)
            : null;

    private static PartySlot Slot(string key, string characterId, SlotKind kind, string title, double? load, Func<double, string> detail) =>
        load is { } l
            ? new PartySlot(key, characterId, kind, title, detail(l), l)
            : new PartySlot(key, characterId, kind, title, Missing, 0, false);

    /// <summary>百分率（0..100）→ 0..1。無ければ null。</summary>
    private static double? Ratio(double? percent) =>
        percent is { } p && double.IsFinite(p) ? Math.Clamp(p / 100.0, 0, 1) : null;

    private static string Capacity(long? used, long? total, int deviceCount, string lang)
    {
        if (used is not { } u || total is not { } t) return deviceCount > 1 ? Units(deviceCount, lang) : "";
        string count = deviceCount > 1 ? $"  ×{deviceCount}" : "";
        return $"{Gb(u):0} / {Gb(t):0} GB{count}";
    }

    private static string Cores(int n, string lang) => lang == "en" ? $"{n} cores" : $"{n} コア";
    private static string Units(int n, string lang) => lang == "en" ? $"{n} drives" : $"{n} 台";
    private static double Gb(long b) => b / 1024.0 / 1024.0 / 1024.0;
}
