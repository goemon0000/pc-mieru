using PcMieru.Party.Game;

namespace PcMieru.Party.Adventure;

/// <summary>
/// 町と遠出の輪。**本筋（章）とは別に回り、本筋の場面と場面の間を埋める。**
///
/// 輪の形:
/// <code>
///   町: 着く → 装備を見直す → 売る → 買う → 休む → 掲示を見て行き先を決める → 発つ
///   遠出: 場面ごとに構えを決める（進む/引き返す・戦う/避ける・脇道に入る/入らない）
///         決めた結果は次の場面で出る
///   帰り道: 歩いた深さの半分ほどで町に着く → また「着く」から
/// </code>
///
/// **進むのは実機の働きからだけ。** 1場面に要る旅程（<see cref="StepCost"/>）は、
/// 本筋と同じ「その秒の働き」から貯まる。放っておけばゆっくり、働けば速い。
/// ただし速すぎると画面が読めないので、場面と場面の間は最低 <see cref="MinStepGap"/> 秒あける。
///
/// **経験値には一切触らない。** 戦って勝っても、レベルは上がらない。
/// レベルはその子自身の仕事でしか増えない、という決まりを崩さないため。
///
/// 毎秒の呼び出しでは、平均を更新して旅程を足すだけ（割り当て無し）。
/// 重い判断は場面が進むとき（最短でも45秒に一度）にだけ走る。
/// </summary>
public sealed class AdventureEngine
{
    public const double StepCost = 20;
    public const double MinStepGap = 45;
    public const int InventoryCap = 12;
    public const long GoldCap = 99_999;
    public const int MaxBuysPerVisit = 2;
    public const int MaxExtraDepth = 3;

    private readonly AdventureBook _book;
    private readonly PartyState _party;
    private readonly Random _rng;

    // 実機の様子。保存しない（起動し直したら、その時の機械から測り直す）
    private double _loadAvg = -1;
    private double _hotAvg;
    private double _memory;
    private double _diskFullest;
    private double _sinceStep = MinStepGap;

    private Member[] _members = Array.Empty<Member>();
    private int _membersSig;
    /// <summary>最後に見た進み具合。「両方とも最初から」で作り直されたら、ありあわせを配り直す。</summary>
    private AdventureState? _seenState;

    private WorldState? _world;
    private string _lang = "ja";

    // 理由 → 文の種類。毎回の文字列連結を避けるため、先に作っておく
    private static readonly string[] WhyCategory =
    {
        "why-none", "why-fresh", "why-rested", "why-gear-strong", "why-streak-up", "why-calm",
        "why-hurt", "why-tired", "why-gear-weak", "why-heavy", "why-streak-down", "why-hot",
    };
    private static readonly string[] BoardCategory = { "board-careful", "board-balanced", "board-bold" };

    private static readonly string[] Fighters = { "gpu", "cpu" };
    private static readonly string[] Keepers = { "cpu-cooler", "psu", "hdd", "motherboard" };
    private static readonly string[] Finders = { "m2", "ssd", "hdd", "memory" };
    private static readonly string[] Carriers = { "memory" };

    public AdventureEngine(AdventureBook book, PartyState party, Random rng)
    {
        _book = book;
        _party = party;
        _rng = rng;
        party.Adventure ??= new AdventureState();
        party.Adventure.Sanitize(book.Towns.Count);
    }

    private AdventureState S => _party.Adventure!;

    public AdventureBook Book => _book;
    public IReadOnlyList<Member> Members => _members;
    public double MachineLoad => Math.Max(0, _loadAvg);
    public double MachineHot => _hotAvg;

    /// <summary>この起動で決めた構えの数 [慎重, 五分, 大胆]。保存しない（試験と点検用）。</summary>
    public int[] StanceCounts { get; } = new int[3];

    // ---------------------------------------------------------------------
    // 毎秒
    // ---------------------------------------------------------------------

    /// <summary>
    /// dt 秒ぶん進める。progress はこの秒に貯まった旅程（本筋と同じ値）。
    /// 場面が進んで文が出たら output に足す。output に既に本筋の文があれば、場面は次の秒へ回す。
    /// </summary>
    public void Tick(double dt, double progress, Work w, IReadOnlyList<PartySlot> slots,
                     WorldState world, List<string> output, string lang)
    {
        TrackMembers(slots);
        TrackStrayGear(dt);

        // 5分・15分の移動平均。**一瞬の山では構えを変えない。** 続いた負荷だけを熱とみなす
        double busy = Math.Clamp(Math.Max(w.Cpu, w.Gpu), 0, 1);
        if (_loadAvg < 0) _loadAvg = busy;
        _loadAvg += (busy - _loadAvg) * Math.Min(1, dt / 300);
        _hotAvg += ((busy >= 0.85 ? 1 : 0) - _hotAvg) * Math.Min(1, dt / 900);
        _memory = w.Memory;
        _diskFullest = w.DiskFullest;

        var s = S;
        if (double.IsFinite(progress) && progress > 0)
            s.Progress = Math.Min(s.Progress + progress, 2 * StepCost);
        _sinceStep += dt;

        if (s.Progress < StepCost || _sinceStep < MinStepGap) return;
        if (output.Count > 0 || _members.Length == 0) return;   // 本筋の文を先に読ませる

        s.Progress -= StepCost;
        _sinceStep = 0;
        _world = world;
        _lang = lang;
        if (Step() is { Length: > 0 } line) output.Add(line);
    }

    /// <summary>顔ぶれが変わったときだけ並べ直す。新しく来た者には、ありあわせを持たせる。</summary>
    private void TrackMembers(IReadOnlyList<PartySlot> slots)
    {
        int sig = slots.Count;
        unchecked
        {
            for (int i = 0; i < slots.Count; i++) sig = HashCode.Combine(sig, slots[i].Key, slots[i].Measured);   // 測れた/測れないの変わり目でも並べ直す
        }
        if (sig == _membersSig && _members.Length > 0 && ReferenceEquals(_seenState, S)) return;
        _membersSig = sig;
        _seenState = S;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Member>(slots.Count);
        var measured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sl in slots)
        {
            if (GpuSlotKeys.IsDisplayOnly(sl.Key)) continue;   // 席の無い GPU は絵だけ。装備も経験も持たない（保存に何も作らない）
            if (!seen.Add(sl.Key)) continue;
            list.Add(new Member(sl.Key, sl.CharacterId));
            if (sl.Measured) measured.Add(sl.Key);
        }
        _members = Equipment.SortMembers(list);
        // 測れていない席（検出はしたが値が無い）には、まだ配らない。最初に測れた秒に配る（上の署名が変わるので、ここへ戻ってくる）
        foreach (var m in _members)
            if (measured.Contains(m.Key)) Equipment.GiveStarters(_book, S, m);
    }

    // ---------------------------------------------------------------------
    // 盤に居ない者の装備
    // ---------------------------------------------------------------------

    /// <summary>
    /// 盤に居ない番号付きの鍵（gpu#3 等）が、この秒数続けて居なければ、その装備を持ち物に戻す。
    /// **一瞬の揺れでは外さない。** 計測が一度途切れただけで装備が剥がれると、戻ってきた者が丸裸になる。
    /// </summary>
    public const double StrayGraceSeconds = 600;
    private const double StrayCheckEvery = 30;
    private double _strayClock;
    private readonly Dictionary<string, double> _strayFor = new(StringComparer.Ordinal);

    /// <summary>
    /// 保存に残った、盤に居ない者の装備を片付ける。
    ///
    /// v0.7.0 の試運転までは、Basic Render や仮想ディスプレイの別名が動いた秒だけ GPU が1枚増えて見え、
    /// その秒の顔ぶれで「新しく来た者」（gpu#3）に装備が配られて保存に残っていた（Core の GpuRoster）。
    /// 冒険はいまの顔ぶれしか見ないので害は無いが、残し続ける理由も無い。
    ///
    /// 外すのは **同じ種類の者が盤に居て、その鍵だけが居ない** ときだけ（gpu#0..#2 が居て gpu#3 が居ない）。
    /// 種類ごと居ない（計測が止まって GPU が一枚も見えない等）なら触らない。番号の無い鍵（memory・hdd 等）も触らない。
    /// 外した物は**捨てずに持ち物へ戻す**（入りきらなければ拾い物の荷に包む＝町で売れる）。他の者の装備には触らない。
    /// </summary>
    private void TrackStrayGear(double dt)
    {
        _strayClock += dt;
        if (_strayClock < StrayCheckEvery) return;
        double step = _strayClock;
        _strayClock = 0;

        var s = S;
        if (_members.Length == 0 || s.Equipped.Count <= _members.Length) { _strayFor.Clear(); return; }

        List<string>? prune = null;
        foreach (var key in s.Equipped.Keys)
        {
            if (!IsStray(key)) { _strayFor.Remove(key); continue; }
            double t = _strayFor.GetValueOrDefault(key) + step;
            _strayFor[key] = t;
            if (t >= StrayGraceSeconds) (prune ??= new List<string>()).Add(key);
        }
        if (prune is null) return;
        foreach (var key in prune)
        {
            ReturnToBag(s.Equipped[key]);
            s.Equipped.Remove(key);
            _strayFor.Remove(key);
        }
    }

    /// <summary>番号付きの鍵で、同じ種類の者は盤に居るのに、その鍵の者だけが居ないか。</summary>
    private bool IsStray(string key)
    {
        int hash = key.IndexOf('#');
        if (hash <= 0) return false;
        var kind = key.AsSpan(0, hash + 1);   // "gpu#"
        bool kindHere = false;
        foreach (var m in _members)
        {
            if (m.Key == key) return false;
            if (m.Key.AsSpan().StartsWith(kind, StringComparison.Ordinal)) kindHere = true;
        }
        return kindHere;
    }

    /// <summary>
    /// その鍵の装備をすべて持ち物へ戻し、装備の記録を消す（GPU の番号を空けるとき: 経験は消えるが、装備は捨てずに返す）。
    /// 入りきらない分は <see cref="ReturnToBag"/> と同じく売れて <see cref="AdventureState.Loot"/> になる。記録が無ければ何もせず false。
    /// </summary>
    public bool ReturnGearOf(string key)
    {
        var s = S;
        if (s.Equipped is not { } worn || !worn.TryGetValue(key, out var gear)) return false;
        if (gear is not null) ReturnToBag(gear);
        worn.Remove(key);
        return true;
    }

    private void ReturnToBag(string[] gear)
    {
        var s = S;
        foreach (var id in gear)
        {
            if (string.IsNullOrEmpty(id) || _book.Item(id) is not { } it) continue;
            if (s.Inventory.Count < InventoryCap) s.Inventory.Add(id);
            else s.Loot += it.SellPrice;
        }
    }

    // ---------------------------------------------------------------------
    // 1場面
    // ---------------------------------------------------------------------

    /// <summary>1場面進める。試験から直接呼べるように公開してある。</summary>
    public string? Step(WorldState world, string lang)
    {
        _world = world;
        _lang = lang;
        return Step();
    }

    private string? Step() => S.Mode switch
    {
        AdventureMode.Out => OutStep(),
        AdventureMode.Back => BackStep(),
        _ => TownStep(),
    };

    // ---- 町 --------------------------------------------------------------

    private string? TownStep()
    {
        var s = S;
        for (int guard = 0; guard < 12; guard++)
        {
            switch (s.Phase)
            {
                case TownPhase.Arrive:
                {
                    // 本筋が先へ進んでいれば、帰る先は次の町になる（物語を最初からにしたら戻る）
                    int target = _book.TownFor(_party.Chapter);
                    // 旅の一番初めは「初めての町」（前の町が無いので、「前の町には戻らず」とは書けない）。
                    // 先の町へ移ったときは「新しい町」。それ以外（戻ったときも）は「帰った」
                    bool first = s.Expeditions == 0 && s.Returns == 0;
                    bool moved = target > s.Town;
                    s.Town = target;
                    s.Mode = AdventureMode.Town;
                    s.Phase = TownPhase.Equip;
                    s.Place = "";
                    s.Pending = Pending.None;
                    s.Depth = s.Planned = s.BackSteps = 0;
                    s.Rests = s.Bought = 0;
                    s.Streak /= 2;
                    return Say(first ? "town-first" : moved ? "town-new" : "town-arrive");
                }
                case TownPhase.Equip:
                {
                    s.Phase = TownPhase.Sell;
                    if (Equip(out var who, out var what)) return Say("equip", item: what, who: who);
                    continue;
                }
                case TownPhase.Sell:
                {
                    s.Phase = TownPhase.Shop;
                    if (SellUnneeded()) return Say("sell");
                    continue;
                }
                case TownPhase.Shop:
                {
                    if (s.Bought < MaxBuysPerVisit && TryBuy(out var who, out var what))
                    {
                        s.Bought++;
                        return Say("buy", item: what, who: who);
                    }
                    s.Phase = TownPhase.Rest;
                    continue;
                }
                case TownPhase.Rest:
                {
                    if (s.Hp >= 0.95 && s.Fatigue <= 0.1)
                    {
                        s.Phase = TownPhase.Board;
                        continue;
                    }
                    bool paid = Rest();
                    s.Rests++;
                    // 続けて休むだけの場面は黙っている。同じ一文が並ぶと、止まって見える
                    return s.Rests == 1 ? Say(paid ? "inn-rest" : "inn-eaves") : null;
                }
                case TownPhase.Board:
                {
                    s.Phase = TownPhase.Depart;
                    return ChooseDestination();
                }
                case TownPhase.Depart:
                {
                    var p = CurrentPlace;
                    if (p is null) { s.Phase = TownPhase.Board; continue; }
                    s.Mode = AdventureMode.Out;
                    s.Phase = TownPhase.Arrive;
                    s.Depth = 0;
                    s.Pending = Pending.None;
                    s.Planned = Math.Max(2, p.Length
                        + (s.Stance == Stance.Bold ? 1 : 0) - (s.Stance == Stance.Careful ? 1 : 0));
                    s.LastPlace = p.Id;
                    s.Expeditions++;
                    // 構えが変わっても持ち替えない（装備の点は構えに依らない。Equipment の注を参照）
                    return Say("depart", place: p);
                }
                default:
                    s.Phase = TownPhase.Arrive;
                    continue;
            }
        }
        return null;
    }

    /// <summary>
    /// 掲示を見て行き先を決める。
    /// 大胆なら手強い所（力が足りないと分かっている所は避ける）、慎重なら易しい所、
    /// 五分なら力がちょうど釣り合う所。前と同じ所は、半分の確率で避ける。
    /// </summary>
    private string? ChooseDestination()
    {
        var s = S;
        var town = CurrentTown;
        double power = Power();
        var r = StanceRules.Decide(Inputs(power / Need(town.Tier, PlaceKind.Field, 0), lootFill: 0), _rng);
        s.Stance = r.Stance;
        StanceCounts[(int)r.Stance]++;

        var places = town.Places;
        int pick = 0;
        double bestKey = double.MaxValue;
        for (int i = 0; i < places.Count; i++)
        {
            double ratio = power / Need(places[i]);
            double key = r.Stance switch
            {
                Stance.Bold => ratio >= 0.85 ? -Need(places[i]) : 1000 + Need(places[i]),
                Stance.Careful => Need(places[i]),
                _ => Math.Abs(ratio - 1.05),
            };
            if (places[i].Id == s.LastPlace && places.Count > 1 && _rng.NextDouble() < 0.5) key += 10_000;
            if (key < bestKey) { bestKey = key; pick = i; }
        }
        var place = places[pick];
        s.Place = place.Id;

        var why = r.Stance switch
        {
            Stance.Bold => r.WhyBold,
            Stance.Careful => r.WhySafe,
            _ => Reason.None,
        };
        return Compose(why, BoardCategory[(int)r.Stance], place);
    }

    private bool SellUnneeded()
    {
        var s = S;
        long value = s.Loot;
        s.Loot = 0;
        var inv = s.Inventory;
        for (int i = inv.Count - 1; i >= 0; i--)
        {
            var it = _book.Item(inv[i]);
            if (it is null) { inv.RemoveAt(i); continue; }
            if (!Equipment.UsefulToAnyone(_book, s, _members, it))
            {
                value += it.SellPrice;
                inv.RemoveAt(i);
            }
        }
        // 上限を超えていれば、値の安い物から手放す
        while (inv.Count > InventoryCap)
        {
            int cheapest = 0;
            for (int i = 1; i < inv.Count; i++)
                if ((_book.Item(inv[i])?.Price ?? 0) < (_book.Item(inv[cheapest])?.Price ?? 0)) cheapest = i;
            value += _book.Item(inv[cheapest])?.SellPrice ?? 0;
            inv.RemoveAt(cheapest);
        }
        if (value <= 0) return false;
        s.Gold = Math.Min(GoldCap, s.Gold + value);
        return true;
    }

    /// <summary>
    /// 一番良くなる買い物を1つだけする。宿代は残す。
    /// 同じ伸びなら、鍵の文字順で先の者・店の並び（id 順）で先の物。
    /// </summary>
    private bool TryBuy(out Member who, out ItemDef? what)
    {
        who = default;
        what = null;
        var s = S;
        if (s.Inventory.Count >= InventoryCap) return false;
        long budget = s.Gold - InnCost();
        if (budget <= 0) return false;

        var stock = _book.ShopStock(CurrentTown.Tier);
        int bestGain = 0;
        ItemDef? best = null;
        Member bestFor = default;
        foreach (var m in _members)
        {
            s.Equipped.TryGetValue(m.Key, out var eq);
            for (int slot = 0; slot < Equipment.SlotCount; slot++)
            {
                // 着け替えと同じ点で比べる。構えの点で買うと、買っても着けない物が出る
                int current = Equipment.BaseScore(eq is null ? null : _book.Item(eq[slot]), m.CharacterId);
                foreach (var it in stock)
                {
                    if ((int)it.SlotValue != slot || it.Price > budget) continue;
                    int gain = Equipment.BaseScore(it, m.CharacterId) - current;
                    if (gain > bestGain) { bestGain = gain; best = it; bestFor = m; }
                }
            }
        }
        if (best is null) return false;

        s.Gold -= best.Price;
        s.Inventory.Add(best.Id);
        if (Equip(out var w2, out var i2))
        {
            who = w2;
            what = i2;
        }
        else
        {
            who = bestFor;
            what = best;
        }
        return true;
    }

    // ---- 着け替え ----------------------------------------------------------

    /// <summary>着け替えの記録。毎回作らずに使い回す。</summary>
    private readonly List<EquipChange> _equipLog = new();

    /// <summary>
    /// 最近外した物（鍵, 道具 id）。**一度外した物に戻しただけなら、「着けた」とは言わない。**
    /// 保存しない（起動し直したら忘れてよい）。数で切る。
    /// </summary>
    private readonly List<(string Key, string Item)> _recentlyWorn = new();
    private const int RecentlyWornCap = 24;

    /// <summary>
    /// 持ち物から着け替える。知らせてよい一件（最近外した物に戻しただけ、ではない物）があれば真。
    /// </summary>
    private bool Equip(out Member who, out ItemDef? what)
    {
        who = default;
        what = null;
        _equipLog.Clear();
        if (Equipment.AutoEquip(_book, S, _members, S.Stance, out _, out _, _equipLog) == 0) return false;
        bool found = false;
        foreach (var c in _equipLog)
        {
            if (!found && !WasWorn(c.Who.Key, c.Put) && _book.Item(c.Put) is { } it)
            {
                who = c.Who;
                what = it;
                found = true;
            }
            if (c.Removed.Length > 0) RememberWorn(c.Who.Key, c.Removed);
        }
        return found;
    }

    private bool WasWorn(string key, string item)
    {
        foreach (var (k, i) in _recentlyWorn)
            if (k == key && i == item) return true;
        return false;
    }

    private void RememberWorn(string key, string item)
    {
        for (int i = _recentlyWorn.Count - 1; i >= 0; i--)
            if (_recentlyWorn[i].Key == key && _recentlyWorn[i].Item == item) _recentlyWorn.RemoveAt(i);
        if (_recentlyWorn.Count >= RecentlyWornCap) _recentlyWorn.RemoveAt(0);
        _recentlyWorn.Add((key, item));
    }

    private int InnCost() => 5 + 5 * CurrentTown.Tier;

    /// <summary>宿で休む。払えなければ軒先で休む（治りは浅い）。**休めないことは無い。**</summary>
    private bool Rest()
    {
        var s = S;
        int cost = InnCost();
        bool paid = s.Gold >= cost;
        if (paid) s.Gold -= cost;
        // 機械が静かなら、よく眠れる
        double calm = StanceRules.IsCalm(MachineLoad) ? 0.1 : 0;
        s.Hp = Math.Min(1, s.Hp + (paid ? 0.5 : 0.3) + calm);
        s.Fatigue = Math.Max(0, s.Fatigue - (paid ? 0.6 : 0.4) - calm);
        return paid;
    }

    // ---- 遠出 ------------------------------------------------------------

    private string? OutStep()
    {
        var s = S;
        var p = CurrentPlace;
        if (p is null)
        {
            // 行き先が本文から消えた（版が変わった等）。黙って帰る
            s.Mode = AdventureMode.Back;
            s.BackSteps = 1;
            return null;
        }
        if (s.Pending != Pending.None) return Resolve(p);

        double ratio = Power() / Need(p);
        var input = Inputs(ratio, LootFill());
        var r = StanceRules.Decide(input, _rng);
        s.Stance = r.Stance;
        StanceCounts[(int)r.Stance]++;

        // 越えたら必ず引き返す線。構えより先に見る
        if (s.Hp <= 0.3) return TurnBack(Reason.Hurt, p);
        if (s.Fatigue >= 0.85) return TurnBack(Reason.Tired, p);
        if (input.LootFill >= 1) return TurnBack(Reason.Heavy, p);

        s.Fatigue = Math.Min(1, s.Fatigue + FatigueStep());
        bool bold = r.Stance == Stance.Bold, careful = r.Stance == Stance.Careful;

        if (s.Depth >= s.Planned)
        {
            // 奥に着いた。大胆なら、もう少しだけ先へ
            if (bold && s.Depth < s.Planned + MaxExtraDepth && s.Hp > 0.5)
            {
                s.Depth++;
                return Compose(r.WhyBold, "do-deeper", p);
            }
            s.Mode = AdventureMode.Back;
            s.BackSteps = BackStepsFor(s.Depth);
            return Say("reach-end", place: p);
        }

        // 何に出会うかは行き先の種類で変わる。原は分かれ道と脇道、洞は敵が多い
        (double fork, double foe) = p.KindValue switch
        {
            PlaceKind.Dungeon => (0.20, 0.60),
            PlaceKind.Side => (0.20, 0.30),
            _ => (0.25, 0.45),
        };
        double roll = _rng.NextDouble();
        double fill = input.LootFill;

        if (roll < fork)
        {
            bool push = bold ? s.Hp > 0.25
                      : careful ? s.Hp > 0.7 && s.Fatigue < 0.35 && fill < 0.4
                      : s.Hp > 0.45 && s.Fatigue < 0.6;
            if (!push) return TurnBack(r.WhySafe, p);
            s.Depth++;
            return Compose(r.WhyBold, "do-push", p);
        }

        s.Depth++;
        if (roll < fork + foe)
        {
            // 大胆でも、勝ち目の薄すぎる相手（力が6割に届かない）には、無傷のとき以外は当たらない
            bool fight = bold ? ratio >= 0.6 || s.Hp > 0.9
                       : careful ? ratio >= 1.25 && s.Hp > 0.6
                       : ratio >= 0.85 || (s.Hp > 0.75 && ratio >= 0.6);
            s.Pending = fight ? Pending.Fight : Pending.Avoided;
            return fight ? Compose(r.WhyBold, "do-fight", p) : Compose(r.WhySafe, "do-avoid", p);
        }

        bool take = bold || (careful ? s.Hp > 0.85 && fill < 0.2 : fill < 0.6 && s.Hp > 0.5);
        if (!take) return Compose(r.WhySafe, "do-skip", p);
        s.Pending = Pending.Side;
        return Compose(r.WhyBold, "do-side", p);
    }

    /// <summary>決めた場面の結果。</summary>
    private string? Resolve(PlaceDef p)
    {
        var s = S;
        var pending = s.Pending;
        s.Pending = Pending.None;
        int tier = AreaTier(p);
        double ratio = Power() / Need(p);

        switch (pending)
        {
            case Pending.Fight:
            {
                double win = Math.Clamp(0.6 + (ratio - 1) * 0.9 + (s.Stance == Stance.Bold ? 0.05 : 0), 0.08, 0.95);
                double soft = 1.5 - Math.Min(ratio, 1.4);
                if (_rng.NextDouble() < win)
                {
                    s.Wins++;
                    s.Streak = Math.Clamp(Math.Max(1, s.Streak + 1), -5, 5);
                    Damage((0.02 + 0.04 * _rng.NextDouble()) * soft, tier);
                    GainLoot(tier, p, 1.0);
                    if (_rng.NextDouble() < 0.2 + LotBonus(tier) && Drop(tier) is { } it)
                    {
                        Take(it);
                        return Say("won-item", item: it, place: p);
                    }
                    return Say("won", place: p);
                }
                s.Losses++;
                s.Streak = Math.Clamp(Math.Min(-1, s.Streak - 1), -5, 5);
                Damage((0.10 + 0.12 * _rng.NextDouble()) * (soft + 0.1), tier);
                return s.Hp <= 0.08 ? Retreat(p) : Say("lost", place: p);
            }
            case Pending.Avoided:
                s.Fatigue = Math.Min(1, s.Fatigue + 0.03);
                return Say("avoided", place: p);

            case Pending.Side:
            {
                double roll = _rng.NextDouble();
                if (roll < 0.45 + LotBonus(tier))
                {
                    int t = tier + (s.Stance == Stance.Bold && s.Depth > s.Planned ? 1 : 0);
                    if (Drop(t) is { } it)
                    {
                        Take(it);
                        return Say("side-found", item: it, place: p);
                    }
                }
                if (roll < 0.70)
                {
                    Damage(0.05 + 0.07 * _rng.NextDouble(), tier);
                    return s.Hp <= 0.08 ? Retreat(p) : Say("side-hurt", place: p);
                }
                GainLoot(tier, p, 0.5);
                return Say("side-small", place: p);
            }
            default:
                return null;
        }
    }

    private string? TurnBack(Reason why, PlaceDef p)
    {
        var s = S;
        s.Mode = AdventureMode.Back;
        s.BackSteps = BackStepsFor(s.Depth);
        return Compose(why, "do-back", p);
    }

    /// <summary>倒れる前に退く。拾い物の半分を置いていく。**全滅は無い。**</summary>
    private string? Retreat(PlaceDef p)
    {
        var s = S;
        s.Hp = Math.Max(s.Hp, 0.05);
        s.Loot /= 2;
        s.Mode = AdventureMode.Back;
        s.BackSteps = BackStepsFor(s.Depth);
        return Say("retreat", place: p);
    }

    private static int BackStepsFor(int depth) => Math.Clamp((depth + 1) / 2, 1, 6);

    private string? BackStep()
    {
        var s = S;
        s.BackSteps--;
        s.Fatigue = Math.Min(1, s.Fatigue + 0.01);
        if (s.BackSteps > 0)
            return _rng.NextDouble() < 0.5 ? Say("walk-back", place: CurrentPlace) : null;

        s.Mode = AdventureMode.Town;
        s.Phase = TownPhase.Arrive;
        s.Returns++;
        return TownStep();
    }

    // ---------------------------------------------------------------------
    // 数の決まり
    // ---------------------------------------------------------------------

    private TownDef CurrentTown => _book.Towns[Math.Clamp(S.Town, 0, _book.Towns.Count - 1)];

    private PlaceDef? CurrentPlace
    {
        get
        {
            var id = S.Place;
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in CurrentTown.Places) if (p.Id == id) return p;
            return null;
        }
    }

    private int AreaTier(PlaceDef p) =>
        Math.Min(CurrentTown.Tier + p.Harder + (S.Depth > S.Planned ? 1 : 0), _book.MaxTier);

    /// <summary>
    /// 行き先の手強さ。格 t の物を役どおりに揃えた一人が、ほぼ釣り合う値にしてある
    /// （揃えた装備 ≈ 6 + 9t。レベルの分 0.6 × Lv が、勝ち越すための上積みになる）。
    /// </summary>
    public static double Need(int tier, PlaceKind kind, int over) =>
        (6 + 9 * tier)
        * kind switch { PlaceKind.Dungeon => 1.15, PlaceKind.Side => 0.9, _ => 1.0 }
        * (1 + 0.06 * Math.Max(0, over));

    private double Need(PlaceDef p) =>
        Need(CurrentTown.Tier + p.Harder, p.KindValue, S.Depth - S.Planned);

    /// <summary>一行の力。一人あたりの装備の点 + 0.6 × 平均レベル。</summary>
    public double Power()
    {
        if (_members.Length == 0) return 0;
        double gear = 0, lv = 0;
        foreach (var m in _members)
        {
            gear += Equipment.MemberScore(_book, S, m, Stance.Balanced);
            lv += _party.Level(m.Key);
        }
        return gear / 100.0 / _members.Length + 0.6 * lv / _members.Length;
    }

    /// <summary>一行が持っている道具の、その得意の合計。</summary>
    private int PartyStat(Stat stat)
    {
        int sum = 0;
        foreach (var m in _members)
        {
            if (!S.Equipped.TryGetValue(m.Key, out var eq)) continue;
            for (int i = 0; i < Equipment.SlotCount; i++)
                if (_book.Item(eq[i]) is { } it) sum += it.Stats[(int)stat];
        }
        return sum;
    }

    private double PerMember(Stat stat) => _members.Length == 0 ? 0 : (double)PartyStat(stat) / _members.Length;

    /// <summary>
    /// 持てる量。メモリの積載量とレベルで増える。
    /// **実機の余裕も効く。** ディスクが9割埋まっていると、拾い物を捨てる場面が増える
    /// （docs/plot.md「ストレージ空き＝余裕」）。メモリが逼迫していても少し減る。
    /// </summary>
    public double Capacity()
    {
        int memLv = 0;
        foreach (var m in _members)
            if (m.CharacterId == "memory") memLv = Math.Max(memLv, _party.Level(m.Key));
        double cap = 40 + 15 * CurrentTown.Tier + PartyStat(Stat.Cap) / 2.0 + memLv * 2;
        if (_diskFullest >= 0.9) cap *= 0.6;
        if (_memory >= 0.9) cap *= 0.8;
        return Math.Max(10, cap);
    }

    private double LootFill() => S.Loot / Capacity();

    private double LotBonus(int tier) =>
        Math.Clamp(PerMember(Stat.Lot) / (4.0 + 6 * tier), 0, 0.3);

    private double FatigueStep()
    {
        int tier = CurrentTown.Tier;
        double guard = Math.Clamp(PerMember(Stat.End) / (2.0 + 3 * tier), 0, 1);
        return Math.Max(0.03, 0.05 * (1 - 0.4 * guard));
    }

    private void Damage(double amount, int tier)
    {
        double guard = Math.Clamp((PerMember(Stat.End) + PerMember(Stat.Sup) * 0.5) / (3.0 + 4.5 * tier), 0, 1);
        S.Hp = Math.Max(0.02, S.Hp - Math.Max(0.005, amount * (1 - 0.3 * guard)));
    }

    private void GainLoot(int tier, PlaceDef p, double mul)
    {
        var s = S;
        double amount = (6 + 5 * tier) * (0.8 + 0.6 * _rng.NextDouble())
                        * (1 + 0.1 * Math.Max(0, s.Depth - 2))
                        * (1 + LotBonus(tier))
                        * (p.KindValue == PlaceKind.Dungeon ? 1.2 : 1.0) * mul;
        // 持てる量の 1.5 倍で頭打ち。溢れた分は置いていく（次の場面で引き返す理由になる）
        s.Loot = (int)Math.Min(s.Loot + Math.Max(1, amount), Capacity() * 1.5);
    }

    private ItemDef? Drop(int tier)
    {
        var pool = _book.Drops(Math.Min(tier, _book.MaxTier));
        return pool.Count == 0 ? null : pool[_rng.Next(pool.Count)];
    }

    /// <summary>拾った物を持つ。持ち物が一杯なら、売る物として荷に包む。</summary>
    private void Take(ItemDef it)
    {
        var s = S;
        if (s.Inventory.Count < InventoryCap)
        {
            s.Inventory.Add(it.Id);
            Equip(out _, out _);
        }
        else
            s.Loot = (int)Math.Min(s.Loot + it.SellPrice, Capacity() * 1.5);
    }

    private StanceInput Inputs(double gearRatio, double lootFill) =>
        new(S.Hp, S.Fatigue, gearRatio, lootFill, S.Streak, MachineLoad, _hotAvg);

    // ---------------------------------------------------------------------
    // 文
    // ---------------------------------------------------------------------

    /// <summary>理由の一文と、した事の一文を繋げる。どちらかが無ければ、ある方だけ。</summary>
    private string? Compose(Reason why, string action, PlaceDef? place)
    {
        string? a = Say(WhyCategory[(int)why], place: place);
        string? b = Say(action, place: place);
        if (a is null) return b;
        if (b is null) return a;
        return _lang == "en" ? a + " " + b : a + b;
    }

    /// <summary>直前に言った二文（理由と、した事）。続けて同じ文を選ばないため。保存しない。</summary>
    private Line? _said1, _said2;

    private string? Say(string category, ItemDef? item = null, Member? who = null, PlaceDef? place = null)
    {
        if (_world is null) return null;
        var line = _book.Pick(category, _world, _rng, _said1, _said2);
        if (line is null) return null;
        _said2 = _said1;
        _said1 = line;
        string t = line.For(_lang);
        if (t.IndexOf('{') < 0) return t;

        if (t.Contains("{town}")) t = t.Replace("{town}", CurrentTown.Name.For(_lang));
        if (t.Contains("{place}")) t = t.Replace("{place}", (place ?? CurrentPlace)?.Name.For(_lang) ?? "");
        if (t.Contains("{item}")) t = t.Replace("{item}", item?.Name.For(_lang) ?? "");
        if (t.Contains("{name}")) t = t.Replace("{name}", NameOf(who ?? Lead(Fighters)));
        if (t.Contains("{fighter}")) t = t.Replace("{fighter}", NameOf(Lead(Fighters)));
        if (t.Contains("{keeper}")) t = t.Replace("{keeper}", NameOf(Lead(Keepers)));
        if (t.Contains("{finder}")) t = t.Replace("{finder}", NameOf(Lead(Finders)));
        if (t.Contains("{carrier}")) t = t.Replace("{carrier}", NameOf(Lead(Carriers)));
        return _lang == "en" ? Capitalize(t) : t;
    }

    /// <summary>
    /// 英語は、差し込んだ名前（"the dust drift" 等）が文頭に来ると小文字で始まってしまう。
    /// 文頭と ". " の後だけ大文字にする。
    /// </summary>
    internal static string Capitalize(string t)
    {
        if (t.Length == 0) return t;
        char[]? buf = null;
        for (int i = 0; i < t.Length; i++)
        {
            bool head = i == 0 || (i >= 2 && t[i - 1] == ' ' && t[i - 2] is '.' or '!' or '?');
            if (head && char.IsLower(t[i]))
            {
                buf ??= t.ToCharArray();
                buf[i] = char.ToUpperInvariant(t[i]);
            }
        }
        return buf is null ? t : new string(buf);
    }

    private string NameOf(Member? m) => m is { } x ? Director.DisplayName(x.CharacterId, _lang) : "";

    /// <summary>その役の中で一番育った者。居なければ先頭の者。</summary>
    private Member? Lead(string[] kinds)
    {
        Member? best = null;
        int bestLv = -1;
        foreach (var k in kinds)
            foreach (var m in _members)
            {
                if (m.CharacterId != k) continue;
                int lv = _party.Level(m.Key);
                if (lv > bestLv) { bestLv = lv; best = m; }
            }
        return best ?? (_members.Length > 0 ? _members[0] : null);
    }

    // ---------------------------------------------------------------------
    // 画面に出す用
    // ---------------------------------------------------------------------

    /// <summary>いま居る所の名前（町か、向かっている行き先）。</summary>
    public string PlaceName(string lang) =>
        S.Mode == AdventureMode.Town ? CurrentTown.Name.For(lang)
        : CurrentPlace?.Name.For(lang) ?? CurrentTown.Name.For(lang);

    /// <summary>
    /// 章題の横に出す形の、いま居る所の名前。英語の行き先は文中に差し込むため小文字の冠詞から
    /// 書いてある（"the steaming creek"）。見出しに置くと小文字で始まって見えるので、**頭の一文字だけ**大文字にする。
    /// 日本語はそのまま。
    /// </summary>
    public string PlaceTitle(string lang) => HeaderForm(PlaceName(lang), lang);

    public static string HeaderForm(string name, string lang) =>
        lang == "en" && name.Length > 0 && char.IsLower(name[0])
            ? string.Concat(char.ToUpperInvariant(name[0]).ToString(), name.AsSpan(1))
            : name;

    /// <summary>その鍵の者が着けている物の名前。何も無ければ空。</summary>
    public string EquippedNames(string key, string lang)
    {
        if (!S.Equipped.TryGetValue(key, out var eq)) return "";
        var names = new List<string>(3);
        foreach (var id in eq)
            if (_book.Item(id) is { } it) names.Add(lang == "en" ? WithoutArticle(it.Name.En) : it.Name.For(lang));
        return string.Join(lang == "en" ? ", " : "・", names);
    }

    /// <summary>英語の道具名は文中に置くため冠詞付きで持っている。一覧では外す。</summary>
    private static string WithoutArticle(string en)
    {
        foreach (var a in new[] { "a ", "an ", "the ", "some " })
            if (en.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return en[a.Length..];
        return en;
    }
}
