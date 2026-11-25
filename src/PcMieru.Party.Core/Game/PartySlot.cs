using PcMieru.Party.Rendering;

namespace PcMieru.Party.Game;

/// <summary>
/// 盤の上に立つ1体ぶん。**実機の構成から毎回組み立てる。**
/// （組み立てるのはアプリ側の Composition。実測に触るのはそちらだけ）
/// </summary>
/// <param name="Key">経験値の鍵。GPU が2枚なら gpu#0 と gpu#1 で別々に育つ</param>
/// <param name="CharacterId">絵</param>
/// <param name="Title">展開したときに出す名前</param>
/// <param name="Detail">その右に出す実測値</param>
/// <param name="Load">0..1。姿・熱・経験値のすべてがこの値から決まる</param>
/// <param name="Measured">測れているか。偽（値が取れなかった席）は Load 0 で、経験値を足さない（<see cref="Director.Tick"/>）</param>
public sealed record PartySlot(
    string Key, string CharacterId, SlotKind Kind,
    string Title, string Detail, double Load, bool Measured = true);
