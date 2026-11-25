using System.Text.Json;
using System.Text.Json.Serialization;
using PcMieru.Party.Game;

namespace PcMieru.Party.Adventure;

// 列挙は名前で保存する。**知らない名前は安全な値に読む**（新しい版の値・綴り違いで保存ごと捨てないため）

[JsonConverter(typeof(AdventureModeConverter))]
public enum AdventureMode { Town, Out, Back }

/// <summary>町に居る間の段取り。上から順に進む。</summary>
[JsonConverter(typeof(TownPhaseConverter))]
public enum TownPhase { Arrive, Equip, Sell, Shop, Rest, Board, Depart }

/// <summary>決めた場面の結果待ち。結果は次の場面で出す（一度に二行流さないため）。</summary>
[JsonConverter(typeof(PendingConverter))]
public enum Pending { None, Fight, Avoided, Side }

public sealed class AdventureModeConverter() : TolerantEnumConverter<AdventureMode>(AdventureMode.Town);
public sealed class TownPhaseConverter() : TolerantEnumConverter<TownPhase>(TownPhase.Arrive);
public sealed class PendingConverter() : TolerantEnumConverter<Pending>(Pending.None);
public sealed class StanceConverter() : TolerantEnumConverter<Stance>(Stance.Balanced);

/// <summary>
/// 冒険の進み具合。state.json の "adventure" に入る。
/// **小さく保つ。** 持ち物は上限で切り、記録は数だけ持つ。常駐物の保存が膨らみ続けてはいけない。
/// </summary>
public sealed class AdventureState
{
    public AdventureMode Mode { get; set; } = AdventureMode.Town;
    public TownPhase Phase { get; set; } = TownPhase.Arrive;

    /// <summary>いま拠点にしている町（adventure.json の並び順）。</summary>
    public int Town { get; set; }

    /// <summary>向かっている行き先の id。町に居る間は空。</summary>
    public string Place { get; set; } = "";
    /// <summary>前に行った行き先。続けて同じ所ばかりにならないように。</summary>
    public string LastPlace { get; set; } = "";
    public int Depth { get; set; }
    public int Planned { get; set; }
    public int BackSteps { get; set; }
    public Pending Pending { get; set; }

    /// <summary>体力 0..1。</summary>
    public double Hp { get; set; } = 1;
    /// <summary>疲れ 0..1。</summary>
    public double Fatigue { get; set; }
    /// <summary>勝ちの連続（正）と負けの連続（負）。</summary>
    public int Streak { get; set; }

    /// <summary>旅の初めの持ち金。最初の町で、ひとつ二つは買える。</summary>
    public const long StartingGold = 50;
    public long Gold { get; set; } = StartingGold;
    /// <summary>担いでいる拾い物の値打ち。町で売って金になる。</summary>
    public int Loot { get; set; }

    /// <summary>持ち物（装備していない道具の id）。上限あり。</summary>
    public List<string> Inventory { get; set; } = new();

    /// <summary>
    /// 鍵（経験値と同じ gpu#0 等）→ [手, 身, 守り] の道具 id。空文字は何も無し。
    /// **鍵ごとに持つ。** GPU が2枚なら、それぞれが自分の物を持つ。
    /// </summary>
    public Dictionary<string, string[]> Equipped { get; set; } = new();

    /// <summary>次の場面までに貯まった旅程。</summary>
    public double Progress { get; set; }

    /// <summary>この町への滞在で、何度休んだか・何を買ったか。</summary>
    public int Rests { get; set; }
    public int Bought { get; set; }

    /// <summary>直近に決めた構え。装備では同点の候補の選び方にだけ使う（構えで持ち替えはしない）。</summary>
    public Stance Stance { get; set; } = Stance.Balanced;

    // 記録（数だけ）
    public int Expeditions { get; set; }
    public int Returns { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }

    /// <summary>
    /// この版が知らない欄（新しい版が書いた物）。**読んだまま書き戻す。**
    /// 古い版で一度開いただけで、新しい版の進みが消えないように。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// 壊れた値・手で書き換えられた値を、動ける範囲に戻す。
    /// **読み込みのたびに通す。** ここを外すと、一つの変な値で冒険が止まったまま気づけない。
    /// </summary>
    public void Sanitize(int townCount)
    {
        Inventory ??= new List<string>();
        Equipped ??= new Dictionary<string, string[]>();
        Place ??= "";
        LastPlace ??= "";
        Inventory.RemoveAll(string.IsNullOrEmpty);
        foreach (var key in Equipped.Keys.ToList())
        {
            var e = Equipped[key];
            if (e is null || e.Length != 3)
            {
                var fixedUp = new string[3] { "", "", "" };
                if (e is not null)
                    for (int i = 0; i < Math.Min(3, e.Length); i++) fixedUp[i] = e[i] ?? "";
                Equipped[key] = fixedUp;
            }
            else
                for (int i = 0; i < 3; i++) e[i] ??= "";
        }
        Town = Math.Clamp(Town, 0, Math.Max(0, townCount - 1));
        Hp = double.IsFinite(Hp) ? Math.Clamp(Hp, 0.05, 1) : 1;
        Fatigue = double.IsFinite(Fatigue) ? Math.Clamp(Fatigue, 0, 1) : 0;
        Progress = double.IsFinite(Progress) ? Math.Clamp(Progress, 0, 1000) : 0;
        Gold = Math.Clamp(Gold, 0, AdventureEngine.GoldCap);
        Loot = Math.Max(0, Loot);
        Streak = Math.Clamp(Streak, -5, 5);
        Depth = Math.Clamp(Depth, 0, 32);
        Planned = Math.Clamp(Planned, 0, 32);
        BackSteps = Math.Clamp(BackSteps, 0, 32);
        if (!Enum.IsDefined(Mode)) Mode = AdventureMode.Town;
        if (!Enum.IsDefined(Phase)) Phase = TownPhase.Arrive;
        if (!Enum.IsDefined(Pending)) Pending = Pending.None;
        if (!Enum.IsDefined(Stance)) Stance = Stance.Balanced;
    }

    /// <summary>物語だけ最初からにしたとき。**持ち物と金は残す**（レベルと同じく、育てた側のもの）。</summary>
    public void ResetJourney()
    {
        Mode = AdventureMode.Town;
        Phase = TownPhase.Arrive;
        Town = 0;
        Place = "";
        LastPlace = "";
        Depth = Planned = BackSteps = 0;
        Pending = Pending.None;
        Hp = 1;
        Fatigue = 0;
        Streak = 0;
        Progress = 0;
        Rests = Bought = 0;
    }
}
