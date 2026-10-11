using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.Plugin.Bluetooth.Commands;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth;

/// <summary>
/// Builds the command picker tree. The SDK has no dropdown for a parameter, so a device is chosen by
/// walking to it: the device, labelled with its name, type, state and battery, then the action.
/// Every leaf bakes the device id into the command's parameter and is named "device — action",
/// which becomes the button's caption.
/// <para>
/// The host gives a menu contributor only a few seconds, so everything comes from the snapshot.
/// Only what the backend supports is offered.
/// </para>
/// </summary>
internal sealed class BluetoothMenu(BluetoothStateStore store, BluetoothSettings settings, IPluginHost host)
{
    public IReadOnlyList<MenuNode> Build()
    {
        BluetoothSnapshot snapshot = store.Current;
        BluetoothCapabilities capabilities = store.Capabilities;

        if (!snapshot.IsAvailable)
        {
            return
            [
                Folder(BluetoothCommands.Group,
                [
                    new MenuNode { Name = host.Tr(BluetoothErrors.ToEnglishText(BluetoothCallOutcome.Unavailable)), CommandName = string.Empty }
                ], translate: false)
            ];
        }

        List<MenuNode> children = [];

        if (capabilities.HasFlag(BluetoothCapabilities.AdapterPower))
        {
            children.AddRange(SwitchLeaves(snapshot, BluetoothCommands.Adapter, "Toggle Adapter", "Adapter On", "Adapter Off"));
        }

        if (capabilities.HasFlag(BluetoothCapabilities.Discovery))
        {
            children.AddRange(SwitchLeaves(snapshot, BluetoothCommands.Discovery, "Discover Devices", "Start Discovery", "Stop Discovery"));
        }

        if (BuildDevices(snapshot, capabilities) is { } devices)
        {
            children.Add(devices);
        }

        if (BuildSlots(snapshot) is { } slots)
        {
            children.Add(slots);
        }

        // The root keeps the group name untranslated: the host merges it with the group by name.
        return [Folder(BluetoothCommands.Group, children, translate: false)];
    }

    /// <summary>
    /// Toggle, on and off for the preferred adapter; with several adapters, the same per adapter in
    /// a folder of its own.
    /// </summary>
    private IEnumerable<MenuNode> SwitchLeaves(BluetoothSnapshot snapshot, string commandName, string toggle, string on, string off)
    {
        yield return SwitchLeaf(host.Tr(toggle), commandName, BluetoothCommands.ModeToggle, BluetoothCommands.Auto);
        yield return SwitchLeaf(host.Tr(on), commandName, BluetoothCommands.ModeOn, BluetoothCommands.Auto);
        yield return SwitchLeaf(host.Tr(off), commandName, BluetoothCommands.ModeOff, BluetoothCommands.Auto);

        if (snapshot.Adapters.Count < 2)
        {
            yield break;
        }

        foreach (BluetoothAdapterState adapter in snapshot.Adapters)
        {
            string name = adapter.DisplayName;
            yield return Folder($"{host.Tr(toggle)} · {name}",
            [
                SwitchLeaf($"{name} — {host.Tr(toggle)}", commandName, BluetoothCommands.ModeToggle, adapter.Address),
                SwitchLeaf($"{name} — {host.Tr(on)}", commandName, BluetoothCommands.ModeOn, adapter.Address),
                SwitchLeaf($"{name} — {host.Tr(off)}", commandName, BluetoothCommands.ModeOff, adapter.Address)
            ], translate: false);
        }
    }

    /// <summary>The paired devices that are not hidden, connected ones first, each with its actions.</summary>
    private MenuNode? BuildDevices(BluetoothSnapshot snapshot, BluetoothCapabilities capabilities)
    {
        List<BluetoothDeviceState> devices =
        [
            .. DeviceOrdering.Sort(snapshot.Devices.Where(device =>
                device.Paired && !settings.IsHidden(device.StableId, device.Address)))
        ];

        if (devices.Count == 0)
        {
            return null;
        }

        bool connect = capabilities.HasFlag(BluetoothCapabilities.Connect);
        bool trust = capabilities.HasFlag(BluetoothCapabilities.Trust);
        bool severalAdapters = snapshot.Adapters.Count > 1;
        List<MenuNode> nodes = [];

        foreach (BluetoothDeviceState device in devices)
        {
            string name = device.DisplayName;
            string id = device.StableId;
            List<MenuNode> leaves = [];

            if (connect)
            {
                leaves.Add(DeviceLeaf($"{name} — {host.Tr("Toggle")}", BluetoothCommands.Toggle, id));
                leaves.Add(DeviceLeaf($"{name} — {host.Tr("Connect")}", BluetoothCommands.Connect, id));
                leaves.Add(DeviceLeaf($"{name} — {host.Tr("Disconnect")}", BluetoothCommands.Disconnect, id));
            }

            if (trust)
            {
                leaves.Add(device.Trusted
                    ? DeviceLeaf($"{name} — {host.Tr("Untrust")}", BluetoothCommands.Untrust, id)
                    : DeviceLeaf($"{name} — {host.Tr("Trust")}", BluetoothCommands.Trust, id));
            }

            if (leaves.Count == 0)
            {
                continue;
            }

            nodes.Add(Folder(Label(device, snapshot, severalAdapters), leaves, translate: false));
        }

        return nodes.Count == 0 ? null : Folder("Devices", nodes);
    }

    /// <summary>The slot commands, named after the favorite each one currently shows.</summary>
    private MenuNode? BuildSlots(BluetoothSnapshot snapshot)
    {
        if (!store.Capabilities.HasFlag(BluetoothCapabilities.Connect))
        {
            return null;
        }

        IReadOnlyList<string> favorites = settings.Favorites;
        List<MenuNode> children = [];

        for (int index = 0; index < favorites.Count && index < BluetoothSettings.FavoriteSlotCount; index++)
        {
            string number = DeviceSlotCommands.Number(index);
            string name = snapshot.FindDevice(favorites[index])?.DisplayName ?? host.Tr("Device not found");

            children.Add(new MenuNode
            {
                Name = $"{number} — {name}",
                CommandName = BluetoothCommands.SlotPrefix + number
            });
        }

        return children.Count == 0 ? null : Folder("Device Slots", children);
    }

    /// <summary>"WH-1000XM5 · Headset · Connected · 82 %", with the adapter when there are several.</summary>
    private string Label(BluetoothDeviceState device, BluetoothSnapshot snapshot, bool withAdapter)
    {
        List<string> parts =
        [
            device.DisplayName,
            host.Tr(BluetoothDeviceKinds.ToEnglishText(device.Kind)),
            DeviceDisplay.StateText(store, store.StateOf(device.StableId, snapshot), device, host)
        ];

        if (withAdapter && snapshot.AdapterOf(device) is { } adapter)
        {
            parts.Add(adapter.DisplayName);
        }

        return string.Join(" · ", parts);
    }

    private MenuNode Folder(string name, IReadOnlyList<MenuNode> children, bool translate = true) => new()
    {
        Name = translate ? host.Tr(name) : name,
        CommandName = string.Empty,
        Children = children
    };

    private static MenuNode SwitchLeaf(string name, string commandName, string mode, string adapter) => new()
    {
        Name = name,
        CommandName = commandName,
        // The mode comes first: a host that applies only one value still gets the right action.
        Parameters = new Dictionary<string, string>
        {
            [BluetoothCommands.ModeParameter] = mode,
            [BluetoothCommands.AdapterParameter] = adapter
        }
    };

    private static MenuNode DeviceLeaf(string name, string commandName, string deviceId) => new()
    {
        Name = name,
        CommandName = commandName,
        Parameters = new Dictionary<string, string> { [BluetoothCommands.DeviceParameter] = deviceId }
    };
}

/// <summary>The order devices are listed in everywhere: connected first, then by name.</summary>
internal static class DeviceOrdering
{
    public static IEnumerable<BluetoothDeviceState> Sort(IEnumerable<BluetoothDeviceState> devices) =>
        devices
            .OrderByDescending(device => device.Connected)
            .ThenByDescending(device => device.Paired)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase);
}
