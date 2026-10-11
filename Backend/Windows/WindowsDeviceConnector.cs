using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LoupixDeck.PluginSdk;
using Microsoft.Win32.SafeHandles;
using static LoupixDeck.Plugin.Bluetooth.Backend.Windows.WindowsNative;

namespace LoupixDeck.Plugin.Bluetooth.Backend.Windows;

/// <summary>
/// Connects and disconnects devices on Windows, which has no general API for it.
/// <list type="bullet">
/// <item>Audio devices: the Bluetooth audio filter driver's one-shot reconnect and disconnect
/// properties (KSPROPSETID_BtAudio), reached through the Core Audio endpoints of the device.</item>
/// <item>Other classic devices: switching their installed services off and on with
/// BluetoothSetServiceState, which makes Windows drop or open the connection.</item>
/// <item>Bluetooth LE devices: Windows connects them on its own; not supported.</item>
/// </list>
/// Every method blocks and must run off the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDeviceConnector(IPluginLogger logger)
{
    /// <summary>Asks the audio filters of a device to connect or disconnect. Null when it has none.</summary>
    public BluetoothCallOutcome? TryAudio(Guid containerId, bool connect)
    {
        if (containerId == Guid.Empty)
        {
            return null;
        }

        if (CreateDeviceEnumerator(out IntPtr enumerator) != SOk)
        {
            return null;
        }

        try
        {
            List<string> filterIds = FindAudioFilters(enumerator, containerId);

            if (filterIds.Count == 0)
            {
                return null;
            }

            bool anySucceeded = false;

            foreach (string filterId in filterIds)
            {
                if (SendOneShot(enumerator, filterId, connect))
                {
                    anySucceeded = true;
                }
            }

            return anySucceeded ? BluetoothCallOutcome.Ok : BluetoothCallOutcome.Failed;
        }
        finally
        {
            Release(enumerator);
        }
    }

    /// <summary>
    /// Switches every installed service of a classic device off and back on, which makes Windows
    /// open the connection again. For a disconnect, <paramref name="waitForDisconnect"/> runs while
    /// the services are off, so the device is down before they come back.
    /// </summary>
    public async Task<BluetoothCallOutcome> CycleServicesAsync(ulong address, Func<Task>? waitForDisconnect)
    {
        BluetoothFindRadioParams parameters = new() { Size = (uint)Marshal.SizeOf<BluetoothFindRadioParams>() };
        IntPtr find = BluetoothFindFirstRadio(ref parameters, out SafeFileHandle radio);

        if (find == IntPtr.Zero)
        {
            radio.Dispose();
            return BluetoothCallOutcome.Unavailable;
        }

        BluetoothFindRadioClose(find);

        using (radio)
        {
            BluetoothDeviceInfo device = BluetoothDeviceInfo.For(address);
            uint error = BluetoothGetDeviceInfo(radio, ref device);

            if (error != ErrorSuccess)
            {
                return MapError(error);
            }

            Guid[] services = InstalledServices(radio, ref device);

            if (services.Length == 0)
            {
                return BluetoothCallOutcome.NotSupported;
            }

            BluetoothCallOutcome outcome = SetServices(radio, ref device, services, BluetoothServiceDisable);

            if (outcome != BluetoothCallOutcome.Ok)
            {
                return outcome;
            }

            if (waitForDisconnect is not null)
            {
                await waitForDisconnect().ConfigureAwait(false);
            }

            return SetServices(radio, ref device, services, BluetoothServiceEnable);
        }
    }

    private BluetoothCallOutcome SetServices(SafeFileHandle radio, ref BluetoothDeviceInfo device, Guid[] services, uint flags)
    {
        BluetoothCallOutcome outcome = BluetoothCallOutcome.Ok;

        foreach (Guid service in services)
        {
            Guid current = service;
            uint error = BluetoothSetServiceState(radio, ref device, ref current, flags);

            if (error != ErrorSuccess)
            {
                logger.Info($"Bluetooth: switching service {service} of {device.Name} {(flags == BluetoothServiceEnable ? "on" : "off")} failed with error {error}.");
                outcome = MapError(error);
            }
        }

        return outcome;
    }

    private static Guid[] InstalledServices(SafeFileHandle radio, ref BluetoothDeviceInfo device)
    {
        uint count = 0;
        uint error = BluetoothEnumerateInstalledServices(radio, ref device, ref count, null);

        if ((error != ErrorSuccess && error != ErrorMoreData) || count == 0)
        {
            return [];
        }

        Guid[] services = new Guid[count];
        error = BluetoothEnumerateInstalledServices(radio, ref device, ref count, services);

        return error == ErrorSuccess ? services[..(int)Math.Min(count, (uint)services.Length)] : [];
    }

    private static BluetoothCallOutcome MapError(uint error) => error switch
    {
        ErrorSuccess => BluetoothCallOutcome.Ok,
        ErrorAccessDenied => BluetoothCallOutcome.PermissionDenied,
        ErrorNotFound => BluetoothCallOutcome.NotFound,
        _ => BluetoothCallOutcome.Failed
    };

    /// <summary>
    /// The device ids of the Bluetooth audio filters behind every endpoint of the device. A headset
    /// has several (stereo playback, hands-free playback and microphone), each with its own filter.
    /// </summary>
    private List<string> FindAudioFilters(IntPtr enumerator, Guid containerId)
    {
        List<string> filterIds = [];
        IntPtr endpoints = IntPtr.Zero;

        try
        {
            if (EnumAudioEndpoints(enumerator, DataFlowAll, DeviceStateMaskAll, out endpoints) != SOk ||
                GetCount(endpoints, out uint count) != SOk)
            {
                return filterIds;
            }

            for (uint index = 0; index < count; index++)
            {
                if (Item(endpoints, index, out IntPtr endpoint) != SOk)
                {
                    continue;
                }

                try
                {
                    if (ContainerIdOf(endpoint) == containerId &&
                        FilterIdOf(endpoint) is { } filterId &&
                        IsBluetoothFilter(filterId) &&
                        !filterIds.Contains(filterId, StringComparer.OrdinalIgnoreCase))
                    {
                        filterIds.Add(filterId);
                    }
                }
                finally
                {
                    Release(endpoint);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth: reading the audio endpoints failed: {ex.Message}");
        }
        finally
        {
            Release(endpoints);
        }

        return filterIds;
    }

    private static bool IsBluetoothFilter(string filterId) =>
        filterId.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
        filterId.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase);

    private static Guid ContainerIdOf(IntPtr endpoint)
    {
        if (OpenPropertyStore(endpoint, out IntPtr store) != SOk)
        {
            return Guid.Empty;
        }

        try
        {
            if (GetValue(store, DeviceContainerIdKey, out PropVariant value) != SOk)
            {
                return Guid.Empty;
            }

            try
            {
                return value.VarType == VtClsid && value.Value != IntPtr.Zero
                    ? Marshal.PtrToStructure<Guid>(value.Value)
                    : Guid.Empty;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    /// <summary>The device id of the kernel streaming filter the endpoint's connector leads to.</summary>
    private static string? FilterIdOf(IntPtr endpoint)
    {
        if (Activate(endpoint, DeviceTopologyIid, out IntPtr topology) != SOk)
        {
            return null;
        }

        IntPtr connector = IntPtr.Zero;

        try
        {
            if (GetConnectorCount(topology, out uint count) != SOk || count == 0 ||
                GetConnector(topology, 0, out connector) != SOk)
            {
                return null;
            }

            return GetDeviceIdConnectedTo(connector, out string? filterId) == SOk ? filterId : null;
        }
        finally
        {
            Release(connector);
            Release(topology);
        }
    }

    private bool SendOneShot(IntPtr enumerator, string filterId, bool connect)
    {
        if (GetDevice(enumerator, filterId, out IntPtr filter) != SOk)
        {
            return false;
        }

        IntPtr control = IntPtr.Zero;

        try
        {
            if (Activate(filter, KsControlIid, out control) != SOk)
            {
                return false;
            }

            KsProperty property = new()
            {
                Set = KsPropSetIdBtAudio,
                Id = connect ? KsPropertyOneShotReconnect : KsPropertyOneShotDisconnect,
                Flags = KsPropertyTypeGet
            };

            int result = KsPropertyCall(control, property);

            if (result != SOk)
            {
                logger.Info($"Bluetooth: the audio filter refused the {(connect ? "reconnect" : "disconnect")} (0x{result:X8}).");
            }

            return result == SOk;
        }
        finally
        {
            Release(control);
            Release(filter);
        }
    }
}
