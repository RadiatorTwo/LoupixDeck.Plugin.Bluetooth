using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>Turns an adapter on, off or flips it.</summary>
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

/// <summary>Starts or stops a time-limited discovery on an adapter.</summary>
internal sealed class DiscoveryCommand(BluetoothStateStore store) : BluetoothCommandBase(Describe(), store)
{
    /// <summary>The button states, index 0 first. Persisted in the button config; never rename.</summary>
    public static readonly IReadOnlyList<ButtonStateDescriptor> States =
    [
        BluetoothCommands.State("Idle", "No discovery is running", BluetoothButtonLayouts.Discovery, "Discover", BluetoothButtonLayouts.OffColor),
        BluetoothCommands.State("Discovering", "Looking for devices nearby", BluetoothButtonLayouts.Discovery, "Discovering", BluetoothButtonLayouts.BusyColor),
        BluetoothCommands.State("Unavailable", "BlueZ or the adapter is not available", BluetoothButtonLayouts.Off, "Unavailable", BluetoothButtonLayouts.UnavailableColor)
    ];

    /// <summary>
    /// The state the switch shows for the preferred adapter. A discovery another program runs shows
    /// too, because the adapter reports nearby devices either way.
    /// </summary>
    public static string StateFor(BluetoothStateStore store) => store.ResolveAdapter(null) switch
    {
        null or { Powered: false } => "Unavailable",
        { Discovering: true } => "Discovering",
        { } adapter when store.IsDiscovering(adapter) => "Discovering",
        _ => "Idle"
    };

    protected override async Task ExecuteCore(CommandContext ctx)
    {
        string? adapter = BluetoothCommands.ReadParameter(ctx, 1);
        bool? target = BluetoothCommands.ToTarget(BluetoothCommands.ReadMode(ctx));

        BluetoothActionResult result = await Store.SetDiscoveryAsync(adapter, target).ConfigureAwait(false);
        Report(ctx, result, adapter ?? "default adapter");
    }

    private static CommandDescriptor Describe() => BluetoothCommands.SwitchDescriptor(
        BluetoothCommands.Discovery,
        "Discover Devices",
        "Look for devices nearby for a limited time (see the settings). Found devices appear in the device folder, where a press pairs them.",
        BluetoothButtonLayouts.Discovery,
        States);
}
