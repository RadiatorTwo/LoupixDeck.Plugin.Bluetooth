using LoupixDeck.Plugin.Bluetooth.State;

namespace LoupixDeck.Plugin.Bluetooth.Backend;

/// <summary>
/// What a backend can do. The command picker only offers what the active backend supports, so a
/// later Windows backend can leave out connect and disconnect without the deck showing dead buttons.
/// </summary>
[Flags]
internal enum BluetoothCapabilities
{
    None = 0,
    AdapterPower = 1,
    Connect = 2,
    Trust = 4,
    Discovery = 8,
    Battery = 16,
    All = AdapterPower | Connect | Trust | Discovery | Battery
}

/// <summary>
/// The platform side of the plugin. Everything above it (commands, displays, the folder and the
/// menu) only sees <see cref="BluetoothSnapshot"/> and stable device ids, so a second backend can be
/// added without touching them.
/// <para>
/// Actions report how the request ended. The transitional states (connecting, disconnecting) are
/// kept above it, in <see cref="BluetoothStateStore"/>.
/// </para>
/// </summary>
internal interface IBluetoothBackend : IDisposable
{
    BluetoothCapabilities Capabilities { get; }

    /// <summary>The latest snapshot. Never null; <see cref="BluetoothSnapshot.Unavailable"/> while not connected.</summary>
    BluetoothSnapshot Current { get; }

    /// <summary>Completes once the first snapshot (available or not) has been published.</summary>
    Task Ready { get; }

    /// <summary>Raised on a background thread whenever <see cref="Current"/> was replaced.</summary>
    event Action? Changed;

    /// <summary>Connects and loads the first snapshot. Never throws.</summary>
    Task StartAsync();

    /// <summary>Connects a device and returns once BlueZ answered or <paramref name="timeout"/> ran out.</summary>
    Task<BluetoothCallOutcome> ConnectAsync(string deviceId, TimeSpan timeout);

    Task<BluetoothCallOutcome> DisconnectAsync(string deviceId, TimeSpan timeout);

    Task<BluetoothCallOutcome> SetTrustedAsync(string deviceId, bool trusted);

    Task<BluetoothCallOutcome> SetPoweredAsync(string adapterAddress, bool powered);

    Task<BluetoothCallOutcome> StartDiscoveryAsync(string adapterAddress);

    Task<BluetoothCallOutcome> StopDiscoveryAsync(string adapterAddress);
}

/// <summary>The backend for a platform the plugin has no implementation for yet.</summary>
internal sealed class UnsupportedBluetoothBackend : IBluetoothBackend
{
    public BluetoothCapabilities Capabilities => BluetoothCapabilities.None;

    public BluetoothSnapshot Current => BluetoothSnapshot.Unavailable;

    public Task Ready => Task.CompletedTask;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task StartAsync() => Task.CompletedTask;

    public Task<BluetoothCallOutcome> ConnectAsync(string deviceId, TimeSpan timeout) => NotSupported();

    public Task<BluetoothCallOutcome> DisconnectAsync(string deviceId, TimeSpan timeout) => NotSupported();

    public Task<BluetoothCallOutcome> SetTrustedAsync(string deviceId, bool trusted) => NotSupported();

    public Task<BluetoothCallOutcome> SetPoweredAsync(string adapterAddress, bool powered) => NotSupported();

    public Task<BluetoothCallOutcome> StartDiscoveryAsync(string adapterAddress) => NotSupported();

    public Task<BluetoothCallOutcome> StopDiscoveryAsync(string adapterAddress) => NotSupported();

    public void Dispose()
    {
    }

    private static Task<BluetoothCallOutcome> NotSupported() => Task.FromResult(BluetoothCallOutcome.NotSupported);
}
