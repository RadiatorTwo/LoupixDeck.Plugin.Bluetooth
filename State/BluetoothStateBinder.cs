using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.State;

/// <summary>A command whose button state the binder keeps up to date.</summary>
internal readonly record struct StatefulCommand(string CommandName, Func<string> StateFor);

/// <summary>
/// Pushes the cached Bluetooth state onto the deck: it sets the active state of the stateful buttons
/// and asks the host to redraw every display command.
/// <para>
/// This is the only type that calls back into the host from a background thread, so every call is
/// guarded; a host that throws must not take the D-Bus read loop with it.
/// </para>
/// </summary>
internal sealed class BluetoothStateBinder(
    IPluginHost host,
    BluetoothStateStore store,
    IReadOnlyList<StatefulCommand> statefulCommands,
    IReadOnlyList<string> displayCommandNames) : IDisposable
{
    /// <summary>
    /// How often the states are applied again. The host starts a newly assigned button on its
    /// first state and does not tell the plugin, so without this a new button would show a wrong
    /// state until the next change.
    /// </summary>
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(2);

    private bool _started;
    private Timer? _stateTimer;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        store.Changed += ReplayAll;
        _started = true;
        _stateTimer = new Timer(_ => ApplyStates(), null, StateInterval, StateInterval);

        // Buttons created before the first read have to be brought up to date once.
        ReplayAll();
    }

    /// <summary>Applies the current state to every stateful button and redraws the displays.</summary>
    public void ReplayAll()
    {
        ApplyStates();

        foreach (string commandName in displayCommandNames)
        {
            Refresh(commandName);
        }
    }

    /// <summary>Sets every stateful button to its current state. Cheap when nothing changed.</summary>
    private void ApplyStates()
    {
        foreach (StatefulCommand command in statefulCommands)
        {
            SetState(command.CommandName, command.StateFor());
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _stateTimer?.Dispose();
        _stateTimer = null;
        store.Changed -= ReplayAll;
        _started = false;
    }

    private void SetState(string commandName, string stateName)
    {
        try
        {
            // False only means no button offers the state or it is already active.
            host.SetActiveButtonState(commandName, stateName);
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Bluetooth: cannot set {commandName} to {stateName}: {ex.Message}");
        }
    }

    private void Refresh(string commandName)
    {
        try
        {
            host.RequestButtonRefresh(commandName);
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Bluetooth: cannot refresh {commandName}: {ex.Message}");
        }
    }
}
