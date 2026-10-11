using System.Collections.Concurrent;
using LoupixDeck.Plugin.Bluetooth.Backend;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.State;

/// <summary>How an action ended, with a reason when it failed.</summary>
internal readonly record struct BluetoothActionResult(BluetoothCallOutcome Outcome)
{
    public bool IsSuccess => Outcome == BluetoothCallOutcome.Ok;

    /// <summary>The English text a button shows for the result.</summary>
    public string Text => BluetoothErrors.ToEnglishText(Outcome);
}

/// <summary>
/// The plugin's view of Bluetooth: the backend's latest snapshot plus what the plugin keeps itself:
/// the operations it is running (BlueZ has no Connecting property), the failures it remembers and
/// the discovery sessions it started. Commands act through it.
/// <para>
/// Everything that reads state reads cached data, so it is safe to call from a render path.
/// </para>
/// </summary>
internal sealed class BluetoothStateStore : IDisposable
{
    /// <summary>How long a failure stays visible before the button falls back to the real state.</summary>
    private static readonly TimeSpan FailureDuration = TimeSpan.FromSeconds(5);

    /// <summary>How long a new discovery session is kept before the snapshot has to confirm it.</summary>
    private static readonly TimeSpan DiscoveryStartGrace = TimeSpan.FromSeconds(3);

    private readonly IBluetoothBackend _backend;
    private readonly BluetoothSettings _settings;
    private readonly IPluginLogger _logger;

    /// <summary>The operation running per device, by stable id.</summary>
    private readonly ConcurrentDictionary<string, DeviceConnectionState> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The discovery sessions this plugin started, by adapter address.</summary>
    private readonly ConcurrentDictionary<string, DiscoverySession> _discoveries = new(StringComparer.OrdinalIgnoreCase);

    public BluetoothStateStore(IBluetoothBackend backend, BluetoothSettings settings, IPluginLogger logger)
    {
        _backend = backend;
        _settings = settings;
        _logger = logger;

        _backend.Changed += OnBackendChanged;
    }

    /// <summary>Raised on a background thread after the snapshot or plugin-side state changed.</summary>
    public event Action? Changed;

    public BluetoothSnapshot Current => _backend.Current;

    public IBluetoothBackend Backend => _backend;

    public BluetoothCapabilities Capabilities => _backend.Capabilities;

    /// <summary>The last error any action ended with, in English, or null.</summary>
    public string? LastError { get; private set; }

    public void Dispose()
    {
        _backend.Changed -= OnBackendChanged;

        foreach (DiscoverySession session in _discoveries.Values)
        {
            session.Dispose();
        }

        _discoveries.Clear();
    }

    /// <summary>The adapter a button means: its own address, or the preferred adapter, or the first one.</summary>
    public BluetoothAdapterState? ResolveAdapter(string? address) =>
        Current.FindAdapter(address, _settings.PreferredAdapter);

    /// <summary>The state a device is in, with the plugin's own operations and failures taking precedence.</summary>
    public DeviceConnectionState StateOf(string? deviceId) => StateOf(deviceId, Current);

    public DeviceConnectionState StateOf(string? deviceId, BluetoothSnapshot snapshot)
    {
        if (!snapshot.IsAvailable || snapshot.FindDevice(deviceId) is not { } device)
        {
            return DeviceConnectionState.Unavailable;
        }

        if (_pending.TryGetValue(device.StableId, out DeviceConnectionState pending))
        {
            return pending;
        }

        if (_failures.TryGetValue(device.StableId, out Failure failure) && !failure.IsExpired)
        {
            return DeviceConnectionState.Failed;
        }

        if (device.Connected)
        {
            return DeviceConnectionState.Connected;
        }

        // Nothing connects through an adapter that is off.
        return snapshot.AdapterOf(device) is { Powered: true }
            ? DeviceConnectionState.Disconnected
            : DeviceConnectionState.Unavailable;
    }

    /// <summary>The reason of a remembered failure, or null.</summary>
    public string? FailureOf(string stableId) =>
        _failures.TryGetValue(stableId, out Failure failure) && !failure.IsExpired ? failure.Reason : null;

    /// <summary>
    /// Raises <see cref="Changed"/> after something outside the snapshot changed what the buttons
    /// show, such as the favorites list behind the slots.
    /// </summary>
    public void NotifySettingsChanged() => RaiseChanged();

    public Task<BluetoothActionResult> ToggleAsync(string deviceId) =>
        DeviceConnectionStates.IsUp(StateOf(deviceId)) ? DisconnectAsync(deviceId) : ConnectAsync(deviceId);

    /// <summary>Connects a device, retrying once when the settings ask for it.</summary>
    public async Task<BluetoothActionResult> ConnectAsync(string deviceId)
    {
        if (Current.FindDevice(deviceId) is not { } device)
        {
            return Fail(null, BluetoothCallOutcome.NotFound);
        }

        if (!_pending.TryAdd(device.StableId, DeviceConnectionState.Connecting))
        {
            return new BluetoothActionResult(BluetoothCallOutcome.InProgress);
        }

        _failures.TryRemove(device.StableId, out _);
        RaiseChanged();

        BluetoothCallOutcome outcome;
        try
        {
            outcome = await _backend.ConnectAsync(device.StableId, _settings.ConnectTimeout).ConfigureAwait(false);

            if (outcome != BluetoothCallOutcome.Ok && _settings.RetryOnce && IsRetryable(outcome))
            {
                _logger.Info($"Bluetooth: connecting {device.DisplayName} failed ({BluetoothErrors.ToEnglishText(outcome)}), trying once more.");
                outcome = await _backend.ConnectAsync(device.StableId, _settings.ConnectTimeout).ConfigureAwait(false);
            }

            if (outcome == BluetoothCallOutcome.Ok && _settings.AutoTrust && Current.FindDevice(device.StableId) is { Trusted: false })
            {
                BluetoothCallOutcome trusted = await _backend.SetTrustedAsync(device.StableId, true).ConfigureAwait(false);

                if (trusted != BluetoothCallOutcome.Ok)
                {
                    _logger.Info($"Bluetooth: could not trust {device.DisplayName}: {BluetoothErrors.ToEnglishText(trusted)}.");
                }
            }
        }
        finally
        {
            _pending.TryRemove(device.StableId, out _);
        }

        return Finish(device.StableId, outcome);
    }

    public async Task<BluetoothActionResult> DisconnectAsync(string deviceId)
    {
        if (Current.FindDevice(deviceId) is not { } device)
        {
            return Fail(null, BluetoothCallOutcome.NotFound);
        }

        if (!_pending.TryAdd(device.StableId, DeviceConnectionState.Disconnecting))
        {
            return new BluetoothActionResult(BluetoothCallOutcome.InProgress);
        }

        _failures.TryRemove(device.StableId, out _);
        RaiseChanged();

        BluetoothCallOutcome outcome;
        try
        {
            outcome = await _backend.DisconnectAsync(device.StableId, _settings.ConnectTimeout).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(device.StableId, out _);
        }

        return Finish(device.StableId, outcome);
    }

    public async Task<BluetoothActionResult> SetTrustedAsync(string deviceId, bool trusted)
    {
        if (Current.FindDevice(deviceId) is not { } device)
        {
            return Fail(null, BluetoothCallOutcome.NotFound);
        }

        BluetoothCallOutcome outcome = await _backend.SetTrustedAsync(device.StableId, trusted).ConfigureAwait(false);
        return outcome == BluetoothCallOutcome.Ok ? new BluetoothActionResult(outcome) : Fail(null, outcome);
    }

    /// <summary>Turns an adapter on or off; null flips it.</summary>
    public async Task<BluetoothActionResult> SetPoweredAsync(string? adapterAddress, bool? powered)
    {
        if (ResolveAdapter(adapterAddress) is not { } adapter)
        {
            return Fail(null, Current.IsAvailable ? BluetoothCallOutcome.NotFound : BluetoothCallOutcome.Unavailable);
        }

        BluetoothCallOutcome outcome = await _backend.SetPoweredAsync(adapter.Address, powered ?? !adapter.Powered).ConfigureAwait(false);
        return outcome == BluetoothCallOutcome.Ok ? new BluetoothActionResult(outcome) : Fail(null, outcome);
    }

    /// <summary>Whether this plugin runs a discovery session on the adapter right now.</summary>
    public bool IsDiscovering(BluetoothAdapterState adapter) => _discoveries.ContainsKey(adapter.Address);

    /// <summary>How long the plugin's discovery on the adapter still runs, or null.</summary>
    public TimeSpan? DiscoveryRemaining(BluetoothAdapterState adapter)
    {
        if (!_discoveries.TryGetValue(adapter.Address, out DiscoverySession? session))
        {
            return null;
        }

        TimeSpan remaining = session.EndsAt - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Starts, stops or (null) flips a time-limited discovery on an adapter.</summary>
    public async Task<BluetoothActionResult> SetDiscoveryAsync(string? adapterAddress, bool? start)
    {
        if (ResolveAdapter(adapterAddress) is not { } adapter)
        {
            return Fail(null, Current.IsAvailable ? BluetoothCallOutcome.NotFound : BluetoothCallOutcome.Unavailable);
        }

        bool shouldStart = start ?? !IsDiscovering(adapter);

        if (!shouldStart)
        {
            return await StopDiscoveryAsync(adapter.Address).ConfigureAwait(false);
        }

        if (!adapter.Powered)
        {
            return Fail(null, BluetoothCallOutcome.AdapterOff);
        }

        if (IsDiscovering(adapter))
        {
            return new BluetoothActionResult(BluetoothCallOutcome.Ok);
        }

        BluetoothCallOutcome outcome = await _backend.StartDiscoveryAsync(adapter.Address).ConfigureAwait(false);

        if (outcome != BluetoothCallOutcome.Ok)
        {
            return Fail(null, outcome);
        }

        TimeSpan duration = _settings.DiscoveryDuration;
        string address = adapter.Address;
        DiscoverySession session = new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + duration, new Timer(_ => _ = StopDiscoveryAsync(address), null, duration, Timeout.InfiniteTimeSpan));

        if (_discoveries.TryAdd(address, session))
        {
            RaiseChanged();
        }
        else
        {
            session.Dispose();
        }

        return new BluetoothActionResult(BluetoothCallOutcome.Ok);
    }

    private async Task<BluetoothActionResult> StopDiscoveryAsync(string adapterAddress)
    {
        if (_discoveries.TryRemove(adapterAddress, out DiscoverySession? session))
        {
            session.Dispose();
            RaiseChanged();
        }

        // BlueZ refuses to stop a discovery another client started; this plugin only stops its own.
        if (session is null)
        {
            return new BluetoothActionResult(BluetoothCallOutcome.Ok);
        }

        BluetoothCallOutcome outcome = await _backend.StopDiscoveryAsync(adapterAddress).ConfigureAwait(false);

        // A discovery that already ended (the adapter went off, BlueZ restarted) is what was asked for.
        return outcome is BluetoothCallOutcome.Ok or BluetoothCallOutcome.Failed or BluetoothCallOutcome.NotFound or BluetoothCallOutcome.Unavailable
            ? new BluetoothActionResult(BluetoothCallOutcome.Ok)
            : Fail(null, outcome);
    }

    /// <summary>Errors that a second attempt cannot fix.</summary>
    private static bool IsRetryable(BluetoothCallOutcome outcome) => outcome is
        BluetoothCallOutcome.Failed or BluetoothCallOutcome.Timeout or BluetoothCallOutcome.InProgress;

    private BluetoothActionResult Finish(string stableId, BluetoothCallOutcome outcome)
    {
        if (outcome == BluetoothCallOutcome.Ok)
        {
            RaiseChanged();
            return new BluetoothActionResult(outcome);
        }

        return Fail(stableId, outcome);
    }

    /// <summary>Remembers a failure; with a device it shows as Failed on its buttons for a few seconds.</summary>
    private BluetoothActionResult Fail(string? stableId, BluetoothCallOutcome outcome)
    {
        string reason = BluetoothErrors.ToEnglishText(outcome);
        LastError = reason;

        // A device that is simply gone reads as unavailable rather than as failed.
        if (stableId is not null && outcome != BluetoothCallOutcome.NotFound)
        {
            _failures[stableId] = new Failure(reason, DateTimeOffset.UtcNow + FailureDuration);

            _ = Task.Delay(FailureDuration).ContinueWith(_ =>
            {
                if (_failures.TryGetValue(stableId, out Failure failure) && failure.IsExpired)
                {
                    _failures.TryRemove(stableId, out Failure _);
                    RaiseChanged();
                }
            }, TaskScheduler.Default);
        }

        RaiseChanged();
        return new BluetoothActionResult(outcome);
    }

    private void OnBackendChanged()
    {
        BluetoothSnapshot snapshot = Current;

        // BlueZ ends a discovery session when the adapter goes off or BlueZ restarts; the plugin's
        // timer then has nothing left to stop.
        foreach (string address in _discoveries.Keys)
        {
            // The first snapshots after the start may still predate Discovering turning true.
            if (_discoveries.TryGetValue(address, out DiscoverySession? running) &&
                DateTimeOffset.UtcNow - running.StartedAt > DiscoveryStartGrace &&
                snapshot.FindAdapter(address) is not { Powered: true, Discovering: true } &&
                _discoveries.TryRemove(address, out DiscoverySession? session))
            {
                session.Dispose();
            }
        }

        // A device that came up after all no longer counts as failed.
        foreach (string stableId in _failures.Keys)
        {
            if (snapshot.FindDevice(stableId) is { Connected: true })
            {
                _failures.TryRemove(stableId, out _);
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bluetooth: a state handler failed: {ex.Message}");
        }
    }

    private readonly record struct Failure(string Reason, DateTimeOffset Expires)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= Expires;
    }

    private sealed record DiscoverySession(DateTimeOffset StartedAt, DateTimeOffset EndsAt, Timer Timer) : IDisposable
    {
        public void Dispose() => Timer.Dispose();
    }
}
