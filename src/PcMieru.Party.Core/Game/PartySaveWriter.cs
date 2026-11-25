namespace PcMieru.Party.Game;

/// <summary>
/// 保存の最後の結果。<see cref="Ok"/> が偽なら <see cref="Message"/> に例外の文。
/// <see cref="Blocked"/> は「書けなかった」ではなく「書かないと決めてある」（読めなかった保存を空の進みで潰さないため）。
/// 成功ではない: 閉じるときに「この回の進みは保存されない」と伝えるのに使う。
/// </summary>
public readonly record struct SaveResult(bool Ok, string? Message = null, bool Blocked = false)
{
    public static SaveResult Success { get; } = new(true);

    /// <summary>保存を止めてあるときの結果。</summary>
    public static SaveResult Refused { get; } = new(false, "saving is blocked: the save file could not be read, so it is left as it is", Blocked: true);
}

/// <summary>
/// 保存の書き手（DESIGN-C 3 の 6）。**窓の UI スレッドは本文（<see cref="PartyState.ToJson"/>、UI スレッドで取る）と書き先を渡すだけ**で、
/// 円盤への書き込み（Flush(true) まで含む）は背景の 1 本が受け持つ。
///
/// 最新が勝つ: 書いている間に何度渡されても、待っているのは最後の 1 つだけ（古い本文は捨てる）。
/// 書き方は <see cref="PartyState.Save"/> と同じ（別名に書いて円盤まで書き切り、置き換える）。
/// **「保存した」と言うのは書けたときだけ**: 失敗は <see cref="Last"/> に例外の文で残り、<see cref="LastSavedUtc"/> は進まない。
/// 読めなかった保存を空の進みで潰さないため、<c>saveBlocked</c> で作ったときは何も書かない（<see cref="Submit"/> も <see cref="Flush"/> も
/// <see cref="SaveResult.Refused"/> を返す。成功とは言わない）。
/// </summary>
public sealed class PartySaveWriter
{
    private readonly object _gate = new();
    private readonly bool _blocked;
    private readonly Action<string, string> _write;
    private (string Path, string Json)? _pending;
    private bool _running;
    private SaveResult _last = SaveResult.Success;
    private DateTime? _lastSavedUtc;

    /// <param name="saveBlocked"><see cref="PartyState.SaveBlocked"/>。真なら <see cref="Submit"/> は何も書かず、結果は <see cref="SaveResult.Refused"/>。</param>
    public PartySaveWriter(bool saveBlocked) : this(saveBlocked, PartyState.WriteAtomic)
    {
    }

    /// <summary>試験用: 書く処理を差し替える（書いている最中を作るため）。引数は (書き先, 本文)。</summary>
    internal PartySaveWriter(bool saveBlocked, Action<string, string> write)
    {
        _blocked = saveBlocked;
        _write = write;
    }

    public bool Blocked => _blocked;

    /// <summary>直近の書き込みの結果。まだ何も書いていなければ成功（失敗を知らないだけ）。</summary>
    public SaveResult Last
    {
        get { lock (_gate) return _blocked ? SaveResult.Refused : _last; }
    }

    /// <summary>最後に**書けた**時刻（UTC）。一度も書けていなければ null。失敗では進まない。</summary>
    public DateTime? LastSavedUtc
    {
        get { lock (_gate) return _lastSavedUtc; }
    }

    /// <summary>
    /// 本文と書き先を渡す。すぐ戻る。待っている古い本文があれば置き換える。
    /// 保存を止めてあるときは何も渡さず <see cref="SaveResult.Refused"/> を返す（それ以外は成功＝受け付けた。書けたかは <see cref="Flush"/>）。
    /// </summary>
    public SaveResult Submit(string json, string path)
    {
        if (_blocked) return SaveResult.Refused;
        lock (_gate)
        {
            _pending = (path, json);
            if (_running) return SaveResult.Success;
            _running = true;
        }

        Task.Run(Drain);
        return SaveResult.Success;
    }

    private void Drain()
    {
        while (true)
        {
            (string Path, string Json) job;
            lock (_gate)
            {
                if (_pending is not { } next)
                {
                    _running = false;
                    Monitor.PulseAll(_gate);
                    return;
                }

                job = next;
                _pending = null;
            }

            SaveResult result;
            try
            {
                _write(job.Path, job.Json);
                result = SaveResult.Success;
            }
            catch (Exception e)
            {
                result = new SaveResult(false, e.Message);
            }

            lock (_gate)
            {
                _last = result;
                if (result.Ok) _lastSavedUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// 待っている分と書いている最中の分が終わるのを待ち、最後の結果を返す。終了時の保存に使う。
    /// 間に合わなければ失敗（時間切れ）。何も渡していなければ成功。保存を止めてあるときは <see cref="SaveResult.Refused"/>（成功ではない）。
    /// </summary>
    public SaveResult Flush(TimeSpan timeout)
    {
        if (_blocked) return SaveResult.Refused;
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_running || _pending is not null)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return new SaveResult(false, "timed out while writing");
                Monitor.Wait(_gate, left);
            }

            return _last;
        }
    }

    /// <summary><see cref="Flush"/> を UI スレッドを止めずに待つ形。</summary>
    public Task<SaveResult> FlushAsync(TimeSpan timeout) => Task.Run(() => Flush(timeout));
}
