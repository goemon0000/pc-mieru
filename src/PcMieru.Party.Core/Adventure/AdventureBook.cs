using System.Text.Json;
using System.Text.Json.Serialization;
using PcMieru.Party.Game;

namespace PcMieru.Party.Adventure;

/// <summary>
/// 冒険の本文と決まりの表（町・行き先・道具・文章）。**中身は assets/adventure.json にある。**
/// 物語の本文と同じく、ここには一文も書かない。足すときは story-work/ の原稿を直して
/// tools/build-story.py で組み上げる。
/// </summary>
public sealed class AdventureBook
{
    public const int CurrentVersion = 1;

    public IReadOnlyList<TownDef> Towns { get; }
    public IReadOnlyList<ItemDef> Items { get; }
    public int MaxTier { get; }

    private readonly Dictionary<string, ItemDef> _byId;
    private readonly Dictionary<string, Line[]> _lines;
    private readonly ItemDef[][] _shopByTier;    // [町の格] → 店に並ぶ物
    private readonly ItemDef[][] _dropByTier;    // [格] → 拾える物
    private readonly ItemDef[] _starters;

    private AdventureBook(BookFile f)
    {
        if (f.Towns.Count == 0) throw new InvalidDataException("adventure.json: 町が1つも無い");
        if (f.Items.Count == 0) throw new InvalidDataException("adventure.json: 道具が1つも無い");

        // 町は章の順に並べる。旅は前にしか進まない
        Towns = f.Towns.OrderBy(t => t.FromChapter).ToList();
        foreach (var t in Towns)
        {
            if (t.Places.Count == 0) throw new InvalidDataException($"adventure.json: 町 {t.Id} に行き先が無い");
            foreach (var p in t.Places) p.Prepare();
        }

        // 並びは id 順に固定する。**同点のときの選び方が、読み込み順で変わらないように。**
        Items = f.Items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        _byId = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            it.Prepare(i);
            if (!_byId.TryAdd(it.Id, it))
                throw new InvalidDataException($"adventure.json: 道具の id が重複 {it.Id}");
        }
        MaxTier = Items.Max(i => i.Tier);

        _shopByTier = new ItemDef[MaxTier + 2][];
        _dropByTier = new ItemDef[MaxTier + 2][];
        for (int t = 0; t < _shopByTier.Length; t++)
        {
            int tt = t;
            // 店はその町の格と、ひとつ下の格を並べる。拾い物も同じ幅
            _shopByTier[t] = Items.Where(i => i.Shop && !i.Starter && (i.Tier == tt || i.Tier == tt - 1)).ToArray();
            _dropByTier[t] = Items.Where(i => !i.Starter && (i.Tier == tt || i.Tier == tt - 1)).ToArray();
        }
        _starters = Items.Where(i => i.Starter).ToArray();

        _lines = new Dictionary<string, Line[]>(StringComparer.Ordinal);
        foreach (var (k, v) in f.Lines) _lines[k] = v.ToArray();
    }

    public static AdventureBook Parse(string json)
    {
        var file = JsonSerializer.Deserialize<BookFile>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("adventure.json を読めませんでした");
        if (file.Version != CurrentVersion)
            throw new InvalidDataException($"adventure.json の形式が想定と違います (version={file.Version})");
        return new AdventureBook(file);
    }

    public ItemDef? Item(string? id) =>
        string.IsNullOrEmpty(id) ? null : _byId.GetValueOrDefault(id);

    /// <summary>章から、一行が拠点にしているべき町の番号。</summary>
    public int TownFor(int chapter)
    {
        int idx = 0;
        for (int i = 0; i < Towns.Count; i++)
            if (chapter >= Towns[i].FromChapter) idx = i;
        return idx;
    }

    public IReadOnlyList<ItemDef> ShopStock(int tier) => _shopByTier[Math.Clamp(tier, 0, _shopByTier.Length - 1)];
    public IReadOnlyList<ItemDef> Drops(int tier) => _dropByTier[Math.Clamp(tier, 0, _dropByTier.Length - 1)];
    public IReadOnlyList<ItemDef> Starters => _starters;

    public IReadOnlyList<Line> Lines(string category) =>
        _lines.TryGetValue(category, out var l) ? l : Array.Empty<Line>();

    public IEnumerable<string> Categories => _lines.Keys;

    /// <summary>
    /// 条件に合う文から1つ選ぶ。**一覧を作らずに選ぶ**（毎回の割り当てを出さないため）。
    /// 合うものが無ければ null。
    /// </summary>
    public Line? Pick(string category, WorldState world, Random rng) => Pick(category, world, rng, null, null);

    /// <summary>
    /// 条件に合う文から1つ選ぶ。<paramref name="avoid"/>・<paramref name="avoid2"/>（直前に言った文）は、
    /// 他に出せる文がある限り選ばない。**同じ帰り道の一文が二場面続くと、止まって見える。**
    /// </summary>
    public Line? Pick(string category, WorldState world, Random rng, Line? avoid, Line? avoid2)
    {
        if (!_lines.TryGetValue(category, out var all) || all.Length == 0) return null;
        int usable = 0, fresh = 0;
        foreach (var l in all)
        {
            if (!Conditions.Match(l.When, world)) continue;
            usable++;
            if (!ReferenceEquals(l, avoid) && !ReferenceEquals(l, avoid2)) fresh++;
        }
        if (usable == 0) return null;
        bool skip = fresh > 0;
        int k = rng.Next(skip ? fresh : usable);
        foreach (var l in all)
        {
            if (!Conditions.Match(l.When, world)) continue;
            if (skip && (ReferenceEquals(l, avoid) || ReferenceEquals(l, avoid2))) continue;
            if (k-- == 0) return l;
        }
        return null;
    }

    // -- json の形 ------------------------------------------------------------
    private sealed class BookFile
    {
        public int Version { get; set; }
        public List<TownDef> Towns { get; set; } = new();
        public List<ItemDef> Items { get; set; } = new();
        public Dictionary<string, List<Line>> Lines { get; set; } = new();
    }
}

/// <summary>町。宿（休む）・店（道具）・掲示（行き先）がある。</summary>
public sealed class TownDef
{
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    /// <summary>この章に入ったら、次に帰るのはこの町になる。</summary>
    public int FromChapter { get; set; }
    /// <summary>町の格。店に並ぶ物と、近くの行き先の手強さを決める。</summary>
    public int Tier { get; set; } = 1;
    public List<PlaceDef> Places { get; set; } = new();
}

public enum PlaceKind { Field, Dungeon, Side }

/// <summary>掲示に出ている行き先。</summary>
public sealed class PlaceDef
{
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    /// <summary>"field" / "dungeon" / "side"</summary>
    public string Kind { get; set; } = "field";
    /// <summary>奥に着くまでの場面の数（決める場面の数）。</summary>
    public int Length { get; set; } = 4;
    /// <summary>町の格より手強いか（0 か 1）。</summary>
    public int Harder { get; set; }

    [JsonIgnore] public PlaceKind KindValue { get; private set; }

    internal void Prepare()
    {
        KindValue = Kind switch
        {
            "dungeon" => PlaceKind.Dungeon,
            "side" => PlaceKind.Side,
            _ => PlaceKind.Field,
        };
        Length = Math.Clamp(Length, 2, 8);
        Harder = Math.Clamp(Harder, 0, 1);
    }
}

/// <summary>
/// 道具。**数値は原稿に書かせず、格と得意から決める。**
/// 書き手に数値まで持たせると、足すたびに釣り合いが崩れる（物語の旅程と同じ考え）。
/// </summary>
public sealed class ItemDef
{
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    /// <summary>"hand" / "body" / "charm"</summary>
    public string Slot { get; set; } = "hand";
    public int Tier { get; set; }
    /// <summary>主な得意（atk spd cap sup mag bst end lot）。</summary>
    public string Primary { get; set; } = "atk";
    public string? Secondary { get; set; }
    /// <summary>使える者を限る（キャラid）。空なら誰でも。</summary>
    public List<string>? Fits { get; set; }
    /// <summary>店に並ぶか。偽なら拾うしかない。</summary>
    public bool Shop { get; set; } = true;
    /// <summary>旅の初めに、ありあわせで持たせる物。</summary>
    public bool Starter { get; set; }

    [JsonIgnore] public EquipSlot SlotValue { get; private set; }
    [JsonIgnore] public int[] Stats { get; } = new int[Roles.StatCount];
    [JsonIgnore] public int Price { get; private set; }
    [JsonIgnore] public int SellPrice => Price / 2;
    [JsonIgnore] public int Order { get; private set; }

    public bool FitsCharacter(string characterId) =>
        Fits is not { Count: > 0 } || Fits.Contains(characterId);

    internal void Prepare(int order)
    {
        Order = order;
        SlotValue = Slot switch
        {
            "body" => EquipSlot.Body,
            "charm" => EquipSlot.Charm,
            _ => EquipSlot.Hand,
        };
        Tier = Math.Max(0, Tier);
        Array.Clear(Stats);
        if (Roles.TryParseStat(Primary, out var p)) Stats[(int)p] += PrimaryValue(Tier);
        if (Roles.TryParseStat(Secondary, out var s)) Stats[(int)s] += SecondaryValue(Tier);
        Price = 12 * (Tier + 1) * (Tier + 1);
    }

    public static int PrimaryValue(int tier) => 2 + 3 * tier;
    public static int SecondaryValue(int tier) => 1 + 3 * tier / 2;
}

public enum EquipSlot { Hand = 0, Body = 1, Charm = 2 }
