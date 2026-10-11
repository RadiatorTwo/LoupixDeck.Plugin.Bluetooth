using LoupixDeck.Plugin.Bluetooth.Backend.Windows;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Backend;

/// <summary>Picks the backend for the running platform.</summary>
internal static class BluetoothBackends
{
    /// <summary>The oldest Windows build the WinRT backend targets (Windows 10 2004).</summary>
    public const int WindowsMinimumBuild = 19041;

    public static bool IsWindowsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, WindowsMinimumBuild);

    // The WinRT projection ships in every package; it is only loaded when the Windows backend runs.
    public static IBluetoothBackend Create(IPluginLogger logger)
    {
        if (OperatingSystem.IsLinux())
        {
            return new BlueZBackend(logger);
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, WindowsMinimumBuild))
        {
            return new WinRtBluetoothBackend(logger);
        }

        return new UnsupportedBluetoothBackend();
    }
}
