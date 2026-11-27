using System.Text.Json.Serialization;

namespace PcMieru.Party.Adventure;

/// <summary>場面ごとの構え。**選ぶのは一行自身で、利用者は選べない。**</summary>
[JsonConverter(typeof(StanceConverter))]
public enum Stance { Careful = 0, Balanced = 1, Bold = 2 }

/// <summary>
/// 構えを決めた理由。物語には「なぜそうしたか」を一文で添える。
/// 前半は大胆に傾ける理由、後半は慎重に傾ける理由。
/// </summary>
public enum Reason
{
    None = 0,
    Fresh,        // 傷が無い
    Rested,       // 疲れていない
    GearStrong,   // 道具が行き先に勝っている
    StreakUp,     // 勝ちが続いている
    Calm,         // 機械が静か
    Hurt,         // 傷が深い
    Tired,        // 疲れている
    GearWeak,     // 道具が行き先に負けている
    Heavy,        // 荷が重い
    StreakDown,   // 負けが続いている
    Hot,          // 機械が熱い（高い負荷が続いている）
}

/// <summary>構えを決めるのに使う材料。すべて 0..1 か、それに近い幅。</summary>
/// <param name="Hp">一行の体力 0..1</param>
/// <param name="Fatigue">疲れ 0..1</param>
/// <param name="GearRatio">一行の力 ÷ 行き先の手強さ。1 で互角</param>
/// <param name="LootFill">拾い物の重さ ÷ 持てる量 0..1</param>
/// <param name="Streak">勝ちの連続（正）・負けの連続（負）</param>
/// <param name="MachineLoad">実機の負荷の 5 分平均（CPU と GPU の忙しい方）0..1</param>
/// <param name="MachineHot">高い負荷（85% 以上）が続いた割合の 15 分平均 0..1。熱の代わり</param>
public readonly record struct StanceInput(
    double Hp, double Fatigue, double GearRatio, double LootFill,
    int Streak, double MachineLoad, double MachineHot);

/// <summary>決めた構えと、その理由のうち一番効いたもの（大胆側・慎重側それぞれ）。</summary>
public readonly record struct StanceResult(
    Stance Stance, double Score,
    Reason Bolder, double BolderWeight,
    Reason Safer, double SaferWeight)
{
    /// <summary>大胆な方を選んだときに添える理由。効きが弱ければ「特に無い」。</summary>
    public Reason WhyBold => BolderWeight >= StanceRules.ReasonFloor ? Bolder : Reason.None;
    /// <summary>慎重な方を選んだときに添える理由。</summary>
    public Reason WhySafe => SaferWeight >= StanceRules.ReasonFloor ? Safer : Reason.None;
}

/// <summary>
/// 構えの決め方。**わざと単純な足し算にしてある。** 理由を一文で言えない決め方は、
/// 物語に「なぜ」を書けないので使えない。
///
/// | 材料 | 点 |
/// |---|---|
/// | 体力 | (体力 − 0.6) × 2.0 |
/// | 疲れ | −(疲れ − 0.3) × 1.5 |
/// | 道具と行き先 | clamp(比 − 1, −1, 1) × 1.6 |
/// | 拾い物の重さ | −重さ × 1.0 |
/// | 勝ち負けの続き | clamp(連続, −3, 3) × 0.15 |
/// | 実機の熱 | −熱さ × 1.2。熱さ = max((5分平均 − 0.45) ÷ 0.45, 高負荷の割合 × 1.2) を 0..1 に |
/// | 実機の静けさ | 5分平均が 0.10 未満なら +0.4 |
/// | ゆらぎ | ±0.25 の一様乱数 |
///
/// 合計が +0.5 以上なら大胆、−0.5 以下なら慎重、その間は五分。
/// 理由の一文は、一番効いた材料から選ぶ。ただし体力を「傷が無い」と書くのは <see cref="FreshAt"/> 以上のときだけ。
///
/// **機械が熱いと慎重になり、静かだと大胆になる。** 高い負荷が続いている間は旅程が速く進むので、
/// 一行は速く、しかし用心して歩く。放っておかれた機械では、ゆっくり、しかし思い切って歩く。
/// </summary>
public static class StanceRules
{
    public const double BoldAt = 0.5;
    public const double CarefulAt = -0.5;
    public const double Noise = 0.25;
    /// <summary>これより効きの弱い材料は、理由として書かない。</summary>
    public const double ReasonFloor = 0.15;
    /// <summary>体力をこれ以上残しているときだけ、「傷が無い」を理由に書く（点には体力の分がいつも入る）。</summary>
    public const double FreshAt = 0.95;

    public static double Heat(double load, double hot) =>
        Math.Clamp(Math.Max((load - 0.45) / 0.45, hot * 1.2), 0, 1);

    public static bool IsCalm(double load) => load < 0.10;

    public static StanceResult Decide(in StanceInput i, Random rng) =>
        Decide(i, (rng.NextDouble() * 2 - 1) * Noise);

    /// <summary>ゆらぎを外から与える版（試験用）。</summary>
    public static StanceResult Decide(in StanceInput i, double noise)
    {
        Reason up = Reason.None, down = Reason.None;
        double upW = 0, downW = 0, sum = 0;

        void Add(double v, Reason pos, Reason neg, bool posIsReason = true)
        {
            sum += v;
            if (posIsReason && v > upW) { upW = v; up = pos; }
            if (-v > downW) { downW = -v; down = neg; }
        }

        // 体力は 0.6 を超えた分だけ大胆に寄せるが、「傷が無い」と書くのはほぼ無傷のときだけ。
        // （効きだけで選ぶと、体力 0.68 で「誰も傷を負っていなかった」が出た）
        double hp = Math.Clamp(i.Hp, 0, 1);
        Add((hp - 0.6) * 2.0, Reason.Fresh, Reason.Hurt, posIsReason: hp >= FreshAt);
        Add(-(Math.Clamp(i.Fatigue, 0, 1) - 0.3) * 1.5, Reason.Rested, Reason.Tired);
        Add(Math.Clamp(i.GearRatio - 1, -1, 1) * 1.6, Reason.GearStrong, Reason.GearWeak);
        Add(-Math.Clamp(i.LootFill, 0, 1), Reason.None, Reason.Heavy);
        Add(Math.Clamp(i.Streak, -3, 3) * 0.15, Reason.StreakUp, Reason.StreakDown);
        Add(-Heat(i.MachineLoad, i.MachineHot) * 1.2, Reason.None, Reason.Hot);
        Add(IsCalm(i.MachineLoad) ? 0.4 : 0, Reason.Calm, Reason.None);

        double score = sum + noise;
        var stance = score >= BoldAt ? Stance.Bold : score <= CarefulAt ? Stance.Careful : Stance.Balanced;
        return new StanceResult(stance, score, up, upW, down, downW);
    }
}
