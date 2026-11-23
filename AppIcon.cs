using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AiCallAssistant;

/// <summary>
/// The app icon: a speech bubble with an AI sparkle. Drawn as vectors in a 32×32 coordinate space,
/// so it stays sharp at any size and DPI.
/// </summary>
static class AppIcon
{
    static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
    static Icon? _icon;

    /// <summary>Multi-size icon for windows (Windows picks the frame that fits the DPI).</summary>
    public static Icon Icon => _icon ??= new Icon(new MemoryStream(CreateIco()));

    /// <summary>Paints the icon in 32×32 units; scale the graphics for other sizes.</summary>
    public static void Paint(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var bubble = new GraphicsPath();
        const float l = 2, t = 3, r = 30, b = 24, rad = 7;
        bubble.AddArc(l, t, rad * 2, rad * 2, 180, 90);
        bubble.AddArc(r - rad * 2, t, rad * 2, rad * 2, 270, 90);
        bubble.AddArc(r - rad * 2, b - rad * 2, rad * 2, rad * 2, 0, 90);
        bubble.AddLine(14, b, 10, 30);   // tail
        bubble.AddLine(10, 30, 8, b);
        bubble.AddArc(l, b - rad * 2, rad * 2, rad * 2, 90, 90);
        bubble.CloseFigure();

        using (var fill = new LinearGradientBrush(new PointF(2, 3), new PointF(30, 30),
                   Color.FromArgb(79, 70, 229), Color.FromArgb(147, 51, 234)))
            g.FillPath(fill, bubble);

        using var white = new SolidBrush(Color.White);
        g.FillPolygon(white, Sparkle(14, 13.5f, 7.5f, 1.9f));
        g.FillPolygon(white, Sparkle(23.5f, 8, 3.2f, 0.9f));
    }

    /// <summary>Four-pointed star.</summary>
    static PointF[] Sparkle(float cx, float cy, float outer, float inner)
    {
        var points = new PointF[8];
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.PI / 4 * i - Math.PI / 2;
            float radius = i % 2 == 0 ? outer : inner;
            points[i] = new PointF(cx + (float)(Math.Cos(angle) * radius), cy + (float)(Math.Sin(angle) * radius));
        }
        return points;
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 32f, size / 32f);
        Paint(g);
        return bmp;
    }

    /// <summary>An .ico file with PNG frames of all common sizes (also used to regenerate app.ico).</summary>
    public static byte[] CreateIco()
    {
        var frames = IcoSizes.Select(size =>
        {
            using var bmp = Render(size);
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            return (size, data: png.ToArray());
        }).ToList();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames)
            w.Write(data);
        w.Flush();
        return ms.ToArray();
    }
}
