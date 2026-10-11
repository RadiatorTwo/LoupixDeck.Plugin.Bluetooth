using System.Globalization;
using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.Plugin.Bluetooth.Commands;
using LoupixDeck.Plugin.Bluetooth.Rendering;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Folder;

/// <summary>
/// The touch-screen folder that lists the known devices with their state and battery level,
/// connected ones first. Pressing a paired device connects or disconnects it.
/// <para>
/// The first entry runs a time-limited discovery. While it runs, the named devices it finds are
/// listed after the paired ones (with "paired devices only" off they stay listed afterwards), and
/// pressing one pairs, trusts and connects it. During a pairing the folder shows the pairing
/// instead: the code to compare or type, and the buttons to confirm or cancel.
/// </para>
/// </summary>
internal sealed class DeviceFolderProvider(BluetoothStateStore store, BluetoothSettings settings, IPluginHost host) : FolderProviderBase
{

    private static readonly TimeSpan OverlayDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MarqueeInterval = TimeSpan.FromMilliseconds(66);

    private readonly Lock _lock = new();
    private Timer? _countdown;
    private Timer? _marquee;

    /// <summary>Set while the folder shows found devices, whose long lines scroll.</summary>
    private volatile bool _scrolling;
    private int _page;

    public override string Title => host.Tr("Bluetooth");

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        // Set again by every found device this build shows.
        _scrolling = false;

        FolderGridInfo grid = host.FolderGrid;
        BluetoothSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return [Message(grid, BluetoothErrors.ToEnglishText(BluetoothCallOutcome.Unavailable))];
        }

        if (store.Pairing is { } pairing)
        {
            return PairingEntries(grid, pairing);
        }

        List<FolderEntry> entries = [];
        int index = 0;
        BluetoothAdapterState? adapter = store.ResolveAdapter(null);
        bool discovering = adapter is not null && (adapter.Discovering || store.IsDiscovering(adapter));

        if (store.Capabilities.HasFlag(BluetoothCapabilities.Discovery) && adapter is not null)
        {
            entries.Add(DiscoveryEntry(grid.SlotForIndex(index++), adapter));
        }

        List<BluetoothDeviceState> devices = [.. DeviceOrdering.Sort(snapshot.Devices.Where(device => IsListed(device, discovering)))];

        if (devices.Count == 0)
        {
            string empty = discovering ? "Searching for devices" : settings.PairedOnly ? "No paired devices" : "No devices";
            entries.Add(Message(grid, empty, index));
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

        // Found devices scroll a name that is too long; the repaint is what moves it. About 15 frames
        // per second, and only while such tiles are shown.
        _marquee = new Timer(_ => ScrollTick(), null, MarqueeInterval, MarqueeInterval);
    }

    public override void OnExit()
    {
        store.Changed -= OnStoreChanged;
        _countdown?.Dispose();
        _countdown = null;
        _marquee?.Dispose();
        _marquee = null;
    }

    private void OnStoreChanged() => RaiseEntriesChanged();

    private void ScrollTick()
    {
        if (_scrolling)
        {
            RaiseEntriesChanged();
        }
    }

    private void Tick()
    {
        if (store.ResolveAdapter(null) is { } adapter && store.IsDiscovering(adapter))
        {
            RaiseEntriesChanged();
        }
    }

    /// <summary>
    /// Paired devices always; found ones while a discovery runs, or always with "paired devices
    /// only" off. A found device without a name is left out: BlueZ reports many anonymous BLE
    /// advertisers nobody wants to pair with.
    /// </summary>
    private bool IsListed(BluetoothDeviceState device, bool discovering) =>
        !settings.IsHidden(device.StableId, device.Address) &&
        (device.Paired || ((discovering || !settings.PairedOnly) && device.Name.Length > 0));

    /// <summary>The pairing view: what is happening, the code, and what the user can answer.</summary>
    private List<FolderEntry> PairingEntries(FolderGridInfo grid, PairingSession session)
    {
        List<FolderEntry> entries = [];
        int index = 0;

        string status = session.Stage switch
        {
            PairingStage.Confirm => "Does the code match?",
            PairingStage.TypeCode => "Type the code on the device",
            PairingStage.Connecting => "Connecting",
            PairingStage.Succeeded => session.Message ?? "Paired",
            PairingStage.Failed => session.Message ?? "Pairing failed",
            _ => "Pairing"
        };

        PluginColor statusColor = session.Stage switch
        {
            PairingStage.Succeeded => TileRenderer.ConnectedColor,
            PairingStage.Failed => TileRenderer.FailedColor,
            _ => TileRenderer.BusyColor
        };

        string statusText = host.Tr(status);
        entries.Add(new FolderEntry
        {
            SlotIndex = grid.SlotForIndex(index++),
            BackColor = statusColor,
            Render = canvas => TileRenderer.Draw(canvas, statusColor, "bluetooth-settings", PluginColor.White, session.DeviceName, statusText, null)
        });

        if (session.Code is { } code)
        {
            entries.Add(new FolderEntry
            {
                SlotIndex = grid.SlotForIndex(index++),
                BackColor = TileRenderer.DiscoveryColor,
                Render = canvas => DrawCode(canvas, code)
            });
        }

        if (session.Stage == PairingStage.Confirm)
        {
            entries.Add(ActionEntry(grid.SlotForIndex(index++), "Confirm", TileRenderer.ConfirmColor, "check", () => session.Answer(true)));
            entries.Add(ActionEntry(grid.SlotForIndex(index++), "Reject", TileRenderer.CancelColor, "close", () => session.Answer(false)));
        }
        else if (session.IsFinished)
        {
            entries.Add(ActionEntry(grid.SlotForIndex(index++), "Close", TileRenderer.NavigationColor, "arrow-left", store.DismissPairing));
        }
        else
        {
            entries.Add(ActionEntry(grid.SlotForIndex(index++), "Cancel", TileRenderer.CancelColor, "close", session.Cancel));
        }

        return entries;
    }

    private FolderEntry ActionEntry(int slot, string english, PluginColor color, string symbol, Action press)
    {
        string text = host.Tr(english);

        return new FolderEntry
        {
            SlotIndex = slot,
            BackColor = color,
            Render = canvas => TileRenderer.Draw(canvas, color, symbol, PluginColor.White, text, null, null),
            OnPress = () =>
            {
                press();
                RaiseEntriesChanged();
                return Task.CompletedTask;
            }
        };
    }

    /// <summary>A pairing code, as large as the tile allows.</summary>
    private static void DrawCode(IRenderCanvas canvas, string code)
    {
        int size = Math.Min(canvas.Width, canvas.Height);
        double k = size / 90.0;

        canvas.Clear(TileRenderer.DiscoveryColor);
        canvas.DrawText(code, 0, 0, canvas.Width, canvas.Height, PluginColor.White, (float)((code.Length > 6 ? 14 : 19) * k), bold: true);
    }

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
        string title = remaining is null
            ? adapter.Powered ? host.Tr("Discover") : host.Tr("Adapter is off")
            : host.Tr("Discovering");
        string? countdown = remaining is { } left
            ? Math.Ceiling(left.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s"
            : null;

        return new FolderEntry
        {
            SlotIndex = slot,
            BackColor = remaining is null ? TileRenderer.DisconnectedColor : TileRenderer.DiscoveryColor,
            Render = canvas => TileRenderer.Draw(canvas, remaining is null ? TileRenderer.DisconnectedColor : TileRenderer.DiscoveryColor, "radar", PluginColor.White, title, countdown, null),
            OnPress = () => DiscoveryPressAsync(slot)
        };
    }

    private FolderEntry DeviceEntry(int slot, BluetoothDeviceState device, BluetoothSnapshot snapshot)
    {
        DeviceConnectionState state = store.StateOf(device.StableId, snapshot);
        PluginColor back = device.Paired ? ColorFor(state) : TileRenderer.UnpairedColor;
        string stateText = device.Paired
            ? host.Tr(DeviceConnectionStates.ToEnglishText(state))
            : RssiText(device);
        if (state == DeviceConnectionState.Failed && store.FailureOf(device.StableId) is { } reason)
        {
            stateText = host.Tr(reason);
        }

        string? battery = device.BatteryPercent is byte percent ? percent.ToString(CultureInfo.InvariantCulture) + " %" : null;

        if (!device.Paired)
        {
            // A found device: large text that scrolls when it is too long.
            _scrolling = true;

            return new FolderEntry
            {
                SlotIndex = slot,
                BackColor = back,
                Render = canvas => TileRenderer.DrawScrolling(canvas, back, BluetoothDeviceKinds.SymbolName(device.Kind), TileRenderer.DimTextColor, device.DisplayName, stateText),
                OnPress = () => DevicePressAsync(slot, device)
            };
        }

        return new FolderEntry
        {
            SlotIndex = slot,
            BackColor = back,
            Render = canvas => TileRenderer.Draw(
                canvas,
                back,
                BluetoothDeviceKinds.SymbolName(device.Kind),
                state == DeviceConnectionState.Connected ? PluginColor.White : TileRenderer.DimTextColor,
                device.DisplayName,
                stateText,
                battery is null ? null : (battery, device.BatteryPercent <= 20 ? TileRenderer.LowBatteryColor : PluginColor.White)),
            OnPress = () => DevicePressAsync(slot, device)
        };
    }

    private FolderEntry NavigationEntry(int slot, string text, int delta, int pageCount) => new()
    {
        SlotIndex = slot,
        Text = text,
        TextSize = 14,
        BackColor = TileRenderer.NavigationColor,
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
        BackColor = TileRenderer.UnavailableColor,
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
            await PairPressAsync(slot, device).ConfigureAwait(false);
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

    private async Task PairPressAsync(int slot, BluetoothDeviceState device)
    {
        if (!store.Capabilities.HasFlag(BluetoothCapabilities.Pairing))
        {
            host.OverlayTouchText(slot, host.Tr(BluetoothErrors.ToEnglishText(BluetoothCallOutcome.NotSupported)), OverlayDuration);
            return;
        }

        try
        {
            // The pairing view replaces the list while it runs and reports how it ended.
            BluetoothActionResult result = await store.PairAsync(device.StableId).ConfigureAwait(false);

            if (result.Outcome == BluetoothCallOutcome.InProgress)
            {
                host.OverlayTouchText(slot, host.Tr("Another pairing is running"), OverlayDuration);
            }
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Bluetooth: pairing {device.DisplayName} failed: {ex.Message}");
        }

        RaiseEntriesChanged();
    }

    private string RssiText(BluetoothDeviceState device) =>
        device.Rssi is short rssi
            ? $"{host.Tr("Press to pair")} · {rssi.ToString(CultureInfo.InvariantCulture)} dBm"
            : host.Tr("Press to pair");

    private static PluginColor ColorFor(DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Connected => TileRenderer.ConnectedColor,
        DeviceConnectionState.Connecting or DeviceConnectionState.Disconnecting => TileRenderer.BusyColor,
        DeviceConnectionState.Failed => TileRenderer.FailedColor,
        DeviceConnectionState.Unavailable => TileRenderer.UnavailableColor,
        _ => TileRenderer.DisconnectedColor
    };
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
        Description = "Open a folder with the paired devices, their state and battery level. A press connects or disconnects a device; during a discovery, found devices can be paired from here."
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
