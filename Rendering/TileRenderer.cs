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
