using System.Runtime.InteropServices;
using System.Text;

namespace CoQuo;

internal sealed record QuotaTooltipRow(int Remaining, bool Stale, TimeBar? Bar, string Countdown);
internal sealed record QuotaTooltipContent(QuotaTooltipRow? FiveHour, QuotaTooltipRow? Weekly, string? Status, int IconSize);

/// <summary>A hover-only window anchored to one notification icon.</summary>
internal sealed class QuotaTooltip : Form
{
    private const int HoverCheckInterval = 100;
    private const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    private readonly NotifyIcon _tray;
    private readonly IconProvider _provider;
    private readonly Func<QuotaTooltipContent> _readContent;
    private readonly System.Windows.Forms.Timer _hoverTimer = new();
    private QuotaTooltipContent _content = new(null, null, null, TrayIconSizeClient.StandardSize);
    private Rectangle _anchor;
    private Font _textFont = new(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 12, GraphicsUnit.Pixel);
    private int _dpi = 96;
    private int _iconLeft;
    private int _iconWidth;
    private int _iconHeight;
    private bool _inOverflow;

    public QuotaTooltip(NotifyIcon tray, IconProvider provider, Func<QuotaTooltipContent> readContent)
    {
        _tray = tray;
        _provider = provider;
        _readContent = readContent;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = SystemInformation.HighContrast ? SystemColors.Info : Color.FromArgb(38, 38, 38);
        ForeColor = SystemInformation.HighContrast ? SystemColors.InfoText : Color.White;

        _tray.MouseMove += BeginHover;
        _tray.MouseDown += EndHover;
        _hoverTimer.Tick += CheckHover;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int toolWindow = 0x00000080;
            const int noActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= toolWindow | noActivate;
            return parameters;
        }
    }

    private void BeginHover(object? sender, MouseEventArgs e)
    {
        if (_hoverTimer.Enabled || !TrackAnchor(Cursor.Position)) return;

        _hoverTimer.Interval = Math.Max(1, SystemInformation.MouseHoverTime);
        _hoverTimer.Start();
    }

    private void EndHover(object? sender, MouseEventArgs e) => Dismiss();

    private void CheckHover(object? sender, EventArgs e)
    {
        var cursor = Cursor.Position;
        var previousAnchor = _anchor;
        // Explorer can reflow the tray or close the overflow while the cursor stays still.
        if (!previousAnchor.Contains(cursor) || !TrackAnchor(cursor))
        {
            Dismiss();
            return;
        }
        if (Visible && _anchor == previousAnchor) return;

        var trayWindow = WindowFromPoint(cursor);
        _inOverflow = IsOverflowWindow(trayWindow);
        int dpi = (int)GetDpiForWindow(trayWindow);
        if (dpi > 0 && dpi != _dpi)
        {
            _dpi = dpi;
            _textFont.Dispose();
            _textFont = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, Scale(12), GraphicsUnit.Pixel);
        }
        _content = _readContent();
        PositionWindow();
        if (Visible)
        {
            Invalidate();
        }
        else
        {
            _hoverTimer.Interval = HoverCheckInterval;
            Show();
        }
    }

    private bool TrackAnchor(Point cursor) =>
        _tray.Visible && _tray.ContextMenuStrip?.Visible != true &&
        TryGetIconBounds(_tray, out _anchor) && _anchor.Contains(cursor);

    public void Dismiss()
    {
        _hoverTimer.Stop();
        if (Visible) Hide();
    }

    public void RefreshContent()
    {
        if (!Visible) return;
        var content = _readContent();
        if (content == _content) return;
        _content = content;
        PositionWindow();
        Invalidate();
    }

    private int Scale(int pixels) => (pixels * _dpi + 48) / 96;

    private int Measure(string text) => TextRenderer.MeasureText(text, _textFont, Size.Empty, TextFlags).Width;

    private void PositionWindow()
    {
        var screen = Screen.FromRectangle(_anchor);
        var work = screen.WorkingArea;
        int padding = Scale(8), gap = Scale(7);
        int rows = (_content.FiveHour is null ? 0 : 1) + (_content.Weekly is null ? 0 : 1);
        bool hasBar = _content.FiveHour?.Bar is not null || _content.Weekly?.Bar is not null;
        // The native resize hook skips the overflow, where Explorer draws the original square HICON.
        _iconHeight = Scale(_inOverflow ? TrayIconSizeClient.StandardSize : _content.IconSize);
        _iconWidth = hasBar && !_inOverflow
            ? (int)Math.Round(_iconHeight * TrayIconPainter.TimeBarAspect)
            : _iconHeight;
        int beforeIcon = padding + Math.Max(Measure("5h"), Measure("7d")) + gap;
        int afterIcon = gap + Math.Max(Measure(_content.FiveHour?.Countdown ?? ""), Measure(_content.Weekly?.Countdown ?? "")) + padding;
        int statusWidth = _content.Status is { } status ? Math.Min(Measure(status), Scale(320)) + 2 * padding : 0;
        int rowWidth = rows > 0 ? beforeIcon + _iconWidth + afterIcon : 0;
        int width = Math.Min(work.Width, Math.Max(rowWidth, statusWidth));
        int height = 2 * padding + rows * _iconHeight + (rows == 2 ? gap : 0);
        if (_content.Status is not null) height += Scale(16) + (rows > 0 ? gap : 0);

        int anchorX = _anchor.Left + _anchor.Width / 2;
        int left = Math.Clamp(anchorX - _iconWidth / 2 - beforeIcon, work.Left, work.Right - width);
        _iconLeft = Math.Clamp(anchorX - _iconWidth / 2 - left, beforeIcon, Math.Max(beforeIcon, width - _iconWidth - afterIcon));
        int top = Math.Min(_anchor.Top - gap, work.Bottom) - height;
        if (top < work.Top) top = Math.Max(_anchor.Bottom + gap, work.Top);
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        Bounds = new Rectangle(left, top, width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int padding = Scale(8), gap = Scale(7), y = padding;
        DrawRow("5h", _content.FiveHour);
        DrawRow("7d", _content.Weekly);
        if (_content.Status is { } status)
        {
            var bounds = new Rectangle(padding, y, ClientSize.Width - 2 * padding, Scale(16));
            TextRenderer.DrawText(e.Graphics, status, _textFont, bounds, ForeColor, TextFlags | TextFormatFlags.EndEllipsis);
        }

        void DrawRow(string label, QuotaTooltipRow? row)
        {
            if (row is null) return;
            var labelBounds = new Rectangle(padding, y, _iconLeft - padding - gap, _iconHeight);
            TextRenderer.DrawText(e.Graphics, label, _textFont, labelBounds, ForeColor, TextFlags | TextFormatFlags.Right);
            var iconBounds = new Rectangle(_iconLeft, y, _iconWidth, _iconHeight);
            TrayIconPainter.Paint(e.Graphics, iconBounds, row.Remaining, row.Stale, _provider, row.Bar);
            int textX = iconBounds.Right + gap;
            var textBounds = new Rectangle(textX, y, ClientSize.Width - textX - padding, _iconHeight);
            TextRenderer.DrawText(e.Graphics, row.Countdown, _textFont, textBounds, ForeColor, TextFlags);
            y += _iconHeight + gap;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.MouseMove -= BeginHover;
            _tray.MouseDown -= EndHover;
            _hoverTimer.Dispose();
            _textFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private static bool TryGetIconBounds(NotifyIcon tray, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!TrayIconSizeClient.TryGetIdentity(tray, out var hwnd, out var id)) return false;
        var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = hwnd, Id = id };
        if (Shell_NotifyIconGetRect(in identifier, out var rect) != 0) return false;
        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(in NotifyIconIdentifier identifier, out NativeRect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr WindowFromPoint(Point point);

    private static bool IsOverflowWindow(IntPtr hwnd)
    {
        const uint rootAncestor = 2;
        var root = GetAncestor(hwnd, rootAncestor);
        var name = new StringBuilder(64);
        GetClassNameW(root, name, name.Capacity);
        return name.ToString() is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int capacity);
}
