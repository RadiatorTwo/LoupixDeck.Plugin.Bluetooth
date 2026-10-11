using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth;

/// <summary>
/// Typed access to the plugin's settings. The keys are persisted in
/// <c>plugins/bluetooth/settings.json</c> and must not change: a file written by an older version
/// has to keep loading unchanged. Every key is optional and falls back to its default.
/// </summary>
internal sealed class BluetoothSettings(IPluginSettings settings)
{
    public const string PreferredAdapterKey = "adapter:preferred";
    public const string PairedOnlyKey = "folder:pairedOnly";
    public const string HiddenKey = "devices:hidden";
    public const string FavoritesKey = "devices:favorites";
    public const string DiscoveryDurationKey = "discovery:durationSeconds";
    public const string ConnectTimeoutKey = "connect:timeoutSeconds";
    public const string RetryOnceKey = "connect:retryOnce";
    public const string AutoTrustKey = "connect:autoTrust";

    public const bool DefaultPairedOnly = true;
    public const int DefaultDiscoveryDurationSeconds = 30;
    public const int DefaultConnectTimeoutSeconds = 20;
    public const bool DefaultRetryOnce = false;
    public const bool DefaultAutoTrust = false;

    /// <summary>How many favorites the slot commands can address.</summary>
    public const int FavoriteSlotCount = 10;

    private const int MinimumDiscoveryDurationSeconds = 5;
    private const int MaximumDiscoveryDurationSeconds = 300;
    private const int MinimumConnectTimeoutSeconds = 5;
    private const int MaximumConnectTimeoutSeconds = 120;

    private static readonly char[] ListSeparators = [',', ';', ' ', '\n', '\r'];

    /// <summary>The adapter used when a button names none, by address. Empty takes the first adapter.</summary>
    public string PreferredAdapter => (settings.Get(PreferredAdapterKey, string.Empty) ?? string.Empty).Trim();

    /// <summary>On: the folder lists only paired devices. Off: devices found by discovery show too.</summary>
    public bool PairedOnly => settings.Get(PairedOnlyKey, DefaultPairedOnly);

    /// <summary>Devices kept out of the menu and the folder, by device id or address.</summary>
    public IReadOnlyList<string> Hidden => ParseList(settings.Get(HiddenKey, string.Empty));

    /// <summary>The devices behind the slot commands, in slot order.</summary>
    public IReadOnlyList<string> Favorites => ParseList(settings.Get(FavoritesKey, string.Empty));

    public TimeSpan DiscoveryDuration => TimeSpan.FromSeconds(
        ReadClamped(DiscoveryDurationKey, DefaultDiscoveryDurationSeconds, MinimumDiscoveryDurationSeconds, MaximumDiscoveryDurationSeconds));

    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(
        ReadClamped(ConnectTimeoutKey, DefaultConnectTimeoutSeconds, MinimumConnectTimeoutSeconds, MaximumConnectTimeoutSeconds));

    /// <summary>On: a failed connection is tried a second time before it counts as failed.</summary>
    public bool RetryOnce => settings.Get(RetryOnceKey, DefaultRetryOnce);

    /// <summary>On: a device the plugin connected successfully is marked trusted.</summary>
    public bool AutoTrust => settings.Get(AutoTrustKey, DefaultAutoTrust);

    /// <summary>The favorite behind a slot command, or null when the slot is empty.</summary>
    public string? FavoriteAt(int slotIndex)
    {
        IReadOnlyList<string> favorites = Favorites;
        return slotIndex >= 0 && slotIndex < favorites.Count ? favorites[slotIndex] : null;
    }

    public bool IsHidden(string stableId, string address) =>
        Hidden.Any(entry => string.Equals(entry, stableId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry, address, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a device to the favorites. Returns false when it was already there.</summary>
    public bool AddFavorite(string deviceId)
    {
        List<string> favorites = [.. Favorites];

        if (favorites.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        favorites.Add(deviceId);
        WriteList(FavoritesKey, favorites);
        return true;
    }

    /// <summary>Removes a device from the favorites. Returns false when it was not there.</summary>
    public bool RemoveFavorite(string deviceId)
    {
        List<string> favorites = [.. Favorites];

        if (favorites.RemoveAll(entry => string.Equals(entry, deviceId, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        WriteList(FavoritesKey, favorites);
        return true;
    }

    private void WriteList(string key, IEnumerable<string> entries)
    {
        settings.Set(key, string.Join(", ", entries));
        settings.Save();
    }

    /// <summary>
    /// The SDK has no list editor, so a list is a single text field; commas, semicolons, spaces and
    /// line breaks all separate entries (an id never contains any of them). Blanks and duplicates
    /// are dropped.
    /// </summary>
    private static IReadOnlyList<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<string> entries = [];

        foreach (string part in value.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entries.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(part);
            }
        }

        return entries;
    }

    /// <summary>
    /// Reads a number setting and keeps it inside its supported range. The host stores it as a
    /// JSON integer and falls back to the default when a stored value cannot be read as one.
    /// </summary>
    private int ReadClamped(string key, int fallback, int minimum, int maximum) =>
        (int)Math.Clamp(settings.Get(key, (long)fallback), minimum, maximum);
}
