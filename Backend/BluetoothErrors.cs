using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Bluetooth.Backend;

/// <summary>How a call to the Bluetooth backend ended. Anything but <see cref="Ok"/> is shown to the user.</summary>
public enum BluetoothCallOutcome
{
    Ok,

    /// <summary>The bus policy or PolicyKit refused the call.</summary>
    PermissionDenied,

    /// <summary>The backend does not know this adapter or device (any more).</summary>
    NotFound,

    /// <summary>The adapter is powered off, so nothing can connect through it.</summary>
    AdapterOff,

    /// <summary>The radio is blocked by rfkill.</summary>
    Blocked,

    /// <summary>Another operation on the same object is still running.</summary>
    InProgress,

    /// <summary>The bus or BlueZ is not reachable right now.</summary>
    Unavailable,

    /// <summary>The request did not finish in time.</summary>
    Timeout,

    /// <summary>The backend cannot do this on the current platform.</summary>
    NotSupported,

    /// <summary>The call reached BlueZ and was rejected, or the connection attempt failed.</summary>
    Failed
}

/// <summary>Maps a D-Bus or BlueZ error onto a <see cref="BluetoothCallOutcome"/>.</summary>
internal static class BluetoothErrors
{
    public const string AccessDenied = "org.freedesktop.DBus.Error.AccessDenied";
    public const string InteractiveAuthorizationRequired = "org.freedesktop.DBus.Error.InteractiveAuthorizationRequired";
    public const string UnknownObject = "org.freedesktop.DBus.Error.UnknownObject";
    public const string UnknownMethod = "org.freedesktop.DBus.Error.UnknownMethod";
    public const string ServiceUnknown = "org.freedesktop.DBus.Error.ServiceUnknown";
    public const string NameHasNoOwner = "org.freedesktop.DBus.Error.NameHasNoOwner";
    public const string NoReply = "org.freedesktop.DBus.Error.NoReply";
    public const string Disconnected = "org.freedesktop.DBus.Error.Disconnected";

    public const string NotAuthorized = "org.bluez.Error.NotAuthorized";
    public const string DoesNotExist = "org.bluez.Error.DoesNotExist";
    public const string NotReady = "org.bluez.Error.NotReady";
    public const string Blocked = "org.bluez.Error.Blocked";
    public const string InProgress = "org.bluez.Error.InProgress";
    public const string NotSupported = "org.bluez.Error.NotSupported";

    /// <summary>
    /// Answers to a request for something that already holds. The caller already has what it asked
    /// for, so they count as success.
    /// </summary>
    public const string AlreadyConnected = "org.bluez.Error.AlreadyConnected";
    public const string NotConnected = "org.bluez.Error.NotConnected";

    /// <summary>Classifies an exception raised by a D-Bus call.</summary>
    public static BluetoothCallOutcome Map(Exception? exception)
    {
        return exception switch
        {
            null => BluetoothCallOutcome.Ok,
            DBusErrorReplyException reply => Map(reply.ErrorName, reply.Message),
            TimeoutException => BluetoothCallOutcome.Timeout,
            OperationCanceledException => BluetoothCallOutcome.Unavailable,
            ObjectDisposedException => BluetoothCallOutcome.Unavailable,
            _ => BluetoothCallOutcome.Failed
        };
    }

    /// <summary>Classifies a D-Bus error name, with the error message for BlueZ's generic errors.</summary>
    public static BluetoothCallOutcome Map(string? errorName, string? message = null)
    {
        return errorName switch
        {
            null or "" => BluetoothCallOutcome.Failed,
            AlreadyConnected or NotConnected => BluetoothCallOutcome.Ok,
            AccessDenied or InteractiveAuthorizationRequired or NotAuthorized => BluetoothCallOutcome.PermissionDenied,
            UnknownObject or UnknownMethod or DoesNotExist => BluetoothCallOutcome.NotFound,
            NotReady => BluetoothCallOutcome.AdapterOff,
            Blocked => BluetoothCallOutcome.Blocked,
            InProgress => BluetoothCallOutcome.InProgress,
            NotSupported => BluetoothCallOutcome.NotSupported,
            ServiceUnknown or NameHasNoOwner or NoReply or Disconnected => BluetoothCallOutcome.Unavailable,
            // BlueZ reports an rfkill block on a power change as a generic failure.
            _ when message?.Contains("rfkill", StringComparison.OrdinalIgnoreCase) == true => BluetoothCallOutcome.Blocked,
            _ => BluetoothCallOutcome.Failed
        };
    }

    /// <summary>The English text a button or the log shows for an outcome.</summary>
    public static string ToEnglishText(BluetoothCallOutcome outcome)
    {
        return outcome switch
        {
            BluetoothCallOutcome.Ok => "Done",
            BluetoothCallOutcome.PermissionDenied => "Not allowed",
            BluetoothCallOutcome.NotFound => "Device not found",
            BluetoothCallOutcome.AdapterOff => "Adapter is off",
            BluetoothCallOutcome.Blocked => "Bluetooth is blocked",
            BluetoothCallOutcome.InProgress => "Busy",
            BluetoothCallOutcome.Unavailable => "Bluetooth unavailable",
            BluetoothCallOutcome.Timeout => "Timed out",
            BluetoothCallOutcome.NotSupported => "Not supported",
            _ => "Failed"
        };
    }
}
