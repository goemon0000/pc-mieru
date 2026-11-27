namespace PcMieru.Party.Hardware;

/// <summary>
/// 同じ GPU の別名を見分ける決まり。**盤（PartsLife.Core の GpuRoster）と点検（PartsLife.Checks の GpuAdapters）で同じ物を使う。**
/// 片方だけ直すと、盤は GPU 4 枚・点検は 3 枚、のように食い違う（実際に盤の側だけ幻の gpu#3 が出ていた）。
///
/// 仮想ディスプレイのドライバ（VR のストリーミング等）が入っていると、同じカードが別の LUID で
/// もう 1 枚見える（実機で RTX 5070 が 2 枚に見えた）。そちらは PCI の番地が引けない。
/// **番地の無い物のうち、VRAM が番地のある物と同じで、名前も同じ（または名前が無い）なら別名**とみなす。
/// D3DKMT は別名に名前を付けて返さない（空になる）ので、名前が無い場合も別名に数える。
/// 同じ型番を本当に 2 枚挿している場合は、どちらにも番地があるので消えない。
///
/// このファイルは両方のプロジェクトにソースとして取り込む（internal）。点検の側は管理者の補助 exe にも入るので、
/// Core を参照させて依存を増やすことはしない。
/// </summary>
internal static class GpuAliasRule
{
    public static List<T> WithoutAliases<T>(IReadOnlyList<T> list, Func<T, string> name, Func<T, ulong> dedicatedBytes,
                                            Func<T, bool> hasPciAddress)
    {
        var result = new List<T>(list.Count);
        foreach (var a in list)
            if (!IsAlias(a, list, name, dedicatedBytes, hasPciAddress)) result.Add(a);
        return result;
    }

    public static bool IsAlias<T>(T a, IReadOnlyList<T> all, Func<T, string> name, Func<T, ulong> dedicatedBytes,
                                  Func<T, bool> hasPciAddress)
    {
        if (hasPciAddress(a)) return false;
        string n = name(a);
        ulong v = dedicatedBytes(a);
        foreach (var b in all)
            if (hasPciAddress(b) && dedicatedBytes(b) == v && (n.Length == 0 || name(b) == n)) return true;
        return false;
    }
}
