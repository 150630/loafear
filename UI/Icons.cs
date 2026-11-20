using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LoafEar.UI;

/// <summary>
/// 播放控制那几枚图标的矢量几何（播放页和右键菜单共用一套）。
/// 用 Path 画而不是放图片文件，这样仍然是单文件程序，且任意 DPI 下都清晰。
/// 坐标系统一按 16×16 设计。
/// </summary>
internal static class Icons
{
    /// <summary>
    /// 「上一句 / 下一句」：设计稿是 ⏮ ⏭ 那种「竖条 + 三角」，16×16 方格内画。
    /// </summary>
    public const string SkipPrev =
        "M 0.6,1.6 L 3,1.6 L 3,14.4 L 0.6,14.4 Z " +
        "M 14.4,1.9 L 4.4,8 L 14.4,14.1 Z";

    public const string SkipNext =
        "M 13,1.6 L 15.4,1.6 L 15.4,14.4 L 13,14.4 Z " +
        "M 1.6,1.9 L 11.6,8 L 1.6,14.1 Z";

    /// <summary>
    /// 收藏用的五角星。16×16 方格内画，尖角留 1px 余量，
    /// 小尺寸下靠圆角描边糊不掉，实心填充一眼就能看出「点亮」。
    /// </summary>
    public const string Star =
        "M 8,1.8 L 9.94,5.87 L 14.4,6.5 L 11.2,9.63 L 11.94,14.06 " +
        "L 8,11.96 L 4.06,14.06 L 4.8,9.63 L 1.6,6.5 L 6.06,5.87 Z";

    /// <summary>造一个图标元素：填充型（播放/暂停）用 Fill，线条型用 Stroke。</summary>
    public static FrameworkElement Make(string data, double size, Brush brush, bool filled = false)
    {
        var geometry = Geometry.Parse(data);

        var path = new Path
        {
            Data = geometry,
            Stroke = filled ? null : brush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = filled ? brush : null,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 线条型图标的包围盒比方形小，加个 Viewbox 保证视觉尺寸一致
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = path,
        };
    }

    /// <summary>
    /// 实心五角星。收藏行里那一列都是「已收藏」，
    /// 所以只有点亮一种形态：灰掉的那份只在鼠标悬停想取消时才出现。
    /// </summary>
    public static FrameworkElement MakeStar(double size, Brush brush)
    {
        var path = new Path
        {
            Data = Geometry.Parse(Star),
            Fill = brush,
            Stroke = brush,
            StrokeThickness = 0.6,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
        };

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = path,
        };
    }

    // ------------------------------------------------------------------ 2026-11-19 设计稿抠出来的三枚

    /// <summary>
    /// 设计稿里播放圆按钮的白色三角，画在 28×28 的画布里（2026-11-19 第二版稿）。
    /// 三角本身略微右偏（x 中心 14.5 而不是 14），这是设计师做的视觉居中，照抄。
    /// 圆盘不在这里画：按钮自己的椭圆就是圆盘，这里只出白色图形。
    /// </summary>
    private const string PlayTriangle =
        "M 8.855,4.13 L 2.017,0.182 C 1.122,-0.336 0,0.312 0,1.347 " +
        "L 0,9.242 C 0,10.278 1.122,10.925 2.017,10.407 " +
        "L 8.855,6.46 C 9.75,5.942 9.75,4.648 8.855,4.13 Z";

    /// <summary>播放（白三角）。<paramref name="disc"/> 传按钮圆盘的直径，三角按设计稿比例缩放。</summary>
    public static FrameworkElement MakePlayGlyph(double disc, Brush? glyph = null)
    {
        const double box = 28;
        var fill = glyph ?? Brushes.White;

        return GlyphBox(box, disc, () => new Path
        {
            Data = Geometry.Parse(PlayTriangle),
            Fill = fill,
            RenderTransform = new TranslateTransform(9.783, 8.705),
        });
    }

    /// <summary>
    /// 暂停（两根白竖条，四角带小圆角），画在同一张 28×28 的画布里。
    /// 两根条的交界处圆角是 0.765 / 0.817，差不到半个像素，统一取 0.78。
    /// </summary>
    public static FrameworkElement MakePauseGlyph(double disc, Brush? glyph = null)
    {
        const double box = 28;
        var bars = new[] { 8.845, 15.732 };
        var fill = glyph ?? Brushes.White;

        return GlyphBox(box, disc, () =>
        {
            var canvas = new Canvas();

            foreach (var left in bars)
            {
                var bar = new Rectangle
                {
                    Width = 3.423,
                    Height = 11.501,
                    RadiusX = 0.78,
                    RadiusY = 0.78,
                    Fill = fill,
                };

                Canvas.SetLeft(bar, left);
                Canvas.SetTop(bar, 8.245);
                canvas.Children.Add(bar);
            }

            return canvas;
        });
    }

    /// <summary>
    /// 播放键里那两根白竖条，单独抽成独立的画布：
    /// 右键菜单要的是「只有图形」的版本（菜单按钮没有深色圆盘，套用带圆盘的画板会把图形缩成一个小点）。
    /// <para>
    /// 这里必须用 Canvas + Rectangle，不能像播放三角那样丢一段 Path 给 Viewbox 去缩 ——
    /// 带圆弧的两条子路径交给 Viewbox，它会按一个比实际大一圈的包围盒等比缩，
    /// 结果 16px 的槽里只画出 9px 高（实测），比旁边的三角小一号。
    /// </para>
    /// </summary>
    public static FrameworkElement MakePauseBare(double size, Brush brush)
    {
        // 设计稿 28×28 画板里两根竖条的实际包围盒
        const double left = 8.845;
        const double width = 10.31;
        const double height = 11.501;

        var canvas = new Canvas { Width = width, Height = height };

        foreach (var x in new[] { 8.845, 15.732 })
        {
            var bar = new Rectangle
            {
                Width = 3.423,
                Height = height,
                RadiusX = 0.78,
                RadiusY = 0.78,
                Fill = brush,
            };

            Canvas.SetLeft(bar, x - left);
            Canvas.SetTop(bar, 0);
            canvas.Children.Add(bar);
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas,
        };
    }

    /// <summary>播放页那枚白三角，去掉圆盘、按包围盒铺满 —— 右键菜单那一排用。</summary>
    public static FrameworkElement MakePlayBare(double size, Brush brush) =>
        BareGlyph(PlayTriangle, size, brush);

    /// <summary>把一段设计稿几何按包围盒等比铺进 size×size。</summary>
    private static FrameworkElement BareGlyph(string data, double size, Brush brush) =>
        new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = new Path
            {
                Data = Geometry.Parse(data),
                Fill = brush,
            },
        };

    /// <summary>
    /// 单句循环（播放页用）：设计稿那只「上箭头向右、下箭头向左」的折线 + 中间一个 1，
    /// 原始画布 11.72×8.95。播放页和右键菜单用的是同一枚。
    /// </summary>
    private static readonly string[] LoopArrowParts =
    {
        "M 3.36,1.99 L 7.79,1.99 L 7.79,2.98 L 10.37,1.49 L 7.79,0 L 7.79,0.99 " +
        "L 3.36,0.99 C 1.51,0.99 0,2.5 0,4.35 L 0,5.12 L 1,5.12 L 1,4.35 " +
        "C 1,3.05 2.06,2 3.35,2 L 3.36,1.99 Z",

        "M 9.36,0.01 L 9.36,0.78 C 9.36,2.08 8.3,3.13 7.01,3.13 L 2.58,3.13 " +
        "L 2.58,2.14 L 0,3.63 L 2.58,5.12 L 2.58,4.13 L 7.01,4.13 " +
        "C 8.86,4.13 10.37,2.62 10.37,0.77 L 10.37,0 L 9.37,0 Z",

        "M 1.43,3.12 L 1.43,0 L 0.94,0 L 0,0.18 L 0,0.73 L 0.77,0.59 L 0.77,3.12 L 1.43,3.12 Z",
    };

    private static readonly (double X, double Y)[] LoopArrowOffsets =
    {
        (0, 0), (1.35, 3.83), (4.84, 2.92),
    };

    public static FrameworkElement MakeLoopArrows(double width, Brush brush)
    {
        const double w = 11.72;
        const double h = 8.95;

        var canvas = new Canvas { Width = w, Height = h };

        for (var i = 0; i < LoopArrowParts.Length; i++)
        {
            var part = new Path
            {
                Data = Geometry.Parse(LoopArrowParts[i]),
                Fill = brush,
                RenderTransform = new TranslateTransform(LoopArrowOffsets[i].X, LoopArrowOffsets[i].Y),
            };

            canvas.Children.Add(part);
        }

        return new Viewbox
        {
            Width = width,
            Height = width * h / w,
            Stretch = Stretch.Uniform,
            Child = canvas,
        };
    }

    /// <summary>
    /// 悬浮字幕那颗锁：锁体是圆角方块，锁梁是一段开口圆弧。
    /// 解锁时锁梁往右挪、只搭住一半，一眼能看出「现在是能拖的」。
    /// </summary>
    public static FrameworkElement MakeLock(double size, Brush brush, bool open)
    {
        const double box = 16;

        var canvas = new Canvas { Width = box, Height = box };

        canvas.Children.Add(new Rectangle
        {
            Width = box,
            Height = box,
            Fill = Brushes.Transparent,
        });

        var body = new Rectangle
        {
            Width = 9.2,
            Height = 6.2,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = brush,
        };

        Canvas.SetLeft(body, 3.4);
        Canvas.SetTop(body, 7.6);
        canvas.Children.Add(body);

        var shackle = new Path
        {
            Data = Geometry.Parse(open
                ? "M 8.6,7.7 L 8.6,5.4 A 2.5,2.5 0 0 1 13.6,5.4 L 13.6,6.4"
                : "M 5.4,7.7 L 5.4,5.2 A 2.6,2.6 0 0 1 10.6,5.2 L 10.6,7.7"),
            Stroke = brush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        canvas.Children.Add(shackle);

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas,
        };
    }

    /// <summary>
    /// 把一片「设计稿原始坐标」的图形按圆盘直径缩放到目标大小：
    /// 画布里垫一块同尺寸透明块，Viewbox 才会按整圆（而不是图形自身包围盒）缩放，
    /// 图形在圆里的偏心位置就跟设计稿完全一致。
    /// </summary>
    private static FrameworkElement GlyphBox(double box, double size, Func<FrameworkElement> build)
    {
        var canvas = new Canvas { Width = box, Height = box };

        canvas.Children.Add(new Rectangle
        {
            Width = box,
            Height = box,
            Fill = Brushes.Transparent,
        });

        canvas.Children.Add(build());

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas,
        };
    }
}
