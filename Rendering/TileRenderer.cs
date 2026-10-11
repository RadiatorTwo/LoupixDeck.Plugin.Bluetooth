using System.Diagnostics;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Rendering;

/// <summary>The tiles the plugin draws itself: an icon over up to three lines that always fit.</summary>
internal static class TileRenderer
{
    public static readonly PluginColor ConnectedColor = PluginColor.FromRgb(0x1B, 0x5E, 0x20);
    public static readonly PluginColor DisconnectedColor = PluginColor.FromRgb(0x1A, 0x23, 0x3A);
    public static readonly PluginColor UnpairedColor = PluginColor.FromRgb(0x22, 0x22, 0x22);
    public static readonly PluginColor FailedColor = PluginColor.FromRgb(0x70, 0x20, 0x20);
    public static readonly PluginColor BusyColor = PluginColor.FromRgb(0x70, 0x55, 0x10);
    public static readonly PluginColor UnavailableColor = PluginColor.FromRgb(0x20, 0x20, 0x20);
    public static readonly PluginColor DiscoveryColor = PluginColor.FromRgb(0x0D, 0x3B, 0x66);
    public static readonly PluginColor NavigationColor = PluginColor.FromRgb(0x30, 0x30, 0x30);
    public static readonly PluginColor DimTextColor = PluginColor.FromRgb(0xB0, 0xB0, 0xB0);
    public static readonly PluginColor SubtitleColor = PluginColor.FromRgb(0xE6, 0xE6, 0xE6);
    public static readonly PluginColor LowBatteryColor = PluginColor.FromRgb(0xFF, 0x8A, 0x65);
    public static readonly PluginColor ConfirmColor = PluginColor.FromRgb(0x2E, 0x7D, 0x32);
    public static readonly PluginColor CancelColor = PluginColor.FromRgb(0x8E, 0x24, 0x24);

    /// <summary>Scroll speed of a line that is too long, in pixels of a 90 px key per second.</summary>
    private const double ScrollSpeed = 30;

    /// <summary>How long a scrolling line rests at its start before it moves.</summary>
    private const double ScrollPauseMs = 1500;

    /// <summary>The gap between the end of a scrolling line and its next pass, in pixels of a 90 px key.</summary>
    private const double ScrollGap = 24;

    /// <summary>One clock for every scrolling line, so lines on neighbouring tiles move together.</summary>
    private static readonly Stopwatch ScrollClock = Stopwatch.StartNew();

    /// <summary>
    /// A device found by a discovery: an icon over a large name and a second line. A line that does
    /// not fit scrolls instead of being shrunk, so the whole name stays readable; the caller has to
    /// repaint the tile regularly for it to move.
    /// </summary>
    public static void DrawScrolling(
        IRenderCanvas canvas,
        PluginColor back,
        string symbol,
        PluginColor symbolColor,
        string title,
        string subtitle)
    {
        int size = Math.Min(canvas.Width, canvas.Height);
        double k = size / 90.0;
        int Px(double design) => (int)Math.Round(design * k);

        canvas.Clear(back);

        int icon = Px(26);
        canvas.DrawSymbol(symbol, (canvas.Width - icon) / 2, Px(5), icon, icon, symbolColor);

        ScrollLine(canvas, title, Px(35), Px(22), PluginColor.White, (float)(16 * k), k, bold: true);
        ScrollLine(canvas, subtitle, Px(61), Px(19), SubtitleColor, (float)(13 * k), k, bold: false);
    }

    /// <summary>
    /// Draws one line: centred when it fits, otherwise as a marquee that rests at its start, then
    /// runs through and comes round again. The canvas ends at the tile's edge, which clips it.
    /// </summary>
    private static void ScrollLine(IRenderCanvas canvas, string text, int y, int height, PluginColor color, float fontSize, double k, bool bold)
    {
        int margin = (int)Math.Round(3 * k);
        int available = canvas.Width - (2 * margin);
        float width = canvas.MeasureText(text, fontSize, bold);

        if (width <= available)
        {
            canvas.DrawText(text, 0, y, canvas.Width, height, color, fontSize, bold: bold);
            return;
        }

        double period = width + (ScrollGap * k);
        double speed = ScrollSpeed * k;
        double cycleMs = ScrollPauseMs + (period / speed * 1000);
        double t = ScrollClock.Elapsed.TotalMilliseconds % cycleMs;
        double offset = t < ScrollPauseMs ? 0 : (t - ScrollPauseMs) * speed / 1000;

        // The box is wider than the text, so the canvas never wraps it.
        int boxWidth = (int)Math.Ceiling(width) + (int)Math.Round(8 * k);
        int x = margin - (int)Math.Round(offset);
        canvas.DrawText(text, x, y, boxWidth, height, color, fontSize, TextHAlign.Left, TextVAlign.Middle, bold);
        canvas.DrawText(text, x + (int)Math.Round(period), y, boxWidth, height, color, fontSize, TextHAlign.Left, TextVAlign.Middle, bold);
    }

    /// <summary>
    /// Tiles drawn here must not set <see cref="FolderEntry.Text"/>: the host draws the text on top
    /// of <see cref="FolderEntry.Render"/>, so both would show.
    /// <para>
    /// An icon over up to three lines: the name, the state and the battery level. Laid out for a
    /// 90 px tile and scaled onto the real one.
    /// </para>
    /// </summary>
    public static void Draw(
        IRenderCanvas canvas,
        PluginColor back,
        string symbol,
        PluginColor symbolColor,
        string title,
        string? subtitle,
        (string Text, PluginColor Color)? battery)
    {
        int size = Math.Min(canvas.Width, canvas.Height);
        double k = size / 90.0;
        int Px(double design) => (int)Math.Round(design * k);
        float Font(double design) => (float)(design * k);

        canvas.Clear(back);

        // The fewer lines a tile has, the more room goes to the icon and the text.
        TileLayout layout = (subtitle, battery) switch
        {
            (null, null) => new TileLayout(Icon: 36, IconTop: 8, TitleTop: 50, TitleSize: 15, LineTop: 0, LineSize: 0, BatteryTop: 0),
            (_, null) or (null, _) => new TileLayout(Icon: 30, IconTop: 5, TitleTop: 41, TitleSize: 14, LineTop: 62, LineSize: 14, BatteryTop: 62),
            _ => new TileLayout(Icon: 24, IconTop: 3, TitleTop: 32, TitleSize: 13, LineTop: 50, LineSize: 12, BatteryTop: 68)
        };

        int icon = Px(layout.Icon);
        canvas.DrawSymbol(symbol, (canvas.Width - icon) / 2, Px(layout.IconTop), icon, icon, symbolColor);

        int boxWidth = Px(88);
        int left = (canvas.Width - boxWidth) / 2;
        DrawLine(canvas, title, left, Px(layout.TitleTop), boxWidth, Px(layout.TitleSize + 6), PluginColor.White, Font(layout.TitleSize), Font(10), bold: true);

        if (subtitle is not null)
        {
            DrawLine(canvas, subtitle, left, Px(layout.LineTop), boxWidth, Px(layout.LineSize + 6), SubtitleColor, Font(layout.LineSize), Font(9), bold: false);
        }

        if (battery is { } level)
        {
            DrawLine(canvas, level.Text, left, Px(layout.BatteryTop), boxWidth, Px(20), level.Color, Font(15), Font(15), bold: true);
        }
    }

    /// <summary>
    /// Draws text on exactly one line. The canvas wraps text that is wider than its box, which
    /// pushed a long name into the line below. A line that does not fit is drawn smaller, down to
    /// <paramref name="minimumSize"/>, and only then shortened with an ellipsis.
    /// </summary>
    private static void DrawLine(
        IRenderCanvas canvas,
        string text,
        int x,
        int y,
        int width,
        int height,
        PluginColor color,
        float size,
        float minimumSize,
        bool bold)
    {
        // A small margin: the measured width and the wrapping decision do not match to the pixel.
        float available = width * 0.94f;

        while (size > minimumSize && canvas.MeasureText(text, size, bold) > available)
        {
            size = Math.Max(minimumSize, size - 0.5f);
        }

        if (canvas.MeasureText(text, size, bold) > available)
        {
            string shortened = text;

            while (shortened.Length > 1 && canvas.MeasureText(shortened.TrimEnd() + "…", size, bold) > available)
            {
                shortened = shortened[..^1];
            }

            text = shortened.TrimEnd() + "…";
        }

        canvas.DrawText(text, x, y, width, height, color, size, bold: bold);
    }

    /// <summary>Positions and sizes of a tile, in pixels of a 90 px key.</summary>
    private readonly record struct TileLayout(int Icon, int IconTop, int TitleTop, int TitleSize, int LineTop, int LineSize, int BatteryTop);
}
