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

    /// <summary>Maps a classic Class of Device (major class bits 8-12, minor bits 2-7) onto a kind.</summary>
    public static BluetoothDeviceKind FromClassOfDevice(uint classOfDevice)
    {
        uint major = (classOfDevice >> 8) & 0x1F;
        uint minor = (classOfDevice >> 2) & 0x3F;

        return major switch
        {
            1 => BluetoothDeviceKind.Computer,
            2 => BluetoothDeviceKind.Phone,
            4 => minor switch
            {
                1 or 2 or 6 => BluetoothDeviceKind.Headset,
                5 or 7 or 8 or 10 => BluetoothDeviceKind.Speaker,
                14 or 15 => BluetoothDeviceKind.Display,
                _ => BluetoothDeviceKind.Other
            },
            // Peripheral: the low bits name a game controller, the high bits keyboard or pointer.
            5 when (minor & 0x0F) is 1 or 2 => BluetoothDeviceKind.Gamepad,
            5 => ((minor >> 4) & 0x3) switch
            {
                1 or 3 => BluetoothDeviceKind.Keyboard,
                2 => BluetoothDeviceKind.Mouse,
                _ => BluetoothDeviceKind.Other
            },
            7 when minor == 1 => BluetoothDeviceKind.Watch,
            8 when minor == 4 => BluetoothDeviceKind.Gamepad,
            _ => BluetoothDeviceKind.Other
        };
    }

    /// <summary>Maps a Bluetooth LE appearance (category in bits 6-15) onto a kind.</summary>
    public static BluetoothDeviceKind FromAppearance(ushort appearance)
    {
        int category = appearance >> 6;
        int subcategory = appearance & 0x3F;

        return category switch
        {
            1 => BluetoothDeviceKind.Phone,
            2 => BluetoothDeviceKind.Computer,
            3 => BluetoothDeviceKind.Watch,
            5 => BluetoothDeviceKind.Display,
            15 => subcategory switch
            {
                1 => BluetoothDeviceKind.Keyboard,
                2 => BluetoothDeviceKind.Mouse,
                3 or 4 => BluetoothDeviceKind.Gamepad,
                _ => BluetoothDeviceKind.Other
            },
            33 => BluetoothDeviceKind.Speaker,
            37 => BluetoothDeviceKind.Headset,
            _ => BluetoothDeviceKind.Other
        };
    }

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
