using System.Globalization;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>
/// One favorite slot. The host binds a button state to a command name alone, so a command that
/// takes its device from a parameter cannot carry per-device states: two buttons would share one.
/// A slot therefore has a command name of its own and takes its device from the favorites list,
/// which is what makes the Connected/Connecting/Disconnected/Failed/Unavailable states work.
/// </summary>
internal sealed class DeviceSlotCommand(
    CommandDescriptor descriptor,
    int slotIndex,
    BluetoothStateStore store,
    BluetoothSettings settings) : BluetoothCommandBase(descriptor, store), IDisplayCommand
{
    /// <summary>State changes are pushed; the poll only covers the short window before the first read.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    /// <summary>The favorite this slot shows, or null while the slot is empty.</summary>
    public string? DeviceId => settings.FavoriteAt(slotIndex);

    public string GetText(CommandContext ctx)
    {
        string? deviceId = DeviceId;

        if (deviceId is null)
        {
            return ctx.Host.Tr("Empty slot");
        }

        BluetoothSnapshot snapshot = Store.Current;
        DeviceConnectionState state = Store.StateOf(deviceId, snapshot);

        if (snapshot.FindDevice(deviceId) is not { } device)
        {
            return string.Join(Environment.NewLine, ctx.Host.Tr("Device not found"), ctx.Host.Tr(DeviceConnectionStates.ToEnglishText(state)));
        }

        return string.Join(Environment.NewLine, device.DisplayName, DeviceDisplay.StateText(Store, state, device, ctx.Host));
    }

    protected override async Task ExecuteCore(CommandContext ctx)
    {
        string? deviceId = DeviceId;

        if (deviceId is null)
        {
            ShowOverlay(ctx, ctx.Host.Tr("Empty slot"));
            return;
        }

        BluetoothActionResult result = await Store.ToggleAsync(deviceId).ConfigureAwait(false);
        Report(ctx, result, deviceId);
    }
}

/// <summary>Builds the slot commands.</summary>
internal static class DeviceSlotCommands
{
    /// <summary>
    /// The states a slot button carries, in the order the host materializes them. Index 0 is the
    /// resting state. These names are persisted in the button config and must never change.
    /// </summary>
    public static readonly IReadOnlyList<ButtonStateDescriptor> States =
    [
        State(DeviceConnectionState.Disconnected, "The device is not connected", BluetoothButtonLayouts.Group, BluetoothButtonLayouts.OffColor),
        State(DeviceConnectionState.Connecting, "The device is connecting", BluetoothButtonLayouts.Connecting, BluetoothButtonLayouts.BusyColor),
        State(DeviceConnectionState.Connected, "The device is connected", BluetoothButtonLayouts.Connect, BluetoothButtonLayouts.ConnectedColor),
        State(DeviceConnectionState.Disconnecting, "The device is disconnecting", BluetoothButtonLayouts.Connecting, BluetoothButtonLayouts.BusyColor),
        State(DeviceConnectionState.Failed, "The last connection attempt failed", BluetoothButtonLayouts.Failed, BluetoothButtonLayouts.FailedColor),
        State(DeviceConnectionState.Unavailable, "BlueZ, the adapter or the device is not available", BluetoothButtonLayouts.Off, BluetoothButtonLayouts.UnavailableColor)
    ];

    /// <summary>
    /// A state with its own icon. The caption is left to the host and then replaced by the slot's
    /// live text, which names the device and its state.
    /// </summary>
    private static ButtonStateDescriptor State(DeviceConnectionState state, string description, string glyph, string color) => new()
    {
        Name = DeviceConnectionStates.ToButtonState(state),
        Description = description,
        Layout = BluetoothButtonLayouts.IconWithCaption(glyph, caption: null, tall: true, color)
    };

    public static IReadOnlyList<DeviceSlotCommand> Create(BluetoothStateStore store, BluetoothSettings settings)
    {
        List<DeviceSlotCommand> commands = [];

        for (int slotIndex = 0; slotIndex < BluetoothSettings.FavoriteSlotCount; slotIndex++)
        {
            string number = Number(slotIndex);

            CommandDescriptor descriptor = new()
            {
                CommandName = BluetoothCommands.SlotPrefix + number,
                DisplayName = "Device Slot " + number,
                Group = BluetoothCommands.Group,
                Icon = BluetoothButtonLayouts.Group,
                // Every state brings its own icon; the slot's live text replaces the caption.
                ButtonLayout = BluetoothButtonLayouts.None,
                Description = "Show and toggle favorite device " + number + ". The button follows the device's state.",
                HiddenFromMenu = true,
                States = States
            };

            commands.Add(new DeviceSlotCommand(descriptor, slotIndex, store, settings));
        }

        return commands;
    }

    /// <summary>The two-digit slot number used in command names and the menu.</summary>
    public static string Number(int slotIndex) => (slotIndex + 1).ToString("00", CultureInfo.InvariantCulture);
}
