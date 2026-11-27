using System.Text;

namespace PcMieru.Party.I18n;

/// <summary>
/// 引き継ぎの確認（直せなかった欄の一覧）に出す、欄の呼び名。
/// 取り込みの検査（FieldAudit）は、読めなかった欄を内部の道筋（<c>Adventure.Gold</c>・<c>Experience[cpu#0]</c>・<c>GpuSlots[1]</c>）で返す。
/// それをそのまま見せても利用者には読めないので、ここで日本語・英語の名前にする。
/// 道筋の <c>[…]</c>（何件目か・どの鍵か）は名前のあとに添える。パーツの名前は盤の上と同じ（CPU / FAN / MEM / GPU0 / M.2 / SSD / HDD / PSU）。
/// **知らない道筋は道筋のまま出す**（何も隠さない）。表に無い欄が出ないことは、PartyState をたどるテストが全部の欄で確かめる。
/// </summary>
internal static class ImportFieldNames
{
    /// <summary>道筋（<c>[…]</c> は <c>[]</c> にそろえた形）→ 日本語・英語の名前。</summary>
    private static readonly Dictionary<string, (string Ja, string En)> Names = Build();

    /// <summary>表にある道筋の形。テストが PartyState をたどって出した形と突き合わせる。</summary>
    public static IReadOnlyCollection<string> KnownPatterns => Names.Keys;

    private static Dictionary<string, (string Ja, string En)> Build()
    {
        var t = new Dictionary<string, (string Ja, string En)>(StringComparer.Ordinal);
        void Add(string pattern, string ja, string en) => t.Add(pattern, (ja, en));

        // ---- 保存の本体（PartyState） ----
        Add("SaveVersion", "保存の版", "Save version");
        Add("Adventure", "冒険の進み", "Adventure progress");
        Add("Experience", "各パーツの経験値", "Experience of the parts");
        Add("Experience[]", "経験値", "Experience");
        Add("GpuSlots", "GPU の番号の記録", "GPU slot records");
        Add("GpuSlots[]", "GPU の番号の記録", "GPU slot record");
        Add("GpuSlotsRepaired", "GPU の番号を直した印", "GPU slot repair mark");
        Add("Journey", "旅の道のり", "Journey distance");
        Add("Chapter", "いまの章", "Current chapter");
        Add("Scene", "章の中の場面", "Scene in the chapter");
        Add("Log", "物語のログ", "Story log");
        Add("Log[]", "物語のログ", "Story log");
        Add("LastEventAt", "傍白を出した時刻の記録", "Times of the latest asides");
        Add("LastEventAt[]", "傍白を出した時刻", "Time of an aside");
        Add("RecentVignettes", "直近の小話", "Recent vignettes");
        Add("RecentVignettes[]", "直近の小話", "Recent vignette");
        Add("UptimeSeconds", "累計の起動時間", "Total running time");
        Add("LastSeenUnix", "前回終了した時刻", "Time of the last close");
        Add("Boots", "起動した回数", "Number of starts");
        Add("Lang", "表示言語", "Display language");
        Add("Zoom", "窓の大きさ", "Window size");
        Add("WindowLeft", "窓の左の位置", "Window left position");
        Add("WindowTop", "窓の上の位置", "Window top position");
        Add("Topmost", "常に前面に表示", "Always on top");
        Add("Expanded", "各パーツの稼働状況の表示", "Per-part status display");
        Add("Labels", "名札の出し方", "Name tag style");
        Add("UpdateMode", "更新の設定", "Update setting");

        // ---- 冒険（AdventureState） ----
        Add("Adventure.Mode", "冒険の段階（町・道中・帰り道）", "Adventure stage (town, out, back)");
        Add("Adventure.Phase", "町での過ごし方の段階", "Stage of the town visit");
        Add("Adventure.Town", "拠点の町", "Home town");
        Add("Adventure.Place", "向かっている行き先", "Current destination");
        Add("Adventure.LastPlace", "前に行った行き先", "Previous destination");
        Add("Adventure.Depth", "進んだ深さ", "Depth reached");
        Add("Adventure.Planned", "予定の長さ", "Planned length");
        Add("Adventure.BackSteps", "帰り道の歩数", "Steps on the way back");
        Add("Adventure.Pending", "決まっている次の出来事", "Event waiting to happen");
        Add("Adventure.Hp", "冒険の体力", "Adventure HP");
        Add("Adventure.Fatigue", "冒険の疲れ", "Adventure fatigue");
        Add("Adventure.Streak", "勝ち負けの連続", "Win/loss streak");
        Add("Adventure.Gold", "冒険のお金", "Adventure gold");
        Add("Adventure.Loot", "担いでいる拾い物", "Carried loot");
        Add("Adventure.Inventory", "冒険の持ち物", "Adventure items");
        Add("Adventure.Inventory[]", "冒険の持ち物", "Adventure item");
        Add("Adventure.Equipped", "冒険の装備", "Adventure gear");
        Add("Adventure.Equipped[]", "冒険の装備", "Adventure gear");
        Add("Adventure.Equipped[][]", "冒険の装備", "Adventure gear");
        Add("Adventure.Progress", "次の場面までの旅程", "Progress toward the next scene");
        Add("Adventure.Rests", "町で休んだ回数", "Rests in town");
        Add("Adventure.Bought", "町で買った回数", "Purchases in town");
        Add("Adventure.Stance", "直近の構え", "Latest stance");
        Add("Adventure.Expeditions", "遠出の回数", "Number of expeditions");
        Add("Adventure.Returns", "帰還の回数", "Number of returns");
        Add("Adventure.Wins", "勝ちの数", "Number of wins");
        Add("Adventure.Losses", "負けの数", "Number of losses");
        return t;
    }

    /// <summary>道筋を、いまの言語での名前にする。知らない道筋・形の合わない道筋はそのまま返す。</summary>
    public static string Display(string path)
    {
        bool en = Strings.Lang == "en";

        // 検査が最後に足す 2 つ（Count が上限を超えたとき・欄を特定できなかったとき）
        if (path == "<unidentified>")
            return en ? "A field (which one is not known)" : "読み取れなかった欄（どの欄かは分かりません）";
        if (path.StartsWith('+') && path.EndsWith(" more", StringComparison.Ordinal)
            && int.TryParse(path.AsSpan(1, path.Length - 6), out int more) && more > 0)
            return en ? $"and {more} more" : $"ほか {more} 件";

        if (!TryParse(path, out string pattern, out var subs) || !Names.TryGetValue(pattern, out var name))
            return path;

        string text = en ? name.En : name.Ja;
        if (subs.Count == 0) return text;
        var shown = subs.Select(s => Subscript(s, en));
        return en ? $"{text} ({string.Join(", ", shown)})" : $"{text}（{string.Join("、", shown)}）";
    }

    /// <summary>
    /// <c>Name</c>・<c>Name[sub]</c>・<c>Name[sub][sub].Next…</c> の形を、<c>[sub]</c> を <c>[]</c> にした形と添え字の並びに分ける。
    /// 鍵は長くて任意の文字を含みうる（検査が 48 文字で切る）ので、閉じ括弧は「そのあとが終わり・「.」・「[」のどれか」の所とする。形が合わなければ false。
    /// </summary>
    private static bool TryParse(string path, out string pattern, out List<string> subs)
    {
        pattern = "";
        subs = [];
        var sb = new StringBuilder();
        int i = 0;
        while (true)
        {
            int start = i;
            while (i < path.Length && (char.IsAsciiLetterOrDigit(path[i]) || path[i] == '_')) i++;
            if (i == start) return false;
            sb.Append(path, start, i - start);

            while (i < path.Length && path[i] == '[')
            {
                int close = FindClose(path, i + 1);
                if (close < 0) return false;
                subs.Add(path[(i + 1)..close]);
                sb.Append("[]");
                i = close + 1;
            }

            if (i == path.Length) break;
            if (path[i] != '.') return false;
            sb.Append('.');
            i++;
        }

        pattern = sb.ToString();
        return true;
    }

    private static int FindClose(string path, int from)
    {
        for (int j = from; j < path.Length; j++)
            if (path[j] == ']' && (j + 1 == path.Length || path[j + 1] is '.' or '['))
                return j;
        return -1;
    }

    /// <summary>添え字: 数字だけなら何件目か（0 始まりを 1 始まりにする）、それ以外は鍵（パーツの鍵は盤の上の名前に）。</summary>
    private static string Subscript(string sub, bool en)
    {
        if (sub.Length > 0 && sub.All(char.IsAsciiDigit) && int.TryParse(sub, out int index) && index < int.MaxValue)
            return en ? $"#{index + 1}" : $"{index + 1} 件目";
        return PartKey(sub);
    }

    /// <summary>経験値・装備の鍵（<c>cpu#0</c>・<c>cpu-cooler#0</c>・<c>memory</c>・<c>gpu#1</c>・<c>m2</c>…）を、盤の上のパーツ名にする。知らない鍵はそのまま。</summary>
    private static string PartKey(string key)
    {
        string head = key, tail = "";
        int hash = key.IndexOf('#');
        if (hash >= 0)
        {
            head = key[..hash];
            tail = key[(hash + 1)..];
            if (tail.Length == 0 || !tail.All(char.IsAsciiDigit)) return key;
        }

        string? part = head switch
        {
            "cpu" => "CPU",
            "cpu-cooler" => "FAN",
            "memory" => "MEM",
            "gpu" => "GPU",
            "m2" => "M.2",
            "ssd" => "SSD",
            "hdd" => "HDD",
            "psu" => "PSU",
            _ => null,
        };
        return part is null ? key : part + tail;
    }
}
