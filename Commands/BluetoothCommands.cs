using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>Whether a switch command turns something on, off or flips it.</summary>
internal enum SwitchMode
{
    Toggle,
    On,
    Off
}

/// <summary>
/// The names the plugin publishes. A command name is a stable public identifier: a button stores
/// it, so renaming one would break every deck that uses it.
/// </summary>
internal static class BluetoothCommands
{
    public const string Prefix = "Bluetooth.";
    public const string Group = "Bluetooth";

    public const string Adapter = Prefix + "Adapter";
    public const string Discovery = Prefix + "Discovery";
    public const string Connect = Prefix + "Connect";
    public const string Disconnect = Prefix + "Disconnect";
    public const string Toggle = Prefix + "Toggle";
    public const string Trust = Prefix + "Trust";
    public const string Untrust = Prefix + "Untrust";
    public const string OpenDevices = Prefix + "OpenDevices";
    public const string AddFavorite = Prefix + "AddFavorite";
    public const string RemoveFavorite = Prefix + "RemoveFavorite";

    /// <summary>The prefix of the slot commands, followed by a two-digit number.</summary>
    public const string SlotPrefix = Prefix + "Device";

    /// <summary>The device parameter: adapter address and device address, written by the menu.</summary>
    public const string DeviceParameter = "Device";
    public const string DeviceTemplate = "({Device})";

    /// <summary>The parameters of the adapter commands: the mode, then an optional adapter address.</summary>
    public const string ModeParameter = "Mode";
    public const string AdapterParameter = "Adapter";
    public const string SwitchTemplate = "({Mode},{Adapter})";

    /// <summary>
    /// The default of an optional parameter: the preferred adapter. It has to be a real value,
    /// because the host fills an empty first parameter with the name the command was picked under.
    /// </summary>
    public const string Auto = "auto";

    public const string ModeToggle = "toggle";
    public const string ModeOn = "on";
    public const string ModeOff = "off";

    /// <summary>
    /// Reads a positional parameter. Empty values (the host drops trailing ones) and
    /// <see cref="Auto"/> both read as null, meaning "not set".
    /// </summary>
    public static string? ReadParameter(CommandContext ctx, int index)
    {
        string? value = ctx.Parameters.Length > index ? ctx.Parameters[index]?.Trim() : null;

        return string.IsNullOrEmpty(value) || string.Equals(value, Auto, StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    /// <summary>Reads the mode parameter. Anything unknown, including no value, toggles.</summary>
    public static SwitchMode ReadMode(CommandContext ctx)
    {
        return ReadParameter(ctx, 0)?.ToLowerInvariant() switch
        {
            ModeOn or "start" or "1" or "true" or "enable" => SwitchMode.On,
            ModeOff or "stop" or "0" or "false" or "disable" => SwitchMode.Off,
            _ => SwitchMode.Toggle
        };
    }

    /// <summary>What a switch mode asks for: true for on, false for off, null to flip.</summary>
    public static bool? ToTarget(SwitchMode mode) => mode switch
    {
        SwitchMode.On => true,
        SwitchMode.Off => false,
        _ => null
    };

    /// <summary>The descriptor of a command that acts on the device in its parameter.</summary>
    public static CommandDescriptor DeviceDescriptor(
        string commandName,
        string displayName,
        string description,
        string glyph) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = Group,
        Icon = glyph,
        // The command draws the whole key itself.
        ButtonLayout = BluetoothButtonLayouts.None,
        Description = description,
        ParameterTemplate = DeviceTemplate,
        Parameters = [new CommandParameter(DeviceParameter, typeof(string))],
        HiddenFromMenu = true
    };

    /// <summary>The descriptor of an adapter switch; every state brings its own icon and caption.</summary>
    public static CommandDescriptor SwitchDescriptor(
        string commandName,
        string displayName,
        string description,
        string glyph,
        IReadOnlyList<ButtonStateDescriptor> states) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = Group,
        Icon = glyph,
        ButtonLayout = BluetoothButtonLayouts.None,
        Description = description,
        ParameterTemplate = SwitchTemplate,
        Parameters =
        [
            new CommandParameter(ModeParameter, typeof(string)) { DefaultValue = ModeToggle },
            new CommandParameter(AdapterParameter, typeof(string)) { DefaultValue = Auto }
        ],
        HiddenFromMenu = true,
        States = states
    };

    /// <summary>A state with its own icon and caption.</summary>
    public static ButtonStateDescriptor State(string name, string description, string glyph, string caption, string color) => new()
    {
        Name = name,
        Description = description,
        Layout = BluetoothButtonLayouts.IconWithCaption(glyph, caption, tall: false, color)
    };
}
