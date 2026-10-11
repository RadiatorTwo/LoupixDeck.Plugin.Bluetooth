using System.Reflection;
using System.Runtime.Versioning;
using LoupixDeck.PluginSdk;
using WinRT;

namespace LoupixDeck.Plugin.Bluetooth.Backend.Windows;

/// <summary>
/// Lets this plugin's copy of the WinRT runtime (CsWinRT) work next to other plugins that ship
/// their own copy.
/// <para>
/// On first use CsWinRT registers its ComWrappers as the process-wide tracker-support instance.
/// That registration can happen only once per process, and every plugin load context carries its
/// own WinRT.Runtime, so the second plugin to touch WinRT fails with "Attempt to update previously
/// set global instance". The global instance only serves XAML reference tracking, which the
/// Bluetooth APIs do not use, so this copy gets its default ComWrappers without registering it.
/// It also leaves the global slot free for the other plugin.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinRtComWrappers
{
    private static int _initialized;

    /// <summary>Must run before the first WinRT call. Safe to call more than once.</summary>
    public static void InitializeWithoutGlobalRegistration(IPluginLogger logger)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type support = typeof(ComWrappersSupport);
            FieldInfo? field = support.GetField("_comWrappers", flags);
            PropertyInfo? instance = support.GetProperty("DefaultComWrappersInstance", flags);

            if (field is null || instance is null)
            {
                logger.Warn("Bluetooth: this WinRT runtime version is unknown; WinRT may fail when another plugin also uses it.");
                return;
            }

            if (field.GetValue(null) is null)
            {
                field.SetValue(null, instance.GetValue(null));
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth: preparing the WinRT runtime failed: {ex.Message}");
        }
    }
}
