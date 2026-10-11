namespace LoupixDeck.Plugin.Bluetooth.State;

/// <summary>
/// The normalized connection state of a device. BlueZ only knows Connected true or false; the
/// transitions and the failure are kept by the plugin while its own operation runs.
/// </summary>
public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Failed,
    Unavailable
}

/// <summary>Texts and glyphs for <see cref="DeviceConnectionState"/>.</summary>
internal static class DeviceConnectionStates
{
    /// <summary>The English text a button shows for a state; the host translates it.</summary>
    public static string ToEnglishText(DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Connected => "Connected",
        DeviceConnectionState.Connecting => "Connecting",
        DeviceConnectionState.Disconnecting => "Disconnecting",
        DeviceConnectionState.Failed => "Failed",
        DeviceConnectionState.Unavailable => "Unavailable",
        _ => "Disconnected"
    };

    /// <summary>A single character that reads at a glance on a small key.</summary>
    public static string Glyph(DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Connected => "●",
        DeviceConnectionState.Connecting or DeviceConnectionState.Disconnecting => "◐",
        DeviceConnectionState.Failed => "✕",
        DeviceConnectionState.Unavailable => "–",
        _ => "○"
    };

    /// <summary>The button state name of a slot. These names are persisted and must never change.</summary>
    public static string ToButtonState(DeviceConnectionState state) => state.ToString();

    /// <summary>Whether a press on a toggle should take the device down.</summary>
    public static bool IsUp(DeviceConnectionState state) =>
        state is DeviceConnectionState.Connected or DeviceConnectionState.Connecting;
}
