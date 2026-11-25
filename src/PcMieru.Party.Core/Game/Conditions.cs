using System.Globalization;

namespace PcMieru.Party.Game;

/// <summary>条件を判定するのに要る、いまの機械と進行の状態。</summary>
public sealed record WorldState(
    IReadOnlyDictionary<string, int> Levels,   // "cpu" → その種類で最も育った個体のレベル
    int Chapter,
    int Gpus,
    int Cpus,
    IReadOnlySet<string> Present,              // "m2" / "ssd" / "hdd" など、載っている種類
    int Boots,
    double Hours);

/// <summary>
/// 物語の分岐条件。
///
/// **式を解釈する仕組みは、あえて最小限にしてある。**
/// and / or / 括弧を許すと、本文を足したいだけの作業に言語処理系の面倒が付いてくる。
/// 書けるのは「1つの値を1つの数と比べる」だけ。それで足りないと分かったら、
/// そのときに広げればよい。
///
/// 書ける形:
///   level:cpu&gt;=12   chapter&gt;=5   gpus&gt;=2   cpus&gt;=2
///   has:m2           boots&gt;=50    hours&gt;=100
///
/// **読めない条件は「偽」にする。** 真にすると、綴りを間違えた条件が
/// どこでも通ってしまい、間違いに気づけない。
/// </summary>
public static class Conditions
{
    public static bool Match(string? when, WorldState w)
    {
        if (string.IsNullOrWhiteSpace(when)) return true;   // 条件なし＝常に出る
        string s = when.Trim();

        if (s.StartsWith("has:", StringComparison.Ordinal))
            return w.Present.Contains(s[4..].Trim());

        if (!Split(s, out string left, out string op, out double n)) return false;

        double? value = left switch
        {
            "chapter" => w.Chapter,
            "gpus" => w.Gpus,
            "cpus" => w.Cpus,
            "boots" => w.Boots,
            "hours" => w.Hours,
            _ when left.StartsWith("level:", StringComparison.Ordinal)
                => w.Levels.TryGetValue(left[6..].Trim(), out int lv) ? lv : 0,
            _ => null,
        };
        if (value is null) return false;

        return op switch
        {
            ">=" => value >= n,
            "<=" => value <= n,
            ">" => value > n,
            "<" => value < n,
            "==" => Math.Abs(value.Value - n) < 0.0001,
            _ => false,
        };
    }

    /// <summary>2文字の演算子を先に見ること。">=" を ">" と読むと意味が変わる。</summary>
    private static bool Split(string s, out string left, out string op, out double n)
    {
        left = ""; op = ""; n = 0;
        foreach (var candidate in new[] { ">=", "<=", "==", ">", "<" })
        {
            int at = s.IndexOf(candidate, StringComparison.Ordinal);
            if (at <= 0) continue;
            left = s[..at].Trim();
            op = candidate;
            return double.TryParse(s[(at + candidate.Length)..].Trim(),
                                   NumberStyles.Float, CultureInfo.InvariantCulture, out n);
        }
        return false;
    }
}
