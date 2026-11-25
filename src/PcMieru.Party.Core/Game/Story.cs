using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Party.Game;

/// <summary>
/// 物語の本文。**中身は assets/story.json にあり、ここには一文も書かない。**
/// 場面を足すのに C# の再ビルドを要らなくするため。
/// 条件（when）だけは名前で決め打ちにしてある。式を解釈する仕組みを入れると、
/// 「文章を足したいだけ」の作業に言語処理系の面倒が付いてくるため。
/// </summary>
public sealed class Story
{
    public IReadOnlyList<Chapter> Chapters { get; }
    public IReadOnlyList<StoryEvent> Events { get; }

    private Story(StoryFile f)
    {
        Chapters = f.Chapters;
        Events = f.Events;
        Vignettes = f.Vignettes;
        // 傍白は状況が続く間、毎秒引かれる。そのたびに一覧を作らないよう、先に分けておく
        _eventsByWhen = f.Events.GroupBy(e => e.When)
                         .ToDictionary(g => g.Key, g => (IReadOnlyList<StoryEvent>)g.ToArray());
        _eventsById = f.Events.Where(e => e.Id.Length > 0)
                       .GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
    }

    private readonly Dictionary<string, IReadOnlyList<StoryEvent>> _eventsByWhen;
    private readonly Dictionary<string, StoryEvent> _eventsById;

    /// <summary>
    /// json の本文から組み立てる。**読み込み元（exe に埋めた資源か、試験用のファイルか）は呼ぶ側が決める。**
    /// ここが WPF の資源に直接触ると、画面の無い機械で試験が回せない。
    /// </summary>
    public static Story Parse(string json)
    {
        var file = JsonSerializer.Deserialize<StoryFile>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("story.json を読めませんでした");
        if (file.Version is not (1 or 2))
            throw new InvalidDataException($"story.json の形式が想定と違います (version={file.Version})");
        return new Story(file);
    }

    /// <summary>旅程から、いまいるべき章の番号。</summary>
    public int ChapterAt(double journey)
    {
        int idx = 0;
        for (int i = 0; i < Chapters.Count; i++)
            if (journey >= Chapters[i].Require) idx = i;
        return idx;
    }

    public StoryEvent? EventById(string id) =>
        _eventsById.TryGetValue(id, out var e) ? e : Events.FirstOrDefault(x => x.When == id);
    public IReadOnlyList<StoryEvent> EventsFor(string when) =>
        _eventsByWhen.TryGetValue(when, out var list) ? list : Array.Empty<StoryEvent>();

    /// <summary>章に属さない小話。ふとした瞬間に1つだけ流れる。</summary>
    public IReadOnlyList<Line> Vignettes { get; }

    /// <summary>全部でいくつの文章を持っているか。README と画面に出す用。</summary>
    public int LineCount =>
        Chapters.Sum(c => c.Scenes.Sum(s => s.Count))
        + Events.Sum(e => e.Texts.Count)
        + Vignettes.Count;

    // -- json の形 ------------------------------------------------------------
    private sealed class StoryFile
    {
        public int Version { get; set; }
        public List<Chapter> Chapters { get; set; } = new();
        public List<StoryEvent> Events { get; set; } = new();
        public List<Line> Vignettes { get; set; } = new();
    }
}

public sealed class Chapter
{
    public string Id { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    /// <summary>この章に入るのに必要な旅程。</summary>
    public double Require { get; set; }
    /// <summary>次の場面へ進むのに要る旅程。</summary>
    public double ScenePer { get; set; } = 20;

    /// <summary>
    /// 場面。**1場面が複数の候補を持てる。**
    /// 条件に当たったものが選ばれ、どれにも当たらなければ最後のものが出る。
    /// </summary>
    public List<Scene> Scenes { get; set; } = new();
}

/// <summary>
/// 1場面。候補が1つだけのこともある。
///
/// json では2通りの書き方を許す。**書く側の手数を増やさないため。**
///   { "ja": "...", "en": "..." }                     … 分岐しない場面
///   { "variants": [ {...}, {...} ] }                 … 分岐する場面
/// </summary>
[JsonConverter(typeof(SceneConverter))]
public sealed class Scene
{
    /// <summary>候補。上から順に条件を見て、最初に当たったものを出す。</summary>
    public List<Line> Variants { get; set; } = new();

    public int Count => Variants.Count;

    public Line? Pick(WorldState w)
    {
        foreach (var v in Variants)
            if (Conditions.Match(v.When, w)) return v;
        // どれにも当たらなければ、条件の無いものを探す。それも無ければ最後
        return Variants.LastOrDefault(v => string.IsNullOrEmpty(v.When)) ?? Variants.LastOrDefault();
    }
}

/// <summary>条件付きの一文。</summary>
public sealed class Line : LocalizedText
{
    /// <summary>この文が出る条件。空なら常に候補になる。</summary>
    public string? When { get; set; }
}

/// <summary>場面を、分岐あり・なしの両方の書き方から読む。</summary>
public sealed class SceneConverter : JsonConverter<Scene>
{
    private static readonly JsonSerializerOptions Inner = new() { PropertyNameCaseInsensitive = true };

    public override Scene Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions __)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
            return new Scene
            {
                Variants = variants.Deserialize<List<Line>>(Inner) ?? new List<Line>(),
            };

        var one = root.Deserialize<Line>(Inner);
        return new Scene { Variants = one is null ? new List<Line>() : new List<Line> { one } };
    }

    public override void Write(Utf8JsonWriter writer, Scene value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Variants, options);
}

public sealed class StoryEvent
{
    public string Id { get; set; } = "";
    /// <summary>条件の名前。Director が解釈する。</summary>
    public string When { get; set; } = "";
    /// <summary>同じ種類を再び出せるまでの秒数。</summary>
    public double Cooldown { get; set; }
    public List<Line> Texts { get; set; } = new();
}

public class LocalizedText
{
    public string Ja { get; set; } = "";
    public string En { get; set; } = "";
    public string For(string lang) => lang == "en" ? (En.Length > 0 ? En : Ja) : (Ja.Length > 0 ? Ja : En);
}
