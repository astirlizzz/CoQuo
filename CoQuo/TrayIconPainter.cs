using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CoQuo;

internal enum IconProvider { Codex, Claude }

/// <summary>Gauge next to the icon: share of the limit window still ahead, colored by pace.</summary>
internal sealed record TimeBar(double? Level, QuotaPace? Pace);

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

    // With a time bar the picture is this much wider than tall; TrayIconSize stretches the square icon back.
    public const double TimeBarAspect = 1.25;

    public static Icon Create(int? remaining, bool stale, IconProvider provider, TimeBar? bar = null)
    {
        using var image = Render(remaining, stale, provider, bar);
        var nativeHandle = image.GetHicon();
        try { return (Icon)Icon.FromHandle(nativeHandle).Clone(); }
        finally { DestroyIcon(nativeHandle); }
    }

    internal static Bitmap Render(int? remaining, bool stale, IconProvider provider, TimeBar? bar = null)
    {
        // Render large and let Windows scale to the taskbar's actual size.
        const int size = 128;
        var image = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        if (bar is not null)
        {
            // Draw the wide picture squeezed into the square icon; vector drawing keeps it sharp once stretched back.
            g.ScaleTransform((float)(1 / TimeBarAspect), 1);
            DrawTimeBar(g, bar, stale, size * (float)TimeBarAspect);
        }
        DrawIcon(g, remaining, stale, provider);
        return image;
    }

    private static void DrawIcon(Graphics g, int? remaining, bool stale, IconProvider provider)
    {
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
    }

    // Vertical gauge filling from the bottom in the strip right of the 128-unit icon, up to `right`.
    private static void DrawTimeBar(Graphics g, TimeBar bar, bool stale, float right)
    {
        var track = new RectangleF(right - 24, 6, 22, 116);
        using var trackPath = RoundedRect(track, 7);
        // Light enough to show the empty part on a dark taskbar, dark enough for a light one.
        using var trackBrush = new SolidBrush(Color.FromArgb(70, 82, 90));
        g.FillPath(trackBrush, trackPath);
        if (bar.Level is not { } level || level <= 0) return;
        var color = stale || bar.Pace is null ? Color.FromArgb(135, 148, 160)
                  : bar.Pace == QuotaPace.OnTrack ? Color.FromArgb(57, 230, 90)
                  : bar.Pace == QuotaPace.Behind ? Color.FromArgb(255, 170, 0)
                  : Color.FromArgb(255, 60, 60);
        var inner = RectangleF.Inflate(track, -4, -4);
        // A sliver stays visible until the reset itself.
        float height = Math.Max(6, inner.Height * (float)Math.Min(level, 1));
        using var fillPath = RoundedRect(new RectangleF(inner.Left, inner.Bottom - height, inner.Width, height), 4);
        using var fillBrush = new SolidBrush(color);
        g.FillPath(fillBrush, fillPath);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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
