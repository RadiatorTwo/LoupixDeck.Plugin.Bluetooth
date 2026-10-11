using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.Plugin.Bluetooth.Commands;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth;

/// <summary>
/// Shows and controls known Bluetooth devices. On Linux it talks to BlueZ over its D-Bus API; it
/// never runs bluetoothctl and only works with devices that are already paired.
/// </summary>
public sealed class BluetoothPlugin : LoupixPlugin, IMenuContributor, IPluginSettingsPage, IPluginRequirements
{
    /// <summary>How long the menu and the requirements wait for the first BlueZ snapshot.</summary>
    private static readonly TimeSpan FirstSnapshotWait = TimeSpan.FromSeconds(3);

    private readonly List<IPluginCommand> _commands = [];

    private IPluginHost? _host;
    private BluetoothSettings? _settings;
    private IBluetoothBackend? _backend;
    private BluetoothStateStore? _store;
    private BluetoothStateBinder? _binder;
    private BluetoothMenu? _menu;
    private BluetoothSettingsPage? _settingsPage;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "bluetooth",
        Name = "Bluetooth",
        Version = new Version(1, 0, 0),
        SdkVersion = SdkInfo.Version,
        Author = "RadiatorTwo",
        Description = "Connects, disconnects and shows paired Bluetooth devices through BlueZ over D-Bus.",
        Icon = LoadIcon()
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        BluetoothButtonLayouts.Translate = host.Tr;

        try
        {
            BluetoothSettings settings = new(host.Settings);
            _settings = settings;

            IBluetoothBackend backend = OperatingSystem.IsLinux()
                ? new BlueZBackend(host.Logger)
                : new UnsupportedBluetoothBackend();
            _backend = backend;

            BluetoothStateStore store = new(backend, settings, host.Logger);
            _store = store;

            List<StatefulCommand> stateful = [];
            BluetoothCapabilities capabilities = backend.Capabilities;

            if (capabilities.HasFlag(BluetoothCapabilities.AdapterPower))
            {
                _commands.Add(new AdapterPowerCommand(store));
                stateful.Add(new StatefulCommand(BluetoothCommands.Adapter, () => AdapterPowerCommand.StateFor(store)));
            }

            if (capabilities.HasFlag(BluetoothCapabilities.Discovery))
            {
                _commands.Add(new DiscoveryCommand(store));
                stateful.Add(new StatefulCommand(BluetoothCommands.Discovery, () => DiscoveryCommand.StateFor(store)));
            }

            _commands.AddRange(DeviceCommand.Create(store));

            if (capabilities.HasFlag(BluetoothCapabilities.Connect))
            {
                foreach (DeviceSlotCommand slot in DeviceSlotCommands.Create(store, settings))
                {
                    _commands.Add(slot);
                    stateful.Add(new StatefulCommand(slot.Descriptor.CommandName, () =>
                        slot.DeviceId is { } deviceId
                            ? DeviceConnectionStates.ToButtonState(store.StateOf(deviceId))
                            : DeviceConnectionStates.ToButtonState(DeviceConnectionState.Unavailable)));
                }
            }

            List<string> displayCommandNames =
            [
                .. _commands
                    .Where(command => command is IDisplayCommand or IValueDisplayCommand)
                    .Select(command => command.Descriptor.CommandName)
            ];

            _binder = new BluetoothStateBinder(host, store, stateful, displayCommandNames);
            _binder.Start();

            _menu = new BluetoothMenu(store, settings, host);
            _settingsPage = new BluetoothSettingsPage(store, host);

            // Connecting talks to the bus, so it must not hold up the host's Initialize call.
            _ = Task.Run(backend.StartAsync);
        }
        catch (Exception ex)
        {
            host.Logger.Error("Bluetooth: the plugin could not be initialized.", ex);
            Shutdown();
        }
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = BluetoothCommands.Group,
            Icon = BluetoothButtonLayouts.Group,
            Description = "Connect and show paired Bluetooth devices",
            Section = CommandGroupSection.Plugins
        }
    ];

    public async Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        // The menu may be opened before the first snapshot arrived; the host allows a few seconds.
        if (_backend is { } backend)
        {
            await Task.WhenAny(backend.Ready, Task.Delay(FirstSnapshotWait)).ConfigureAwait(false);
        }

        return _menu?.Build() ?? [];
    }

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema =>
        _settingsPage?.BuildSchema() ?? [];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
        _settingsPage?.BuildActions() ?? [];

    public void OnSettingsSaved()
    {
        // The favorites may now point at other devices, so every slot button is brought up to date.
        _binder?.ReplayAll();
    }

    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        if (!OperatingSystem.IsLinux())
        {
            return
            [
                new PluginRequirement
                {
                    Id = "bluetooth.platform",
                    Name = "Supported platform",
                    IsMet = false,
                    Message = "Bluetooth control is not supported on this platform yet. This version only supports Linux with BlueZ."
                }
            ];
        }

        // The host asks right after loading, off the startup path; without this wait the first
        // answer would report BlueZ as missing only because the bus was not read yet.
        _backend?.Ready.Wait(FirstSnapshotWait);

        bool available = _store?.Current.IsAvailable == true;

        return
        [
            new PluginRequirement
            {
                Id = "bluetooth.bluez",
                Name = "BlueZ",
                IsMet = available,
                Message = available
                    ? "BlueZ is reachable on the system bus."
                    : "BlueZ (org.bluez) is not reachable on the system bus.",
                InstallHint = available
                    ? null
                    : "Install BlueZ (package bluez) and start it with: sudo systemctl enable --now bluetooth"
            }
        ];
    }

    public override void Shutdown()
    {
        _binder?.Dispose();
        _binder = null;
        _menu = null;
        _settingsPage = null;
        _commands.Clear();
        _store?.Dispose();
        _store = null;
        _backend?.Dispose();
        _backend = null;
        _settings = null;
        _host = null;
    }

    /// <summary>The plugin icon (icon.png, embedded). Missing data only costs the icon.</summary>
    private static byte[]? LoadIcon()
    {
        using Stream? stream = typeof(BluetoothPlugin).Assembly.GetManifestResourceStream("LoupixDeck.Plugin.Bluetooth.icon.png");
        if (stream == null) return null;

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
