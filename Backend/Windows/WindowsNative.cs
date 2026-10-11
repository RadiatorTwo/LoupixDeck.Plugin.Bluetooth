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

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDeviceTopology
    {
        [PreserveSig]
        int GetConnectorCount(out uint count);

        [PreserveSig]
        int GetConnector(uint index, out IConnector connector);
    }

    [ComImport]
    [Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IConnector
    {
        [PreserveSig]
        int GetConnectorType(out int type);

        [PreserveSig]
        int GetDataFlow(out int flow);

        [PreserveSig]
        int ConnectTo(IConnector other);

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);

        [PreserveSig]
        int GetConnectedTo(out IConnector other);

        [PreserveSig]
        int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport]
    [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IKsControl
    {
        [PreserveSig]
        int KsProperty(ref KsProperty property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned);
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumerator;

    /// <summary>Releases a runtime callable wrapper now instead of at the next finalizer run.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
