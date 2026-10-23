using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CoQuo;

internal static class TrayIconPainter
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create(int? remaining, bool stale)
    {
        // Render large and let Windows scale to the taskbar's actual size.
        const int size = 128;
        using var image = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var color = stale || remaining is null ? Color.FromArgb(135, 148, 160)
                      : remaining > 25 ? Color.FromArgb(71, 210, 172)
                      : remaining > 10 ? Color.FromArgb(255, 165, 0)
                      : Color.FromArgb(250, 128, 114);
            using var background = new SolidBrush(Color.FromArgb(26, 35, 39));
            using var outline = new Pen(color, 6) { LineJoin = LineJoin.Round };
            using var contour = CreateContour();
            g.FillPath(background, contour);
            g.DrawPath(outline, contour);
            var label = remaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            DrawLabel(g, label);
        }
        var nativeHandle = image.GetHicon();
        try { return (Icon)Icon.FromHandle(nativeHandle).Clone(); }
        finally { DestroyIcon(nativeHandle); }
    }

    private static GraphicsPath CreateContour()
    {
        // Six soft lobes recall the ChatGPT silhouette while leaving the center clear.
        var points = new PointF[120];
        for (int i = 0; i < points.Length; i++)
        {
            double angle = 2 * Math.PI * i / points.Length;
            double radius = 56 + 4.5 * Math.Cos(6 * angle);
            points[i] = new PointF((float)(64 + radius * Math.Cos(angle)),
                (float)(64 + radius * Math.Sin(angle)));
        }
        var path = new GraphicsPath();
        path.AddClosedCurve(points);
        // The six-lobed curve is naturally shorter than it is wide. Fill the
        // available tray square in both directions, leaving room for the stroke.
        var bounds = path.GetBounds();
        const float inset = 3.5f;
        const float extent = 128 - 2 * inset;
        float scaleX = extent / bounds.Width;
        float scaleY = extent / bounds.Height;
        using var transform = new Matrix(scaleX, 0, 0, scaleY,
            inset - bounds.Left * scaleX, inset - bounds.Top * scaleY);
        path.Transform(transform);
        return path;
    }

    private static void DrawLabel(Graphics g, string label)
    {
        using var font = new FontFamily("Segoe UI");
        using var text = new GraphicsPath();
        text.AddString(label, font, (int)FontStyle.Bold, 72, PointF.Empty,
            StringFormat.GenericTypographic);
        var bounds = text.GetBounds();
        // Fit actual glyphs instead of font line spacing: large digits, including 100.
        float width = label.Length == 3 ? 94 : 88;
        float height = label.Length == 3 ? 54 : label.Length == 1 ? 66 : 62;
        float scaleY = height / bounds.Height;
        // Slightly condense wide labels so their height stays readable in the tray.
        float scaleX = Math.Min(width / bounds.Width, scaleY);
        using var transform = new Matrix(scaleX, 0, 0, scaleY,
            64 - (bounds.Left + bounds.Width / 2) * scaleX,
            64 - (bounds.Top + bounds.Height / 2) * scaleY);
        text.Transform(transform);
        g.FillPath(Brushes.White, text);
    }
}
