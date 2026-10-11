using LoupixDeck.Plugin.Bluetooth.State;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth.Commands;

/// <summary>
/// Shared behaviour of the Bluetooth commands: it runs the action and reports a failure on the
/// touch slot. It never throws; a failing command must not take the host's command pipeline with it.
/// </summary>
internal abstract class BluetoothCommandBase(CommandDescriptor descriptor, BluetoothStateStore store) : IPluginCommand
{
    /// <summary>How long a short message stays on the touch slot.</summary>
    private static readonly TimeSpan OverlayDuration = TimeSpan.FromSeconds(2);

    public CommandDescriptor Descriptor { get; } = descriptor;

    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    protected BluetoothStateStore Store { get; } = store;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            await ExecuteCore(ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} failed: {ex.Message}");
        }
    }

    protected abstract Task ExecuteCore(CommandContext ctx);

    /// <summary>Shows how an action ended. Success shows itself on the button.</summary>
    protected void Report(CommandContext ctx, BluetoothActionResult result, string? subject)
    {
        if (result.IsSuccess)
        {
            return;
        }

        ShowOverlay(ctx, ctx.Host.Tr(result.Text));
        ctx.Host.Logger.Info($"{Descriptor.CommandName} on {subject ?? "(none)"}: {result.Text}.");
    }

    /// <summary>
    /// Shows a short message on the touch slot that was pressed. A rotary press has no slot of its
    /// own, so the host is asked for the slot next to it; a plain button has none at all.
    /// </summary>
    protected static void ShowOverlay(CommandContext ctx, string text)
    {
        if (ctx.SourceIndex is not int index || ctx.Target == ButtonTargets.SimpleButton)
        {
            return;
        }

        int slot = ctx.Target == ButtonTargets.RotaryEncoder
            ? ctx.Host.GetTouchSlotForRotary(index)
            : index;

        if (slot < 0)
        {
            return;
        }

        ctx.Host.OverlayTouchText(slot, text, OverlayDuration);
    }
}
