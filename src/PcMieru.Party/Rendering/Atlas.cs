using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PcMieru.Party.Rendering;

/// <summary>
/// ドット絵のアトラス（1枚のPNG＋座標表）を読む。
///
/// 絵の正は TypeScript 側（リポジトリの sprites/）にあり、
/// <c>npx tsx tools/export-atlas.mts</c> でここに焼き込まれる。
/// **C# 側にドット絵を描くコードは持たない。** 二重管理になってズレるため。
/// </summary>
public sealed class Atlas
{
    public int Cell { get; }
    public int MarkCell { get; }
    public BitmapSource Characters { get; }
    public BitmapSource Marks { get; }

    private readonly AtlasFile _file;

    private Atlas(AtlasFile file, BitmapSource characters, BitmapSource marks)
    {
        _file = file;
        Cell = file.Cell;
        MarkCell = file.MarkCell;
        Characters = characters;
        Marks = marks;
    }

    public IReadOnlyCollection<string> CharacterIds => _file.Characters.Keys;

    public CharacterInfo? Character(string id) =>
        _file.Characters.TryGetValue(id, out var c) ? c : null;

    /// <summary>指定のコマの、アトラス上の位置。無ければ null。</summary>
    public Int32Rect? Frame(string characterId, string motion, int frameIndex)
    {
        var motions = Character(characterId)?.Motions;
        if (motions is null || !motions.TryGetValue(motion, out var m) || m.Frames.Count == 0)
            return null;
        var f = m.Frames[((frameIndex % m.Frames.Count) + m.Frames.Count) % m.Frames.Count];
        return new Int32Rect(f[0], f[1], Cell, Cell);
    }

    public MotionInfo? Motion(string characterId, string motion) =>
        Character(characterId)?.Motions.GetValueOrDefault(motion);

    public Int32Rect? MarkFrame(string markId, int frameIndex)
    {
        if (!_file.Marks.TryGetValue(markId, out var m) || m.Frames.Count == 0) return null;
        var f = m.Frames[((frameIndex % m.Frames.Count) + m.Frames.Count) % m.Frames.Count];
        return new Int32Rect(f[0], f[1], MarkCell, MarkCell);
    }

    /// <summary>exe に埋め込んだアセットから読む。</summary>
    public static Atlas Load()
    {
        var json = ReadResourceText("Assets/atlas.json");
        var file = JsonSerializer.Deserialize<AtlasFile>(json, JsonOpts)
                   ?? throw new InvalidDataException("atlas.json を読めませんでした");

        if (file.Version != 1)
            throw new InvalidDataException($"atlas.json の形式が想定と違います (version={file.Version})");

        return new Atlas(file, ReadResourceImage("Assets/characters.png"), ReadResourceImage("Assets/marks.png"));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static Stream OpenResource(string relativePath)
    {
        var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
        return Application.GetResourceStream(uri)?.Stream
               ?? throw new FileNotFoundException($"埋め込みアセットが見つかりません: {relativePath}");
    }

    private static string ReadResourceText(string relativePath)
    {
        using var s = OpenResource(relativePath);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static BitmapSource ReadResourceImage(string relativePath)
    {
        using var s = OpenResource(relativePath);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;      // ストリームを閉じても保持する
        bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bmp.StreamSource = s;
        bmp.EndInit();
        bmp.Freeze();                                     // 別スレッドから触れるようにする
        return bmp;
    }
}

// --- atlas.json の形 ---------------------------------------------------------

public sealed class AtlasFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("cell")] public int Cell { get; set; } = 32;
    [JsonPropertyName("markCell")] public int MarkCell { get; set; } = 16;
    [JsonPropertyName("characters")] public Dictionary<string, CharacterInfo> Characters { get; set; } = new();
    [JsonPropertyName("marks")] public Dictionary<string, MarkInfo> Marks { get; set; } = new();
}

public sealed class LocalizedText
{
    [JsonPropertyName("ja")] public string Ja { get; set; } = "";
    [JsonPropertyName("en")] public string En { get; set; } = "";
    public string For(string lang) => lang == "en" ? En : Ja;
}

public sealed class CharacterInfo
{
    [JsonPropertyName("name")] public LocalizedText Name { get; set; } = new();
    [JsonPropertyName("motif")] public LocalizedText Motif { get; set; } = new();
    [JsonPropertyName("trait")] public LocalizedText Trait { get; set; } = new();
    [JsonPropertyName("why")] public LocalizedText Why { get; set; } = new();
    /// <summary>「この子の話すことは遊びです」の断り書き。表示を省かないこと。</summary>
    [JsonPropertyName("disclaimer")] public LocalizedText Disclaimer { get; set; } = new();
    [JsonPropertyName("family")] public string Family { get; set; } = "pc";
    [JsonPropertyName("motions")] public Dictionary<string, MotionInfo> Motions { get; set; } = new();
}

public sealed class MotionInfo
{
    [JsonPropertyName("ms")] public int Ms { get; set; } = 140;
    /// <summary>足場の線に合わせる点。絵の上端からの割合（足裏0.94 / 尻0.80 / 手0.06）。</summary>
    [JsonPropertyName("anchor")] public double Anchor { get; set; } = 0.94;
    [JsonPropertyName("once")] public bool Once { get; set; }
    [JsonPropertyName("rope")] public double? Rope { get; set; }
    [JsonPropertyName("mark")] public string? Mark { get; set; }
    [JsonPropertyName("frames")] public List<int[]> Frames { get; set; } = new();
}

public sealed class MarkInfo
{
    [JsonPropertyName("frames")] public List<int[]> Frames { get; set; } = new();
}
