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
