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

    /// <summary>
    /// Everything that decides what the name tags say and where they sit: the tag mode, the scale, the screen's DIP factor, the board's pixel width, the language, and each seat's title and level, in order.
    /// The window places the tags only when this changes (C38): a redraw for a new animation frame changes none of it, and re-measuring every title with FormattedText and re-laying out every tag on each of those
    /// redraws was Party's biggest idle cost over the old app.
    /// </summary>
    public static int Fingerprint(int mode, double scale, double dip, int boardPixelWidth, string lang, IReadOnlyList<(string Title, int Level)> seats)
    {
        ArgumentNullException.ThrowIfNull(seats);
        var h = new HashCode();
        h.Add(mode);
        h.Add(scale);
        h.Add(dip);
        h.Add(boardPixelWidth);
        h.Add(lang, StringComparer.Ordinal);
        h.Add(seats.Count);
        foreach (var (title, level) in seats)
        {
            h.Add(title, StringComparer.Ordinal);
            h.Add(level);
        }

        return h.ToHashCode();
    }
}
