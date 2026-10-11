namespace LoupixDeck.Plugin.Bluetooth.State;

/// <summary>One Bluetooth adapter as the backend last read it.</summary>
/// <param name="Path">The backend's runtime handle (a BlueZ object path). Never persisted.</param>
/// <param name="Address">The adapter's MAC address; the stable part of every device id.</param>
internal sealed record BluetoothAdapterState(
    string Path,
    string Address,
    string Name,
    string Alias,
    bool Powered,
    bool Discovering)
{
    public string DisplayName => Alias.Length > 0 ? Alias : Name.Length > 0 ? Name : Address;
}

/// <summary>One known Bluetooth device as the backend last read it.</summary>
/// <param name="Path">The backend's runtime handle (a BlueZ object path). Never persisted.</param>
/// <param name="StableId">Adapter address and device address, see <see cref="DeviceIds"/>.</param>
/// <param name="AdapterId">The address of the adapter the device belongs to.</param>
internal sealed record BluetoothDeviceState(
    string Path,
    string StableId,
    string AdapterId,
    string Address,
    string Name,
    string Alias,
    bool Paired,
    bool Trusted,
    bool Connected,
    byte? BatteryPercent,
    short? Rssi,
    BluetoothDeviceKind Kind,
    DateTimeOffset LastSeen)
{
    public string DisplayName => Alias.Length > 0 ? Alias : Name.Length > 0 ? Name : Address;
}

/// <summary>
/// Everything the backend knows at one moment. Immutable: a change replaces the whole snapshot, so a
/// render path never sees a half-updated device.
/// </summary>
internal sealed record BluetoothSnapshot(
    bool IsAvailable,
    IReadOnlyList<BluetoothAdapterState> Adapters,
    IReadOnlyList<BluetoothDeviceState> Devices)
{
    public static BluetoothSnapshot Unavailable { get; } = new(false, [], []);

    /// <summary>Finds a device by its stable id, or by its bare address on any adapter.</summary>
    public BluetoothDeviceState? FindDevice(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Devices.FirstOrDefault(device => DeviceIds.Matches(id, device));
    }

    /// <summary>
    /// Finds an adapter by address. Without one it falls back to the preferred adapter, then to the
    /// first one, so a button that names no adapter works on a single-adapter machine.
    /// </summary>
    public BluetoothAdapterState? FindAdapter(string? address, string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            return Adapters.FirstOrDefault(adapter => string.Equals(adapter.Address, address.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(preferred) &&
            Adapters.FirstOrDefault(adapter => string.Equals(adapter.Address, preferred.Trim(), StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            return match;
        }

        return Adapters.Count > 0 ? Adapters[0] : null;
    }

    public BluetoothAdapterState? AdapterOf(BluetoothDeviceState device) =>
        Adapters.FirstOrDefault(adapter => string.Equals(adapter.Address, device.AdapterId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The stable device id: <c>&lt;adapter address&gt;/&lt;device address&gt;</c>. It contains no comma or
/// parenthesis, so it is safe as a command parameter. A bare device address is accepted too and
/// matches the device on any adapter.
/// </summary>
internal static class DeviceIds
{
    public const char Separator = '/';

    public static string Create(string adapterAddress, string deviceAddress) =>
        adapterAddress.ToUpperInvariant() + Separator + deviceAddress.ToUpperInvariant();

    public static bool Matches(string id, BluetoothDeviceState device)
    {
        string trimmed = id.Trim();

        return trimmed.Contains(Separator)
            ? string.Equals(trimmed, device.StableId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(trimmed, device.Address, StringComparison.OrdinalIgnoreCase);
    }
}
