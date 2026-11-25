namespace PcMieru.Party.Adventure;

/// <summary>道具の得意。8つに固定する（配列で持ち、毎回の割り当てを出さないため）。</summary>
public enum Stat
{
    Atk = 0,   // 手数・攻め          CPU
    Spd = 1,   // 速さ                CPU / M.2 / SSD
    Cap = 2,   // 積載量              メモリ
    Sup = 3,   // 支え・回復          メモリ / クーラー / 電源
    Mag = 4,   // 魔力（大きな術）    GPU
    Bst = 5,   // 瞬発（一度に出す力）GPU
    End = 6,   // 粘り                ディスク / 電源 / クーラー
    Lot = 7,   // 拾う力              ディスク
}

/// <summary>
/// 役どころ。**部品としての事実から決める**（docs/plot.md「能力値は実機から出す」）。
///
/// | 部品 | 得意 |
/// |---|---|
/// | CPU | 手数・速さ |
/// | メモリ | 積載量・支え |
/// | GPU | 魔力・瞬発 |
/// | M.2 / SSD | 粘り・拾う力・速さ |
/// | HDD | 粘り・拾う力（遅いが多く覚えている） |
/// | クーラー | 粘り・支え（守る相手は CPU ただ一人） |
/// | 電源 | 粘り・支え（落ちたら全部落ちる） |
/// | マザーボード | 支え・積載量 |
///
/// 重みは百分率の整数。**点数を整数で出すので、同点の判定が誤差で揺れない。**
/// </summary>
public static class Roles
{
    public const int StatCount = 8;

    //                                       Atk  Spd  Cap  Sup  Mag  Bst  End  Lot
    private static readonly int[] Cpu      = { 100,  80,  10,  10,  15,  20,  15,  10 };
    private static readonly int[] Cooler   = {  10,  10,  10,  70,  10,  10, 100,  10 };
    private static readonly int[] Memory   = {  10,  20, 100,  80,  10,  10,  20,  30 };
    private static readonly int[] Gpu      = {  20,  10,  10,  10, 100,  80,  15,  10 };
    private static readonly int[] Fast     = {  10,  50,  20,  10,  10,  10,  80,  80 };   // m2 / ssd
    private static readonly int[] Hdd      = {  10,  10,  30,  20,  10,  10, 100,  80 };
    private static readonly int[] Psu      = {  10,  10,  10,  60,  10,  20, 100,  10 };
    private static readonly int[] Board    = {  10,  10,  50, 100,  10,  10,  40,  10 };
    private static readonly int[] Anyone   = {  30,  30,  30,  30,  30,  30,  30,  30 };

    // 構えによる重みの掛け目（百分率）。
    // 攻めるときは攻めと拾う力を、守るときは粘りと支えを重く見る。
    //                                       Atk  Spd  Cap  Sup  Mag  Bst  End  Lot
    private static readonly int[] BoldMul    = { 130, 110,  90,  80, 130, 130,  80, 130 };
    private static readonly int[] CarefulMul = {  80, 100, 120, 130,  80,  80, 130,  80 };

    private static readonly int[][] Table = BuildTable();

    /// <summary>[役][構え][得意] の重み。起動時に1回だけ作る。</summary>
    private static int[][] BuildTable()
    {
        var roles = new[] { Cpu, Cooler, Memory, Gpu, Fast, Hdd, Psu, Board, Anyone };
        var t = new int[roles.Length * 3][];
        for (int r = 0; r < roles.Length; r++)
            for (int st = 0; st < 3; st++)
            {
                var w = new int[StatCount];
                for (int s = 0; s < StatCount; s++)
                {
                    int mul = (Stance)st switch
                    {
                        Stance.Bold => BoldMul[s],
                        Stance.Careful => CarefulMul[s],
                        _ => 100,
                    };
                    w[s] = roles[r][s] * mul / 100;
                }
                t[r * 3 + st] = w;
            }
        return t;
    }

    private static int RoleIndex(string characterId) => characterId switch
    {
        "cpu" => 0,
        "cpu-cooler" => 1,
        "memory" => 2,
        "gpu" => 3,
        "m2" or "ssd" => 4,
        "hdd" => 5,
        "psu" => 6,
        "motherboard" => 7,
        _ => 8,
    };

    /// <summary>その役・その構えでの重み（百分率）。返す配列は書き換えないこと。</summary>
    public static int[] Weights(string characterId, Stance stance) =>
        Table[RoleIndex(characterId) * 3 + (int)stance];

    public static bool TryParseStat(string? key, out Stat stat)
    {
        stat = key switch
        {
            "atk" => Stat.Atk, "spd" => Stat.Spd, "cap" => Stat.Cap, "sup" => Stat.Sup,
            "mag" => Stat.Mag, "bst" => Stat.Bst, "end" => Stat.End, "lot" => Stat.Lot,
            _ => (Stat)(-1),
        };
        return (int)stat >= 0;
    }
}
