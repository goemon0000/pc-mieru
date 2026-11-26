using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PcMieru.Party.Adventure;
using PcMieru.Party.Hardware;

namespace PcMieru.Party.Game;

/// <summary>
/// 保存される進行。**利用者は一切操作できない。**
/// 機械がどれだけ働いたか、だけで動く。
/// </summary>
public sealed class PartyState
{
    /// <summary>
    /// 保存の形の版。**v0.6.0 までの保存には無い（読むと 0 になる）。**
    ///   0 … v0.6.0 まで（物語とレベルだけ）
    ///   1 … 冒険（町・遠出・装備）を足した形
    /// 読み込んだら必ず <see cref="Migrate"/> を通し、足りない物を既定値で埋める。
    /// 物語の進みとレベルには一切触らない。
    /// </summary>
    public const int CurrentVersion = 1;
    public int SaveVersion { get; set; }

    /// <summary>冒険の進み具合。古い保存には無いので、移すときに作る。あれば版の数に関わらず使う。</summary>
    public AdventureState? Adventure { get; set; } = new();

    /// <summary>キャラid → 経験値。**その子自身の仕事でしか増えない。**</summary>
    public Dictionary<string, double> Experience { get; set; } = new();

    /// <summary>
    /// GPU の席（gpu#0〜gpu#5）の覚え書き。どの席にどの名前・メモリ・PCI バスの GPU がいたか。
    /// **この欄の無い古い保存は空**（最初の起動で並び順どおりに番号が振られ、旧版の gpu#i と同じになる）。
    /// 差す・抜くで番号を詰めず、決められない GPU（同型が2枚でバスが読めない等）には席を与えない
    /// （<see cref="GpuSlotMap"/>、DESIGN-C 2.3）。**自動では消さない**（取り外した GPU の席は穴として残る）。
    /// 手放すのは <see cref="GpuSlotMap.Forget"/>（記録を「引退」にして番号を塞ぐ。経験値と装備は残る）で、
    /// 番号を空けるのは <see cref="GpuSlotKeys.ResetSlot"/>（その鍵の経験値と装備を消して記録も消す）だけ。
    /// バスと容量が分からなかった記録は null / 0 のまま書く（0 や既定値を作らない）。
    /// **欄が有るのに壊れている**（null・配列でない・席の番号の重複や負・Slot の無い記録・同一カードの記録が2つ）ときは、
    /// 「欄が無い」とは別に扱う（<see cref="GpuSlotsRepaired"/>）。空として読むと、残った gpu#i の育成が別のカードに付く。
    /// 同一カードの記録が2つのときは消さずに「係争」（<see cref="GpuSlotRecord.Disputed"/>）で残す: 一致するカードは決められないので席も経験値も付けない。
    /// 欄が有って壊れていなくても（空の一覧も）、経験値か装備が残っているのに記録の無い席は「引退」の記録で塞ぐ（<see cref="GpuSlotKeys.RepairLoaded"/>）。
    /// </summary>
    public List<GpuSlotRecord> GpuSlots { get; set; } = new();

    /// <summary>
    /// 席の欄（<see cref="GpuSlots"/>）が**壊れていて、読み込みのときに直した**ことの印。画面（利用者への知らせ）がこれを見る。
    /// 直し方は <see cref="GpuSlotKeys.RepairLoaded"/>: 読める記録は残し、経験値か装備が残っているのに記録の無い席は
    /// 「引退」の記録で番号を塞ぐ（どのカードにも渡さない。育てた分は消さない）。
    /// **保存する。** 直した結果は次の保存で正しい形に書き換わるので、実行中だけの印だと再起動で何も言わずに消え、
    /// 利用者は壊れたことに気づけない。派生（引退の記録があるか）にしないのは、引退は <see cref="GpuSlotMap.Forget"/> の
    /// 意図した結果でもあり、「壊れていた」と区別がつかなくなるため。
    /// 画面が知らせたら、呼ぶ側が false に戻す（この欄は自動では戻らない）。欄が**無い**古い保存は壊れていない（false のまま）。
    /// **false のときは保存に書かない**（ふつうの保存の形を変えない。<see cref="GpuSlotRecord.Retired"/> と同じ）。無ければ false に読む。
    /// 属性の [JsonIgnore] ではなく読み書きの設定（<see cref="Opts"/>）で省く: 属性があると LenientJson・取り込みの欄の検査が
    /// 「保存しない欄」とみなして読み飛ばし、欄ごとに読み直した保存でこの印が消える。
    /// </summary>
    public bool GpuSlotsRepaired { get; set; }

    /// <summary>旅程。物語が進む距離。全体の仕事量から貯まる。</summary>
    public double Journey { get; set; }

    /// <summary>いまの章（0=序章）。</summary>
    public int Chapter { get; set; }

    /// <summary>その章で何場面まで進んだか。</summary>
    public int Scene { get; set; }

    /// <summary>直近に出した文章（画面に残す用）。**新しいものが先頭。**</summary>
    public List<string> Log { get; set; } = new();

    /// <summary>傍白の連発を防ぐため、種類ごとに最後に出した時刻を覚える。</summary>
    public Dictionary<string, long> LastEventAt { get; set; } = new();

    /// <summary>直近に出した小話。同じ話が続けて出ると、量があっても使い回しに見える。</summary>
    public List<string> RecentVignettes { get; set; } = new();

    /// <summary>累計の起動時間（秒）。「長い夜」等の判定に使う。</summary>
    public double UptimeSeconds { get; set; }

    /// <summary>前回終了した時刻。数日ぶりの起動を検知する。</summary>
    public long LastSeenUnix { get; set; }

    public int Boots { get; set; }

    /// <summary>表示言語 "ja" / "en"。空なら OS の設定に従う。</summary>
    public string Lang { get; set; } = "";

    /// <summary>窓の大きさ。整数倍でない値も許す（利用者が自由に引っ張れるため）。</summary>
    public double Zoom { get; set; } = 2.0;

    /// <summary>
    /// 窓の位置。**未設定は NaN。**
    /// 「負なら未設定」にしていたため、主モニタより左にある副モニタへ置いた窓が
    /// 起動のたび主モニタへ飛んでいた（左側のモニタは座標が負になる）。
    /// </summary>
    /// (NaN にはしないこと。System.Text.Json は NaN を書けず例外になり、
    ///  Save は例外を握り潰す作りなので**保存が丸ごと止まったまま気づけない**。)
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public bool Topmost { get; set; } = true;
    public bool Expanded { get; set; }

    /// <summary>名札の出し方 "level" / "name" / "none"。既定は "name"（「CPU Lv1」: どの子かが読める。入らない幅では Lv だけに落ちる）。</summary>
    public string Labels { get; set; } = "name";

    /// <summary>
    /// 更新の設定 "auto"（自動で更新する）/ "notify"（確認だけして知らせる）/ "off"（確認しない）。
    /// **この欄の無い古い保存は "auto"。** 読み方は PartsLife.Update の UpdateModes（知らない値は "notify" に読む）。
    /// 更新の覚え書き（最後に確認した時刻など）はここに書かない（%LOCALAPPDATA%\PartsLife\updates）。
    /// </summary>
    public string UpdateMode { get; set; } = "auto";

    /// <summary>
    /// この版が知らない欄（新しい版が書いた物）。**読んだまま書き戻す。**
    /// 新しい版の保存を古い版で開いても、分かる所だけ使い、分からない所は消さずに残す。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    // ---------------------------------------------------------------------

    /// <summary>
    /// レベル。**対数で伸ばす。**
    /// 線形にすると、128コアの機械が数時間でカンストして意味が無くなる。
    /// 序盤は数分で上がり、後半は日単位でしか動かない。
    /// </summary>
    public static int LevelOf(double exp) =>
        exp <= 0 ? 1 : (int)Math.Floor(Math.Log(1 + exp / 90.0) / Math.Log(1.55)) + 1;

    public static double ExpForLevel(int level) =>
        level <= 1 ? 0 : 90.0 * (Math.Pow(1.55, level - 1) - 1);

    public int Level(string characterId) => LevelOf(Experience.GetValueOrDefault(characterId));

    /// <summary>次のレベルまでの進み具合 0..1。</summary>
    public double LevelProgress(string characterId)
    {
        double exp = Experience.GetValueOrDefault(characterId);
        int lv = LevelOf(exp);
        double lo = ExpForLevel(lv), hi = ExpForLevel(lv + 1);
        return hi <= lo ? 0 : Math.Clamp((exp - lo) / (hi - lo), 0, 1);
    }

    /// <summary>
    /// 経験値の鍵を、1台前提の名前から台数付きの名前へ移す。
    /// **これをやらないと、更新した人の CPU くんと GPU くんだけレベル1に戻る。**
    /// 育てた分が消えるのは、遊びの部分とはいえ一番やってはいけない。
    /// </summary>
    public void MigrateKeys()
    {
        foreach (var id in new[] { "cpu", "cpu-cooler", "gpu" })
        {
            if (!Experience.TryGetValue(id, out double exp)) continue;
            string moved = id + "#0";
            if (!Experience.ContainsKey(moved)) Experience[moved] = exp;
            Experience.Remove(id);
        }
    }

    /// <summary>
    /// 古い形の保存を今の形に揃える。**何度呼んでも同じ結果になる。**
    /// 新しい版で書かれた保存（SaveVersion が先の版）は、分かる所だけ使い、版の数は下げない。
    /// 分からない欄は <see cref="Unknown"/> に残して書き戻し、知らない列挙の値は安全な値に読み、
    /// 形の変わった欄は既定値にする（<see cref="FromJson(string, out bool)"/>）。どれも例外にはしない。
    /// </summary>
    public void Migrate()
    {
        Experience ??= new Dictionary<string, double>();
        GpuSlots ??= new List<GpuSlotRecord>();
        Log ??= new List<string>();
        LastEventAt ??= new Dictionary<string, long>();
        RecentVignettes ??= new List<string>();
        Lang ??= "";
        Labels ??= "name";
        UpdateMode ??= "auto";

        if (SaveVersion < 1) MigrateKeys();
        // v0.6.0 まで → 冒険を足す。旅は最初の町から、何も持たずに始まる
        // （持ち物は、最初に盤の上に並んだときに「ありあわせ」が配られる）
        // **冒険が既にあれば、版の数に関わらず残す。** 版の数だけが無い保存（v0.6.0 は SaveVersion を
        // 知らない。手で直した・別の道具で書き戻した等）を数だけ見て作り直すと、育てた旅が消える。
        // 中身の怪しい値は、冒険の係を作るときに Sanitize が直す
        Adventure ??= new AdventureState();
        if (SaveVersion < CurrentVersion) SaveVersion = CurrentVersion;
    }

    public void AddExp(string characterId, double amount)
    {
        if (amount <= 0) return;
        Experience[characterId] = Experience.GetValueOrDefault(characterId) + amount;
    }

    // ---------------------------------------------------------------------
    // 初期化
    // ---------------------------------------------------------------------

    /// <summary>
    /// 物語だけを最初から。**レベルは残す。**
    /// 育てた分を巻き添えにしないのが要点で、そこを分けないと
    /// 「もう一度読みたい」だけの人が押せなくなる。
    /// </summary>
    public void ResetStory()
    {
        Journey = 0;
        Chapter = 0;
        Scene = 0;
        Log.Clear();
        LastEventAt.Clear();
        RecentVignettes.Clear();
        // 旅も最初の町から。**持ち物と金は残す**（育てた側のもので、物語ではないので）
        Adventure?.ResetJourney();
    }

    /// <summary>レベルだけを最初から。物語は残す。</summary>
    public void ResetLevels() => Experience.Clear();

    /// <summary>両方とも最初から。窓の位置と言語の設定は残す（それは進行ではない）。</summary>
    public void ResetAll()
    {
        ResetStory();
        ResetLevels();
        UptimeSeconds = 0;
        Boots = 0;
        Adventure = new AdventureState();
    }

    // ---------------------------------------------------------------------
    // 保存
    // ---------------------------------------------------------------------
    private static string AppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// 新しい保存先 %APPDATA%\PCMieru\party（DESIGN-C 4.1）。**旧製品の %APPDATA%\PartsLife と %APPDATA%\PartsParty には触らない。**
    /// 旧製品の保存を引き継ぐのは取り込み（step 5、Import/）だけで、引数なしの <see cref="Load()"/> は新しい保存先しか読まない。
    /// </summary>
    internal static string Dir => Path.Combine(AppData, "PCMieru", "party");
    private static string FilePath => Path.Combine(Dir, "state.json");

    /// <summary>この進みを書き戻す先（<see cref="Load(string, string?)"/> が決めた場所）。保存の係（<see cref="PartySaveWriter"/>）へ渡す。</summary>
    [JsonIgnore] public string SavePath => _path ?? FilePath;

    /// <summary>保存の読み書きの設定。取り込み（Import/StateImport）が同じ読み方で検査するため internal。</summary>
    internal static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitUnusedGpuSlotFields } },
    };

    /// <summary>
    /// 席の欄の 2 つを、使っていないときは保存に書かない（ふつうの保存の形を変えない）。読むときは、無ければ既定（空の一覧・false）。
    ///   <see cref="GpuSlotsRepaired"/> … false のとき書かない。
    ///   <see cref="GpuSlots"/> … **空のとき書かない。** 空を書くと、一度も番号を振っていない古い保存が「欄が有って空」に変わり、
    ///   次に読むとき gpu#i の経験値が全部「記録の無い席」とみなされて塞がれる（<see cref="GpuSlotKeys.RepairLoaded"/>）。
    ///   空の一覧は「欄が無い」と同じ意味で足りる: 番号を振った後の一覧は空にならない（カードか、経験値のある席を塞ぐ記録が残る）。
    /// </summary>
    internal static void OmitUnusedGpuSlotFields(JsonTypeInfo info)
    {
        if (info.Type != typeof(PartyState)) return;
        foreach (var p in info.Properties)
        {
            if (p.Name == nameof(GpuSlotsRepaired)) p.ShouldSerialize = static (_, value) => value is true;
            else if (p.Name == nameof(GpuSlots)) p.ShouldSerialize = static (_, value) => value is List<GpuSlotRecord> { Count: > 0 };
        }
    }

    /// <summary>壊れて読めなかった保存の控えを、いくつまで残すか。</summary>
    public const int BadCopiesKept = 3;

    /// <summary>この進みを書き戻す先。<see cref="Load(string, string?)"/> が決める。</summary>
    private string? _path;

    /// <summary>
    /// 保存を書かない。元の保存を読めず、しかも脇へ退けることもできなかったとき（他のプログラムが掴んでいる等）。
    /// **作り直した空の進みで、読めなかった保存を上書きしない**ため。
    /// </summary>
    [JsonIgnore] public bool SaveBlocked { get; private set; }

    public static PartyState Load() => Load(Dir);

    /// <summary>
    /// dir の state.json を読む（無ければ legacyFile を）。試験では一時フォルダを渡す。
    ///
    /// **読めない保存を黙って上書きしない。** 以前は（v0.6.0 も）読めなければ空の進みを返し、
    /// 60秒後の保存で state.json を上書きしていた。冒険の一欄が読めないだけで、レベルも物語も消えた。
    ///   - 一部の欄だけ読めない → その欄だけ既定値にして、残りは使う（直す前の本文は控えに写す）
    ///   - 丸ごと読めない → state.json.bad-日時 へ退けてから、最初から始める（控えは新しい物を3つ）
    ///   - 退けられない・読み出せない → 最初から始めるが、この回は保存しない
    /// </summary>
    public static PartyState Load(string dir, string? legacyFile = null) => Load(dir, legacyFile, KeepBadCopy);

    /// <summary>試験用: 控えを作る処理（path, move → 作れたか）を差し替える（作れなかった場合を作るため）。</summary>
    internal static PartyState Load(string dir, string? legacyFile, Func<string, bool, bool> keepBadCopy) =>
        LoadCore(dir, legacyFile, keepBadCopy, out _);

    /// <summary>
    /// 初回起動の流れのあとの読み込み。<see cref="Load(string, string?)"/> と同じに読むが、保存が「無い」（FileNotFound / DirectoryNotFound）ことを
    /// <paramref name="missing"/> で伝える。流れが「ある」と言ったあとで消えていたとき、空の進みを書ける状態で続けず、初回起動の流れへ戻るため。
    /// 無いときに返す状態は使わない（何も書いていない）。
    /// </summary>
    internal static PartyState LoadExisting(string dir, out bool missing) => LoadCore(dir, null, KeepBadCopy, out missing);

    /// <summary>
    /// 読むだけで開く（新しい保存が読めないと分かっていて、人がそれでも続けると選んだとき）。**ファイルには何もしない**:
    /// 脇へ退けない・控えを作らない・作り直さない。読めれば読み、読めなければ空の進みを返す。どちらでも保存は止まっている（<see cref="SaveBlocked"/>）ので、
    /// この回は何も書かれない。
    /// </summary>
    internal static PartyState LoadReadOnly(string dir)
    {
        string path = Path.Combine(dir, "state.json");
        PartyState state;
        if (TryReadText(path, out string text) == ReadOutcome.Read)
        {
            try
            {
                state = FromJson(text, out _);
            }
            catch (Exception e) when (LenientJson.IsReadError(e))
            {
                state = Fresh();
            }
        }
        else
        {
            state = Fresh();
        }

        state._path = path;
        state.SaveBlocked = true;
        return state;
    }

    private static PartyState LoadCore(string dir, string? legacyFile, Func<string, bool, bool> keepBadCopy, out bool missing)
    {
        string path = Path.Combine(dir, "state.json");
        // **「無い」と決めるのは、開こうとして FileNotFound / DirectoryNotFound が返ったときだけ。**
        // File.Exists は、置き場（リダイレクト先など）が一時的に開けないときも偽を返す。そのまま最初から始めて保存すると、
        // 開けるようになった後の 60 秒で本物の保存を空の進みが潰す。
        string from = path;
        var read = TryReadText(path, out string text);
        if (read == ReadOutcome.Missing && legacyFile is not null)
        {
            from = legacyFile;   // 「パーツパーティ」時代の進行
            read = TryReadText(legacyFile, out text);
        }

        missing = read == ReadOutcome.Missing;
        PartyState state;
        if (read == ReadOutcome.Missing)
        {
            state = Fresh();
        }
        else if (read == ReadOutcome.Failed)
        {
            state = Fresh();
            state.SaveBlocked = true;   // 在るのに読めない（権限・共有違反・I/O エラー）。空の進みで上書きしない
        }
        else
        {
            try
            {
                state = FromJson(text, out bool repaired);
                // 直して読んだときは、直す前の本文を控えに写しておく（読めなかった欄を後から拾えるように）。
                // 控えが作れなかったなら、次の保存が元の本文を消してしまうので保存を止める
                if (repaired && from == path && !keepBadCopy(path, false)) state.SaveBlocked = true;
            }
            catch (Exception e) when (LenientJson.IsReadError(e))
            {
                state = Fresh();
                // 改名前の保存先には書かないので、そちらは退けなくても上書きされない
                if (from == path && !keepBadCopy(path, true)) state.SaveBlocked = true;
            }
        }
        state._path = path;
        return state;
    }

    /// <summary>何も読まない最初の進み（保存先は持たない）。--smoke がこれを使う。</summary>
    public static PartyState Fresh()
    {
        var fresh = new PartyState();
        fresh.Migrate();
        return fresh;
    }

    private enum ReadOutcome { Read, Missing, Failed }

    /// <summary>
    /// 読み出す。ウイルス対策ソフト等が一瞬掴んでいることがあるので、少しだけ待ってやり直す。
    /// Missing は FileNotFound / DirectoryNotFound だけ（在るかを先に尋ねず、開いて確かめる）。それ以外の失敗は Failed。
    /// </summary>
    private static ReadOutcome TryReadText(string path, out string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                text = File.ReadAllText(path);
                return ReadOutcome.Read;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                text = "";
                return ReadOutcome.Missing;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 2) { text = ""; return ReadOutcome.Failed; }
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// 読めなかった保存を state.json.bad-日時 にする（move が偽なら写す）。新しい物から3つ残す。
    /// 退けられなかったら偽。
    /// </summary>
    private static bool KeepBadCopy(string path, bool move)
    {
        try
        {
            string dir = Path.GetDirectoryName(path)!, name = Path.GetFileName(path);
            // 世界時で付ける。夏時間で時計が戻ると、新しい控えの名前が古い物より前に並んで消されるため
            string stamp = name + ".bad-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z";
            // 同じ秒に二度目があれば -001 から。既にある番号より必ず大きくする（名前の順＝古い順を保つ）
            int next = 0;
            foreach (var f in Directory.GetFiles(dir, stamp + "*"))
            {
                string rest = Path.GetFileName(f)[stamp.Length..];
                if (rest.Length == 0) next = Math.Max(next, 1);
                else if (rest[0] == '-' && int.TryParse(rest.AsSpan(1), out int k)) next = Math.Max(next, k + 1);
            }
            string target = Path.Combine(dir, next == 0 ? stamp : $"{stamp}-{next:000}");
            if (move) File.Move(path, target);
            else File.Copy(path, target);

            var stale = Directory.GetFiles(dir, name + ".bad-*")
                                 .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                                 .Skip(BadCopiesKept);
            foreach (var old in stale)
            {
                try { File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>保存の本文から読む。**必ず Migrate を通す。** 丸ごと読めなければ例外。</summary>
    public static PartyState FromJson(string json) => FromJson(json, out _);

    /// <summary>
    /// 保存の本文から読む。まず普通に丸ごと読み、だめなら欄ごとに読む（<see cref="LenientJson"/>）。
    /// **冒険の一欄が読めないことで、物語の進みとレベルを捨てない。**
    /// 欄ごとに読み直したとき、または GPU の席の欄（<see cref="GpuSlots"/>）が壊れていて直したときは repaired が真。
    /// JSON の物（{...}）として読めなければ例外。
    /// </summary>
    public static PartyState FromJson(string json, out bool repaired) => FromJson(json, out repaired, out _);

    /// <summary>
    /// 上と同じ。席の欄が壊れていて直したか（<paramref name="gpuSlotsBroken"/>）も別に返す（取り込みが「直した欄」として数えるため）。
    /// 欄が**無い**（古い保存）のは壊れていない。有るのに null・形違い・重複や負の番号・同一カードの二重記録なら壊れている
    /// （<see cref="GpuSlotKeys.RepairLoaded"/>）。
    /// </summary>
    internal static PartyState FromJson(string json, out bool repaired, out bool gpuSlotsBroken)
    {
        repaired = false;
        PartyState? state;
        try
        {
            state = JsonSerializer.Deserialize<PartyState>(json, Opts);
        }
        catch (Exception e) when (LenientJson.IsReadError(e))
        {
            // ここで例外なら JSON として壊れている。呼ぶ側が控えへ退ける
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("state.json の中身が物（{...}）ではない");
            state = (PartyState)LenientJson.Read(doc.RootElement, typeof(PartyState), Opts);
            repaired = true;
        }
        if (state is null) throw new JsonException("state.json の中身が null");
        // 席の欄が「無い」のか「有るが壊れている」のかは、読み込んだ後では分からない（どちらも空の一覧になる）ので、本文で見る。
        // Migrate が null を空の一覧に替える前に取る
        var rawSlots = GpuSlotKeys.Probe(json);
        state.Migrate();
        gpuSlotsBroken = GpuSlotKeys.RepairLoaded(state, rawSlots);
        if (gpuSlotsBroken) repaired = true;   // 直す前の本文を控えに写す（Load が repaired を見る）
        return state;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    public void Save()
    {
        if (SaveBlocked) return;   // 読めなかった保存を、空の進みで上書きしない
        try
        {
            WriteAtomic(SavePath, ToJson());
        }
        catch { /* 保存できなくても動作は続ける */ }
    }

    /// <summary>
    /// 保存の本文を path へ書く。書き途中で電源が落ちても壊れないよう、別名で書いてから置き換える。
    /// <see cref="Save"/> と <see cref="PartySaveWriter"/> が同じ書き方を使うための 1 か所。失敗は例外のまま返す（呼ぶ側が決める）。
    /// </summary>
    internal static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            w.Write(text);
            w.Flush();
            // 置き換える前に中身を円盤まで書き切る。これが無いと、置き換えの直後に電源が落ちたとき
            // state.json が中身の無いファイルになりうる（点検: 読めない物は退避されて最初からになる）
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
