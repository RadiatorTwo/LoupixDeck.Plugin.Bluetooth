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

        List<string> filterIds = FindAudioFilters(containerId);

        if (filterIds.Count == 0)
        {
            return null;
        }

        IMMDeviceEnumerator enumerator = CreateEnumerator();
        bool anySucceeded = false;

        try
        {
            foreach (string filterId in filterIds)
            {
                if (SendOneShot(enumerator, filterId, connect))
                {
                    anySucceeded = true;
                }
            }
        }
        finally
        {
            Release(enumerator);
        }

        return anySucceeded ? BluetoothCallOutcome.Ok : BluetoothCallOutcome.Failed;
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
    private List<string> FindAudioFilters(Guid containerId)
    {
        List<string> filterIds = [];
        IMMDeviceEnumerator enumerator = CreateEnumerator();
        IMMDeviceCollection? endpoints = null;

        try
        {
            if (enumerator.EnumAudioEndpoints(DataFlowAll, DeviceStateMaskAll, out endpoints) != SOk ||
                endpoints.GetCount(out uint count) != SOk)
            {
                return filterIds;
            }

            for (uint index = 0; index < count; index++)
            {
                if (endpoints.Item(index, out IMMDevice endpoint) != SOk)
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
            Release(enumerator);
        }

        return filterIds;
    }

    private static bool IsBluetoothFilter(string filterId) =>
        filterId.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
        filterId.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase);

    private static Guid ContainerIdOf(IMMDevice endpoint)
    {
        if (endpoint.OpenPropertyStore(StgmRead, out IPropertyStore properties) != SOk)
        {
            return Guid.Empty;
        }

        try
        {
            PropertyKey key = DeviceContainerIdKey;

            if (properties.GetValue(ref key, out PropVariant value) != SOk)
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
            Release(properties);
        }
    }

    /// <summary>The device id of the kernel streaming filter the endpoint's connector leads to.</summary>
    private static string? FilterIdOf(IMMDevice endpoint)
    {
        Guid iid = DeviceTopologyIid;

        if (endpoint.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object instance) != SOk)
        {
            return null;
        }

        IDeviceTopology topology = (IDeviceTopology)instance;
        IConnector? connector = null;

        try
        {
            if (topology.GetConnectorCount(out uint count) != SOk || count == 0 ||
                topology.GetConnector(0, out connector) != SOk)
            {
                return null;
            }

            return connector.GetDeviceIdConnectedTo(out string filterId) == SOk ? filterId : null;
        }
        finally
        {
            Release(connector);
            Release(topology);
        }
    }

    private bool SendOneShot(IMMDeviceEnumerator enumerator, string filterId, bool connect)
    {
        if (enumerator.GetDevice(filterId, out IMMDevice filter) != SOk)
        {
            return false;
        }

        object? instance = null;

        try
        {
            Guid iid = KsControlIid;

            if (filter.Activate(ref iid, ClsctxAll, IntPtr.Zero, out instance) != SOk)
            {
                return false;
            }

            KsProperty property = new()
            {
                Set = KsPropSetIdBtAudio,
                Id = connect ? KsPropertyOneShotReconnect : KsPropertyOneShotDisconnect,
                Flags = KsPropertyTypeGet
            };

            int result = ((IKsControl)instance).KsProperty(ref property, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);

            if (result != SOk)
            {
                logger.Info($"Bluetooth: the audio filter refused the {(connect ? "reconnect" : "disconnect")} (0x{result:X8}).");
            }

            return result == SOk;
        }
        finally
        {
            Release(instance);
            Release(filter);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();
}
