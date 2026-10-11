using LoupixDeck.PluginSdk;
using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Bluetooth.Backend;

/// <summary>
/// Owns the system bus connection and keeps it alive. Nothing here knows anything about Bluetooth.
/// <para>
/// A missing bus or a stopped BlueZ is not an error: the backend simply reports itself as
/// unavailable until BlueZ appears on the bus.
/// </para>
/// </summary>
internal sealed class BlueZConnection(IPluginLogger logger) : IDisposable
{
    public const string Service = "org.bluez";

    private const string BusService = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string BusInterface = "org.freedesktop.DBus";

    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30)
    ];

    private readonly CancellationTokenSource _shutdown = new();

    private DBusConnection? _connection;
    private IDisposable? _nameOwnerWatch;

    /// <summary>The D-Bus helper, or null while the bus is not connected.</summary>
    public DBusClient? Client { get; private set; }

    /// <summary>True while the system bus is connected. BlueZ itself may still be absent.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Raised once the bus is usable and again whenever BlueZ (re)appears on it. Everything
    /// read from the bus has to be rebuilt here: object paths belong to the old BlueZ.
    /// </summary>
    public event Action? ConnectionReady;

    /// <summary>Raised when the bus or BlueZ went away. The cached state is stale from then on.</summary>
    public event Action? ConnectionLost;

    /// <summary>Connects to the system bus. Safe to call once, from the backend's start.</summary>
    public async Task<bool> ConnectAsync()
    {
        string? address = DBusAddress.System;

        if (string.IsNullOrEmpty(address))
        {
            logger.Info("Bluetooth: no system bus address, BlueZ stays unavailable.");
            return false;
        }

        DBusConnection connection = new(address);

        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);

            if (_shutdown.IsCancellationRequested)
            {
                // The plugin was shut down while this (re)connect was under way.
                connection.Dispose();
                return false;
            }

            _connection = connection;
            Client = new DBusClient(connection, logger);
            _nameOwnerWatch = await WatchServiceOwnerAsync().ConfigureAwait(false);
            IsConnected = true;

            _ = Task.Run(() => MonitorConnectionAsync(connection), _shutdown.Token);
            RaiseConnectionReady();
            return true;
        }
        catch (Exception ex)
        {
            logger.Info($"Bluetooth: cannot use the system bus ({ex.Message}), BlueZ stays unavailable.");
            Disconnect();
            connection.Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _nameOwnerWatch?.Dispose();
        _nameOwnerWatch = null;
        Disconnect();
        _shutdown.Dispose();
    }

    private void RaiseConnectionReady()
    {
        try
        {
            ConnectionReady?.Invoke();
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth: a connection handler failed: {ex.Message}");
        }
    }

    private void RaiseConnectionLost()
    {
        try
        {
            ConnectionLost?.Invoke();
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth: a disconnect handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Watches BlueZ's bus name. A restart of BlueZ leaves the bus connection
    /// intact, so this is the only way to notice it.
    /// </summary>
    private Task<IDisposable?> WatchServiceOwnerAsync()
    {
        DBusClient? client = Client;

        if (client is null)
        {
            return Task.FromResult<IDisposable?>(null);
        }

        return client.WatchSignalAsync(
            BusService,
            BusPath,
            BusInterface,
            "NameOwnerChanged",
            ReadNameOwnerChanged,
            OnServiceOwnerChanged);
    }

    private static (string Name, string NewOwner) ReadNameOwnerChanged(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        string name = reader.ReadString();
        reader.ReadString(); // old owner
        string newOwner = reader.ReadString();
        return (name, newOwner);
    }

    private void OnServiceOwnerChanged((string Name, string NewOwner) change)
    {
        if (!string.Equals(change.Name, Service, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrEmpty(change.NewOwner))
        {
            logger.Info("Bluetooth: BlueZ went away.");
            RaiseConnectionLost();
            return;
        }

        logger.Info("Bluetooth: BlueZ is back, the cached state is rebuilt.");
        RaiseConnectionReady();
    }

    private async Task MonitorConnectionAsync(DBusConnection connection)
    {
        Exception? error;
        try
        {
            error = await connection.DisconnectedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        logger.Warn($"Bluetooth: the system bus connection was lost ({error?.Message ?? "unknown reason"}), reconnecting.");
        IsConnected = false;
        RaiseConnectionLost();

        // Only the reconnect is retried; the lost connection is not waited on again. A successful
        // reconnect starts a monitor of its own, so this one ends there.
        for (int attempt = 0; !_shutdown.IsCancellationRequested; attempt++)
        {
            TimeSpan delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];

            try
            {
                await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (await ReconnectAsync().ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> ReconnectAsync()
    {
        _nameOwnerWatch?.Dispose();
        _nameOwnerWatch = null;
        _connection?.Dispose();
        _connection = null;
        Client = null;

        return await ConnectAsync().ConfigureAwait(false);
    }

    private void Disconnect()
    {
        IsConnected = false;
        Client = null;
        _connection?.Dispose();
        _connection = null;
    }
}
