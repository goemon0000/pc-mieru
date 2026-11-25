namespace PcMieru.Party.Hardware;

/// <summary>
/// 表示ドライバ（D3DKMT）が返したアダプタ1枚。
/// <paramref name="Name"/> はドライバが名乗った名前（無ければ空）。
/// <paramref name="PciBus"/> は PCI のバス番号（番地を持たないアダプタは null）。
/// <paramref name="Software"/> は Microsoft Basic Render Driver のようなソフトウェアのアダプタ。
/// </summary>
public sealed record GpuAdapterInfo(string LuidKey, string Name, ulong DedicatedBytes, uint? PciBus, bool Software)
{
    public bool HasPciAddress => PciBus is not null;
}

/// <summary>ある瞬間の GPU 1枚の様子。</summary>
public sealed record GpuReading(string Key, string Name, double Utilization, ulong VramUsedBytes, ulong VramTotalBytes);

/// <summary>
/// 盤に立てる GPU を決める。**WPF も OS の API も持たない**（試験で中身を確かめるため）。
///
/// ■ 幻の gpu#3
/// 以前はカウンタ（PDH）に出てきた LUID を毎秒そのまま並べていた。実機（3080 Ti・5070・Radeon）の
/// D3DKMT は、そのほかに 2 つ返す:
/// <code>
///   luid=0:24F84  （名前なし） 11.66 GB  ← RTX 5070 の別名（仮想ディスプレイ。PCI の番地なし）
///   luid=0:177A6  （名前なし）  0.00 GB  ← Microsoft Basic Render Driver（ソフトウェア）
/// </code>
/// 「名前も VRAM も使用率も無い行は出さない」という除け方は**その秒の使用率**で判定していたので、
/// 何かが WARP（Basic Render）で描いた秒や、別名にカウンタが付いた秒だけ、4 枚目の GPU が立った。
/// 盤は毎秒組み立て直すので一瞬で消えるが、冒険はその秒の顔ぶれで「新しく来た者」に装備を配り、
/// 経験値も付いて、保存に gpu#3 が残った。
///
/// ■ 今の決め方
/// 1. **アダプタの一覧で決める。その秒のカウンタでは決めない。** 顔ぶれが秒ごとに揺れない
/// 2. ソフトウェアのアダプタ・別名（<see cref="GpuAliasRule"/>、点検と同じ決まり）・名前も VRAM も番地も無い物は除く
/// 3. 並びは VRAM の多い順、同じなら PCI のバス番号順（LUID は起動のたびに変わるので並びに使わない）
///
/// アダプタの一覧が取れなかった機械（D3DKMT が無い等）だけ、カウンタに出た LUID から作る（以前と同じ）。
/// </summary>
public static class GpuRoster
{
    /// <summary>盤に立てるアダプタだけを、並べる順に返す。</summary>
    public static List<GpuAdapterInfo> Real(IReadOnlyList<GpuAdapterInfo> adapters)
    {
        var hardware = adapters.Where(a => !a.Software).ToList();
        var kept = GpuAliasRule.WithoutAliases(hardware, a => a.Name, a => a.DedicatedBytes, a => a.HasPciAddress)
            // 名前も VRAM も番地も無い物（中身の無い仮想ディスプレイ）は、GPU として数えない
            .Where(a => a.HasPciAddress || a.Name.Length > 0 || a.DedicatedBytes > 0);
        return Order(kept);
    }

    /// <summary>
    /// 並び順だけ（除く決まりは <see cref="Real"/>）。VRAM の多い順、同じなら PCI のバス番号順、名前、LUID。
    /// 盤の組み立て（BoardFrame）は、Core がもう別名とソフトウェアのアダプタを除いてあるので、これだけを使う
    /// （番地を持たない物を別名として除く決まりを、Core の「番地が読めなかった物」にまで当てないため）。
    /// </summary>
    public static List<GpuAdapterInfo> Order(IEnumerable<GpuAdapterInfo> adapters) =>
        adapters
            .OrderByDescending(a => a.DedicatedBytes)
            .ThenBy(a => a.PciBus ?? uint.MaxValue)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.LuidKey, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// その秒の行。<paramref name="real"/> は <see cref="Real"/> の結果、<paramref name="enumerated"/> は
    /// アダプタの一覧が取れたか（取れていれば、カウンタにしか出てこない LUID は無視する）。
    /// 使用率は 0..1、VRAM はバイト。
    /// </summary>
    public static List<GpuReading> Compose(
        IReadOnlyList<GpuAdapterInfo> real, bool enumerated,
        IReadOnlyDictionary<string, double> utilization, IReadOnlyDictionary<string, double> vramUsed)
    {
        var result = new List<GpuReading>();
        if (enumerated)
        {
            foreach (var a in real)
                result.Add(new GpuReading(a.LuidKey, a.Name.Length > 0 ? a.Name : "GPU",
                    Math.Clamp(utilization.GetValueOrDefault(a.LuidKey), 0, 1),
                    (ulong)Math.Max(0, vramUsed.GetValueOrDefault(a.LuidKey)), a.DedicatedBytes));
            return result;
        }

        // 一覧が取れなかった機械。カウンタに出た物を、動いている秒だけ名無しで出す（以前の振る舞い）
        foreach (string k in utilization.Keys.Union(vramUsed.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            double util = Math.Clamp(utilization.GetValueOrDefault(k), 0, 1);
            if (util <= 0) continue;
            result.Add(new GpuReading(k, "GPU", util, (ulong)Math.Max(0, vramUsed.GetValueOrDefault(k)), 0));
        }
        return result;
    }
}
