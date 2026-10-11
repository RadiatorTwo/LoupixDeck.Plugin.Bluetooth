using System.Collections.Concurrent;
using System.Runtime.Versioning;
using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace LoupixDeck.Plugin.Bluetooth.Backend.Windows;

/// <summary>
/// The Windows backend: the WinRT Bluetooth, radio and device enumeration APIs, plus
/// <see cref="WindowsDeviceConnector"/> for connecting and disconnecting, which WinRT cannot do.
/// <para>
/// State is event-driven. A device watcher over the paired association endpoints (classic and LE)
/// reports every added, removed and changed device, including its connection state; the radio
/// reports power changes. A short debounce coalesces a burst into one snapshot, like the BlueZ
/// backend does. Windows has one adapter and no trust flag.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WinRtBluetoothBackend : IBluetoothBackend
{
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string IsPairedProperty = "System.Devices.Aep.IsPaired";
    private const string SignalStrengthProperty = "System.Devices.Aep.SignalStrength";
    private const string ContainerIdProperty = "System.Devices.Aep.ContainerId";
    private const string ProtocolIdProperty = "System.Devices.Aep.ProtocolId";

    /// <summary>DEVPKEY_Bluetooth_Battery: the level Windows' Bluetooth settings page shows.</summary>
    private const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private const DevicePairingKinds PairingKinds =
        DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin;

    private static readonly Guid LowEnergyProtocol = new("BB7BB05E-5972-42B5-94FC-76EAA7084D49");
    private static readonly string[] RequestedProperties =
        [AddressProperty, IsConnectedProperty, IsPairedProperty, SignalStrengthProperty, ContainerIdProperty, ProtocolIdProperty];

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan BatteryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WatcherRestartDelay = TimeSpan.FromSeconds(2);

    private readonly IPluginLogger _logger;
    private readonly WindowsDeviceConnector _connector;
    private readonly Timer _debounce;
    private readonly Timer _batteryTimer;
    private readonly SemaphoreSlim _adapterLock = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Guards the adapter, the radio, the watchers and the endpoint maps below.</summary>
    private readonly Lock _lock = new();

    /// <summary>Serializes publishing, so two refreshes cannot overwrite each other out of order.</summary>
    private readonly Lock _publishLock = new();

    private readonly Dictionary<string, DeviceInformation> _paired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceInformation> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BluetoothDeviceKind> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte> _batteries = new();

    private BluetoothAdapter? _adapter;
    private Radio? _radio;
    private DeviceWatcher? _adapterWatcher;
    private DeviceWatcher? _pairedWatcher;
    private DeviceWatcher? _discoveryWatcher;
    private bool _adapterLoaded;
    private bool _pairedEnumerated;
    private int _batteryBusy;
    private volatile BluetoothSnapshot _current = BluetoothSnapshot.Unavailable;
    private volatile bool _disposed;

    public WinRtBluetoothBackend(IPluginLogger logger)
    {
        _logger = logger;
        WinRtComWrappers.InitializeWithoutGlobalRegistration(logger);
        _connector = new WindowsDeviceConnector(logger);
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        _batteryTimer = new Timer(_ => _ = RefreshBatteriesAsync(), null, BatteryInterval, BatteryInterval);
    }

    public BluetoothCapabilities Capabilities =>
        BluetoothCapabilities.AdapterPower | BluetoothCapabilities.Connect | BluetoothCapabilities.Discovery |
        BluetoothCapabilities.Battery | BluetoothCapabilities.Pairing;

    public BluetoothSnapshot Current => _current;

    public Task Ready => _ready.Task;

    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            await LoadAdapterAsync().ConfigureAwait(false);

            DeviceWatcher adapterWatcher = DeviceInformation.CreateWatcher(BluetoothAdapter.GetDeviceSelector());
            adapterWatcher.Added += (_, _) => OnAdaptersChanged();
            adapterWatcher.Removed += (_, _) => OnAdaptersChanged();

            DeviceWatcher pairedWatcher = CreateDeviceWatcher(paired: true, _paired);
            pairedWatcher.EnumerationCompleted += (_, _) =>
            {
                lock (_lock)
                {
                    _pairedEnumerated = true;
                }

                ScheduleRefresh();
            };
            pairedWatcher.Stopped += (watcher, _) => OnPairedWatcherStopped(watcher);

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _adapterWatcher = adapterWatcher;
                _pairedWatcher = pairedWatcher;
            }

            adapterWatcher.Start();
            pairedWatcher.Start();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: the Windows Bluetooth API could not be started: {ex.Message}");
            Publish(BluetoothSnapshot.Unavailable);
        }
    }

    public void Dispose()
    {
        DeviceWatcher? adapterWatcher;
        DeviceWatcher? pairedWatcher;
        DeviceWatcher? discoveryWatcher;

        lock (_lock)
        {
            _disposed = true;
            adapterWatcher = _adapterWatcher;
            pairedWatcher = _pairedWatcher;
            discoveryWatcher = _discoveryWatcher;
            _adapterWatcher = _pairedWatcher = _discoveryWatcher = null;
            SwapRadio(null);
        }

        StopWatcher(adapterWatcher);
        StopWatcher(pairedWatcher);
        StopWatcher(discoveryWatcher);
        _debounce.Dispose();
        _batteryTimer.Dispose();
    }

    public Task<BluetoothCallOutcome> ConnectAsync(string deviceId, TimeSpan timeout) =>
        ChangeConnectionAsync(deviceId, connect: true, timeout);

    public Task<BluetoothCallOutcome> DisconnectAsync(string deviceId, TimeSpan timeout) =>
        ChangeConnectionAsync(deviceId, connect: false, timeout);

    /// <summary>Windows has no trust flag; a paired device may always reconnect.</summary>
    public Task<BluetoothCallOutcome> SetTrustedAsync(string deviceId, bool trusted) =>
        Task.FromResult(BluetoothCallOutcome.NotSupported);

    public async Task<BluetoothCallOutcome> SetPoweredAsync(string adapterAddress, bool powered)
    {
        Radio? radio;

        lock (_lock)
        {
            radio = _radio;
        }

        if (radio is null || !_current.IsAvailable)
        {
            return BluetoothCallOutcome.Unavailable;
        }

        if (_current.FindAdapter(adapterAddress) is null)
        {
            return BluetoothCallOutcome.NotFound;
        }

        // Disabled is the airplane mode or a hardware switch; the radio cannot be turned on from here.
        if (radio.State == RadioState.Disabled)
        {
            return BluetoothCallOutcome.Blocked;
        }

        try
        {
            RadioAccessStatus status = await radio.SetStateAsync(powered ? RadioState.On : RadioState.Off).AsTask().ConfigureAwait(false);
            ScheduleRefresh();

            return status switch
            {
                RadioAccessStatus.Allowed => BluetoothCallOutcome.Ok,
                RadioAccessStatus.DeniedByUser or RadioAccessStatus.DeniedBySystem => BluetoothCallOutcome.PermissionDenied,
                _ => BluetoothCallOutcome.Failed
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: switching the radio {(powered ? "on" : "off")} failed: {ex.Message}");
            return Map(ex);
        }
    }

    public Task<BluetoothCallOutcome> StartDiscoveryAsync(string adapterAddress)
    {
        DeviceWatcher? watcher = null;

        lock (_lock)
        {
            if (_adapter is null || _disposed)
            {
                return Task.FromResult(BluetoothCallOutcome.Unavailable);
            }

            if (_radio?.State != RadioState.On)
            {
                return Task.FromResult(BluetoothCallOutcome.AdapterOff);
            }

            if (_discoveryWatcher is null)
            {
                watcher = CreateDeviceWatcher(paired: false, _found);
                _discoveryWatcher = watcher;
            }
        }

        try
        {
            watcher?.Start();
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _discoveryWatcher = null;
            }

            _logger.Warn($"Bluetooth: starting the discovery failed: {ex.Message}");
            return Task.FromResult(Map(ex));
        }

        ScheduleRefresh();
        return Task.FromResult(BluetoothCallOutcome.Ok);
    }

    public Task<BluetoothCallOutcome> StopDiscoveryAsync(string adapterAddress)
    {
        DeviceWatcher? watcher;

        lock (_lock)
        {
            watcher = _discoveryWatcher;
            _discoveryWatcher = null;
        }

        // The devices it found stay listed, like the ones BlueZ keeps after a discovery.
        StopWatcher(watcher);
        ScheduleRefresh();
        return Task.FromResult(BluetoothCallOutcome.Ok);
    }

    public async Task<BluetoothCallOutcome> PairAsync(string deviceId, IPairingPrompt prompt, TimeSpan timeout, CancellationToken cancellation)
    {
        if (!_current.IsAvailable)
        {
            return BluetoothCallOutcome.Unavailable;
        }

        if (_current.FindDevice(deviceId) is not { } device)
        {
            return BluetoothCallOutcome.NotFound;
        }

        if (device.Paired)
        {
            return BluetoothCallOutcome.Ok;
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);

        try
        {
            DeviceInformation info = await DeviceInformation.CreateFromIdAsync(device.Path).AsTask(limit.Token).ConfigureAwait(false);
            DeviceInformationCustomPairing custom = info.Pairing.Custom;
            TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler =
                (_, args) => OnPairingRequested(args, prompt, limit.Token);

            custom.PairingRequested += handler;
            try
            {
                DevicePairingResult result = await custom.PairAsync(PairingKinds, DevicePairingProtectionLevel.Default)
                    .AsTask(limit.Token)
                    .ConfigureAwait(false);
                ScheduleRefresh();
                return Map(result.Status);
            }
            finally
            {
                custom.PairingRequested -= handler;
            }
        }
        catch (OperationCanceledException)
        {
            return cancellation.IsCancellationRequested ? BluetoothCallOutcome.Cancelled : BluetoothCallOutcome.Timeout;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: pairing {device.DisplayName} failed: {ex.Message}");
            return Map(ex);
        }
    }

    /// <summary>Answers Windows' pairing ceremony from the folder. Runs on a background thread.</summary>
    private async void OnPairingRequested(DevicePairingRequestedEventArgs args, IPairingPrompt prompt, CancellationToken cancellation)
    {
        try
        {
            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    args.Accept();
                    break;

                case DevicePairingKinds.DisplayPin:
                    prompt.ShowCode(args.Pin);
                    args.Accept();
                    break;

                case DevicePairingKinds.ConfirmPinMatch:
                {
                    Deferral deferral = args.GetDeferral();
                    try
                    {
                        if (await prompt.ConfirmAsync(args.Pin, cancellation).ConfigureAwait(false))
                        {
                            args.Accept();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Not accepting rejects the pairing.
                    }
                    finally
                    {
                        deferral.Complete();
                    }

                    break;
                }

                default:
                    // Not accepting rejects the pairing; the session shows why.
                    prompt.Unsupported("Entering a PIN is not supported");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: answering the pairing request failed: {ex.Message}");
        }
    }

    private async Task<BluetoothCallOutcome> ChangeConnectionAsync(string deviceId, bool connect, TimeSpan timeout)
    {
        BluetoothSnapshot snapshot = _current;

        if (!snapshot.IsAvailable)
        {
            return BluetoothCallOutcome.Unavailable;
        }

        if (snapshot.FindDevice(deviceId) is not { } device)
        {
            return BluetoothCallOutcome.NotFound;
        }

        if (device.Connected == connect)
        {
            return BluetoothCallOutcome.Ok;
        }

        if (connect && snapshot.AdapterOf(device) is { Powered: false })
        {
            return BluetoothCallOutcome.AdapterOff;
        }

        if (WindowsAddress.Parse(device.Address) is not { } address)
        {
            return BluetoothCallOutcome.NotFound;
        }

        (Guid containerId, bool lowEnergy) = Describe(device.Path);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        try
        {
            // The audio and the classic API block, so they run off the caller's thread.
            BluetoothCallOutcome? audio = await Task.Run(() => _connector.TryAudio(containerId, connect)).ConfigureAwait(false);

            if (audio is not BluetoothCallOutcome.Ok)
            {
                if (lowEnergy)
                {
                    return audio ?? BluetoothCallOutcome.NotSupported;
                }

                bool wentDown = false;
                BluetoothCallOutcome services = await Task.Run(() => _connector.CycleServicesAsync(
                    address,
                    connect ? null : async () => wentDown = await WaitForAsync(device.StableId, false, deadline).ConfigureAwait(false))).ConfigureAwait(false);

                if (services != BluetoothCallOutcome.Ok)
                {
                    return services;
                }

                // A device whose services came back on may reconnect on its own right away; it went down.
                if (!connect)
                {
                    return wentDown ? BluetoothCallOutcome.Ok : BluetoothCallOutcome.Timeout;
                }
            }

            return await WaitForAsync(device.StableId, connect, deadline).ConfigureAwait(false)
                ? BluetoothCallOutcome.Ok
                : BluetoothCallOutcome.Timeout;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: {(connect ? "connecting" : "disconnecting")} {device.DisplayName} failed: {ex.Message}");
            return Map(ex);
        }
    }

    /// <summary>Waits until the watcher reports the device in the wanted connection state.</summary>
    private async Task<bool> WaitForAsync(string stableId, bool connected, DateTimeOffset deadline)
    {
        while (true)
        {
            if (_current.FindDevice(stableId)?.Connected == connected)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline || _disposed)
            {
                return false;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    private (Guid ContainerId, bool LowEnergy) Describe(string endpointId)
    {
        lock (_lock)
        {
            if (!_paired.TryGetValue(endpointId, out DeviceInformation? info) && !_found.TryGetValue(endpointId, out info))
            {
                return (Guid.Empty, false);
            }

            return (Read<Guid>(info, ContainerIdProperty) ?? Guid.Empty, Read<Guid>(info, ProtocolIdProperty) == LowEnergyProtocol);
        }
    }

    private DeviceWatcher CreateDeviceWatcher(bool paired, Dictionary<string, DeviceInformation> target)
    {
        string selector =
            $"({BluetoothDevice.GetDeviceSelectorFromPairingState(paired)}) OR ({BluetoothLEDevice.GetDeviceSelectorFromPairingState(paired)})";
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(selector, RequestedProperties, DeviceInformationKind.AssociationEndpoint);

        watcher.Added += (_, info) =>
        {
            lock (_lock)
            {
                target[info.Id] = info;
            }

            LoadKind(info);
            ScheduleRefresh();
        };
        watcher.Updated += (_, update) =>
        {
            lock (_lock)
            {
                if (target.TryGetValue(update.Id, out DeviceInformation? info))
                {
                    info.Update(update);
                }
            }

            ScheduleRefresh();
        };
        watcher.Removed += (_, update) =>
        {
            lock (_lock)
            {
                target.Remove(update.Id);
            }

            ScheduleRefresh();
        };

        return watcher;
    }

    private void OnPairedWatcherStopped(DeviceWatcher watcher)
    {
        if (_disposed || watcher.Status != DeviceWatcherStatus.Aborted)
        {
            return;
        }

        // The watcher dies with the Bluetooth service; it is started again once the service is back.
        _logger.Info("Bluetooth: the device watcher was aborted, restarting it.");
        _ = Task.Delay(WatcherRestartDelay).ContinueWith(_ =>
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed || _pairedWatcher != watcher)
                    {
                        return;
                    }

                    _paired.Clear();
                    _pairedEnumerated = false;
                }

                watcher.Start();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Bluetooth: restarting the device watcher failed: {ex.Message}");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Reads the device's class or appearance once; the icon follows when it arrives.</summary>
    private void LoadKind(DeviceInformation info)
    {
        if (!_kinds.TryAdd(info.Id, BluetoothDeviceKind.Other))
        {
            return;
        }

        bool lowEnergy = Read<Guid>(info, ProtocolIdProperty) == LowEnergyProtocol;
        string id = info.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                BluetoothDeviceKind kind = BluetoothDeviceKind.Other;

                if (lowEnergy)
                {
                    using BluetoothLEDevice? device = await BluetoothLEDevice.FromIdAsync(id).AsTask().ConfigureAwait(false);
                    if (device is not null)
                    {
                        kind = BluetoothDeviceKinds.FromAppearance(device.Appearance.RawValue);
                    }
                }
                else
                {
                    using BluetoothDevice? device = await BluetoothDevice.FromIdAsync(id).AsTask().ConfigureAwait(false);
                    if (device is not null)
                    {
                        kind = BluetoothDeviceKinds.FromClassOfDevice(device.ClassOfDevice.RawValue);
                    }
                }

                if (kind != BluetoothDeviceKind.Other)
                {
                    _kinds[id] = kind;
                    ScheduleRefresh();
                }
            }
            catch (Exception)
            {
                // Without a class the device keeps the generic icon.
            }
        });
    }

    private void OnAdaptersChanged()
    {
        bool loaded;

        lock (_lock)
        {
            loaded = _adapterLoaded && !_disposed;
        }

        if (loaded)
        {
            _ = Task.Run(LoadAdapterAsync);
        }
    }

    private async Task LoadAdapterAsync()
    {
        await _adapterLock.WaitAsync().ConfigureAwait(false);

        try
        {
            BluetoothAdapter? adapter = null;
            Radio? radio = null;

            try
            {
                adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().ConfigureAwait(false);

                if (adapter is not null)
                {
                    radio = await adapter.GetRadioAsync().AsTask().ConfigureAwait(false);

                    // A desktop app is normally allowed; without it the radio still reports its state.
                    await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Bluetooth: reading the Bluetooth adapter failed: {ex.Message}");
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _adapter = adapter;
                _adapterLoaded = true;
                SwapRadio(radio);
            }

            if (adapter is null)
            {
                _logger.Info("Bluetooth: no Bluetooth adapter found.");
            }
        }
        finally
        {
            _adapterLock.Release();
        }

        ScheduleRefresh();
    }

    /// <summary>Replaces the watched radio. Called under <see cref="_lock"/>.</summary>
    private void SwapRadio(Radio? radio)
    {
        if (_radio is not null)
        {
            _radio.StateChanged -= OnRadioStateChanged;
        }

        _radio = radio;

        if (radio is not null)
        {
            radio.StateChanged += OnRadioStateChanged;
        }
    }

    private void OnRadioStateChanged(Radio sender, object args) => ScheduleRefresh();

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
            // Shutting down.
        }
    }

    private void Refresh()
    {
        try
        {
            BluetoothSnapshot previous = _current;
            BluetoothSnapshot snapshot;

            lock (_lock)
            {
                if (_disposed || !_adapterLoaded)
                {
                    return;
                }

                if (_adapter is null)
                {
                    snapshot = BluetoothSnapshot.Unavailable;
                }
                else if (!_pairedEnumerated)
                {
                    // The first snapshot waits for the paired devices, so it does not show an empty list.
                    return;
                }
                else
                {
                    snapshot = BuildSnapshot(_adapter, previous);
                }
            }

            Publish(snapshot);

            if (snapshot.Devices.Any(device => device.Connected && previous.FindDevice(device.StableId) is not { Connected: true }))
            {
                _ = RefreshBatteriesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: reading the Bluetooth state failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a snapshot from the watched endpoints. Called under <see cref="_lock"/>. A paired
    /// endpoint wins over a found one with the same address. A device keeps its old LastSeen when
    /// nothing about it changed.
    /// </summary>
    private BluetoothSnapshot BuildSnapshot(BluetoothAdapter adapter, BluetoothSnapshot previous)
    {
        string adapterAddress = WindowsAddress.Format(adapter.BluetoothAddress);
        BluetoothAdapterState adapterState = new(
            adapter.DeviceId,
            adapterAddress,
            _radio?.Name ?? string.Empty,
            string.Empty,
            _radio?.State == RadioState.On,
            _discoveryWatcher is not null);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Dictionary<string, BluetoothDeviceState> devices = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceInformation info in _paired.Values.Concat(_found.Values))
        {
            if (WindowsAddress.Parse(Read(info, AddressProperty)) is not { } raw)
            {
                continue;
            }

            string address = WindowsAddress.Format(raw);

            if (devices.ContainsKey(address))
            {
                continue;
            }

            bool connected = Read<bool>(info, IsConnectedProperty) ?? false;
            byte? battery = connected && _batteries.TryGetValue(Read<Guid>(info, ContainerIdProperty) ?? Guid.Empty, out byte level) ? level : null;
            short? rssi = Read<int>(info, SignalStrengthProperty) is int signal ? (short)Math.Clamp(signal, short.MinValue, short.MaxValue) : null;

            BluetoothDeviceState device = new(
                info.Id,
                DeviceIds.Create(adapterAddress, address),
                adapterAddress,
                address,
                info.Name ?? string.Empty,
                string.Empty,
                Read<bool>(info, IsPairedProperty) ?? false,
                false,
                connected,
                battery,
                rssi,
                _kinds.GetValueOrDefault(info.Id, BluetoothDeviceKind.Other),
                now);

            if (previous.FindDevice(device.StableId) is { } old && old with { LastSeen = now } == device)
            {
                device = old;
            }

            devices[address] = device;
        }

        List<BluetoothDeviceState> sorted = [.. devices.Values];
        sorted.Sort((left, right) => string.Compare(left.StableId, right.StableId, StringComparison.Ordinal));

        return new BluetoothSnapshot(true, [adapterState], sorted);
    }

    /// <summary>Reads the battery level of every connected paired device from its device node.</summary>
    private async Task RefreshBatteriesAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _batteryBusy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            List<Guid> containers;

            lock (_lock)
            {
                containers =
                [
                    .. _paired.Values
                        .Where(info => Read<bool>(info, IsConnectedProperty) == true)
                        .Select(info => Read<Guid>(info, ContainerIdProperty) ?? Guid.Empty)
                        .Where(container => container != Guid.Empty)
                        .Distinct()
                ];
            }

            bool changed = false;

            foreach (Guid container in containers)
            {
                byte? level = await ReadBatteryAsync(container).ConfigureAwait(false);

                if (level is byte value)
                {
                    changed |= !_batteries.TryGetValue(container, out byte old) || old != value;
                    _batteries[container] = value;
                }
                else
                {
                    changed |= _batteries.TryRemove(container, out _);
                }
            }

            if (changed)
            {
                ScheduleRefresh();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: reading the battery levels failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _batteryBusy, 0);
        }
    }

    private static async Task<byte?> ReadBatteryAsync(Guid container)
    {
        DeviceInformationCollection nodes = await DeviceInformation.FindAllAsync(
            $"System.Devices.ContainerId:=\"{container:B}\"",
            [BatteryProperty],
            DeviceInformationKind.Device).AsTask().ConfigureAwait(false);

        foreach (DeviceInformation node in nodes)
        {
            if (node.Properties.TryGetValue(BatteryProperty, out object? value) && value is byte level && level <= 100)
            {
                return level;
            }
        }

        return null;
    }

    private void Publish(BluetoothSnapshot snapshot)
    {
        bool changed;

        lock (_publishLock)
        {
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

    private static bool SameContent(BluetoothSnapshot left, BluetoothSnapshot right) =>
        left.IsAvailable == right.IsAvailable &&
        left.Adapters.SequenceEqual(right.Adapters) &&
        left.Devices.SequenceEqual(right.Devices);

    private static T? Read<T>(DeviceInformation info, string key) where T : struct =>
        info.Properties.TryGetValue(key, out object? value) && value is T typed ? typed : null;

    private static string? Read(DeviceInformation info, string key) =>
        info.Properties.TryGetValue(key, out object? value) ? value as string : null;

    private static void StopWatcher(DeviceWatcher? watcher)
    {
        if (watcher?.Status is not (DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted))
        {
            return;
        }

        try
        {
            watcher.Stop();
        }
        catch (Exception)
        {
            // Already stopping.
        }
    }

    private static BluetoothCallOutcome Map(DevicePairingResultStatus status) => status switch
    {
        DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => BluetoothCallOutcome.Ok,
        DevicePairingResultStatus.AuthenticationTimeout => BluetoothCallOutcome.Timeout,
        DevicePairingResultStatus.AuthenticationFailure or
            DevicePairingResultStatus.RejectedByHandler or
            DevicePairingResultStatus.ConnectionRejected => BluetoothCallOutcome.Rejected,
        DevicePairingResultStatus.PairingCanceled => BluetoothCallOutcome.Cancelled,
        DevicePairingResultStatus.AccessDenied or DevicePairingResultStatus.AuthenticationNotAllowed => BluetoothCallOutcome.PermissionDenied,
        DevicePairingResultStatus.OperationAlreadyInProgress => BluetoothCallOutcome.InProgress,
        _ => BluetoothCallOutcome.Failed
    };

    private static BluetoothCallOutcome Map(Exception exception) => exception switch
    {
        UnauthorizedAccessException => BluetoothCallOutcome.PermissionDenied,
        TimeoutException => BluetoothCallOutcome.Timeout,
        OperationCanceledException => BluetoothCallOutcome.Cancelled,
        _ => BluetoothCallOutcome.Failed
    };
}
