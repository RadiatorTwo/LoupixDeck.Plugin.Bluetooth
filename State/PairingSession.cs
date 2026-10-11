using LoupixDeck.Plugin.Bluetooth.Backend;

namespace LoupixDeck.Plugin.Bluetooth.State;

/// <summary>Where a pairing stands, as the folder shows it.</summary>
internal enum PairingStage
{
    /// <summary>BlueZ is talking to the device; nothing to do for the user.</summary>
    Pairing,

    /// <summary>Both sides show a code; the user confirms that they match.</summary>
    Confirm,

    /// <summary>The user types the shown code on the device, such as a keyboard.</summary>
    TypeCode,

    /// <summary>Paired; the device is being trusted and connected.</summary>
    Connecting,

    Succeeded,
    Failed
}

/// <summary>
/// One pairing started from the deck. It answers the backend's prompts from the folder's buttons
/// and is what the folder draws while it runs. Thread-safe: the prompts come from the D-Bus read
/// loop, the answers from the folder.
/// </summary>
internal sealed class PairingSession(BluetoothDeviceState device) : IPairingPrompt, IDisposable
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cancellation = new();
    private TaskCompletionSource<bool>? _confirmation;

    public string DeviceId { get; } = device.StableId;

    public string DeviceName { get; } = device.DisplayName;

    public PairingStage Stage { get; private set; } = PairingStage.Pairing;

    /// <summary>The code to compare or type, while one is shown.</summary>
    public string? Code { get; private set; }

    /// <summary>The English reason of a failure, or a note on a success (such as "Paired, not connected").</summary>
    public string? Message { get; private set; }

    public bool IsFinished => Stage is PairingStage.Succeeded or PairingStage.Failed;

    public CancellationToken Cancellation => _cancellation.Token;

    /// <summary>Raised on a background thread whenever the stage or the code changed.</summary>
    public event Action? Changed;

    public void ShowCode(string code) => Update(PairingStage.TypeCode, code);

    public async Task<bool> ConfirmAsync(string code, CancellationToken cancellation)
    {
        TaskCompletionSource<bool> confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            _confirmation = confirmation;
        }

        Update(PairingStage.Confirm, code);

        using CancellationTokenRegistration onCancel = cancellation.Register(() => confirmation.TrySetCanceled());
        using CancellationTokenRegistration onSessionCancel = _cancellation.Token.Register(() => confirmation.TrySetCanceled());

        bool accepted = await confirmation.Task.ConfigureAwait(false);
        Update(PairingStage.Pairing, null);
        return accepted;
    }

    public void Unsupported(string englishReason)
    {
        lock (_lock)
        {
            Message = englishReason;
        }
    }

    /// <summary>The user's answer to a confirmation. Ignored when none is pending.</summary>
    public void Answer(bool accepted)
    {
        TaskCompletionSource<bool>? confirmation;

        lock (_lock)
        {
            confirmation = _confirmation;
            _confirmation = null;
        }

        confirmation?.TrySetResult(accepted);
    }

    /// <summary>Aborts the pairing; the backend cancels it at BlueZ.</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session is already over.
        }
    }

    public void MarkConnecting() => Update(PairingStage.Connecting, null);

    public void Finish(bool succeeded, string? message)
    {
        lock (_lock)
        {
            // A reason the agent recorded (such as an unsupported PIN entry) explains a failure
            // better than the generic outcome.
            Message = succeeded ? message : Message ?? message;
        }

        Update(succeeded ? PairingStage.Succeeded : PairingStage.Failed, null);
    }

    public void Dispose() => _cancellation.Dispose();

    private void Update(PairingStage stage, string? code)
    {
        lock (_lock)
        {
            if (IsFinished)
            {
                return;
            }

            Stage = stage;
            Code = code;
        }

        Changed?.Invoke();
    }
}
