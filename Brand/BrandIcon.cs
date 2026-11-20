using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoafEar.Brand;

/// <summary>
/// 品牌图形：一只面包（设计稿 SVG 的路径与椭圆原样搬过来，viewBox 841.89×595.28）。
/// 面包体是米黄面加上深灰描边，三条割口是红色椭圆。
/// exe 图标、窗口图标、托盘图标都从这一份矢量数据渲染，任意尺寸都不会糊。
/// </summary>
internal static class BrandIcon
{
    /// <summary>主色：面包外壳的米黄，与设计稿的 #E2BB60 一致。</summary>
    public static readonly Color Accent = Color.FromRgb(0xE2, 0xBB, 0x60);

    private static readonly Color Slit = Color.FromRgb(0xDE, 0x59, 0x36);
    private static readonly Color Outline = Color.FromRgb(0x59, 0x57, 0x57);

    private static readonly Brush CrustBrush = MakeBrush(Accent);
    private static readonly Brush SlitBrush = MakeBrush(Slit);
    private static readonly Brush OutlineBrush = MakeBrush(Outline);

    /// <summary>面包体：一条闭合路径画出的扁椭圆，底部比顶部更平。</summary>
    private const string LoafPath =
        "M355.33,325.53c0-23.73-35.82-42.97-80-42.97s-80,19.24-80,42.97" +
        "s35.82,29.81,80,29.81S355.33,349.26,355.33,325.53z";

    private static readonly Layer[] Layers = BuildLayers();

    private static readonly Rect Bounds = ComputeBounds();

    private sealed class Layer
    {
        public required Geometry Geometry;
        public required Brush Fill;
        public required Pen Stroke;

        /// <summary>三条割口。尺寸太小的时候它们连描边一起不到 2 px，要特殊处理。</summary>
        public bool IsSlit;
    }

    private static Layer[] BuildLayers()
    {
        return new[]
        {
            new Layer
            {
                Geometry = Freeze(Geometry.Parse(LoafPath)),
                Fill = CrustBrush,
                Stroke = MakePen(OutlineBrush, 1.4173),
            },
            new Layer
            {
                Geometry = Freeze(new EllipseGeometry(new Point(275.33, 309.33), 8, 25.17)),
                Fill = SlitBrush,
                Stroke = MakePen(OutlineBrush, 1.2583),
                IsSlit = true,
            },
            new Layer
            {
                Geometry = Freeze(new EllipseGeometry(new Point(238.21, 309.33), 6.2, 19.5)),
                Fill = SlitBrush,
                Stroke = MakePen(OutlineBrush, 1.2583),
                IsSlit = true,
            },
            new Layer
            {
                Geometry = Freeze(new EllipseGeometry(new Point(312.45, 309.33), 6.2, 19.5)),
                Fill = SlitBrush,
                Stroke = MakePen(OutlineBrush, 1.2583),
                IsSlit = true,
            },
        };
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>所有图层（含描边向外的一半）的并集，用来算居中缩放。</summary>
    private static Rect ComputeBounds()
    {
        var rect = Rect.Empty;

        foreach (var layer in Layers)
        {
            var bounds = layer.Geometry.Bounds;
            var grow = layer.Stroke.Thickness / 2;
            bounds.Inflate(grow, grow);
            rect.Union(bounds);
        }

        return rect;
    }

    /// <summary>
    /// 按正方形画布渲染一枚图标，图形等比缩放后居中。
    /// 所有尺寸都用同一套图形（面包 + 三条割口），只在小尺寸上给描边一个最小设备像素宽度，
    /// 否则缩到 16 px 时 1.4173 的描边只剩 0.12 px，轮廓直接消失、看起来和任务栏里的大图标不是同一个图标。
    /// </summary>
    public static BitmapSource Render(int size)
    {
        // 描边的最小设备像素宽度：越小越需要托底，再往上就让它按比例缩
        var minStrokePx = size <= 20 ? 0.75 : size <= 30 ? 0.6 : size <= 48 ? 0.45 : 0;

        // 描边加粗后会向内外各长出一半，内边距要按加粗量留够，不然外沿会被画布切掉
        var pad = Math.Max(size * 0.035, minStrokePx * 0.5 + 0.4);

        var scale = Math.Min(
            (size - pad * 2) / Bounds.Width,
            (size - pad * 2) / Bounds.Height);

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(
                scale, 0, 0, scale,
                (size - Bounds.Width * scale) / 2 - Bounds.X * scale,
                (size - Bounds.Height * scale) / 2 - Bounds.Y * scale));

            foreach (var layer in Layers)
            {
                // 24 px 及以下的割口只有 2 px 宽，再套 0.6 px 描边红色就被吃光了，
                // 这几档只留红色色块，形状和位置不变
                if (layer.IsSlit && size <= 24)
                {
                    dc.DrawGeometry(layer.Fill, null, layer.Geometry);
                    continue;
                }

                var thickness = Math.Max(layer.Stroke.Thickness, minStrokePx / scale);

                var pen = new Pen(layer.Stroke.Brush, thickness)
                {
                    LineJoin = PenLineJoin.Round,
                };
                pen.Freeze();

                dc.DrawGeometry(layer.Fill, pen, layer.Geometry);
            }

            dc.Pop();
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 写多尺寸 .ico。每个尺寸存成 PNG 帧（Vista 起系统原生支持），
    /// 16/20/24 给托盘和任务栏，256 给资源管理器大图标视图。
    /// </summary>
    public static void WriteIco(string path, IReadOnlyList<int> sizes)
    {
        var frames = new List<byte[]>(sizes.Count);

        foreach (var size in sizes)
            frames.Add(EncodePng(Render(size)));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);            // 保留
        writer.Write((ushort)1);            // 1 = 图标
        writer.Write((ushort)sizes.Count);

        var offset = 6 + 16 * sizes.Count;

        for (var i = 0; i < sizes.Count; i++)
        {
            var size = sizes[i];
            var dimension = (byte)(size >= 256 ? 0 : size);   // 0 表示 256

            writer.Write(dimension);        // 宽
            writer.Write(dimension);        // 高
            writer.Write((byte)0);          // 调色板数量
            writer.Write((byte)0);          // 保留
            writer.Write((ushort)1);        // 色彩平面
            writer.Write((ushort)32);       // 位深
            writer.Write(frames[i].Length);
            writer.Write(offset);

            offset += frames[i].Length;
        }

        foreach (var frame in frames)
            writer.Write(frame);
    }
}
