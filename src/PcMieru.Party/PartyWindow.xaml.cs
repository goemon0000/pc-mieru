using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PcMieru.Party.Game;
using PcMieru.Party.Hardware;
using PcMieru.Party.I18n;
using PcMieru.Party.Rendering;
using PcMieru.Party.Ui;

namespace PcMieru.Party;

/// <summary>
/// パーツぐらしの窓（旧 Parts Life の MainWindow を移した物。DESIGN-C 8 の 4a）。数字は <see cref="IBoardFeed"/> から読み、
/// <see cref="PartyComposition"/> で席を組み、<see cref="Director"/> を進めて描く。実測も保存の I/O も窓は持たない。
/// 保存は <see cref="PartySaveWriter"/> に本文を渡すだけ（UI スレッドは <c>state.ToJson()</c> を取るところまで）。writer が null なら一切保存しない（--smoke）。
/// </summary>
internal sealed partial class PartyWindow : Window
{
    private readonly Atlas _atlas = Atlas.Load();
    private readonly BoardRenderer _board;
    private readonly IBoardFeed _feed;
    private readonly PartySaveWriter? _writer;

    private readonly PartyState _state;
    private readonly Director _director;

    private readonly DispatcherTimer _noticeTimer = new();

    /// <summary>
    /// 絵の拡大率。**整数でなくてもよい。**
    /// 掴んで引けば自由に変わり、⤢ で整数（＝ドットが崩れない値）に揃う。
    /// </summary>
    private double _scale = 2.0;

    /// <summary>数値欄がこれより狭くなると、GPU の型番が入らない。</summary>
    private const double MinPanelWidth = 520;

    /// <summary>数字を読みに行く間隔（秒）。フィードは 1 秒に 1 枚。Director へ渡す dt の上限はこの 2 倍。</summary>
    private const double SensorPeriodSeconds = 1.0;

    private bool _expanded;
    private int _boardPixelWidth;

    /// <summary>名札の出し方。パーツが増えると字が邪魔になる人もいるので選べるようにする。</summary>
    private enum Labels { LevelOnly, NameAndLevel, None }
    private Labels _labels = Labels.LevelOnly;
    private BoardFrame? _frame;
    private Work _work = new(0, 0, 0, 0, 0);

    /// <summary>絵のコマ送り。全キャラで同じ時計を使う（別々にすると揃わない）。</summary>
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromSeconds(SensorPeriodSeconds) };

    /// <summary>前回 Director を進めた標本の印（単調時計）。同じ標本では二度進めない。順序も dt もこれだけで決める。</summary>
    private long? _lastStamp;

    /// <summary>標本が古い（max(3 × 周期, 6 秒)）。値を薄くして見せている。</summary>
    private bool _stale;

    /// <summary>
    /// 60 秒ごとの保存。**標本を受け取る処理とは別の時計**で回す（フィードが止まったり同じ標本を繰り返したりして標本の処理が早く戻っても、
    /// 溜まった進みと窓の位置は保存される）。
    /// </summary>
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    /// <summary>Windows の終了・サインアウトが始まった。閉じるときの箱を出さない。</summary>
    private bool _sessionEnding;

    /// <summary>終了・サインアウトのときに最後の保存を待つ上限（箱は出せないので短く）。</summary>
    private static readonly TimeSpan SessionEndFlush = TimeSpan.FromSeconds(2);

    /// <summary>コマ送りの時計。**モーションごとの速さは絵の側が決めている。**</summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>前回描いた絵の指紋。同じなら描き直さない。</summary>
    private int _lastSignature;

    private int _failures;

    private readonly List<TextBlock> _levelLabels = new();

    /// <summary>重ねたときに出す小さな札。WPF の ToolTip は遅延と再表示の癖が強いので自前で持つ。</summary>
    private readonly System.Windows.Controls.Primitives.Popup _hoverTip = new()
    {
        AllowsTransparency = true,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
    };
    private readonly TextBlock _hoverTipText = new()
    {
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xF8)),
        LineHeight = 15,
    };

    /// <summary>
    /// いま盤に立っている顔ぶれ。**フィードの 1 枚から毎秒組み立て直す。**
    /// 固定の並びにしていたため、GPU が2枚の機械でも GPU くんが1体しか立たなかった。
    /// </summary>
    private List<PartySlot> _slots = new();

    /// <param name="language">この回の言語。--lang があればそれ、無ければ保存の言語、無ければ OS（Program が決める）。保存へは書き戻さない（切り替えボタンを押したときだけ書く）。</param>
    /// <param name="state">進み。smoke は <see cref="PartyState.Fresh"/>。</param>
    /// <param name="feed">数字の出どころ。</param>
    /// <param name="writer">保存の書き手。null なら一切保存しない。</param>
    public PartyWindow(PartyLanguage language, PartyState state, IBoardFeed feed, PartySaveWriter? writer = null)
    {
        _state = state;
        _feed = feed;
        _writer = writer;
        Strings.Lang = language == PartyLanguage.Ja ? "ja" : "en";
        InitializeComponent();
        Title = PartyStrings.Title(language);
        _board = new BoardRenderer(_atlas);
        _hoverTip.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x13, 0x19)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x5D, 0xD8, 0xBB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 5),
            Child = _hoverTipText,
        };
        _director = new Director(GameAssets.LoadStory(), _state, GameAssets.LoadAdventure());
        _state.MigrateKeys();
        _labels = _state.Labels switch
        {
            "name" => Labels.NameAndLevel,
            "none" => Labels.None,
            _ => Labels.LevelOnly,
        };

        _scale = _state.Zoom > 0 ? Math.Clamp(_state.Zoom, 0.8, 6.0) : 2.0;
        Topmost = _state.Topmost;

        Loaded += OnLoaded;
        // 描かれたこと（開いただけでなく）。smoke の判定が使う
        ContentRendered += (_, _) => Rendered = true;
        Closing += (_, _) =>
        {
            // 最後の保存の間に次の読み取りが割り込まないよう、先に止める
            _frameTimer.Stop();
            _sensorTimer.Stop();
            _autosaveTimer.Stop();
            SaveOnClose();
        };
        Closed += (_, _) =>
        {
            if (Application.Current is { } app) app.SessionEnding -= OnSessionEnding;
            _noticeTimer.Stop();
            _frameTimer.Stop();
            _sensorTimer.Stop();
            _autosaveTimer.Stop();
            _hoverTip.IsOpen = false;
        };
        // 窓を動かしたり後ろに回ったりしたら、吹き出しは畳む。
        // Popup は常に最前面に出るので、置き去りにすると独りで浮いて残る
        LocationChanged += (_, _) => _hoverTip.IsOpen = false;
        Deactivated += (_, _) => _hoverTip.IsOpen = false;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        ExpandButton.Click += (_, _) => { SetExpanded(!_expanded); SaveState(); };
        SizeButton.Click += (_, _) => { SnapScale(); SaveState(); };
        LangButton.Click += (_, _) => ToggleLanguage();
        TopmostButton.Click += (_, _) => ToggleTopmost();
        CloseButton.Click += (_, _) => Close();
        NoticeClose.Click += (_, _) => HideNotice();
        _noticeTimer.Tick += (_, _) => HideNotice();

        Grip.DragDelta += OnGripDrag;
        Grip.DragCompleted += (_, _) => SaveState();
        BoardHost.MouseMove += OnBoardHover;
        BoardHost.MouseLeave += (_, _) => _hoverTip.IsOpen = false;
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
        PreviewKeyDown += OnTabKeys;

        // **飾りのために巻き添えで落ちない。**
        // 想定外の例外で常駐物が「動作を停止しました」を出すのは、それ自体が害になる。
        // ただし黙って握りつぶすだけにはしない: 最初の 1 件は FirstFailure に残し、smoke の判定が見る。
        _frameTimer.Tick += (_, _) => Guarded(() => Redraw());
        _sensorTimer.Tick += (_, _) => Guarded(OnSensorTick);
        _autosaveTimer.Tick += (_, _) => Guarded(SaveState);
    }

    /// <summary>窓が実際に一度描かれた（作っただけでなく）。smoke が「ok」と言う条件。</summary>
    public bool Rendered { get; private set; }

    /// <summary>タイマーの中で握りつぶした最初の例外（無ければ null）。smoke の判定はこれが有れば失敗にする。</summary>
    public string? FirstFailure { get; private set; }

    private void Guarded(Action work)
    {
        try { work(); }
        catch (Exception ex)
        {
            _failures++;
            FirstFailure ??= ex.ToString();
            if (_failures <= 3) System.Diagnostics.Debug.WriteLine(ex);
            // 続けて失敗するなら、無言で回り続けるより静かに止まる方がまし
            if (_failures > 60) { _frameTimer.Stop(); _sensorTimer.Stop(); }
        }
    }

    /// <summary>
    /// いまの窓の見た目そのままを 1 枚の絵にする（--smoke --shot）。**作り物のモックではなく、本物の描画をそのまま使う。**
    /// 開いている窓から取る（描かれた後に呼ぶこと）。書き出しの間だけ掴みを隠す。
    /// </summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderShot(bool expanded, double scale)
    {
        _scale = scale;
        SetExpanded(expanded);
        Grip.Visibility = Visibility.Collapsed;   // 宣材に掴みは要らない
        try
        {
            Redraw(force: true);
            UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(Shell.ActualWidth), (int)Math.Ceiling(Shell.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(Shell);
            return rtb;
        }
        finally
        {
            Grip.Visibility = Visibility.Visible;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyLanguage();
        TopmostButton.Opacity = Topmost ? 1.0 : 0.45;
        OnSensorTick();
        Redraw(force: true);
        SetExpanded(_state.Expanded);
        PlaceWindow();
        _frameTimer.Start();
        _sensorTimer.Start();
        _autosaveTimer.Start();
        // Windows の終了・サインアウトでは、Closing が来ないことがある。ここで最後の保存を試みる
        if (Application.Current is { } app) app.SessionEnding += OnSessionEnding;
    }

    /// <summary>
    /// Windows の終了・サインアウト。**UI スレッドで本文を取り**（SaveState）、短い上限（2 秒）で書き切るのを待つ。箱は出さない。
    /// 取り消さない（終了を止めるのは我々の仕事ではない）。
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _sessionEnding = true;
        if (_writer is null) return;
        try
        {
            _frameTimer.Stop();
            _sensorTimer.Stop();
            _autosaveTimer.Stop();
            SaveState();
            _writer.Flush(SessionEndFlush);
        }
        catch (Exception ex)
        {
            FirstFailure ??= ex.ToString();
        }
    }

    // -----------------------------------------------------------------------
    // お知らせの 1 行
    // -----------------------------------------------------------------------

    /// <summary>物語の下に 1 行のお知らせを出す。hideAfter 経てば畳む（null なら ✕ を押すまで）。</summary>
    internal void ShowNotice(string text, TimeSpan? hideAfter = null)
    {
        NoticeText.Text = text;
        NoticeText.ToolTip = text;
        NoticeBar.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        if (hideAfter is { } h) { _noticeTimer.Interval = h; _noticeTimer.Start(); }
    }

    internal void HideNotice()
    {
        _noticeTimer.Stop();
        NoticeBar.Visibility = Visibility.Collapsed;
    }

    // -----------------------------------------------------------------------
    // 保存（本文を取るのは UI スレッド。書くのは PartySaveWriter）
    // -----------------------------------------------------------------------

    /// <summary>いまの進みを書き手へ渡す。すぐ戻る。writer が無ければ何もしない。</summary>
    private void SaveState()
    {
        if (_writer is null) return;

        _state.Zoom = _scale;
        if (WindowState == WindowState.Normal && !double.IsNaN(Left) && !double.IsNaN(Top))
        {
            _state.WindowLeft = Left;
            _state.WindowTop = Top;
        }
        _state.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _writer.Submit(_state.ToJson(), _state.SavePath);
    }

    /// <summary>
    /// 閉じるときの保存。書き切るのを待つ。失敗したら「もう一度」か「保存せずに閉じる」かを聞く
    /// （黙って閉じると、数時間ぶんの進みが消えたことに気づけない）。
    /// </summary>
    private void SaveOnClose()
    {
        if (_writer is null) return;
        if (_sessionEnding)
        {
            // Windows の終了・サインアウト中。箱は出せない（出すと終了が止まる）。SessionEnding で書いた分に、最後の分を短く足すだけ
            SaveState();
            _writer.Flush(SessionEndFlush);
            return;
        }

        while (true)
        {
            SaveState();
            var result = _writer.Flush(TimeSpan.FromSeconds(5));
            if (result.Ok) return;
            if (result.Blocked)
            {
                // 保存を止めてある回。書けなかったのではなく書かないので、「もう一度」は意味が無い。伝えるだけ（OK のみ・一度だけ）
                bool top = Topmost;
                Topmost = false;             // 確認の窓が最前面の後ろに隠れないように
                try
                {
                    MessageBox.Show(this, Strings.SaveBlockedNote, Strings.SaveBlockedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { Topmost = top; }
                return;
            }

            bool wasTopmost = Topmost;
            Topmost = false;                 // 確認の窓が最前面の後ろに隠れないように
            MessageBoxResult answer;
            try
            {
                answer = MessageBox.Show(this, Strings.SaveFailedAsk(result.Message ?? ""), Strings.SaveFailedTitle,
                                         MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            }
            finally { Topmost = wasTopmost; }
            if (answer != MessageBoxResult.Yes) return;
        }
    }

    /// <summary>
    /// 前回の位置を覚えていればそこへ。無ければ主モニタの右上。
    ///
    /// **画面全体（仮想デスクトップ）で判定する。**
    /// 主モニタの作業領域だけで見ていたため、副モニタに置いていた窓が
    /// 起動のたび主モニタへ飛んでいた。左側の副モニタは座標が負になるので、
    /// 「0未満なら未設定」という判定も間違いだった。
    /// </summary>
    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        if (_state.WindowLeft is { } sl && _state.WindowTop is { } st)
        {
            if (IsOnScreen(sl, st))
            {
                Left = sl;
                Top = st;
                return;
            }

            // 覚えていた位置が、いまのどのモニタの作業領域にも掛からない（モニタを外した）。主モニタの真ん中へ戻す
            Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
            Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
            return;
        }
        Left = area.Right - ActualWidth - 16;
        Top = area.Top + 16;
    }

    /// <summary>その位置に窓の頭が見えているか。モニタを外したあとに行方不明にならないよう確かめる。</summary>
    private bool IsOnScreen(double left, double top)
    {
        // 掴める分（頭 80x40）が、**どれかのモニタの作業領域**に掛かっていれば良しとする。
        // 仮想デスクトップの外枠で見ると、モニタを外した後の「どのモニタにも無い隙間」に窓が残って掴めなくなる
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Monitors.IntersectsWorkArea(
            (int)Math.Floor(left * scale), (int)Math.Floor(top * scale),
            (int)Math.Ceiling((left + 80) * scale), (int)Math.Ceiling((top + 40) * scale));
    }

    private void SetExpanded(bool value)
    {
        _expanded = value;
        _state.Expanded = value;
        _feed.SetFolded(!value);   // 畳んでいる間は採取が 2 秒に 1 回（止めはしない）
        DetailPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = value ? "▲" : "▼";
        ExpandButton.ToolTip = value ? Strings.CollapseTip : Strings.ExpandTip;
        UpdateDetail();
    }

    // -----------------------------------------------------------------------
    // 大きさ
    // -----------------------------------------------------------------------

    /// <summary>
    /// 掴んで引いた分を拡大率に直す。横に引いた量で決める
    /// （縦横を別々に伸ばすと、ドット絵の縦横比が崩れて別物に見える）。
    /// </summary>
    private void OnGripDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double baseWidth = BoardRenderer.BoardWidth(BoardRenderer.PerRow(_slots.Count));
        _scale = Math.Clamp(_scale + e.HorizontalChange / baseWidth, 0.8, 6.0);
        Redraw(force: true);
        KeepOnScreen();
    }

    /// <summary>
    /// ⤢: **物理ピクセルで整数倍になる値**に揃える。
    /// 拡大率が半端だと、ドットの太さが場所によって 2px と 3px に割れて汚くなる。
    /// 表示倍率 125% の画面では 1.6 倍が「物理2倍」なので、そこへ寄せる。
    /// </summary>
    private void SnapScale()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0) dpi = 1;
        int n = (int)Math.Round(_scale * dpi);
        n = n % 3 + 1;                       // 1 → 2 → 3 → 1 と回す
        _scale = n / dpi;
        Redraw(force: true);
        KeepOnScreen();
    }

    /// <summary>
    /// 大きくしたときに画面からはみ出さないよう戻す。
    /// **仮想デスクトップ全体で見る。** 主モニタだけで見ていたため、
    /// 副モニタで大きくすると窓が主モニタへ引き戻されていた。
    /// </summary>
    private void KeepOnScreen()
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        if (Left + ActualWidth > vr) Left = Math.Max(vl, vr - ActualWidth);
        if (Top + ActualHeight > vb) Top = Math.Max(vt, vb - ActualHeight);
    }

    // -----------------------------------------------------------------------
    // 描画
    // -----------------------------------------------------------------------
    private void Redraw(bool force = false)
    {
        if (_slots.Count == 0) return;

        // 湯気だけは速い時計で動かす。姿のコマ送りより細かく揺れてほしい
        bool anyHot = _slots.Any(s => s.Load >= 0.60);
        int steamTick = anyHot ? (int)(_clock.ElapsedMilliseconds / 130) : 0;

        var slots = new List<BoardSlot>(_slots.Count);
        foreach (var s in _slots)
        {
            string motion = MotionFor(s.Load);
            slots.Add(new BoardSlot(s.CharacterId, s.Kind, motion, FrameOf(s.CharacterId, motion),
                                    _state.Level(s.Key), _state.LevelProgress(s.Key), s.Load, steamTick));
        }

        // **同じ絵なら描き直さない。**
        // 常駐して置いておくものなので、何も変わっていないのに毎秒7回
        // 盤を焼き直すのは、そのぶん誰かの CPU を食っているだけになる。
        var sig = new HashCode();
        sig.Add(_scale);
        sig.Add((int)_labels);
        foreach (var b in slots)
        {
            sig.Add(b.CharacterId);
            sig.Add(b.Motion);
            sig.Add(b.Frame);
            sig.Add(b.Level);
            sig.Add((int)(b.LevelProgress * 60));   // 経験値バーの見た目が変わる粒度まで
            sig.Add((int)(b.Load * 40));
            sig.Add(b.SteamTick);
        }
        int fingerprint = sig.ToHashCode();
        if (!force && fingerprint == _lastSignature) return;
        _lastSignature = fingerprint;

        int minArtWidth = (int)Math.Ceiling(MinPanelWidth / Math.Max(_scale, 0.1));
        BoardImage.Redraw(dc => _board.Draw(dc, slots, minArtWidth), _scale);

        double w = Math.Round(BoardImage.ArtSize.Width * _scale);
        double h = Math.Round(BoardImage.ArtSize.Height * _scale);
        BoardHost.Width = w;
        BoardHost.Height = h;
        // **枠の幅は盤の幅と切り離す。**
        // 段を折り返すと盤は細くなるが、数値欄まで一緒に細くすると
        // GPU の型番が入らなくなる。狭いときは枠だけ広げて、盤を中央に置く。
        double panel = Math.Max(w, MinPanelWidth);
        _boardPixelWidth = (int)BoardImage.ArtSize.Width;
        TabBar.Width = panel;
        SettingsPanel.Width = panel;
        StoryPanel.Width = panel;
        ArrowBar.Width = panel;
        DetailPanel.Width = panel;

        PlaceLevelLabels();
    }

    /// <summary>レベルの字を、基板の帯の上に置く。字は絵と違って拡大せず、常に読める大きさで。</summary>
    private void PlaceLevelLabels()
    {
        while (_levelLabels.Count < _slots.Count)
        {
            var t = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xE8, 0xDD)),
                FontWeight = FontWeights.SemiBold,
            };
            _levelLabels.Add(t);
            LevelLayer.Children.Add(t);
        }
        for (int i = _slots.Count; i < _levelLabels.Count; i++) _levelLabels[i].Text = "";

        double font = Math.Clamp(6 + 3 * _scale, 9, 15);
        double cell = BoardRenderer.Cell * _scale;
        int perRow = BoardRenderer.PerRow(_slots.Count);

        for (int i = 0; i < _slots.Count; i++)
        {
            var t = _levelLabels[i];
            t.FontSize = font;
            t.Width = cell;
            int lv = _state.Level(_slots[i].Key);
            t.Text = _labels switch
            {
                Labels.None => "",
                Labels.NameAndLevel => $"{_slots[i].Title} {Strings.Level}{lv}",
                _ => Strings.Level + lv,
            };
            // 名前まで出すと 32px の枠には入らない。**枠を越えて中央に伸ばす**
            t.Width = _labels == Labels.NameAndLevel ? cell * 2 : cell;
            int row = i / perRow;
            double left = (BoardRenderer.RowInset(_slots.Count, row, _boardPixelWidth)
                           + BoardRenderer.SlotLeft(i % perRow)) * _scale;
            Canvas.SetLeft(t, left - (t.Width - cell) / 2);
            Canvas.SetTop(t, BoardRenderer.LabelBandTop(row) * _scale);
        }
    }

    /// <summary>そのモーションのいまのコマ。速さは絵の側（アトラス）が持っている値に従う。</summary>
    private int FrameOf(string characterId, string motion)
    {
        var m = _atlas.Motion(characterId, motion);
        if (m is null || m.Frames.Count == 0) return 0;
        return (int)(_clock.ElapsedMilliseconds / Math.Max(40, m.Ms)) % m.Frames.Count;
    }

    /// <summary>
    /// 稼働状況を動きで表す。**数字は出さない。**
    /// 判定に使う値は経験値と同じものを使う。表示と中身がずれると嘘くさくなる。
    /// </summary>
    private static string MotionFor(double load)
    {
        if (load >= 0.85) return "peek";   // 必死の顔
        if (load >= 0.45) return "walk";   // 動きが速い
        if (load <= 0.03) return "sleep";  // ほぼ何もしていない
        return "idle";
    }

    // -----------------------------------------------------------------------
    // フィード → 席 → 経験値 → 物語
    // -----------------------------------------------------------------------
    private void OnSensorTick()
    {
        if (_feed.Latest is not { } latest) return;
        var frame = latest.Frame;
        // 古さも順序も dt も単調時計の印だけで決める（壁時計が戻っても、新しい標本が古く見えて固まることがない）
        SetStale(latest.AgeSeconds > IBoardFeed.StaleAfter(_feed.Period).TotalSeconds);
        // 同じ標本で二度は進めない（時間だけが進んだことになり、経験値が二重に入る）
        if (_lastStamp is { } previous && latest.Stamp <= previous) return;

        // dt は標本と標本の間隔。最初の 1 枚は 0。止まっていた後に大きく入らないよう、周期の 2 倍までに丸める
        double dt = _lastStamp is { } p
            ? Math.Clamp(BoardSample.SecondsBetween(p, latest.Stamp), 0, 2 * _feed.Period.TotalSeconds)
            : 0;
        _lastStamp = latest.Stamp;

        _frame = frame;
        _work = PartyComposition.WorkOf(frame);
        _slots = PartyComposition.Build(frame, Strings.Lang);

        int levelsBefore = LevelSum();
        var produced = _director.Tick(dt, _work, _slots, Strings.Lang);
        UpdateStoryText();

        // 変わったとき（レベルが上がった・物語の行が出た）に保存する。60 秒ごとの保存は _autosaveTimer（標本とは別の時計）
        bool changed = produced.Count > 0 || LevelSum() != levelsBefore;
        if (changed) SaveState();

        UpdateDetail();
    }

    /// <summary>古い標本の値は薄く見せる（最新だと思わせない）。</summary>
    private void SetStale(bool stale)
    {
        if (stale == _stale) return;
        _stale = stale;
        double opacity = stale ? 0.45 : 1.0;
        BoardHost.Opacity = opacity;
        DetailPanel.Opacity = opacity;
    }

    private int LevelSum()
    {
        int sum = 0;
        foreach (var s in _slots) sum += _state.Level(s.Key);
        return sum;
    }

    private void UpdateStoryText()
    {
        var log = _director.Recent;
        // 章題の横に、いま一行が居る所（町か行き先）を小さく添える。
        // 英語の名前は文中に差し込む形（"the steaming creek"）なので、見出しの形に直したものを使う
        string where = _director.Adventure?.PlaceTitle(Strings.Lang) ?? "";
        ChapterText.Text = _director.CurrentChapter.Title.For(Strings.Lang);
        PlaceText.Text = where.Length > 0 ? "  ·  " + where : "";
        StoryLine1.Text = log.Count > 0 ? log[0] : "";
        StoryLine2.Text = log.Count > 1 ? log[1] : "";
    }

    /// <summary>
    /// 絵の上を指したとき、それが何かを出す。
    ///
    /// **盤は1枚の絵なので、当たり判定は座標から自分で割り出す。**
    /// 部品ごとに UI 要素を置く手もあるが、そうすると絵と当たり判定が
    /// 二重管理になり、並びを変えたときにずれる。
    /// </summary>
    private void OnBoardHover(object sender, MouseEventArgs e)
    {
        int index = SlotAt(e.GetPosition(BoardHost));
        if (index < 0 || index >= _slots.Count)
        {
            _hoverTip.IsOpen = false;
            return;
        }

        var slot = _slots[index];
        int lv = _state.Level(slot.Key);
        // 測れていない席は 0% と言わない（Detail も「—」）
        string load = slot.Measured ? $"{slot.Load * 100:0}%" : PartyComposition.Missing;
        string detail = slot.Measured && slot.Detail.Length > 0 ? "\n" + slot.Detail : "";
        string gear = _director.Adventure?.EquippedNames(slot.Key, Strings.Lang) ?? "";
        _hoverTipText.Text =
            $"{Director.DisplayName(slot.CharacterId, Strings.Lang)}  {Strings.Level}{lv}\n" +
            $"{slot.Title}  {load}{detail}" +
            (gear.Length > 0 ? $"\n{Strings.Gear}: {gear}" : "");

        _hoverTip.PlacementTarget = BoardHost;
        _hoverTip.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        var p = e.GetPosition(BoardHost);
        _hoverTip.HorizontalOffset = p.X + 14;
        _hoverTip.VerticalOffset = p.Y + 18;
        _hoverTip.IsOpen = true;
    }

    /// <summary>盤の中の座標から、何番目の子かを割り出す。外していれば -1。</summary>
    private int SlotAt(Point p)
    {
        if (_slots.Count == 0 || _scale <= 0) return -1;
        double ax = p.X / _scale, ay = p.Y / _scale;

        int row = (int)(ay / BoardRenderer.RowHeight);
        int perRow = BoardRenderer.PerRow(_slots.Count);
        if (row < 0 || row >= BoardRenderer.RowsFor(_slots.Count)) return -1;

        double top = BoardRenderer.RowTop(row) + BoardRenderer.BoardTop;
        // 頭の少し上から、名札の帯までを当たりにする。厳密に体だけだと掴みにくい
        if (ay < top - 3 || ay > top + BoardRenderer.Cell + BoardRenderer.SlotH) return -1;

        double x = ax - BoardRenderer.RowInset(_slots.Count, row, _boardPixelWidth) - BoardRenderer.PadX;
        if (x < 0) return -1;
        int col = (int)(x / (BoardRenderer.Cell + BoardRenderer.Gap));
        if (col >= perRow) return -1;
        // 隙間を指しているときは何も出さない
        if (x - col * (BoardRenderer.Cell + BoardRenderer.Gap) > BoardRenderer.Cell) return -1;

        int index = row * perRow + col;
        return index < _slots.Count ? index : -1;
    }

    /// <summary>「点検」の窓を開く。</summary>
    private static void OpenChecks()
    {
        // step 4b: PCMieru.OpenChecks.v1
    }

    private void ConfirmReset(string ask, Action apply)
    {
        bool wasTopmost = Topmost;
        Topmost = false;                 // 確認の窓が最前面の後ろに隠れないように
        var answer = MessageBox.Show(this, ask, Strings.ResetTitle,
                                     MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                                     MessageBoxResult.Cancel);
        Topmost = wasTopmost;
        if (answer != MessageBoxResult.OK) return;

        apply();
        SaveState();                     // すぐ書き戻す。ここで落ちても戻らないように
        _lastSignature = 0;
        UpdateStoryText();
        Redraw(force: true);
        UpdateDetail();
    }

    private void ApplyLanguage()
    {
        LangButton.Content = Strings.Lang == "ja" ? "JA" : "EN";
        LangButton.ToolTip = Strings.LangTip;
        CloseButton.ToolTip = Strings.Close;
        SizeButton.ToolTip = Strings.SizeTip;
        TopmostButton.ToolTip = Strings.TopmostTip;
        Grip.ToolTip = Strings.ResizeTip;
        ExpandButton.ToolTip = _expanded ? Strings.CollapseTip : Strings.ExpandTip;
        BuildTabs();
        RefreshSettings();
        UpdateStoryText();
        PlaceLevelLabels();
        UpdateDetail();
    }

    // -----------------------------------------------------------------------
    // 展開時の数値（BoardFrame から。値の無い所は「—」）
    // -----------------------------------------------------------------------
    private void UpdateDetail()
    {
        if (!_expanded) return;
        DetailStack.Children.Clear();
        if (_frame is not { } f) return;

        // CPU は載っている数だけ。2ソケットなら2行出る。ソケットの割り当てが無ければ全体で 1 行
        IReadOnlyList<BoardCpuSocket> sockets = f.Sockets is { Count: > 0 }
            ? f.Sockets
            : [new BoardCpuSocket(f.CpuPercent, f.LogicalProcessors)];
        for (int p = 0; p < sockets.Count; p++)
        {
            var s = sockets[p];
            DetailStack.Children.Add(BarRow(sockets.Count > 1 ? $"CPU{p}" : "CPU", Ratio(s.Percent),
                s.Percent is { } pct ? $"{pct:0}%  {Strings.CoresSuffix(s.LogicalProcessors)}" : PartyComposition.Missing));
        }

        DetailStack.Children.Add(f.Memory is { } m
            ? BarRow("MEM", Ratio(m.Percent), $"{Gb(m.UsedBytes):0.0} / {Gb(m.TotalBytes):0.0} GB")
            : BarRow("MEM", 0, PartyComposition.Missing));

        // GPU は**必ず台数ぶん並べる。**1枚しか出さないと、複数挿しの機械で嘘になる。
        // 一覧そのものが読めていない（null）ときは「—」、読めて 0 枚のときは「見つかりません」
        var gpus = f.Gpus;
        if (gpus is null)
        {
            DetailStack.Children.Add(BarRow("GPU", 0, PartyComposition.Missing));
        }
        else if (gpus.Count == 0)
        {
            DetailStack.Children.Add(BarRow("GPU", 0, Strings.NoGpu));
        }
        else
        {
            for (int i = 0; i < Math.Min(gpus.Count, BoardFrame.MaxGpus); i++)
            {
                var g = gpus[i];
                string label = gpus.Count == 1 ? "GPU" : $"GPU{i}";
                string value = g.Percent is { } gp ? $"{gp:0}%" : PartyComposition.Missing;
                DetailStack.Children.Add(BarRow(label, Ratio(g.Percent), $"{value}   {g.Name}"));
                if (g.VramUsedBytes is { } used && g.VramTotalBytes is { } total && total > 0)
                    DetailStack.Children.Add(BarRow(Strings.Vram, (double)used / total,
                        $"{Gb(used):0.0} / {Gb(total):0.0} GB"));
                else if (g.VramUsedBytes is { } only && only > 0)
                    DetailStack.Children.Add(BarRow(Strings.Vram, 0, $"{Gb(only):0.0} GB"));
                else if (g.VramUsedBytes is null)
                    DetailStack.Children.Add(BarRow(Strings.Vram, 0, PartyComposition.Missing));
            }
        }

        // 記憶装置は**種類ごとに**出す。総量ひとつだと、どれが動いているのか分からない
        foreach (var slot in _slots.Where(s2 => s2.Key is "m2" or "ssd" or "hdd"))
        {
            var kind = f.DiskKinds?.FirstOrDefault(k => DiskSlots.For(k.Kind)?.Key == slot.Key);
            double? bytes = kind is null ? f.Disk.BytesPerSec : kind.BytesPerSec;
            string rate = bytes is { } b ? $"{Mb(b):0.0} MB/s" : PartyComposition.Missing;
            string capacity = kind is { UsedBytes: { } u, TotalBytes: { } t } && t > 0
                ? $"  ·  {Gb(u):0} / {Gb(t):0} GB" + (kind.DeviceCount > 1 ? $"  ×{kind.DeviceCount}" : "")
                : "";
            DetailStack.Children.Add(BarRow(slot.Title, slot.Load, rate + capacity));
        }

        double? disk = f.Disk.BytesPerSec is { } io && double.IsFinite(io) && io >= 0
            ? Math.Clamp(io / DiskSlots.WorkScaleBytesPerSec, 0, 1)
            : null;
        DetailStack.Children.Add(BarRow("I/O", disk ?? 0,
            f.Disk.BytesPerSec is { } io2 ? $"{Mb(io2):0.0} MB/s" : PartyComposition.Missing));

        DetailStack.Children.Add(new TextBlock
        {
            Text = Strings.Disclaimer,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x7A, 0x93)),
        });
    }

    /// <summary>百分率（0..100）→ 0..1。無ければ 0（バーが空なだけで、数字の欄は「—」）。</summary>
    private static double Ratio(double? percent) =>
        percent is { } p && double.IsFinite(p) ? Math.Clamp(p / 100.0, 0, 1) : 0;

    private static double Gb(long bytes) => bytes / 1024.0 / 1024.0 / 1024.0;
    private static double Mb(double bytes) => bytes / 1024.0 / 1024.0;

    private static Brush LoadBrush(double v) =>
        v >= 0.85 ? new SolidColorBrush(Color.FromRgb(0xF0, 0x50, 0x70))
      : v >= 0.60 ? new SolidColorBrush(Color.FromRgb(0xE7, 0xC8, 0x4B))
                  : new SolidColorBrush(Color.FromRgb(0x5D, 0xD8, 0xBB));

    private UIElement BarRow(string label, double ratio, string text)
    {
        var grid = NewRow(label, text);
        var track = new Border
        {
            Height = 7,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fill = new Border
        {
            Height = 7,
            Background = LoadBrush(ratio),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        track.SizeChanged += (_, e) => fill.Width = Math.Max(0, e.NewSize.Width * Math.Clamp(ratio, 0, 1));
        var host = new Grid();
        host.Children.Add(track);
        host.Children.Add(fill);
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);
        return grid;
    }

    private static Grid NewRow(string label, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var l = new TextBlock
        {
            Text = label, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xC4)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(l, 0);
        grid.Children.Add(l);

        var t = new TextBlock
        {
            Text = text, FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xF8)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            // **切れたら「…」を出す。**
            // 黙って切れると「RTX 3080 Ti」が「RTX 3080」と読めてしまう
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text,
        };
        Grid.SetColumn(t, 2);
        grid.Children.Add(t);
        return grid;
    }
}
