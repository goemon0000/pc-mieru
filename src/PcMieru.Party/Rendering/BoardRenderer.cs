using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcMieru.Party.Rendering;

/// <summary>基板に挿さっている1体ぶんの情報。</summary>
public sealed record BoardSlot(
    string CharacterId,
    SlotKind Kind,
    string Motion,
    int Frame,
    int Level = 1,
    double LevelProgress = 0,
    /// <summary>いまの働き 0..1。赤みと湯気の量を決める。</summary>
    double Load = 0,
    /// <summary>湯気の位相。姿のコマ送りとは別の時計で動かす。</summary>
    int SteamTick = 0);

/// <summary>
/// 基板とキャラを1枚の絵に描く。
///
/// **ドット絵の解像度のまま描いて、表示側で整数倍に拡大する。**
/// 拡大してから描くと、線の太さが拡大率で変わってドット絵に見えなくなる。
///
/// 配置の数値は tools/mock/index.html で目視して決めたものと同じ。
/// 見た目を変えるときは、先にあの見本で確かめてからここへ移すこと。
/// </summary>
public sealed class BoardRenderer
{
    // --- 配置（単位はドット絵の1画素）---
    public const int Cell = 32;
    /// <summary>スロット同士の間。**広めに取る。**
    /// 詰めると基板というより一列のアイコンに見え、レベルの字も入らない。</summary>
    public const int Gap = 15;
    public const int PadX = 10;
    /// <summary>基板の上端からキャラの頭まで。**湯気を昇らせる余白を含む。**</summary>
    public const int BoardTop = 13;
    public const int SlotH = 8;
    public const int BoardPadBottom = 3;
    /// <summary>スロットの下に取る、レベルを書くための帯。</summary>
    public const int LabelH = 11;
    /// <summary>足がスロットに入る深さ。これが無いと「乗っているだけ」に見える。</summary>
    public const int SlotInset = 4;

    /// <summary>1段ぶんの高さ。**GPU が何枚もある機械では段を折り返す。**</summary>
    public static int RowHeight => BoardTop + Cell + SlotH + LabelH;
    public static int BoardHeight(int rows) => Math.Max(1, rows) * RowHeight + BoardPadBottom;
    public static int RowTop(int row) => row * RowHeight;
    /// <summary>レベルの帯の上端（ドット絵の座標）。</summary>
    public static int LabelBandTop(int row) => RowTop(row) + BoardTop + Cell - SlotInset + SlotH;
    /// <summary>段の中で i 番目のスロットの左端。文字を重ねる側もこれを使う。</summary>
    public static int SlotLeft(int index) => PadX + index * (Cell + Gap);

    /// <summary>その段を中央に寄せるための左の余白。描画側と文字側で必ず同じ値を使うこと。</summary>
    public static int RowInset(int count, int row, int boardWidth)
    {
        int perRow = PerRow(count);
        int inRow = Math.Min(count - row * perRow, perRow);
        int inset = (boardWidth - BoardWidth(perRow)) / 2;
        return inset + (inRow < perRow ? (perRow - inRow) * (Cell + Gap) / 2 : 0);
    }

    /// <summary>1段に並べる上限。これを超えたら折り返す（窓が画面幅を超えないように）。</summary>
    public const int MaxPerRow = 8;

    public static int RowsFor(int count) => Math.Max(1, (Math.Max(count, 1) + MaxPerRow - 1) / MaxPerRow);

    /// <summary>
    /// 1段に何体置くか。**段の数で割り切って均す。**
    /// 上限で切ると 9体が 8+1 になり、2段目に1体だけ立った間抜けな絵になる。
    /// 均せば 5+4 になり、盤として収まる。
    /// </summary>
    public static int PerRow(int count)
    {
        int n = Math.Max(count, 1);
        int rows = RowsFor(n);
        return (n + rows - 1) / rows;
    }
    public static int BoardWidth(int slotCount) =>
        PadX * 2 + slotCount * Cell + Math.Max(0, slotCount - 1) * Gap;

    // --- 色 ---
    private static readonly Color Pcb      = C(0x22, 0x40, 0x2f);
    private static readonly Color PcbLine  = C(0x2e, 0x55, 0x40);
    private static readonly Color PcbEdge  = C(0x18, 0x2d, 0x22);
    private static readonly Color Slot     = C(0x0f, 0x17, 0x20);
    private static readonly Color SlotLip  = C(0x4d, 0x5a, 0x70);
    private static readonly Color Gold     = C(0xd8, 0xb2, 0x5c);
    private static readonly Color ChipRes  = C(0x1b, 0x2b, 0x22);
    private static readonly Color ChipCap  = C(0x2a, 0x35, 0x50);
    private static readonly Color ExpTrack = C(0x1b, 0x33, 0x27);
    private static readonly Color ExpFill  = C(0x5d, 0xd8, 0xbb);

    private static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private readonly Atlas _atlas;

    /// <summary>描画先。大きさが変わらない限り作り直さない。</summary>
    private RenderTargetBitmap? _canvas;

    /// <summary>基板の下地。大きさが変わらない限り焼き直さない。</summary>
    private RenderTargetBitmap? _background;

    /// <summary>切り出したコマ。**同じコマを毎回切り出し直さない。**</summary>
    private readonly Dictionary<(string, string, int), CroppedBitmap> _frames = new();

    /// <summary>熱を滲ませるときの型抜き。コマと1対1なので同じ鍵で持てる。</summary>
    private readonly Dictionary<(string, string, int), ImageBrush> _masks = new();

    public BoardRenderer(Atlas atlas) => _atlas = atlas;

    /// <summary>
    /// 基板1枚を描く。返るのは等倍（拡大していない）の絵。
    /// <paramref name="minWidth"/> を渡すと、足りない分は基板を広げて中央に寄せる。
    /// 段を折り返すと盤が枠より細くなり、両脇に隙間が空いて浮いて見えるため。
    /// </summary>
    /// <summary>絵を1枚のビットマップに焼く。**書き出し（--render）専用。**</summary>
    public BitmapSource Render(IReadOnlyList<BoardSlot> slots, int minWidth = 0)
    {
        var dv = new DrawingVisual();
        Size size;
        using (var dc = dv.RenderOpen()) size = Draw(dc, slots, minWidth);

        if (_canvas is null || _canvas.PixelWidth != (int)size.Width || _canvas.PixelHeight != (int)size.Height)
            _canvas = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        else
            _canvas.Clear();
        _canvas.Render(dv);
        return _canvas;
    }

    /// <summary>
    /// 与えられた描画面に盤を描き、その大きさ（ドット絵の画素）を返す。
    ///
    /// **常用の経路はこちら。** 毎コマ RenderTargetBitmap に焼いていたが、
    /// あれは WPF の外で1枚ずつラスタライズする処理で、実機で測ると
    /// 描画だけで CPU の 2% を使っていた。描画内容を WPF に預ければ、
    /// 合成は WPF 側（多くの環境で GPU）が受け持つ。
    /// </summary>
    public Size Draw(DrawingContext dc, IReadOnlyList<BoardSlot> slots, int minWidth = 0)
    {
        int perRow = PerRow(slots.Count);
        int rows = RowsFor(slots.Count);
        int natural = BoardWidth(perRow);
        int w = Math.Max(natural, minWidth);
        int h = BoardHeight(rows);
        int inset = (w - natural) / 2;

        {
            // 基板の模様は固定の種で描いていて毎回同じ絵になる。
            // **1回だけ焼いて、以後は貼るだけにする。**
            // 配線とチップを毎コマ描き直すのは、常駐しているだけの時間の無駄。
            dc.DrawImage(Background(w, h), new Rect(0, 0, w, h));

            for (int row = 0; row < rows; row++)
            {
                int from = row * perRow;
                int to = Math.Min(from + perRow, slots.Count);
                int charY = RowTop(row) + BoardTop;
                int slotY = charY + Cell - SlotInset;

                // 奥 → キャラ → 手前 の順。これで「挿さっている」ように見える。
                // 最後の段が埋まらないときは、その段だけ中央に寄せる
                int rowInset = inset + (to - from < perRow ? (perRow - (to - from)) * (Cell + Gap) / 2 : 0);

                for (int i = from; i < to; i++)
                    DrawSlotBack(dc, slots[i].Kind, rowInset + SlotX(i - from), slotY, Cell, SlotH);

                for (int i = from; i < to; i++)
                    DrawCharacter(dc, slots[i], rowInset + SlotX(i - from), charY);

                for (int i = from; i < to; i++)
                    DrawSlotFront(dc, slots[i].Kind, rowInset + SlotX(i - from), slotY, Cell, SlotH);

                // 経験値の進み具合。数字は上に重ねる文字で出すので、ここは線だけ。
                for (int i = from; i < to; i++)
                    DrawExpBar(dc, rowInset + SlotX(i - from), LabelBandTop(row) + LabelH - 3, Cell,
                               slots[i].LevelProgress);

                // 湯気は最後。スロットの手前より更に上に出す
                for (int i = from; i < to; i++)
                    DrawSteam(dc, rowInset + SlotX(i - from), charY, slots[i].Load, slots[i].SteamTick);
            }
        }

        return new Size(w, h);
    }

    private static int SlotX(int index) => SlotLeft(index);

    private static void DrawExpBar(DrawingContext dc, int x, int y, int w, double progress)
    {
        Fill(dc, ExpTrack, x + 2, y, w - 4, 1);
        int filled = (int)Math.Round((w - 4) * Math.Clamp(progress, 0, 1));
        if (filled > 0) Fill(dc, ExpFill, x + 2, y, filled, 1);
    }

    private void DrawCharacter(DrawingContext dc, BoardSlot slot, int x, int y)
    {
        var key = (slot.CharacterId, slot.Motion, slot.Frame);
        if (!_frames.TryGetValue(key, out var cropped))
        {
            var src = _atlas.Frame(slot.CharacterId, slot.Motion, slot.Frame);
            if (src is null) return;
            cropped = new CroppedBitmap(_atlas.Characters, src.Value);
            cropped.Freeze();
            _frames[key] = cropped;
        }
        var rect = new Rect(x, y, Cell, Cell);

        // **働くほど熱くなる。**
        // 動きの速さだけでは「速い」と「必死」の区別が付かない。色なら一目で分かる。
        // 55% を超えてから効かせる。常時ほのかに赤いと、逆に何も伝わらなくなる。
        double heat = Math.Clamp((slot.Load - 0.55) / 0.45, 0, 1);

        // 熱は**輪郭の外側に滲ませる。**
        // 体そのものを赤く塗ると、実機で試したとき7体とも同じ桃色の塊になって
        // 誰が誰だか分からなくなった。青灰色の体に赤を重ねると、熱ではなく
        // ただの濁った紫になる。外に滲ませれば、体の色は残ったまま熱く見える。
        if (heat > 0.01)
        {
            // 上下左右は濃く、斜めは薄く。**斜めを抜くと角が四角く見えて熱に見えない。**
            var near = new SolidColorBrush(Color.FromArgb((byte)(heat * 145), 0xFF, 0x5A, 0x1E));
            var far = new SolidColorBrush(Color.FromArgb((byte)(heat * 80), 0xFF, 0x7A, 0x2E));
            near.Freeze();
            far.Freeze();
            var mask0 = MaskFor(key, cropped);
            foreach (var (dx, dy, brush) in new[]
                     {
                         (-1, 0, near), (1, 0, near), (0, -1, near), (0, 1, near),
                         (-1, -1, far), (1, -1, far), (-1, 1, far), (1, 1, far),
                     })
            {
                dc.PushOpacityMask(mask0);
                dc.DrawRectangle(brush, null, new Rect(x + dx, y + dy, Cell, Cell));
                dc.Pop();
            }
        }

        dc.DrawImage(cropped, rect);

        if (heat <= 0.01) return;

        // 体の側はごく薄く暖めるだけ。色を奪わない程度に留める
        dc.PushOpacityMask(MaskFor(key, cropped));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(heat * 38), 0xFF, 0x76, 0x30)), null, rect);
        dc.Pop();
    }

    /// <summary>
    /// 頭から立ちのぼる湯気。**昇りながら薄く広がる。**
    /// 同じ形が点滅するだけだと湯気に見えず、ただの点滅した点になる。
    /// </summary>
    private static void DrawSteam(DrawingContext dc, int x, int y, double load, int frame)
    {
        if (load < 0.60) return;
        int puffs = load >= 0.85 ? 3 : 2;
        int cx = x + Cell / 2;

        for (int i = 0; i < puffs; i++)
        {
            int phase = (frame + i * 4) % 12;
            int py = y - phase;
            if (py < 0) continue;

            // **同じ所から出て、昇るほど太く・薄く・横に逸れる。**
            // 大きさも位置も変えずに点滅させると、湯気ではなく画面の汚れに見える。
            int w = phase < 4 ? 2 : phase < 8 ? 3 : 4;
            int drift = phase / 3 * (i % 2 == 0 ? 1 : -1);
            byte a = (byte)Math.Clamp(215 - phase * 17, 0, 255);
            Fill(dc, Color.FromArgb(a, 0xEE, 0xF6, 0xFF), cx - w / 2 + drift, py, w, 2);
        }
    }

    private RenderTargetBitmap Background(int w, int h)
    {
        if (_background is { } b && b.PixelWidth == w && b.PixelHeight == h) return b;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) DrawBoard(dc, w, h);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        _background = rtb;
        return rtb;
    }

    private ImageBrush MaskFor((string, string, int) key, CroppedBitmap cropped)
    {
        if (_masks.TryGetValue(key, out var m)) return m;
        m = new ImageBrush(cropped) { Stretch = Stretch.Fill };
        m.Freeze();
        _masks[key] = m;
        return m;
    }

    private static void Fill(DrawingContext dc, Color c, double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(x, y, w, h));
    }

    private static void DrawSlotBack(DrawingContext dc, SlotKind kind, int x, int y, int w, int h)
    {
        switch (kind)
        {
            case SlotKind.Socket: Fill(dc, Slot, x - 3, y, w + 6, h); break;
            case SlotKind.Pcie:   Fill(dc, Slot, x - 4, y, w + 8, h); break;
            case SlotKind.Atx:    Fill(dc, Slot, x + 1, y, w - 2, h); break;
            default:              Fill(dc, Slot, x + 2, y, w - 4, h); break;
        }
    }

    private static void DrawSlotFront(DrawingContext dc, SlotKind kind, int x, int y, int w, int h)
    {
        int bot = y + h;
        switch (kind)
        {
            case SlotKind.Socket:
                Fill(dc, SlotLip, x - 3, y, 3, h);
                Fill(dc, SlotLip, x + w, y, 3, h);
                Fill(dc, SlotLip, x - 3, y, w + 6, 1);
                for (int i = 1; i < w - 1; i += 2) Fill(dc, Gold, x + i, bot - 2, 1, 1);
                break;

            case SlotKind.Dimm:
                // 両端の白いツメ。DIMM だと分かる一番の目印
                Fill(dc, SlotLip, x, y - 5, 3, h + 5);
                Fill(dc, SlotLip, x + w - 3, y - 5, 3, h + 5);
                Fill(dc, PcbEdge, x, y - 5, 3, 2);
                Fill(dc, PcbEdge, x + w - 3, y - 5, 3, 2);
                Fill(dc, Gold, x + 4, bot - 2, w - 8, 1);
                break;

            case SlotKind.Pcie:
                Fill(dc, SlotLip, x + w + 1, y - 4, 3, h + 4);
                Fill(dc, SlotLip, x - 4, y, w + 8, 1);
                Fill(dc, Gold, x - 2, bot - 2, w + 4, 1);
                break;

            case SlotKind.M2:
                Fill(dc, SlotLip, x + w - 4, bot - 4, 4, 4);
                Fill(dc, PcbEdge, x + w - 3, bot - 3, 2, 2);
                Fill(dc, Gold, x + 3, bot - 2, w - 12, 1);
                break;

            case SlotKind.Sata:
                Fill(dc, SlotLip, x + 2, y, w - 4, 1);
                Fill(dc, SlotLip, x + 2, y, 2, h);
                Fill(dc, Gold, x + 6, bot - 2, w - 12, 1);
                break;

            case SlotKind.Atx:
                Fill(dc, SlotLip, x + 1, y, w - 2, 1);
                for (int i = 0; i < 6; i++) Fill(dc, Gold, x + 3 + i * 4, bot - 3, 2, 2);
                break;
        }
    }

    /// <summary>
    /// 基板。**部品を少し散らす**のが要点。
    /// 配線だけだと方眼紙に見え、チップ抵抗とコンデンサを置くと一気に基板に見える。
    /// 乱数は固定の種で回す（毎フレーム模様が変わるとちらつく）。
    /// </summary>
    private static void DrawBoard(DrawingContext dc, int w, int h)
    {
        Fill(dc, Pcb, 0, 0, w, h);
        Fill(dc, PcbEdge, 0, 0, w, 1);
        Fill(dc, PcbEdge, 0, h - 1, w, 1);

        var rnd = new Random(11);

        for (int i = 0; i < 18; i++)
        {
            int px = 3 + rnd.Next(Math.Max(1, w - 10));
            int py = 3 + rnd.Next(Math.Max(1, h - 8));
            int len = 5 + rnd.Next(14);
            if (rnd.NextDouble() > 0.5)
            {
                Fill(dc, PcbLine, px, py, Math.Min(len, w - 3 - px), 1);
                Fill(dc, PcbLine, Math.Min(px + len, w - 4), py, 1, Math.Min(5, h - 3 - py));
            }
            else
            {
                Fill(dc, PcbLine, px, py, 1, Math.Min(len, h - 3 - py));
                Fill(dc, PcbLine, px, Math.Min(py + len, h - 4), Math.Min(6, w - 3 - px), 1);
            }
        }

        for (int i = 0; i < 22; i++)
        {
            int px = 4 + rnd.Next(Math.Max(1, w - 10));
            int py = 3 + rnd.Next(Math.Max(1, h - 7));
            if (rnd.NextDouble() > 0.55)
            {
                Fill(dc, ChipRes, px, py, 3, 2);
                Fill(dc, Gold, px, py, 1, 2);
                Fill(dc, Gold, px + 2, py, 1, 2);
            }
            else
            {
                Fill(dc, ChipCap, px, py, 2, 3);
                Fill(dc, SlotLip, px, py, 2, 1);
            }
        }

        foreach (var (dx, dy) in new[] { (2, 2), (w - 4, 2), (2, h - 4), (w - 4, h - 4) })
        {
            Fill(dc, SlotLip, dx, dy, 2, 2);
            Fill(dc, PcbEdge, dx, dy, 1, 1);
        }
    }
}
