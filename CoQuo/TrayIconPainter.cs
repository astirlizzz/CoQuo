using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CoQuo;

internal enum IconProvider { Codex, Claude }

internal static class TrayIconPainter
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Condensed digits stay tall inside the same width; Segoe UI is the fallback on systems without Bahnschrift.
    private static readonly (string Family, FontStyle Style)[] LabelFonts =
    {
        ("Bahnschrift SemiBold Condensed", FontStyle.Regular),
        ("Segoe UI", FontStyle.Bold)
    };

    public static Icon Create(int? remaining, bool stale, IconProvider provider)
    {
        using var image = Render(remaining, stale, provider);
        var nativeHandle = image.GetHicon();
        try { return (Icon)Icon.FromHandle(nativeHandle).Clone(); }
        finally { DestroyIcon(nativeHandle); }
    }

    internal static Bitmap Render(int? remaining, bool stale, IconProvider provider)
    {
        // Render large and let Windows scale to the taskbar's actual size.
        const int size = 128;
        var image = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        var color = stale || remaining is null ? Color.FromArgb(135, 148, 160)
                  : remaining > 25 ? Color.FromArgb(71, 210, 172)
                  : remaining > 10 ? Color.FromArgb(255, 165, 0)
                  : Color.FromArgb(250, 128, 114);
        // Same silhouette for both sources (maximum room for digits); the fill tells them apart.
        var fill = provider == IconProvider.Codex ? Color.FromArgb(26, 35, 39)
                 : stale || remaining is null ? Color.FromArgb(110, 74, 62)
                 : Color.FromArgb(184, 90, 58);
        var shade = provider == IconProvider.Codex ? fill : Color.FromArgb(40, 22, 16);
        using var background = new SolidBrush(fill);
        using var outline = new Pen(color, 6) { LineJoin = LineJoin.Round };
        using var contour = CreateContour();
        g.FillPath(background, contour);
        if (provider == IconProvider.Claude)
        {
            // Dark gap ring keeps amber/red status strokes from blending into the terracotta fill.
            using var gap = new Pen(shade, 13) { LineJoin = LineJoin.Round };
            g.DrawPath(gap, contour);
        }
        g.DrawPath(outline, contour);
        // Three digits are cramped at tray size; a full allowance reads better as a check mark.
        if (remaining == 100) DrawCheck(g, shade);
        else DrawLabel(g, remaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", shade);
        return image;
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

    private static FontFamily LabelFamily(out FontStyle style)
    {
        foreach (var (name, fontStyle) in LabelFonts)
        {
            try
            {
                var family = new FontFamily(name);
                if (family.IsStyleAvailable(fontStyle)) { style = fontStyle; return family; }
                family.Dispose();
            }
            catch (ArgumentException) { /* font not installed */ }
        }
        style = FontStyle.Bold;
        return new FontFamily(GenericFontFamilies.SansSerif);
    }

    private static void DrawLabel(Graphics g, string label, Color halo)
    {
        using var font = LabelFamily(out var style);
        // Winding: variable fonts such as Bahnschrift have overlapping glyph contours ("4").
        using var text = new GraphicsPath(FillMode.Winding);
        text.AddString(label, font, (int)style, 72, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = text.GetBounds();
        // Fit actual glyphs instead of font line spacing: large digits, including 100.
        const float width = 90;
        float height = label.Length == 1 ? 76 : 74;
        float scaleY = height / bounds.Height;
        // Slightly condense wide labels so their height stays readable in the tray.
        float scaleX = Math.Min(width / bounds.Width, scaleY);
        using var transform = new Matrix(scaleX, 0, 0, scaleY,
            64 - (bounds.Left + bounds.Width / 2) * scaleX,
            64 - (bounds.Top + bounds.Height / 2) * scaleY);
        text.Transform(transform);
        // A dark halo separates the digits from the outline wherever they overlap it.
        using var haloPen = new Pen(halo, 9) { LineJoin = LineJoin.Round };
        g.DrawPath(haloPen, text);
        g.FillPath(Brushes.White, text);
    }

    private static void DrawCheck(Graphics g, Color halo)
    {
        var points = new[] { new PointF(33, 66), new PointF(55, 88), new PointF(95, 42) };
        using var haloPen = new Pen(halo, 27) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var pen = new Pen(Color.White, 17) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(haloPen, points);
        g.DrawLines(pen, points);
    }
}
