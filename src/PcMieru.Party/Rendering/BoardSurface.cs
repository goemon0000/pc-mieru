using System.Windows;
using System.Windows.Media;

namespace PcMieru.Party.Rendering;

/// <summary>
/// 盤を置く面。
///
/// **毎コマ画像に焼くのをやめるために置いた。**
/// もとは RenderTargetBitmap に焼いて &lt;Image&gt; に差していたが、あれは
/// WPF の外で1枚ずつラスタライズする処理で、実機で測ると描画だけで
/// CPU の 2% を使っていた。描いた内容をそのまま WPF に預ければ、
/// 合成は WPF 側が受け持ち、変化が無いコマでは何も起きない。
///
/// 拡大は要素側の変換で行う。ドット絵なので**最近傍**で拡げること。
/// </summary>
public sealed class BoardSurface : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private Size _art = new(1, 1);
    private double _scale = 1;
    private bool _transformSet;

    public BoardSurface()
    {
        RenderOptions.SetBitmapScalingMode(_visual, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_visual, EdgeMode.Aliased);
        AddVisualChild(_visual);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    /// <summary>ドット絵の画素での大きさ（拡大前）。</summary>
    public Size ArtSize => _art;

    /// <summary>盤を描き直す。<paramref name="draw"/> は描いた大きさを返すこと。</summary>
    public void Redraw(Func<DrawingContext, Size> draw, double scale)
    {
        var artBefore = _art;
        var scaleBefore = _scale;
        using (var dc = _visual.RenderOpen()) _art = draw(dc);
        _scale = scale <= 0 ? 1 : scale;
        // C38: a redraw for a new animation frame keeps the size and the scale. A new transform and a measure pass for that only cost the UI and render threads for nothing.
        if (!_transformSet || _scale != scaleBefore)
        {
            _visual.Transform = new ScaleTransform(_scale, _scale);
            _transformSet = true;
        }

        if (_art != artBefore || _scale != scaleBefore) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Round(_art.Width * _scale), Math.Round(_art.Height * _scale));
}
