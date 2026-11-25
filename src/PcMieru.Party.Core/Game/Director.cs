using PcMieru.Party.Adventure;

namespace PcMieru.Party.Game;

/// <summary>どのチャンネルが測れていたか（<see cref="Work.Present"/>）。</summary>
[Flags]
public enum WorkChannels
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Memory = 4,
    Disk = 8,
    DiskFullest = 16,
    All = Cpu | Gpu | Memory | Disk | DiskFullest,
}

/// <summary>
/// 実測から作った、1秒ぶんの「働き」。
/// 測れていないチャンネルは値が 0 のままだが、<see cref="Present"/> に立たない（0 と読ませない）。既定は全部あり。
/// </summary>
public sealed record Work(
    double Cpu,          // 0..1
    double Gpu,          // 0..1（最も忙しいGPU）
    double Memory,       // 0..1
    double Disk,         // 0..1 に丸めた読み書き
    double DiskFullest,  // 0..1 一番埋まっているドライブ
    WorkChannels Present = WorkChannels.All)
{
    /// <summary>そのチャンネルが測れていたか。</summary>
    public bool Has(WorkChannels channel) => (Present & channel) == channel;
}

/// <summary>
/// 実機の稼働を、経験値と物語に変える係。
///
/// **設計の要点は「働いた分だけ進む」を利用者が体感できること。**
/// cpu-z で負荷をかけたのに何も動かない、では仕掛けが無いのと同じになる。
/// そのため負荷時と待機時で、進む速さに 5倍以上の差を付けてある。
///
/// 経験値は **その子自身の仕事でしか増えない。**
/// CPU を回しても GPU は上がらない。そこが崩れると、ただの時間経過になる。
/// 冒険（町・遠出・装備）も経験値には触らない。勝っても負けても、レベルは仕事でしか動かない。
/// </summary>
public sealed class Director
{
    private readonly Story _story;
    private readonly PartyState _state;
    private readonly AdventureEngine? _adventure;

    /// <summary>待機が続いた秒数。焚き火の場面を出す判定に使う。</summary>
    private double _idleSeconds;
    private bool _bootDone;
    private readonly Random _rnd;
    private readonly Func<long> _clock;

    /// <summary>前の小話から何秒経ったか。</summary>
    private double _sinceVignette;
    private double _nextVignetteAt = 120;

    /// <summary>
    /// 今回出た文章。**毎秒作り直さず、使い回す。**
    /// 常駐物が毎秒ゴミを出し続けると、それだけで GC が回る。
    /// </summary>
    private readonly List<string> _produced = new();

    // 条件判定に使う状態。顔ぶれとレベルと章が変わったときだけ作り直す
    private WorldState? _world;
    private int _worldSig;

    /// <param name="adventure">冒険の本文。null なら冒険は回さない（物語とレベルだけ）</param>
    /// <param name="random">乱数。試験では種を固定したものを渡す</param>
    /// <param name="clock">いまの時刻（UNIX 秒）。試験では模擬の時計を渡す</param>
    public Director(Story story, PartyState state, AdventureBook? adventure = null,
                    Random? random = null, Func<long>? clock = null)
    {
        _story = story;
        _state = state;
        _rnd = random ?? new Random();
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (adventure is not null)
            _adventure = new AdventureEngine(adventure, state, new Random(_rnd.Next()));
    }

    public PartyState State => _state;
    public Story Story => _story;
    public AdventureEngine? Adventure => _adventure;

    /// <summary>いまの章。</summary>
    public Chapter CurrentChapter => _story.Chapters[Math.Clamp(_state.Chapter, 0, _story.Chapters.Count - 1)];

    /// <summary>直近に出した文章（新しいものが先頭）。</summary>
    public IReadOnlyList<string> Recent => _state.Log;

    // ---------------------------------------------------------------------
    // 経験値
    // ---------------------------------------------------------------------

    /// <summary>1秒あたりの取得経験値。待機でも僅かに入る（点けているだけでも進む）。</summary>
    public static double ExpRate(double load) => 0.6 + Math.Clamp(load, 0, 1) * 11.0;

    /// <summary>1秒あたりの旅程。待機 0.15、全力で 1.4 前後。</summary>
    public static double JourneyRate(Work w)
    {
        // 測れていないチャンネルは式に入れない（0 として読まない）。全部測れているフレームでは旧版と同じ数（足す順も同じ）
        double sum = 0;
        if (w.Has(WorkChannels.Cpu)) sum += w.Cpu;
        if (w.Has(WorkChannels.Gpu)) sum += w.Gpu;
        if (w.Has(WorkChannels.Memory)) sum += w.Memory * 0.3;
        if (w.Has(WorkChannels.Disk)) sum += w.Disk * 0.5;
        return 0.15 + sum * 0.62;
    }

    // ---------------------------------------------------------------------
    // 進行
    // ---------------------------------------------------------------------

    /// <summary>
    /// dt 秒ぶん進める。呼ぶのは1秒に1回で十分。
    /// 返り値は「今回新しく出た文章」（無ければ空）。**次の Tick までしか有効でない**（使い回すため）。
    /// </summary>
    public IReadOnlyList<string> Tick(double dt, Work w, IReadOnlyList<PartySlot> slots, string lang)
    {
        var produced = _produced;
        produced.Clear();
        _state.UptimeSeconds += dt;

        // --- 経験値 ---
        // **その子自身の仕事でしか増えない。**
        // GPU が2枚なら、回っている方だけが育つ。ここが崩れると、ただの時間経過になる。
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            // 測れていない席（値が無い）は育てない。0 で渡すと「測れて 0%」と区別がつかない（DESIGN-C 2.3）。
            if (!slot.Measured) continue;
            if (GpuSlotKeys.IsDisplayOnly(slot.Key)) continue;   // 席の無い GPU は育てない（保存に鍵を作らない）
            int before = _state.Level(slot.Key);
            _state.AddExp(slot.Key, ExpRate(slot.Load) * dt);
            int after = _state.Level(slot.Key);
            if (after > before) NoteLevelUp(slot, after);
        }
        var world = WorldOf(slots);

        // --- 旅程 ---
        // 待機 0.24/秒、全力 1.4/秒 前後。序章の1場面が 14 なので、
        // 負荷をかければ十数秒で文章が動く。放っておいても数分で動く。
        double journey = JourneyRate(w) * dt;
        _state.Journey += journey;

        // --- 章と場面 ---
        int chapter = _story.ChapterAt(_state.Journey);
        if (chapter != _state.Chapter)
        {
            _state.Chapter = chapter;
            _state.Scene = 0;
            produced.Add("── " + CurrentChapter.Title.For(lang) + " ──");
            world = WorldOf(slots);
        }

        var ch = CurrentChapter;
        int wantScene = ch.ScenePer <= 0 ? 0 : (int)((_state.Journey - ch.Require) / ch.ScenePer);
        wantScene = Math.Min(wantScene, ch.Scenes.Count - 1);
        bool sceneShown = false;
        while (_state.Scene < wantScene)
        {
            _state.Scene++;
            if (ch.Scenes[_state.Scene].Pick(world) is { } line)
            {
                produced.Add(line.For(lang));
                sceneShown = true;
            }
        }
        // 章の頭の一文は、まだ何も出していなければ出す。
        // **この秒に場面を出したなら出さない。** Log に入るのはこの秒の最後なので、Log だけ見ていると
        // 「何も出していない」と読めてしまい、いま出した場面をもう一度足していた
        // （旅程が章の途中から始まる保存で「HDDくんの荷が一番重い。」が二行続いた）
        if (!sceneShown && _state.Log.Count == 0 && ch.Scenes.Count > 0
            && ch.Scenes[Math.Clamp(_state.Scene, 0, ch.Scenes.Count - 1)].Pick(world) is { } first)
            produced.Add(first.For(lang));

        // --- 傍白 ---
        CheckEvents(dt, w, lang, world, produced);

        // --- レベルが上がった ---
        // 同じ秒・待ちの間に上がった者は一行にまとめる。本筋か傍白の文が出た秒は、次の秒へ回す
        _sinceLevelLine += dt;
        if (_levelUps.Count > 0 && produced.Count == 0 && _sinceLevelLine >= LevelLineGap)
        {
            if (LevelUpLine(world, lang) is { } up) produced.Add(up);
            _levelUps.Clear();
            _sinceLevelLine = 0;
        }

        // --- 冒険 ---
        // 本筋の場面と場面の間を埋める。進むのは同じ旅程から（実機の働きからだけ）。
        // 本筋の文が出た秒には場面を進めない（一度に流れると読めない）。
        _adventure?.Tick(dt, journey, w, slots, world, produced, lang);

        // --- 小話 ---
        // 章の進行とは別に、ふとした瞬間に1つだけ流す。
        // **物語が進み切ったあとも、ここが尽きない。**
        _sinceVignette += dt;
        if (_sinceVignette >= _nextVignetteAt && produced.Count == 0)
        {
            _sinceVignette = 0;
            _nextVignetteAt = 90 + _rnd.NextDouble() * 210;   // 1分半〜5分に1つ
            if (PickVignette(world) is { } v) produced.Add(v.For(lang));
        }

        // **同じ文を二行続けて流さない。** 道筋（本筋・傍白・レベル・冒険・小話）が別々に選ぶので、
        // どれか一つが直前と同じ文を選ぶと、画面の二行が同じになる。最後の砦としてここで落とす
        for (int i = 0; i < produced.Count; i++)
        {
            string previous = i > 0 ? produced[i - 1] : _state.Log.Count > 0 ? _state.Log[0] : "";
            if (produced[i] == previous) produced.RemoveAt(i--);
        }
        foreach (var line in produced)
        {
            _state.Log.Insert(0, line);
            if (_state.Log.Count > 40) _state.Log.RemoveRange(40, _state.Log.Count - 40);
        }
        return produced;
    }

    /// <summary>
    /// 出せる小話から1つ選ぶ。**直近に出したものは避ける。**
    /// 同じ話が続けて出ると、量があっても「使い回している」と感じられる。
    /// </summary>
    private Line? PickVignette(WorldState world)
    {
        var pool = _story.Vignettes.Where(v => Conditions.Match(v.When, world)).ToList();
        if (pool.Count == 0) return null;

        var recent = _state.RecentVignettes;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var pick = pool[_rnd.Next(pool.Count)];
            if (!recent.Contains(pick.Ja)) return Remember(pick);
        }
        // 引き当てられなければ、出していない物 → 一番前に出した物の順に選ぶ。
        // **ここで乱数に任せると、直前に出した物を引き直すことがある**（出せる話が少ない機械で続けて同じ話になる）
        Line? oldest = null;
        int oldestAt = int.MaxValue;
        foreach (var v in pool)
        {
            int at = recent.IndexOf(v.Ja);
            if (at < 0) return Remember(v);
            if (at < oldestAt) { oldestAt = at; oldest = v; }
        }
        return Remember(oldest!);
    }

    private Line Remember(Line line)
    {
        // 既に覚えている話なら、一番新しい所へ移す（同じ話を二重に数えない）
        _state.RecentVignettes.Remove(line.Ja);
        _state.RecentVignettes.Add(line.Ja);
        // 覚えておく数は、出せる話の3割ほど。多すぎると選べる物が無くなる
        int keep = Math.Clamp(_story.Vignettes.Count / 3, 10, 120);
        if (_state.RecentVignettes.Count > keep)
            _state.RecentVignettes.RemoveRange(0, _state.RecentVignettes.Count - keep);
        return line;
    }

    /// <summary>
    /// いまの機械と進行を、条件判定に使える形にまとめる。
    /// **変わっていなければ前の物を使う。** 毎秒、辞書と集合を作り直していた。
    /// 変わったかどうかは、顔ぶれ・レベル・章・起動回数・起動時間（分）から見る。
    /// </summary>
    private WorldState WorldOf(IReadOnlyList<PartySlot> slots)
    {
        int sig;
        unchecked
        {
            sig = _state.Chapter * 397 ^ _state.Boots * 31 ^ (int)(_state.UptimeSeconds / 60) * 7919;
            for (int i = 0; i < slots.Count; i++)
                sig = (sig * 31 + slots[i].Key.GetHashCode()) * 31 + _state.Level(slots[i].Key);
        }
        if (_world is not null && sig == _worldSig) return _world;
        _worldSig = sig;

        var levels = new Dictionary<string, int>();
        var present = new HashSet<string>();
        int gpus = 0, cpus = 0;
        foreach (var s in slots)
        {
            present.Add(s.CharacterId);
            if (s.CharacterId == "gpu") gpus++;
            if (s.CharacterId == "cpu") cpus++;
            int lv = _state.Level(s.Key);
            // 同じ種類が複数あるときは、**最も育っている個体**を代表にする
            if (!levels.TryGetValue(s.CharacterId, out int cur) || lv > cur)
                levels[s.CharacterId] = lv;
        }
        return _world = new WorldState(
            levels, _state.Chapter, gpus, cpus,
            present, _state.Boots, _state.UptimeSeconds / 3600.0);
    }

    /// <summary>傍白。**毎秒の一覧づくりをしない**（状況が続く間、毎秒ここを通るため）。</summary>
    private void CheckEvents(double dt, Work w, string lang, WorldState world, List<string> output)
    {
        long now = _clock();

        if (!_bootDone)
        {
            _bootDone = true;
            long gap = _state.LastSeenUnix == 0 ? 0 : now - _state.LastSeenUnix;
            string kind = gap > 3 * 24 * 3600 ? "long-absence" : "boot";
            TryFire(kind, now, lang, world, output);
        }

        _idleSeconds = w.Has(WorkChannels.Cpu) && w.Cpu <= 0.05 ? _idleSeconds + dt : 0;   // CPU が測れていないのは「暇」ではない

        // いま満たしている状況の種類
        if (w.Cpu >= 0.90) TryFire("cpu-very-high", now, lang, world, output);
        if (w.Gpu >= 0.85) TryFire("gpu-very-high", now, lang, world, output);
        if (w.Memory >= 0.90) TryFire("memory-tight", now, lang, world, output);
        if (w.Disk >= 0.60) TryFire("disk-busy", now, lang, world, output);
        if (w.DiskFullest >= 0.90) TryFire("disk-full", now, lang, world, output);
        if (_idleSeconds >= 120) TryFire("idle-long", now, lang, world, output);
    }

    private void TryFire(string when, long now, string lang, WorldState world, List<string> output)
    {
        // 同じ種類の傍白は、同じ待ち時間を共有する。先に待ち時間を見て、無駄な選定をしない
        var candidates = _story.EventsFor(when);
        if (candidates.Count == 0) return;
        var ev = candidates[candidates.Count == 1 ? 0 : _rnd.Next(candidates.Count)];

        if (_state.LastEventAt.TryGetValue(ev.When, out long last) && now - last < ev.Cooldown)
            return;

        // 条件に当たるものだけから選ぶ。育った機械でだけ出る一行がある
        int usable = 0;
        foreach (var t in ev.Texts) if (Conditions.Match(t.When, world)) usable++;
        if (usable == 0) return;
        int k = _rnd.Next(usable);
        foreach (var t in ev.Texts)
        {
            if (!Conditions.Match(t.When, world) || k-- != 0) continue;
            _state.LastEventAt[ev.When] = now;
            output.Add(t.For(lang));
            return;
        }
    }

    // ---------------------------------------------------------------------
    // レベルが上がった一行
    // ---------------------------------------------------------------------

    /// <summary>
    /// レベルの一行どうしの最短の間隔（秒）。**レベルの知らせで物語を押し流さない。**
    /// 全力で回すと、一つの部品が数分で Lv2 から Lv10 まで上がる。CPU くんとクーラーくんは
    /// 同じ負荷を見ているので同じ秒に上がる。一つずつ流すと、画面の2行がレベルの知らせだけになる。
    /// 間に上がった分は、次の一行にまとめて書く（最後のレベルだけ）。
    /// </summary>
    public const double LevelLineGap = 300;

    /// <summary>まだ知らせていない上がり（鍵ごとに、上がった後のレベル）。部品の数より増えない。</summary>
    private readonly List<(PartySlot Slot, int Level)> _levelUps = new();
    private double _sinceLevelLine = LevelLineGap;   // 最初の一回はすぐ出す

    private void NoteLevelUp(PartySlot slot, int level)
    {
        for (int i = 0; i < _levelUps.Count; i++)
            if (_levelUps[i].Slot.Key == slot.Key) { _levelUps[i] = (slot, level); return; }
        _levelUps.Add((slot, level));
    }

    /// <summary>
    /// まとめた上がりを一行にする。名を出すのは一番高くなった者で、他の者は括弧の中に添える。
    /// <code>
    ///   CPUくん が一段、板についてきた。  (Lv.6)
    ///   CPUくん が一段、板についてきた。  (Lv.6・クーラーくん Lv.6)
    ///   CPUくん が一段、板についてきた。  (Lv.6・ほか2名)
    /// </code>
    /// </summary>
    private string? LevelUpLine(WorldState world, string lang)
    {
        var ev = _story.EventById("level-up");
        if (ev is null) return null;
        int lead = 0;
        for (int i = 1; i < _levelUps.Count; i++)
            if (_levelUps[i].Level > _levelUps[lead].Level) lead = i;
        var (slot, level) = _levelUps[lead];
        if (PickLevelUp(ev, world, slot.CharacterId, _rnd) is not { } text) return null;

        string line = text.For(lang).Replace("{name}", DisplayName(slot.CharacterId, lang)) + $"  (Lv.{level}";
        int others = _levelUps.Count - 1;
        if (others == 1)
        {
            var (o, ol) = _levelUps[lead == 0 ? 1 : 0];
            string name = DisplayName(o.CharacterId, lang);
            if (lang == "en" && name.StartsWith("The ", StringComparison.Ordinal)) name = "the " + name[4..];
            line += (lang == "en" ? ", " : "・") + name + $" Lv.{ol}";
        }
        else if (others > 1)
            line += lang == "en" ? $", and {others} others" : $"・ほか{others}名";
        return line + ")";
    }

    /// <summary>
    /// レベルの一文を選ぶ。**条件（when）に合うものだけ**から選ぶ（傍白と同じ決まり）。
    /// それに加えて、上がった本人を地の文で名指しする文は除く（「HDDくんが HDDくん のことを書き留めた」）。
    /// 合うものが無ければ null。
    /// </summary>
    public static Line? PickLevelUp(StoryEvent ev, WorldState world, string characterId, Random rng)
    {
        string self = DisplayName(characterId, "ja");
        int usable = 0;
        foreach (var t in ev.Texts) if (Usable(t)) usable++;
        if (usable == 0) return null;
        int k = rng.Next(usable);
        foreach (var t in ev.Texts)
            if (Usable(t) && k-- == 0) return t;
        return null;

        bool Usable(Line t) =>
            Conditions.Match(t.When, world)
            && !t.Ja.Replace("{name}", "", StringComparison.Ordinal).Contains(self, StringComparison.Ordinal);
    }

    /// <summary>物語の中で呼ぶ名。アトラス側の name とは別に持つ（文章の据わりが違うため）。</summary>
    public static string DisplayName(string id, string lang) => lang == "en"
        ? id switch
        {
            "cpu" => "The CPU", "cpu-cooler" => "The cooler", "memory" => "The memory",
            "gpu" => "The GPU", "m2" => "The M.2", "ssd" => "The SSD", "hdd" => "The HDD",
            "psu" => "The PSU", "motherboard" => "The motherboard", _ => id,
        }
        : id switch
        {
            "cpu" => "CPUくん", "cpu-cooler" => "クーラーくん", "memory" => "メモリくん",
            "gpu" => "GPUくん", "m2" => "M.2くん", "ssd" => "SSDくん", "hdd" => "HDDくん",
            "psu" => "電源くん", "motherboard" => "マザーボードくん", _ => id,
        };
}
