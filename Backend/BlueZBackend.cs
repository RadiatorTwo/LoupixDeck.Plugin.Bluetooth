using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;
using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Bluetooth.Backend;

/// <summary>
/// The Linux backend: BlueZ over its D-Bus API on the system bus. It never starts bluetoothctl.
/// <para>
/// State is event-driven. BlueZ publishes everything through the standard ObjectManager and
/// Properties interfaces, so one match rule per signal covers every adapter and device. A signal
/// only marks the snapshot dirty; a short debounce coalesces a burst (a reconnect, RSSI updates
/// during discovery) into one GetManagedObjects read. A full read keeps the snapshot consistent on
/// its own: a Battery1 interface that appears after its Device1, or vanishes before it, needs no
/// bookkeeping of its own.
/// </para>
/// </summary>
internal sealed class BlueZBackend : IBluetoothBackend
{
    private const string Service = BlueZConnection.Service;
    private const string RootPath = "/";
    private const string BlueZPath = "/org/bluez";
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";
    private const string AdapterInterface = "org.bluez.Adapter1";
    private const string DeviceInterface = "org.bluez.Device1";
    private const string BatteryInterface = "org.bluez.Battery1";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly BlueZConnection _connection;
    private readonly IPluginLogger _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Timer _debounce;
    private readonly List<IDisposable> _watches = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Serializes publishing, so a refresh that read the old BlueZ cannot overwrite its loss.</summary>
    private readonly Lock _publishLock = new();

    private volatile BluetoothSnapshot _current = BluetoothSnapshot.Unavailable;
    private int _generation;
    private volatile bool _disposed;

    public BlueZBackend(IPluginLogger logger)
    {
        _logger = logger;
        _connection = new BlueZConnection(logger);
        _debounce = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public BluetoothCapabilities Capabilities => BluetoothCapabilities.All;

    public BluetoothSnapshot Current => _current;

    public Task Ready => _ready.Task;

    public event Action? Changed;

    public async Task StartAsync()
    {
        _connection.ConnectionReady += OnConnectionReady;
        _connection.ConnectionLost += OnConnectionLost;

        if (!await _connection.ConnectAsync().ConfigureAwait(false))
        {
            Publish(BluetoothSnapshot.Unavailable, Volatile.Read(ref _generation));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _connection.ConnectionReady -= OnConnectionReady;
        _connection.ConnectionLost -= OnConnectionLost;
        _debounce.Dispose();

        // A subscription started by a reconnect may still be adding watches.
        bool locked = _refreshLock.Wait(TimeSpan.FromSeconds(2));
        try
        {
            DisposeWatches();
        }
        finally
        {
            if (locked)
            {
                _refreshLock.Release();
            }
        }

        _connection.Dispose();
    }

    public async Task<BluetoothCallOutcome> ConnectAsync(string deviceId, TimeSpan timeout)
    {
        // Connect only answers once a profile is up, which for a headset can take several seconds.
        BluetoothCallOutcome outcome = await CallDeviceAsync(deviceId, "Connect", timeout).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        return outcome;
    }

    public async Task<BluetoothCallOutcome> DisconnectAsync(string deviceId, TimeSpan timeout)
    {
        BluetoothCallOutcome outcome = await CallDeviceAsync(deviceId, "Disconnect", timeout).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        return outcome;
    }

    public async Task<BluetoothCallOutcome> SetTrustedAsync(string deviceId, bool trusted)
    {
        DBusClient? client = _connection.Client;

        if (client is null || !_current.IsAvailable)
        {
            return BluetoothCallOutcome.Unavailable;
        }

        if (_current.FindDevice(deviceId) is not { } device)
        {
            return BluetoothCallOutcome.NotFound;
        }

        BluetoothCallOutcome outcome = await SetBoolPropertyAsync(client, device.Path, DeviceInterface, "Trusted", trusted).ConfigureAwait(false);
        ScheduleRefresh();
        return outcome;
    }

    public async Task<BluetoothCallOutcome> SetPoweredAsync(string adapterAddress, bool powered)
    {
        DBusClient? client = _connection.Client;

        if (client is null || !_current.IsAvailable)
        {
            return BluetoothCallOutcome.Unavailable;
        }

        if (_current.FindAdapter(adapterAddress) is not { } adapter)
        {
            return BluetoothCallOutcome.NotFound;
        }

        BluetoothCallOutcome outcome = await SetBoolPropertyAsync(client, adapter.Path, AdapterInterface, "Powered", powered).ConfigureAwait(false);
        ScheduleRefresh();
        return outcome;
    }

    public Task<BluetoothCallOutcome> StartDiscoveryAsync(string adapterAddress) =>
        CallAdapterAsync(adapterAddress, "StartDiscovery");

    public Task<BluetoothCallOutcome> StopDiscoveryAsync(string adapterAddress) =>
        CallAdapterAsync(adapterAddress, "StopDiscovery");

    private async Task<BluetoothCallOutcome> CallDeviceAsync(string deviceId, string member, TimeSpan timeout)
    {
        DBusClient? client = _connection.Client;

        if (client is null || !_current.IsAvailable)
        {
            return BluetoothCallOutcome.Unavailable;
        }

        if (_current.FindDevice(deviceId) is not { } device)
        {
            return BluetoothCallOutcome.NotFound;
        }

        return await client.CallAsync(Service, device.Path, DeviceInterface, member, timeout: timeout).ConfigureAwait(false);
    }

    private async Task<BluetoothCallOutcome> CallAdapterAsync(string adapterAddress, string member)
    {
        DBusClient? client = _connection.Client;

        if (client is null || !_current.IsAvailable)
        {
            return BluetoothCallOutcome.Unavailable;
        }

        if (_current.FindAdapter(adapterAddress) is not { } adapter)
        {
            return BluetoothCallOutcome.NotFound;
        }

        BluetoothCallOutcome outcome = await client.CallAsync(Service, adapter.Path, AdapterInterface, member).ConfigureAwait(false);
        ScheduleRefresh();
        return outcome;
    }

    private static Task<BluetoothCallOutcome> SetBoolPropertyAsync(DBusClient client, string path, string @interface, string name, bool value) =>
        client.CallAsync(
            Service,
            path,
            DBusClient.PropertiesInterface,
            "Set",
            "ssv",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(@interface);
                writer.WriteString(name);
                writer.WriteVariantBool(value);
            });

    private void OnConnectionReady()
    {
        // Runs on the read loop or the reconnect task; subscribing and reading must not block either.
        _ = Task.Run(async () =>
        {
            await SubscribeAsync().ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        });
    }

    private void OnConnectionLost()
    {
        // Object paths and discovery sessions belong to the old BlueZ; nothing of it can be reused.
        // A refresh still reading the old BlueZ must not publish over this.
        Publish(BluetoothSnapshot.Unavailable, Interlocked.Increment(ref _generation));
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; nothing left to refresh.
        }
    }

    private async Task SubscribeAsync()
    {
        DBusClient? client = _connection.Client;

        if (client is null)
        {
            return;
        }

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            DisposeWatches();

            Add(await client.WatchMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    Path = RootPath,
                    Interface = ObjectManagerInterface,
                    Member = "InterfacesAdded"
                },
                ReadSignalPath,
                _ => ScheduleRefresh()).ConfigureAwait(false));

            Add(await client.WatchMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    Path = RootPath,
                    Interface = ObjectManagerInterface,
                    Member = "InterfacesRemoved"
                },
                ReadSignalPath,
                _ => ScheduleRefresh()).ConfigureAwait(false));

            // One rule for every adapter and device: all of them publish their changes through the
            // standard properties interface below /org/bluez.
            Add(await client.WatchMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    PathNamespace = BlueZPath,
                    Interface = DBusClient.PropertiesInterface,
                    Member = "PropertiesChanged"
                },
                ReadSignalPath,
                _ => ScheduleRefresh()).ConfigureAwait(false));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        int generation = Volatile.Read(ref _generation);
        DBusClient? client = _connection.Client;

        if (client is null)
        {
            Publish(BluetoothSnapshot.Unavailable, generation);
            return;
        }

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            DBusResult<ManagedObjects> result = await client.CallAsync(
                Service,
                RootPath,
                ObjectManagerInterface,
                "GetManagedObjects",
                ReadManagedObjects,
                []).ConfigureAwait(false);

            BluetoothSnapshot snapshot = result.IsSuccess
                ? BuildSnapshot(result.Value, _current)
                : BluetoothSnapshot.Unavailable;

            Publish(snapshot, generation);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: reading BlueZ failed: {ex.Message}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Publish(BluetoothSnapshot snapshot, int generation)
    {
        bool changed;

        lock (_publishLock)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                // Read from a BlueZ that has gone away since.
                return;
            }

            changed = !SameContent(_current, snapshot);

            if (changed)
            {
                _current = snapshot;
            }
        }

        _ready.TrySetResult();

        if (!changed)
        {
            return;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: a change handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a snapshot from a GetManagedObjects reply. A device keeps its old LastSeen when nothing
    /// about it changed, so LastSeen tells when BlueZ last reported anything for it.
    /// </summary>
    private static BluetoothSnapshot BuildSnapshot(ManagedObjects objects, BluetoothSnapshot previous)
    {
        Dictionary<string, BluetoothAdapterState> adaptersByPath = new(StringComparer.Ordinal);

        foreach ((string path, Dictionary<string, Dictionary<string, VariantValue>> interfaces) in objects)
        {
            if (interfaces.TryGetValue(AdapterInterface, out Dictionary<string, VariantValue>? properties))
            {
                adaptersByPath[path] = new BluetoothAdapterState(
                    path,
                    ReadString(properties, "Address").ToUpperInvariant(),
                    ReadString(properties, "Name"),
                    ReadString(properties, "Alias"),
                    ReadBool(properties, "Powered"),
                    ReadBool(properties, "Discovering"));
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<BluetoothDeviceState> devices = [];

        foreach ((string path, Dictionary<string, Dictionary<string, VariantValue>> interfaces) in objects)
        {
            if (!interfaces.TryGetValue(DeviceInterface, out Dictionary<string, VariantValue>? properties) ||
                !adaptersByPath.TryGetValue(ReadPath(properties, "Adapter"), out BluetoothAdapterState? adapter))
            {
                continue;
            }

            string address = ReadString(properties, "Address").ToUpperInvariant();
            byte? battery = interfaces.TryGetValue(BatteryInterface, out Dictionary<string, VariantValue>? batteryProperties) &&
                            batteryProperties.TryGetValue("Percentage", out VariantValue percentage)
                ? Unwrap(percentage).GetByte()
                : null;
            short? rssi = properties.TryGetValue("RSSI", out VariantValue rssiValue) ? Unwrap(rssiValue).GetInt16() : null;

            BluetoothDeviceState device = new(
                path,
                DeviceIds.Create(adapter.Address, address),
                adapter.Address,
                address,
                ReadString(properties, "Name"),
                ReadString(properties, "Alias"),
                ReadBool(properties, "Paired"),
                ReadBool(properties, "Trusted"),
                ReadBool(properties, "Connected"),
                battery,
                rssi,
                BluetoothDeviceKinds.FromIcon(ReadString(properties, "Icon")),
                now);

            if (previous.FindDevice(device.StableId) is { } old && old with { LastSeen = now } == device)
            {
                device = old;
            }

            devices.Add(device);
        }

        List<BluetoothAdapterState> adapters = [.. adaptersByPath.Values.OrderBy(adapter => adapter.Path, StringComparer.Ordinal)];
        devices.Sort((left, right) => string.Compare(left.StableId, right.StableId, StringComparison.Ordinal));

        return new BluetoothSnapshot(true, adapters, devices);
    }

    private static bool SameContent(BluetoothSnapshot left, BluetoothSnapshot right) =>
        left.IsAvailable == right.IsAvailable &&
        left.Adapters.SequenceEqual(right.Adapters) &&
        left.Devices.SequenceEqual(right.Devices);

    private void Add(IDisposable? watch)
    {
        if (watch is not null)
        {
            _watches.Add(watch);
        }
    }

    private void DisposeWatches()
    {
        foreach (IDisposable watch in _watches)
        {
            watch.Dispose();
        }

        _watches.Clear();
    }

    private static VariantValue Unwrap(VariantValue value) =>
        value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;

    private static string ReadString(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value) ? Unwrap(value).GetString() : string.Empty;

    private static bool ReadBool(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value) && Unwrap(value).GetBool();

    private static string ReadPath(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value) ? Unwrap(value).GetObjectPathAsString() : string.Empty;

    private static string ReadSignalPath(Message message, object? state) => message.PathAsString ?? string.Empty;

    /// <summary>
    /// Reads the a{oa{sa{sv}}} reply of GetManagedObjects. Only the three interfaces the plugin uses
    /// are kept; BlueZ also lists media, GATT and network objects.
    /// </summary>
    private static ManagedObjects ReadManagedObjects(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        ManagedObjects objects = [];

        ArrayEnd objectsEnd = reader.ReadDictionaryStart();
        while (reader.HasNext(objectsEnd))
        {
            string path = reader.ReadObjectPathAsString();
            Dictionary<string, Dictionary<string, VariantValue>> interfaces = new(StringComparer.Ordinal);

            ArrayEnd interfacesEnd = reader.ReadDictionaryStart();
            while (reader.HasNext(interfacesEnd))
            {
                string name = reader.ReadString();
                Dictionary<string, VariantValue> properties = reader.ReadDictionaryOfStringToVariantValue();

                if (name is AdapterInterface or DeviceInterface or BatteryInterface)
                {
                    interfaces[name] = properties;
                }
            }

            if (interfaces.Count > 0)
            {
                objects[path] = interfaces;
            }
        }

        return objects;
    }

    /// <summary>Object path, then interface name, then property name.</summary>
    private sealed class ManagedObjects : Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>
    {
        public ManagedObjects() : base(StringComparer.Ordinal)
        {
        }
    }
}
