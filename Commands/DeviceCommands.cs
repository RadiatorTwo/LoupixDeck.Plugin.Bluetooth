using System.Globalization;
using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>What a device command does on a press.</summary>
internal enum DeviceAction
{
    Connect,
    Disconnect,
    Toggle,
    Trust,
    Untrust
}

/// <summary>
/// Acts on the device in its parameter and shows that device on the key: name, state, battery
/// level and a battery ring. The value feeds the key's own layers, so the user can restyle them.
/// </summary>
internal sealed class DeviceCommand(CommandDescriptor descriptor, DeviceAction action, BluetoothStateStore store)
    : BluetoothCommandBase(descriptor, store), IValueDisplayCommand
{
    /// <summary>State changes are pushed; the poll only covers what has no event, such as an expired failure.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        string? deviceId = BluetoothCommands.ReadParameter(ctx, 0);
        return DeviceDisplay.Value(Store, deviceId, ctx.Host, showTrust: action is DeviceAction.Trust or DeviceAction.Untrust);
    }

    protected override async Task ExecuteCore(CommandContext ctx)
    {
        string? deviceId = BluetoothCommands.ReadParameter(ctx, 0);

        if (deviceId is null)
        {
            ShowOverlay(ctx, ctx.Host.Tr("No device selected"));
            return;
        }

        BluetoothActionResult result = action switch
        {
            DeviceAction.Connect => await Store.ConnectAsync(deviceId).ConfigureAwait(false),
            DeviceAction.Disconnect => await Store.DisconnectAsync(deviceId).ConfigureAwait(false),
            DeviceAction.Trust => await Store.SetTrustedAsync(deviceId, true).ConfigureAwait(false),
            DeviceAction.Untrust => await Store.SetTrustedAsync(deviceId, false).ConfigureAwait(false),
            _ => await Store.ToggleAsync(deviceId).ConfigureAwait(false)
        };

        Report(ctx, result, deviceId);
    }

    public static IEnumerable<IPluginCommand> Create(BluetoothStateStore store)
    {
        BluetoothCapabilities capabilities = store.Capabilities;

        if (capabilities.HasFlag(BluetoothCapabilities.Connect))
        {
            yield return new DeviceCommand(BluetoothCommands.DeviceDescriptor(
                    BluetoothCommands.Toggle,
                    "Toggle Device",
                    "Connect or disconnect a paired device. The button shows its state and battery level.",
                    BluetoothButtonLayouts.Transfer),
                DeviceAction.Toggle, store);

            yield return new DeviceCommand(BluetoothCommands.DeviceDescriptor(
                    BluetoothCommands.Connect,
                    "Connect Device",
                    "Connect a paired device. The button shows its state and battery level.",
                    BluetoothButtonLayouts.Connect),
                DeviceAction.Connect, store);

            yield return new DeviceCommand(BluetoothCommands.DeviceDescriptor(
                    BluetoothCommands.Disconnect,
                    "Disconnect Device",
                    "Disconnect a device. The button shows its state and battery level.",
                    BluetoothButtonLayouts.Disconnect),
                DeviceAction.Disconnect, store);
        }

        if (capabilities.HasFlag(BluetoothCapabilities.Trust))
        {
            yield return new DeviceCommand(BluetoothCommands.DeviceDescriptor(
                    BluetoothCommands.Trust,
                    "Trust Device",
                    "Mark a device as trusted, so it may connect on its own.",
                    BluetoothButtonLayouts.Trust),
                DeviceAction.Trust, store);

            yield return new DeviceCommand(BluetoothCommands.DeviceDescriptor(
                    BluetoothCommands.Untrust,
                    "Untrust Device",
                    "Remove the trusted status from a device.",
                    BluetoothButtonLayouts.Untrust),
                DeviceAction.Untrust, store);
        }
    }
}

/// <summary>The texts every device key and folder entry shows, built from cached state only.</summary>
internal static class DeviceDisplay
{
    /// <summary>The value of a device key: the name, then the state with the battery level, and the ring.</summary>
    public static AdjustmentValue? Value(BluetoothStateStore store, string? deviceId, IPluginHost host, bool showTrust = false)
    {
        if (deviceId is null)
        {
            return new AdjustmentValue(double.NaN, host.Tr("No device"));
        }

        BluetoothSnapshot snapshot = store.Current;
        BluetoothDeviceState? device = snapshot.FindDevice(deviceId);
        DeviceConnectionState state = store.StateOf(deviceId, snapshot);

        if (device is null)
        {
            string reason = snapshot.IsAvailable ? "Device not found" : "Bluetooth unavailable";
            return new AdjustmentValue(double.NaN, deviceId) { Detail = host.Tr(reason) };
        }

        string detail = showTrust
            ? host.Tr(device.Trusted ? "Trusted" : "Not trusted")
            : StateText(store, state, device, host);

        double normalized = device.BatteryPercent is byte battery ? battery / 100.0 : double.NaN;
        return new AdjustmentValue(normalized, device.DisplayName) { Detail = detail };
    }

    /// <summary>"Connected · 82 %", or the failure reason while a failure is remembered.</summary>
    public static string StateText(BluetoothStateStore store, DeviceConnectionState state, BluetoothDeviceState device, IPluginHost host)
    {
        if (state == DeviceConnectionState.Failed && store.FailureOf(device.StableId) is { } reason)
        {
            // The failure itself reads better than the generic "Failed".
            return host.Tr(reason);
        }

        string text = host.Tr(DeviceConnectionStates.ToEnglishText(state));
        return device.BatteryPercent is byte battery
            ? $"{text} · {battery.ToString(CultureInfo.InvariantCulture)} %"
            : text;
    }
}
