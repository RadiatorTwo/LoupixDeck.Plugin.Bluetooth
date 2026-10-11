namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>
/// Keeps a value text on one line of a host text layer. The host wraps text that is wider than the
/// layer's box, which pushes a long device name into the ring above it, and the plugin cannot
/// measure on the host's canvas. The host draws with Liberation Sans, which has the same advance
/// widths as Arial, so the width is computed from Arial's metrics instead.
/// </summary>
internal static class TextFit
{
    /// <summary>Advance widths of ASCII 32 to 126 in thousandths of the font size (Arial, regular).</summary>
    private static readonly short[] AsciiWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // space to /
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, // 0 to ?
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, // @ to O
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, // P to _
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, // ` to o
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584        // p to ~
    ];

    private const string Ellipsis = "…";

    /// <summary>The share of the box text may use; the host wraps a little before the exact edge.</summary>
    private const double Margin = 0.94;

    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it fits on one line of a box
    /// <paramref name="boxWidth"/> wide at <paramref name="textSize"/>, else shortened with an ellipsis.
    /// </summary>
    public static string Line(string text, int textSize, int boxWidth)
    {
        double available = boxWidth * Margin;

        if (Width(text, textSize) <= available)
        {
            return text;
        }

        string shortened = text;

        while (shortened.Length > 1 && Width(shortened.TrimEnd() + Ellipsis, textSize) > available)
        {
            shortened = shortened[..^1];
        }

        return shortened.TrimEnd() + Ellipsis;
    }

    /// <summary>The width of a single line in pixels.</summary>
    public static double Width(string text, int textSize)
    {
        double units = 0;

        foreach (char c in text)
        {
            units += c switch
            {
                >= ' ' and <= '~' => AsciiWidths[c - ' '],
                '…' => 1000,
                '·' => 278,
                '●' or '○' or '◐' => 604,
                _ => 556 // a typical letter; accented Latin letters share their base letter's width
            };
        }

        return units * textSize / 1000.0;
    }
}
