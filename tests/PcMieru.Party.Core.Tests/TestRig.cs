using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>本物の本文（assets/）を読む。試験のたびに読み直さない。</summary>
public static class TestData
{
    private static readonly Lazy<Story> _story = new(() => Story.Parse(Read("story.json")));
    private static readonly Lazy<AdventureBook> _book = new(() => AdventureBook.Parse(Read("adventure.json")));

    public static Story Story => _story.Value;
    public static AdventureBook Book => _book.Value;

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", name));

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>模擬の負荷。1秒ごとの (CPU, GPU, メモリ, ディスク) を返す。</summary>
public enum Profile { Idle, Office, Gaming, Rendering }

public readonly record struct Load(double Cpu, double Gpu, double Memory, double Disk);

public static class Profiles
{
    /// <summary>
    /// 時刻（模擬開始からの秒）とゆらぎから、その秒の負荷を出す。
    /// 1日の形: 事務は 9〜18 時だけ働く、遊びは夜に GPU を回す、描画は昼夜なく張り付く。
    /// </summary>
    public static Load At(Profile p, long t, Random rng)
    {
        double hour = (t % 86400) / 3600.0;
        double jitter = rng.NextDouble();
        switch (p)
        {
            case Profile.Idle:
                return new(0.02 + 0.03 * jitter, 0.0, 0.35, 0.01);
            case Profile.Office:
                if (hour is >= 9 and < 18)
                    return new(jitter < 0.05 ? 0.7 : 0.10 + 0.15 * jitter, 0.05, 0.55, jitter < 0.1 ? 0.4 : 0.05);
                return new(0.03, 0.0, 0.40, 0.01);
            case Profile.Gaming:
                if (hour is >= 19 and < 24)
                    return new(0.45 + 0.2 * jitter, 0.92 + 0.08 * jitter, 0.75, 0.15);
                if (hour is >= 9 and < 18)
                    return new(0.12 + 0.1 * jitter, 0.05, 0.55, 0.05);
                return new(0.03, 0.0, 0.40, 0.01);
            default:   // Rendering: 張り付いたまま
                return new(0.95 + 0.05 * jitter, 0.88 + 0.1 * jitter, 0.86, 0.35);
        }
    }
}

/// <summary>
/// 模擬の機械。**毎秒の呼び出しで割り当てを出さない**ように、
/// 部品の行と「働き」は量子化して作り置きを使い回す（毎秒の割り当てを測る試験のため）。
/// </summary>
public sealed class Rig
{
    public static readonly (string Key, string Id, SlotKind Kind)[] Desktop =
    {
        ("cpu#0", "cpu", SlotKind.Socket), ("cpu-cooler#0", "cpu-cooler", SlotKind.Socket),
        ("memory", "memory", SlotKind.Dimm), ("gpu#0", "gpu", SlotKind.Pcie), ("gpu#1", "gpu", SlotKind.Pcie),
        ("m2", "m2", SlotKind.M2), ("hdd", "hdd", SlotKind.Sata), ("psu", "psu", SlotKind.Atx),
    };

    private const int Steps = 20;   // 0.05 刻み
    private readonly PartySlot[,] _slotCache;
    private readonly Dictionary<int, Work> _workCache = new();
    private readonly List<PartySlot> _slots = new();

    public IReadOnlyList<PartySlot> Slots => _slots;

    public Rig()
    {
        _slotCache = new PartySlot[Desktop.Length, Steps + 1];
        for (int i = 0; i < Desktop.Length; i++)
            for (int q = 0; q <= Steps; q++)
                _slotCache[i, q] = new PartySlot(Desktop[i].Key, Desktop[i].Id, Desktop[i].Kind, Desktop[i].Key, "", q / (double)Steps);
        for (int i = 0; i < Desktop.Length; i++) _slots.Add(_slotCache[i, 0]);
    }

    private static int Q(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * Steps);

    /// <summary>その秒の負荷を、部品ごとの行と「働き」にする。</summary>
    public Work Apply(Load l)
    {
        // 部品ごとの負荷。2枚目の GPU はあまり使われない、HDD は控えに回っている、という機械
        Span<double> per = stackalloc double[]
        {
            l.Cpu, l.Cpu, l.Memory, l.Gpu, l.Gpu * 0.3, l.Disk, l.Disk * 0.2, (l.Cpu + l.Gpu) * 0.5,
        };
        for (int i = 0; i < per.Length; i++) _slots[i] = _slotCache[i, Q(per[i])];

        int key = ((Q(l.Cpu) * 21 + Q(l.Gpu)) * 21 + Q(l.Memory)) * 21 + Q(l.Disk);
        if (!_workCache.TryGetValue(key, out var w))
        {
            w = new Work(Q(l.Cpu) / (double)Steps, Q(l.Gpu) / (double)Steps,
                         Q(l.Memory) / (double)Steps, Q(l.Disk) / (double)Steps, 0.5);
            _workCache[key] = w;
        }
        return w;
    }

    /// <summary>決まった負荷を全員に。</summary>
    public Work Apply(double cpu, double gpu, double memory, double disk) => Apply(new Load(cpu, gpu, memory, disk));
}

/// <summary>Director 一式を、模擬の時計と種を固定した乱数で動かす。</summary>
public sealed class Sim
{
    public readonly PartyState State;
    public readonly Director Director;
    public readonly Rig Rig = new();
    private readonly Random _loadRng;
    public long Clock { get; private set; } = 1_800_000_000;   // 2027 年ごろ
    public long Elapsed { get; private set; }
    public string Lang { get; set; } = "ja";

    public Sim(int seed, bool adventure = true, PartyState? state = null)
    {
        State = state ?? new PartyState();
        State.Migrate();
        Director = new Director(TestData.Story, State, adventure ? TestData.Book : null,
                                new Random(seed), () => Clock);
        _loadRng = new Random(seed * 7919 + 1);
    }

    public AdventureState Adv => State.Adventure!;

    /// <summary>1秒進める。出た文を返す（次の Tick までしか有効でない）。</summary>
    public IReadOnlyList<string> Tick(Profile p)
    {
        var w = Rig.Apply(Profiles.At(p, Elapsed, _loadRng));
        return TickWith(w);
    }

    public IReadOnlyList<string> TickWith(Work w)
    {
        Clock++;
        Elapsed++;
        return Director.Tick(1.0, w, Rig.Slots, Lang);
    }
}
