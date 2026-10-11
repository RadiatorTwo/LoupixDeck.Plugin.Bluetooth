using System.Text;
using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.Plugin.Bluetooth.Commands;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth;

/// <summary>
/// The plugin's settings form. The host renders the descriptors, so only the values are declared
/// here. Labels and descriptions are English keys the host translates.
/// <para>
/// The SDK has no pick list, so devices and adapters are entered by id or address; "List devices"
/// prints them.
/// </para>
/// </summary>
internal sealed class BluetoothSettingsPage(BluetoothStateStore store, IPluginHost host)
{
    public IReadOnlyList<PluginSettingDescriptor> BuildSchema() =>
    [
        new PluginSettingDescriptor
        {
            Key = "heading:devices",
            Label = "Devices",
            Kind = PluginSettingKind.Heading
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.FavoritesKey,
            Label = "Favorite devices",
            Kind = PluginSettingKind.Text,
            Description = "The devices behind the device slots, in slot order, separated by commas. Device ids or addresses; \"List devices\" prints them.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.HiddenKey,
            Label = "Hidden devices",
            Kind = PluginSettingKind.Text,
            Description = "Devices kept out of the command menu and the device folder, separated by commas. Device ids or addresses.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.PairedOnlyKey,
            Label = "Show paired devices only",
            Kind = PluginSettingKind.Toggle,
            Description = "Off: devices found by a discovery stay listed in the device folder after the discovery ends.",
            DefaultValue = BluetoothSettings.DefaultPairedOnly
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.PreferredAdapterKey,
            Label = "Preferred adapter",
            Kind = PluginSettingKind.Text,
            Description = "The address of the adapter used when a button names none. Empty uses the first adapter.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = "heading:connection",
            Label = "Connection",
            Kind = PluginSettingKind.Heading
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.ConnectTimeoutKey,
            Label = "Connection timeout (s)",
            Kind = PluginSettingKind.Number,
            Description = "How long connecting or disconnecting a device may take before it counts as failed (5 to 120).",
            DefaultValue = BluetoothSettings.DefaultConnectTimeoutSeconds
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.RetryOnceKey,
            Label = "Retry a failed connection once",
            Kind = PluginSettingKind.Toggle,
            Description = "On: a connection that failed or timed out is tried a second time before the button shows Failed.",
            DefaultValue = BluetoothSettings.DefaultRetryOnce
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.AutoTrustKey,
            Label = "Trust connected devices",
            Kind = PluginSettingKind.Toggle,
            Description = "On: a device the deck connected successfully is marked as trusted.",
            DefaultValue = BluetoothSettings.DefaultAutoTrust
        },
        new PluginSettingDescriptor
        {
            Key = "heading:discovery",
            Label = "Discovery",
            Kind = PluginSettingKind.Heading
        },
        new PluginSettingDescriptor
        {
            Key = BluetoothSettings.DiscoveryDurationKey,
            Label = "Discovery duration (s)",
            Kind = PluginSettingKind.Number,
            Description = "How long a discovery started from the deck runs before it stops on its own (5 to 300).",
            DefaultValue = BluetoothSettings.DefaultDiscoveryDurationSeconds
        }
    ];

    public IReadOnlyList<PluginSettingAction> BuildActions() =>
    [
        new PluginSettingAction
        {
            Label = "List devices",
            Invoke = ListDevicesAsync
        },
        new PluginSettingAction
        {
            Label = "Check BlueZ",
            Invoke = CheckAsync
        }
    ];

    /// <summary>Prints every adapter and device with the id the text fields above accept.</summary>
    private Task<string> ListDevicesAsync()
    {
        BluetoothSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return Task.FromResult(host.Tr(BluetoothErrors.ToEnglishText(BluetoothCallOutcome.Unavailable)));
        }

        StringBuilder builder = new();

        foreach (BluetoothAdapterState adapter in snapshot.Adapters)
        {
            builder.AppendLine($"{host.Tr("Adapter")} {adapter.DisplayName} — {adapter.Address}");
        }

        foreach (BluetoothDeviceState device in DeviceOrdering.Sort(snapshot.Devices.Where(device => device.Paired)))
        {
            string state = DeviceDisplay.StateText(store, store.StateOf(device.StableId, snapshot), device, host);
            builder.AppendLine($"{device.DisplayName} · {host.Tr(BluetoothDeviceKinds.ToEnglishText(device.Kind))} · {state} — {device.StableId}");
        }

        if (!snapshot.Devices.Any(device => device.Paired))
        {
            builder.AppendLine(host.Tr("No paired devices"));
        }

        return Task.FromResult(builder.ToString());
    }

    private Task<string> CheckAsync()
    {
        BluetoothSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return Task.FromResult(host.Tr(BluetoothErrors.ToEnglishText(BluetoothCallOutcome.Unavailable)));
        }

        BluetoothAdapterState? adapter = store.ResolveAdapter(null);
        string adapterText = adapter is null
            ? host.Tr("No adapter")
            : $"{adapter.DisplayName}: {host.Tr(adapter.Powered ? "On" : "Off")}";

        string text =
            $"{host.Tr("BlueZ is reachable")} · {adapterText} · " +
            $"{snapshot.Devices.Count(device => device.Paired)} {host.Tr("paired devices")} · " +
            $"{snapshot.Devices.Count(device => device.Connected)} {host.Tr("connected")}";

        if (store.LastError is { } error)
        {
            text += $" · {host.Tr("Last error")}: {host.Tr(error)}";
        }

        return Task.FromResult(text);
    }
}
