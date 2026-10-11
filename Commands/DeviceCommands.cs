using System.Globalization;
using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.Plugin.Bluetooth.Rendering;
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
/// Acts on the device in its parameter and shows that device on the key: icon, name, state and
/// battery level. The plugin draws the key itself, like a folder tile: the host's text layers wrap
/// a long name into the line below, and only the plugin can measure and fit it.
/// </summary>
internal sealed class DeviceCommand(CommandDescriptor descriptor, DeviceAction action, BluetoothStateStore store)
    : BluetoothCommandBase(descriptor, store), IDisplayImageCommand
{
    /// <summary>State changes are pushed; the poll only covers what has no event, such as an expired failure.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        string? deviceId = BluetoothCommands.ReadParameter(ctx, 0);
        DeviceDisplay.Draw(canvas, Store, deviceId, ctx.Host, showTrust: action is DeviceAction.Trust or DeviceAction.Untrust);
        return true;
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

/// <summary>Adds a device to the favorites behind the slots, or removes it.</summary>
internal sealed class FavoriteCommand(CommandDescriptor descriptor, bool add, BluetoothStateStore store, BluetoothSettings settings)
    : BluetoothCommandBase(descriptor, store)
{
    protected override Task ExecuteCore(CommandContext ctx)
    {
        string? deviceId = BluetoothCommands.ReadParameter(ctx, 0);

        if (deviceId is null)
        {
            ShowOverlay(ctx, ctx.Host.Tr("No device selected"));
            return Task.CompletedTask;
        }

        // The stable id survives a second adapter; a bare address typed by hand is kept as it is.
        string id = Store.Current.FindDevice(deviceId)?.StableId ?? deviceId;
        bool changed = add ? settings.AddFavorite(id) : settings.RemoveFavorite(id);

        ShowOverlay(ctx, ctx.Host.Tr(add
            ? changed ? "Added to favorites" : "Already a favorite"
            : changed ? "Removed from favorites" : "Not a favorite"));

        if (changed)
        {
            Store.NotifySettingsChanged();
        }

        return Task.CompletedTask;
    }

    public static IEnumerable<IPluginCommand> Create(BluetoothStateStore store, BluetoothSettings settings)
    {
        yield return new FavoriteCommand(
            Describe(BluetoothCommands.AddFavorite, "Add to Favorites", "Add the device to the favorites shown by the device slots", BluetoothButtonLayouts.AddFavorite),
            add: true, store, settings);

        yield return new FavoriteCommand(
            Describe(BluetoothCommands.RemoveFavorite, "Remove from Favorites", "Remove the device from the favorites", BluetoothButtonLayouts.RemoveFavorite),
            add: false, store, settings);
    }

    private static CommandDescriptor Describe(string commandName, string displayName, string description, string glyph) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = BluetoothCommands.Group,
        Icon = glyph,
        ButtonLayout = BluetoothButtonLayouts.IconWithCaption(glyph, caption: null, tall: true),
        Description = description,
        ParameterTemplate = BluetoothCommands.DeviceTemplate,
        Parameters = [new CommandParameter(BluetoothCommands.DeviceParameter, typeof(string))],
        HiddenFromMenu = true
    };
}

/// <summary>The texts every device key and folder entry shows, built from cached state only.</summary>
internal static class DeviceDisplay
{
    /// <summary>
    /// Draws a device key: the device's tile, or a placeholder when no device is set, the device is
    /// gone or Bluetooth is unavailable.
    /// </summary>
    public static void Draw(IRenderCanvas canvas, BluetoothStateStore store, string? deviceId, IPluginHost host, bool showTrust = false)
    {
        BluetoothSnapshot snapshot = store.Current;

        if (snapshot.FindDevice(deviceId) is not { } device)
        {
            string reason = deviceId is null ? "No device" : snapshot.IsAvailable ? "Device not found" : "Bluetooth unavailable";
            TileRenderer.Draw(canvas, TileRenderer.UnavailableColor, "bluetooth-off", TileRenderer.DimTextColor, host.Tr(reason), null, null);
            return;
        }

        DeviceConnectionState state = store.StateOf(device.StableId, snapshot);
        string stateText = showTrust
            ? host.Tr(device.Trusted ? "Trusted" : "Not trusted")
            : TileStateText(store, state, device, host);

        TileRenderer.DrawDevice(canvas, device, state, stateText, TileRenderer.StateColor(state));
    }

    /// <summary>The state line of a tile: the failure reason while one is remembered, else the state.</summary>
    public static string TileStateText(BluetoothStateStore store, DeviceConnectionState state, BluetoothDeviceState device, IPluginHost host) =>
        state == DeviceConnectionState.Failed && store.FailureOf(device.StableId) is { } reason
            ? host.Tr(reason)
            : host.Tr(DeviceConnectionStates.ToEnglishText(state));

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
