using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace LoupixDeck.Plugin.Bluetooth.Backend.Windows;

/// <summary>
/// The Win32 and COM declarations the Windows backend needs beyond WinRT: the Core Audio and
/// kernel streaming interfaces behind <see cref="WindowsDeviceConnector"/>'s audio path, and the
/// classic Bluetooth API (bthprops.cpl) behind its service path.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsNative
{
    public const int SOk = 0;
    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorMoreData = 234;
    public const uint ErrorNotFound = 1168;

    public const uint BluetoothServiceDisable = 0;
    public const uint BluetoothServiceEnable = 1;

    public const uint ClsctxAll = 0x17;
    public const uint StgmRead = 0;
    public const uint DeviceStateMaskAll = 0xF;
    public const int DataFlowAll = 2;
    public const ushort VtClsid = 72;
    public const uint KsPropertyTypeGet = 1;

    public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid MMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid DeviceTopologyIid = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    public static readonly Guid KsControlIid = new("28F54685-06FD-11D2-B27A-00A0C9223196");

    /// <summary>KSPROPSETID_BtAudio: the property set of the Bluetooth audio filter drivers.</summary>
    public static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    public const uint KsPropertyOneShotReconnect = 0;
    public const uint KsPropertyOneShotDisconnect = 1;

    /// <summary>PKEY_Device_ContainerId: ties an audio endpoint to the physical device.</summary>
    public static readonly PropertyKey DeviceContainerIdKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>The PROPVARIANT head; only the pointer-sized value slot is read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Value2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BluetoothFindRadioParams
    {
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected;
        public int Remembered;
        public int Authenticated;
        public SystemTime LastSeen;
        public SystemTime LastUsed;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;

        public static BluetoothDeviceInfo For(ulong address) => new()
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(),
            Address = address,
            Name = string.Empty
        };
    }

    // The classic Bluetooth API only exists in wide-character form and without an A/W suffix.

    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams parameters, out SafeFileHandle radio);

    [DllImport("bthprops.cpl", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("bthprops.cpl", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint BluetoothGetDeviceInfo(SafeFileHandle radio, ref BluetoothDeviceInfo device);

    [DllImport("bthprops.cpl", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint BluetoothEnumerateInstalledServices(
        SafeFileHandle radio,
        ref BluetoothDeviceInfo device,
        ref uint serviceCount,
        [Out] Guid[]? services);

    [DllImport("bthprops.cpl", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint BluetoothSetServiceState(SafeFileHandle radio, ref BluetoothDeviceInfo device, ref Guid service, uint serviceFlags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(ref PropVariant value);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern unsafe int CoCreateInstance(Guid* classId, IntPtr outer, uint context, Guid* interfaceId, IntPtr* instance);

    // The COM calls below go straight through the vtable instead of [ComImport] interfaces. Built-in
    // COM marshalling turns every interface pointer into an object through the process-wide
    // ComWrappers instance once anyone registered one (CsWinRT in another plugin does), and the
    // cast to a [ComImport] interface then fails. Raw pointers do not depend on that.
    // Slot numbers follow the interface declarations in mmdeviceapi.h, devicetopology.h and ks.h;
    // slots 0 to 2 are IUnknown.

    private static unsafe void** VTable(IntPtr instance) => *(void***)instance;

    public static unsafe void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)VTable(instance)[2])(instance);
        }
    }

    public static unsafe int CreateDeviceEnumerator(out IntPtr enumerator)
    {
        Guid classId = MMDeviceEnumeratorClsid;
        Guid interfaceId = MMDeviceEnumeratorIid;
        IntPtr result;
        int hr = CoCreateInstance(&classId, IntPtr.Zero, ClsctxAll, &interfaceId, &result);
        enumerator = hr == SOk ? result : IntPtr.Zero;
        return hr;
    }

    /// <summary>IMMDeviceEnumerator::EnumAudioEndpoints.</summary>
    public static unsafe int EnumAudioEndpoints(IntPtr enumerator, int dataFlow, uint stateMask, out IntPtr collection)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)VTable(enumerator)[3])(enumerator, dataFlow, stateMask, &result);
        collection = hr == SOk ? result : IntPtr.Zero;
        return hr;
    }

    /// <summary>IMMDeviceEnumerator::GetDevice.</summary>
    public static unsafe int GetDevice(IntPtr enumerator, string id, out IntPtr device)
    {
        IntPtr result;
        int hr;

        fixed (char* text = id)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)VTable(enumerator)[5])(enumerator, text, &result);
        }

        device = hr == SOk ? result : IntPtr.Zero;
        return hr;
    }

    /// <summary>IMMDeviceCollection::GetCount.</summary>
    public static unsafe int GetCount(IntPtr collection, out uint count)
    {
        uint result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VTable(collection)[3])(collection, &result);
        count = hr == SOk ? result : 0;
        return hr;
    }

    /// <summary>IMMDeviceCollection::Item.</summary>
    public static unsafe int Item(IntPtr collection, uint index, out IntPtr device)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(collection)[4])(collection, index, &result);
        device = hr == SOk ? result : IntPtr.Zero;
        return hr;
    }

    /// <summary>IMMDevice::Activate.</summary>
    public static unsafe int Activate(IntPtr device, Guid interfaceId, out IntPtr instance)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)VTable(device)[3])(device, &interfaceId, ClsctxAll, IntPtr.Zero, &result);
        instance = hr == SOk ? result : IntPtr.Zero;
        return hr;
    }

    /// <summary>IMMDevice::OpenPropertyStore.</summary>
    public static unsafe int OpenPropertyStore(IntPtr device, out IntPtr store)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(device)[4])(device, StgmRead, &result);
        store = hr == SOk ? result : IntPtr.Zero;
        return hr;
    }

    /// <summary>IPropertyStore::GetValue. The caller clears the value with <see cref="PropVariantClear"/>.</summary>
    public static unsafe int GetValue(IntPtr store, PropertyKey key, out PropVariant value)
    {
        PropVariant result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)VTable(store)[5])(store, &key, &result);
        value = hr == SOk ? result : default;
        return hr;
    }

    /// <summary>IDeviceTopology::GetConnectorCount.</summary>
    public static unsafe int GetConnectorCount(IntPtr topology, out uint count)
    {
        uint result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VTable(topology)[3])(topology, &result);
        count = hr == SOk ? result : 0;
        return hr;
    }

    /// <summary>IDeviceTopology::GetConnector.</summary>
    public static unsafe int GetConnector(IntPtr topology, uint index, out IntPtr connector)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(topology)[4])(topology, index, &result);
        connector = hr == SOk ? result : IntPtr.Zero;
        return hr;
    }

    /// <summary>IConnector::GetDeviceIdConnectedTo.</summary>
    public static unsafe int GetDeviceIdConnectedTo(IntPtr connector, out string? deviceId)
    {
        char* result = null;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, char**, int>)VTable(connector)[10])(connector, &result);
        deviceId = hr == SOk && result != null ? new string(result) : null;
        Marshal.FreeCoTaskMem((IntPtr)result);
        return hr;
    }

    /// <summary>IKsControl::KsProperty without a data buffer.</summary>
    public static unsafe int KsPropertyCall(IntPtr control, KsProperty property)
    {
        uint returned;
        return ((delegate* unmanaged[Stdcall]<IntPtr, KsProperty*, uint, void*, uint, uint*, int>)VTable(control)[3])(
            control, &property, (uint)sizeof(KsProperty), null, 0, &returned);
    }
}
