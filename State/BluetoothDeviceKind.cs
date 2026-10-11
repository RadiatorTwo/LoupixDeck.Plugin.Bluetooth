namespace LoupixDeck.Plugin.Bluetooth.State;

/// <summary>What a device is, as far as the deck cares: it picks the icon.</summary>
public enum BluetoothDeviceKind
{
    Other,
    Headset,
    Speaker,
    Gamepad,
    Keyboard,
    Mouse,
    Phone,
    Computer,
    Watch,
    Display
}

/// <summary>Mappings and icons for <see cref="BluetoothDeviceKind"/>.</summary>
internal static class BluetoothDeviceKinds
{
    /// <summary>Maps BlueZ's freedesktop icon name (the Device1 "Icon" property) onto a kind.</summary>
    public static BluetoothDeviceKind FromIcon(string? icon) => icon switch
    {
        "audio-headset" or "audio-headphones" => BluetoothDeviceKind.Headset,
        "audio-card" or "audio-speakers" => BluetoothDeviceKind.Speaker,
        "input-gaming" => BluetoothDeviceKind.Gamepad,
        "input-keyboard" => BluetoothDeviceKind.Keyboard,
        "input-mouse" or "input-tablet" => BluetoothDeviceKind.Mouse,
        "phone" => BluetoothDeviceKind.Phone,
        "computer" => BluetoothDeviceKind.Computer,
        "watch" => BluetoothDeviceKind.Watch,
        "video-display" or "multimedia-player" => BluetoothDeviceKind.Display,
        _ => BluetoothDeviceKind.Other
    };

    /// <summary>The Material Design Icons name, for <c>IRenderCanvas.DrawSymbol</c>.</summary>
    public static string SymbolName(BluetoothDeviceKind kind) => kind switch
    {
        BluetoothDeviceKind.Headset => "headphones",
        BluetoothDeviceKind.Speaker => "speaker",
        BluetoothDeviceKind.Gamepad => "gamepad-variant",
        BluetoothDeviceKind.Keyboard => "keyboard",
        BluetoothDeviceKind.Mouse => "mouse",
        BluetoothDeviceKind.Phone => "cellphone",
        BluetoothDeviceKind.Computer => "laptop",
        BluetoothDeviceKind.Watch => "watch",
        BluetoothDeviceKind.Display => "television",
        _ => "bluetooth"
    };

    /// <summary>The English name the menu shows for a kind; the host translates it.</summary>
    public static string ToEnglishText(BluetoothDeviceKind kind) => kind switch
    {
        BluetoothDeviceKind.Headset => "Headset",
        BluetoothDeviceKind.Speaker => "Speaker",
        BluetoothDeviceKind.Gamepad => "Gamepad",
        BluetoothDeviceKind.Keyboard => "Keyboard",
        BluetoothDeviceKind.Mouse => "Mouse",
        BluetoothDeviceKind.Phone => "Phone",
        BluetoothDeviceKind.Computer => "Computer",
        BluetoothDeviceKind.Watch => "Watch",
        BluetoothDeviceKind.Display => "Display",
        _ => "Device"
    };
}
