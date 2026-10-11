using System.Globalization;

namespace LoupixDeck.Plugin.Bluetooth.Backend.Windows;

/// <summary>
/// Converts Windows' 48-bit Bluetooth addresses to and from the <c>XX:XX:XX:XX:XX:XX</c> form BlueZ
/// uses, so device ids look the same on both platforms.
/// </summary>
internal static class WindowsAddress
{
    public static string Format(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(index => ((address >> ((5 - index) * 8)) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Parses an address with or without separators; null for anything else.</summary>
    public static ulong? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string digits = new([.. text.Where(Uri.IsHexDigit)]);

        return digits.Length == 12 && ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
            ? address
            : null;
    }
}
