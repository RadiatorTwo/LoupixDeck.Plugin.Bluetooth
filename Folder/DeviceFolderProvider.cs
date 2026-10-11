using System.Globalization;
using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.Plugin.Bluetooth.Commands;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Folder;

/// <summary>
/// The touch-screen folder that lists the known devices with their state and battery level,
/// connected ones first. Pressing a paired device connects or disconnects it. The first entry runs a
/// time-limited discovery; with "paired devices only" off, the devices it finds show up as well.
/// </summary>
internal sealed class DeviceFolderProvider(BluetoothStateStore store, BluetoothSettings settings, IPluginHost host) : FolderProviderBase
{
    private static readonly PluginColor ConnectedColor = PluginColor.FromRgb(0x1B, 0x5E, 0x20);
    private static readonly PluginColor DisconnectedColor = PluginColor.FromRgb(0x1A, 0x23, 0x3A);
    private static readonly PluginColor UnpairedColor = PluginColor.FromRgb(0x22, 0x22, 0x22);
    private static readonly PluginColor FailedColor = PluginColor.FromRgb(0x70, 0x20, 0x20);
    private static readonly PluginColor BusyColor = PluginColor.FromRgb(0x70, 0x55, 0x10);
    private static readonly PluginColor UnavailableColor = PluginColor.FromRgb(0x20, 0x20, 0x20);
    private static readonly PluginColor DiscoveryColor = PluginColor.FromRgb(0x0D, 0x3B, 0x66);
    private static readonly PluginColor NavigationColor = PluginColor.FromRgb(0x30, 0x30, 0x30);
    private static readonly PluginColor DimTextColor = PluginColor.FromRgb(0xB0, 0xB0, 0xB0);
    private static readonly PluginColor LowBatteryColor = PluginColor.FromRgb(0xFF, 0x8A, 0x65);

    private static readonly TimeSpan OverlayDuration = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private Timer? _countdown;
    private int _page;

    public override string Title => host.Tr("Bluetooth");

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        FolderGridInfo grid = host.FolderGrid;
        BluetoothSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return [Message(grid, BluetoothErrors.ToEnglishText(BluetoothCallOutcome.Unavailable))];
        }

        List<FolderEntry> entries = [];
        int index = 0;

        if (store.Capabilities.HasFlag(BluetoothCapabilities.Discovery) && store.ResolveAdapter(null) is { } adapter)
        {
            entries.Add(DiscoveryEntry(grid.SlotForIndex(index++), adapter));
        }

        List<BluetoothDeviceState> devices = [.. DeviceOrdering.Sort(snapshot.Devices.Where(IsListed))];

        if (devices.Count == 0)
        {
            entries.Add(Message(grid, settings.PairedOnly ? "No paired devices" : "No devices", index));
            return entries;
        }

        int free = FreeSlots(grid) - index;

        // Two navigation tiles need a third slot for at least one device.
        bool paged = devices.Count > free && free >= 3;
        int perPage = paged ? free - 2 : free;
        int pageCount = paged ? (devices.Count + perPage - 1) / perPage : 1;
        int page;

        lock (_lock)
        {
            // A list that shrank moves back to its last page, so the folder never shows an empty one.
            _page = Math.Clamp(_page, 0, pageCount - 1);
            page = _page;
        }

        foreach (BluetoothDeviceState device in devices.Skip(page * perPage).Take(perPage))
        {
            int slot = grid.SlotForIndex(index++);

            if (slot < 0)
            {
                break;
            }

            entries.Add(DeviceEntry(slot, device, snapshot));
        }

        if (paged)
        {
            string position = $"{page + 1}/{pageCount}";
            int total = FreeSlots(grid);
            entries.Add(NavigationEntry(grid.SlotForIndex(total - 2), $"{host.Tr("Previous")} {position}", -1, pageCount));
            entries.Add(NavigationEntry(grid.SlotForIndex(total - 1), $"{host.Tr("Next")} {position}", +1, pageCount));
        }

        return entries;
    }

    public override void OnEnter()
    {
        store.Changed += OnStoreChanged;

        // The discovery entry counts down; only a running discovery needs the tick.
        _countdown = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public override void OnExit()
    {
        store.Changed -= OnStoreChanged;
        _countdown?.Dispose();
        _countdown = null;
    }

    private void OnStoreChanged() => RaiseEntriesChanged();

    private void Tick()
    {
        if (store.ResolveAdapter(null) is { } adapter && store.IsDiscovering(adapter))
        {
            RaiseEntriesChanged();
        }
    }

    private bool IsListed(BluetoothDeviceState device) =>
        (device.Paired || !settings.PairedOnly) && !settings.IsHidden(device.StableId, device.Address);

    /// <summary>The slots a folder page can use; the host keeps the back slot for itself.</summary>
    private static int FreeSlots(FolderGridInfo grid)
    {
        int count = 0;

        while (grid.SlotForIndex(count) >= 0)
        {
            count++;
        }

        return count;
    }

    private FolderEntry DiscoveryEntry(int slot, BluetoothAdapterState adapter)
    {
        TimeSpan? remaining = store.DiscoveryRemaining(adapter);
        string text = remaining is { } left
            ? $"{host.Tr("Discovering")}{Environment.NewLine}{Math.Ceiling(left.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s"
            : adapter.Powered ? host.Tr("Discover") : host.Tr("Adapter is off");

        return new FolderEntry
        {
            SlotIndex = slot,
            Text = text,
            BackColor = remaining is null ? DisconnectedColor : DiscoveryColor,
            TextSize = 13,
            Bold = remaining is not null,
            Render = canvas => DrawTile(canvas, remaining is null ? DisconnectedColor : DiscoveryColor, "radar", PluginColor.White, text, null, null),
            OnPress = () => DiscoveryPressAsync(slot)
        };
    }

    private FolderEntry DeviceEntry(int slot, BluetoothDeviceState device, BluetoothSnapshot snapshot)
    {
        DeviceConnectionState state = store.StateOf(device.StableId, snapshot);
        PluginColor back = device.Paired ? ColorFor(state) : UnpairedColor;
        string stateText = device.Paired
            ? host.Tr(DeviceConnectionStates.ToEnglishText(state))
            : RssiText(device);
        if (state == DeviceConnectionState.Failed && store.FailureOf(device.StableId) is { } reason)
        {
            stateText = host.Tr(reason);
        }

        string? battery = device.BatteryPercent is byte percent ? percent.ToString(CultureInfo.InvariantCulture) + " %" : null;
        string text = string.Join(Environment.NewLine, new[] { device.DisplayName, stateText, battery }.Where(line => line is not null));

        return new FolderEntry
        {
            SlotIndex = slot,
            Text = text,
            BackColor = back,
            TextSize = 13,
            Bold = state == DeviceConnectionState.Connected,
            Render = canvas => DrawTile(
                canvas,
                back,
                BluetoothDeviceKinds.SymbolName(device.Kind),
                state == DeviceConnectionState.Connected ? PluginColor.White : DimTextColor,
                device.DisplayName,
                stateText,
                battery is null ? null : (battery, device.BatteryPercent <= 20 ? LowBatteryColor : PluginColor.White)),
            OnPress = () => DevicePressAsync(slot, device)
        };
    }

    private FolderEntry NavigationEntry(int slot, string text, int delta, int pageCount) => new()
    {
        SlotIndex = slot,
        Text = text,
        TextSize = 14,
        BackColor = NavigationColor,
        OnPress = () =>
        {
            lock (_lock)
            {
                _page = (((_page + delta) % pageCount) + pageCount) % pageCount;
            }

            RaiseEntriesChanged();
            return Task.CompletedTask;
        }
    };

    private FolderEntry Message(FolderGridInfo grid, string english, int index = 0) => new()
    {
        SlotIndex = grid.SlotForIndex(index),
        Text = host.Tr(english),
        BackColor = UnavailableColor,
        TextSize = 13
    };

    private async Task DiscoveryPressAsync(int slot)
    {
        try
        {
            BluetoothActionResult result = await store.SetDiscoveryAsync(null, null).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                host.OverlayTouchText(slot, host.Tr(result.Text), OverlayDuration);
            }
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Bluetooth: folder discovery failed: {ex.Message}");
        }

        RaiseEntriesChanged();
    }

    private async Task DevicePressAsync(int slot, BluetoothDeviceState device)
    {
        if (!device.Paired)
        {
            // Pairing needs a BlueZ agent with PIN and passkey dialogs, which this version leaves out.
            host.OverlayTouchText(slot, host.Tr("Pairing not supported yet"), OverlayDuration);
            return;
        }

        if (!store.Capabilities.HasFlag(BluetoothCapabilities.Connect))
        {
            host.OverlayTouchText(slot, host.Tr(BluetoothErrors.ToEnglishText(BluetoothCallOutcome.NotSupported)), OverlayDuration);
            return;
        }

        try
        {
            BluetoothActionResult result = await store.ToggleAsync(device.StableId).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                host.OverlayTouchText(slot, host.Tr(result.Text), OverlayDuration);
                host.Logger.Info($"Bluetooth: folder action on {device.DisplayName}: {result.Text}.");
            }
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Bluetooth: folder action on {device.DisplayName} failed: {ex.Message}");
        }

        RaiseEntriesChanged();
    }

    private string RssiText(BluetoothDeviceState device) =>
        device.Rssi is short rssi
            ? $"{host.Tr("Not paired")} · {rssi.ToString(CultureInfo.InvariantCulture)} dBm"
            : host.Tr("Not paired");

    private static PluginColor ColorFor(DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Connected => ConnectedColor,
        DeviceConnectionState.Connecting or DeviceConnectionState.Disconnecting => BusyColor,
        DeviceConnectionState.Failed => FailedColor,
        DeviceConnectionState.Unavailable => UnavailableColor,
        _ => DisconnectedColor
    };

    /// <summary>
    /// An icon over up to three lines: the name, the state and the battery level. Laid out for a
    /// 90 px tile and scaled onto the real one.
    /// </summary>
    private static void DrawTile(
        IRenderCanvas canvas,
        PluginColor back,
        string symbol,
        PluginColor symbolColor,
        string title,
        string? subtitle,
        (string Text, PluginColor Color)? battery)
    {
        int size = Math.Min(canvas.Width, canvas.Height);
        double k = size / 90.0;
        int Px(double design) => (int)Math.Round(design * k);

        canvas.Clear(back);

        int icon = Px(30);
        canvas.DrawSymbol(symbol, (canvas.Width - icon) / 2, Px(6), icon, icon, symbolColor);

        int boxWidth = Px(86);
        int left = (canvas.Width - boxWidth) / 2;
        canvas.DrawText(title, left, Px(38), boxWidth, Px(16), PluginColor.White, (float)(11 * k), bold: true);

        if (subtitle is not null)
        {
            canvas.DrawText(subtitle, left, Px(54), boxWidth, Px(14), DimTextColor, (float)(9 * k));
        }

        if (battery is { } level)
        {
            canvas.DrawText(level.Text, left, Px(69), boxWidth, Px(14), level.Color, (float)(10 * k), bold: true);
        }
    }
}

/// <summary>Opens the device folder.</summary>
internal sealed class OpenDeviceFolderCommand(BluetoothStateStore store, BluetoothSettings settings, IPluginHost host) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = BluetoothCommands.OpenDevices,
        DisplayName = "Open Device Folder",
        Group = BluetoothCommands.Group,
        Icon = BluetoothButtonLayouts.Folder,
        ButtonLayout = BluetoothButtonLayouts.IconWithCaption(BluetoothButtonLayouts.Folder, "Bluetooth"),
        Description = "Open a folder with the paired devices, their state and battery level. A press on a device connects or disconnects it."
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public Task Execute(CommandContext ctx)
    {
        try
        {
            ctx.Host.OpenFolder(new DeviceFolderProvider(store, settings, host));
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}
