using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>
/// Flips an adapter and shows its state. Older buttons may still carry the mode on or off; those
/// keep working, but the menu offers <see cref="AdapterSwitchCommand"/> for them, which looks
/// different from the toggle.
/// </summary>
internal sealed class AdapterPowerCommand(BluetoothStateStore store) : BluetoothCommandBase(Describe(), store)
{
    /// <summary>The button states, index 0 first. Persisted in the button config; never rename.</summary>
    public static readonly IReadOnlyList<ButtonStateDescriptor> States =
    [
        BluetoothCommands.State("Off", "The adapter is off", BluetoothButtonLayouts.Off, "Bluetooth Off", BluetoothButtonLayouts.OffColor),
        BluetoothCommands.State("On", "The adapter is on", BluetoothButtonLayouts.Group, "Bluetooth On", BluetoothButtonLayouts.OnColor),
        BluetoothCommands.State("Unavailable", "BlueZ or the adapter is not available", BluetoothButtonLayouts.Off, "Unavailable", BluetoothButtonLayouts.UnavailableColor)
    ];

    /// <summary>The state the switch shows for the preferred adapter.</summary>
    public static string StateFor(BluetoothStateStore store) => store.ResolveAdapter(null) switch
    {
        null => "Unavailable",
        { Powered: true } => "On",
        _ => "Off"
    };

    protected override async Task ExecuteCore(CommandContext ctx)
    {
        string? adapter = BluetoothCommands.ReadParameter(ctx, 1);
        bool? target = BluetoothCommands.ToTarget(BluetoothCommands.ReadMode(ctx));

        BluetoothActionResult result = await Store.SetPoweredAsync(adapter, target).ConfigureAwait(false);
        Report(ctx, result, adapter ?? "default adapter");
    }

    private static CommandDescriptor Describe() => BluetoothCommands.SwitchDescriptor(
        BluetoothCommands.Adapter,
        "Toggle Adapter",
        "Turn the Bluetooth adapter on or off. The button follows the adapter's state.",
        BluetoothButtonLayouts.Group,
        States);
}

/// <summary>
/// Turns an adapter on or off. A command of its own, so its button says what it does instead of
/// showing the adapter's state like the toggle.
/// </summary>
internal sealed class AdapterSwitchCommand(bool on, BluetoothStateStore store) : BluetoothCommandBase(Describe(on), store)
{
    protected override async Task ExecuteCore(CommandContext ctx)
    {
        string? adapter = BluetoothCommands.ReadParameter(ctx, 0);

        BluetoothActionResult result = await Store.SetPoweredAsync(adapter, on).ConfigureAwait(false);
        Report(ctx, result, adapter ?? "default adapter");
    }

    private static CommandDescriptor Describe(bool on) => new()
    {
        CommandName = on ? BluetoothCommands.AdapterOn : BluetoothCommands.AdapterOff,
        DisplayName = on ? "Adapter On" : "Adapter Off",
        Group = BluetoothCommands.Group,
        Icon = on ? BluetoothButtonLayouts.Group : BluetoothButtonLayouts.Off,
        ButtonLayout = BluetoothButtonLayouts.IconWithCaption(
            on ? BluetoothButtonLayouts.Group : BluetoothButtonLayouts.Off,
            on ? "Adapter On" : "Adapter Off",
            tall: false,
            on ? BluetoothButtonLayouts.OnColor : BluetoothButtonLayouts.OffColor),
        Description = on ? "Turn the Bluetooth adapter on." : "Turn the Bluetooth adapter off.",
        ParameterTemplate = BluetoothCommands.AdapterTemplate,
        Parameters = [new CommandParameter(BluetoothCommands.AdapterParameter, typeof(string)) { DefaultValue = BluetoothCommands.Auto }],
        HiddenFromMenu = true
    };
}
