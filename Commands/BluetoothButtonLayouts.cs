using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>
/// The icon and caption every Bluetooth command brings to a touch button, set by the plugin instead
/// of the host's standard look. The geometry follows the host's own icon-and-caption look, so a
/// Bluetooth button sits next to any other button without looking out of place.
/// </summary>
internal static class BluetoothButtonLayouts
{
    // Material Design Icons code points, one per role so a command and its layout cannot drift apart.
    public const string Group = "\U000F00AF";          // mdi-bluetooth
    public const string Off = "\U000F00B2";            // mdi-bluetooth-off
    public const string Connect = "\U000F00B1";        // mdi-bluetooth-connect
    public const string Transfer = "\U000F00B4";       // mdi-bluetooth-transfer
    public const string Disconnect = "\U000F033A";     // mdi-link-variant-off
    public const string Trust = "\U000F0565";          // mdi-shield-check
    public const string Untrust = "\U000F099C";        // mdi-shield-off-outline
    public const string Discovery = "\U000F0437";      // mdi-radar
    public const string Folder = "\U000F0FB0";         // mdi-devices
    public const string AddFavorite = "\U000F1567";    // mdi-star-plus-outline
    public const string RemoveFavorite = "\U000F1568"; // mdi-star-minus-outline

    // State tints: the icon says at a glance which state a key is in.
    public const string OnColor = "#2196F3";
    public const string OffColor = "#8A8A8A";
    public const string BusyColor = "#FFB300";
    public const string UnavailableColor = "#5A5A5A";

    /// <summary>The command layout of a key whose every state brings its own layers.</summary>
    public static ButtonLayoutDescriptor None { get; } = new() { Mode = ButtonLayoutMode.None };

    // Pixel values for a 90 px key; the host scales them onto the key actually being written.
    private const double IconScale = 0.46;
    private const int IconOffsetY = -11;
    private const int CaptionSize = 14;
    private const int CaptionOffsetY = 26;
    private const int CaptionBoxWidth = 88;
    private const int CaptionBoxHeight = 24;

    // The tall variant leaves room for up to three lines of text: a menu leaf is named
    // "device — action", which the host shows as the caption.
    private const double TallIconScale = 0.32;
    private const int TallIconOffsetY = -24;
    private const int TallCaptionSize = 12;
    private const int TallCaptionOffsetY = 15;
    private const int TallCaptionBoxHeight = 50;

    /// <summary>
    /// Translates a caption into the host's language. Set once the plugin is initialised; the layout
    /// is stored on the button at assignment, so it is the language at that moment that counts.
    /// </summary>
    internal static Func<string, string> Translate { get; set; } = static english => english;

    /// <summary>The icon centred above a caption.</summary>
    /// <param name="glyph">The icon.</param>
    /// <param name="caption">English caption; null keeps the name the host would show.</param>
    /// <param name="tall">Gives the caption two lines and shrinks the icon to make room.</param>
    /// <param name="color">The icon's tint, or null for the standard white.</param>
    public static ButtonLayoutDescriptor IconWithCaption(
        string glyph,
        string? caption,
        bool tall = false,
        string? color = null) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol,
                Glyph = glyph,
                IconScale = tall ? TallIconScale : IconScale,
                OffsetY = tall ? TallIconOffsetY : IconOffsetY,
                Color = color
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text,
                Text = caption == null ? null : Translate(caption),
                TextSize = tall ? TallCaptionSize : CaptionSize,
                OffsetY = tall ? TallCaptionOffsetY : CaptionOffsetY,
                BoxWidth = CaptionBoxWidth,
                BoxHeight = tall ? TallCaptionBoxHeight : CaptionBoxHeight
            }
        ]
    };
}
