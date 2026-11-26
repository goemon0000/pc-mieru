namespace PcMieru.Party.Ui;

/// <summary>
/// 盤のキャラの下に出す名札の字。「CPU Lv1」「GPU0 Lv3」のように、**どの子かが読める**形にする。
/// 名前は席の Title（CPU / FAN / MEM / GPU / GPU0 / M.2 / SSD / HDD / PSU）。展開した欄の行と同じ短い名で、別の呼び名は作らない。
/// </summary>
public static class BoardLabel
{
    /// <summary>
    /// 名札の字を決める。<paramref name="maxWidth"/> は 1 体ぶんの幅（隣の子との間隔 = (Cell + Gap) × 倍率 から少し引いた値）。
    /// **枠からはみ出すくらいなら名前を落として「Lv3」だけにする**（隣の名札と重ならないように）。
    /// <paramref name="measure"/> は字の幅を返す（窓の側が実際のフォントで測る。試験では字数で代用できる）。
    /// </summary>
    public static string Text(string title, string levelWord, int level, double maxWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        string levelOnly = levelWord + level;
        if (string.IsNullOrWhiteSpace(title)) return levelOnly;
        string full = title.Trim() + " " + levelOnly;
        return measure(full) <= maxWidth ? full : levelOnly;
    }
}
