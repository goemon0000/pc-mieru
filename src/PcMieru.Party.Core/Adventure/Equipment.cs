namespace PcMieru.Party.Adventure;

/// <summary>一行の一人。鍵は経験値と同じ（gpu#0 等）、CharacterId は役（gpu 等）。</summary>
public readonly record struct Member(string Key, string CharacterId);

/// <summary>着け替えた一件。Removed は外した物の id（何も着けていなかったなら空）。</summary>
public readonly record struct EquipChange(Member Who, EquipSlot Slot, string Put, string Removed);

/// <summary>
/// 装備の決まり。**選ぶのは本人で、利用者は選べない。**
///
/// 決め方:
///   1. 点数 = Σ 道具の得意 × その役の重み（整数）。**構えに依らない（五分の重み）**
///   2. 一人ずつ（鍵の文字順）、手・身・守りの順に、持ち物の中から一番点の高い物に替える
///   3. **今の物より真に高いときだけ替える。** 同点なら替えない
///   4. 同点の候補どうしは、いまの構えでの点が高い方 → 格の高い方 → id の文字順で先の方
///
/// 3 があるので、装備を替えて点が下がることは無い。4 があるので、持ち物の並び順が
/// 違っても結果は同じになる。
///
/// 1 で構えを使わないのは、**構えが変わるたびに持ち替えないため。** 以前は構えの重みで比べていたので、
/// 大胆と慎重が入れ替わるたびに HDD くんの守りが「予備の電池」と「目録の帳面」を往復し、
/// どちらも「誰かの役に立つ」ので売られずに残り続けた。構えは 4 の同点の決め方にだけ効く
/// （今の物との比べには効かないので、往復は起きない）。
/// </summary>
public static class Equipment
{
    public const int SlotCount = 3;

    /// <summary>着け替えを決める点（構えに依らない）。</summary>
    public static int BaseScore(ItemDef? item, string characterId) => ItemScore(item, characterId, Stance.Balanced);

    public static int ItemScore(ItemDef? item, string characterId, Stance stance)
    {
        if (item is null || !item.FitsCharacter(characterId)) return 0;
        var w = Roles.Weights(characterId, stance);
        int score = 0;
        for (int s = 0; s < Roles.StatCount; s++) score += item.Stats[s] * w[s];
        return score;
    }

    public static int MemberScore(AdventureBook book, AdventureState state, in Member m, Stance stance)
    {
        if (!state.Equipped.TryGetValue(m.Key, out var eq)) return 0;
        int sum = 0;
        for (int i = 0; i < SlotCount; i++) sum += ItemScore(book.Item(eq[i]), m.CharacterId, stance);
        return sum;
    }

    /// <summary>a の方が良い候補か（同点の決め方を含む）。</summary>
    private static bool Better(ItemDef a, int baseA, int stanceA, ItemDef b, int baseB, int stanceB)
    {
        if (baseA != baseB) return baseA > baseB;
        if (stanceA != stanceB) return stanceA > stanceB;
        if (a.Tier != b.Tier) return a.Tier > b.Tier;
        return string.CompareOrdinal(a.Id, b.Id) < 0;
    }

    /// <summary>
    /// 持ち物から、一人ひとりに一番良い物を着けさせる。
    /// members は鍵の文字順に並べて渡すこと（<see cref="SortMembers"/>）。
    /// stance は同点の候補を選ぶときにだけ使う。
    /// 返り値は替えた数。最初に替えた者と物を返す（物語の一文に使う）。
    /// log を渡すと、替えたものを順に全部足す（外した物が分かる）。
    /// </summary>
    public static int AutoEquip(AdventureBook book, AdventureState state, IReadOnlyList<Member> members,
                                Stance stance, out Member who, out ItemDef? what, List<EquipChange>? log = null)
    {
        who = default;
        what = null;
        int changes = 0;
        var inv = state.Inventory;

        // 後の者が外した物を、前の者が欲しがることがある。替える物が無くなるまで回す。
        // 替えるたびに点の合計が真に増えるので必ず止まるが、念のため回数で切る
        for (int pass = 0; pass < 8; pass++)
        {
            int before = changes;
            changes = Pass(book, state, members, stance, inv, changes, ref who, ref what, log);
            if (changes == before) break;
        }
        return changes;
    }

    private static int Pass(AdventureBook book, AdventureState state, IReadOnlyList<Member> members,
                            Stance stance, List<string> inv, int changes, ref Member who, ref ItemDef? what,
                            List<EquipChange>? log)
    {
        for (int mi = 0; mi < members.Count; mi++)
        {
            var m = members[mi];
            if (!state.Equipped.TryGetValue(m.Key, out var eq))
            {
                eq = new[] { "", "", "" };
                state.Equipped[m.Key] = eq;
            }

            for (int slot = 0; slot < SlotCount; slot++)
            {
                var current = book.Item(eq[slot]);
                int currentScore = BaseScore(current, m.CharacterId);

                int bestIdx = -1, bestScore = currentScore, bestStance = 0;
                ItemDef? best = null;
                for (int i = 0; i < inv.Count; i++)
                {
                    var cand = book.Item(inv[i]);
                    if (cand is null || (int)cand.SlotValue != slot) continue;
                    int sc = BaseScore(cand, m.CharacterId);
                    if (sc <= currentScore) continue;               // 真に良くなる物だけ
                    int st = ItemScore(cand, m.CharacterId, stance);
                    if (best is null || Better(cand, sc, st, best, bestScore, bestStance))
                    {
                        best = cand; bestScore = sc; bestStance = st; bestIdx = i;
                    }
                }
                if (best is null) continue;

                // 外した物は持ち物に戻す。何も着けていなかったなら、持ち物が一つ減る
                if (current is not null) inv[bestIdx] = current.Id;
                else inv.RemoveAt(bestIdx);
                eq[slot] = best.Id;

                if (changes == 0) { who = m; what = best; }
                log?.Add(new EquipChange(m, (EquipSlot)slot, best.Id, current?.Id ?? ""));
                changes++;
            }
        }
        return changes;
    }

    /// <summary>
    /// その道具が、いまの誰かの装備より真に良くなるか（着け替えと同じ、構えに依らない点で）。
    /// 良くならない物は町で売ってよい。**構えごとに比べると、どの構えかで勝つ物が全部残って売れない。**
    /// </summary>
    public static bool UsefulToAnyone(AdventureBook book, AdventureState state, IReadOnlyList<Member> members, ItemDef item)
    {
        int slot = (int)item.SlotValue;
        for (int mi = 0; mi < members.Count; mi++)
        {
            var m = members[mi];
            if (!item.FitsCharacter(m.CharacterId)) continue;
            state.Equipped.TryGetValue(m.Key, out var eq);
            var current = eq is null ? null : book.Item(eq[slot]);
            if (BaseScore(item, m.CharacterId) > BaseScore(current, m.CharacterId)) return true;
        }
        return false;
    }

    /// <summary>まだ何も持っていない者に、ありあわせを持たせる（新しく挿さった部品、初めての起動）。</summary>
    public static bool GiveStarters(AdventureBook book, AdventureState state, in Member m)
    {
        if (state.Equipped.ContainsKey(m.Key)) return false;
        var eq = new[] { "", "", "" };
        for (int slot = 0; slot < SlotCount; slot++)
        {
            ItemDef? best = null;
            int bestScore = 0;
            foreach (var it in book.Starters)
            {
                if ((int)it.SlotValue != slot) continue;
                int sc = BaseScore(it, m.CharacterId);
                if (sc <= 0) continue;
                if (best is null || Better(it, sc, sc, best, bestScore, bestScore)) { best = it; bestScore = sc; }
            }
            if (best is not null) eq[slot] = best.Id;
        }
        state.Equipped[m.Key] = eq;
        return true;
    }

    public static Member[] SortMembers(IEnumerable<Member> members) =>
        members.OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
}
