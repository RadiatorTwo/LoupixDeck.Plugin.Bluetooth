using System.Globalization;
using LoupixDeck.PluginSdk;
using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Bluetooth.Backend;

/// <summary>
/// A BlueZ pairing agent (org.bluez.Agent1) that exists only while one pairing runs. It is
/// registered for this client alone and never as the default agent, so the desktop's own agent
/// keeps handling every pairing the deck did not start.
/// <para>
/// It announces the capability DisplayYesNo: the deck can show a code and offer yes or no, but it
/// cannot type. Requests for a PIN or passkey entry are therefore refused.
/// </para>
/// </summary>
internal sealed class BlueZPairingAgent(string path, string devicePath, IPairingPrompt prompt, IPluginLogger logger) : IPathMethodHandler
{
    public const string Capability = "DisplayYesNo";

    private const string AgentInterface = "org.bluez.Agent1";
    private const string RejectedError = "org.bluez.Error.Rejected";
    private const string CanceledError = "org.bluez.Error.Canceled";

    /// <summary>Ended by BlueZ's Cancel, or when the pairing is over.</summary>
    private readonly CancellationTokenSource _cancel = new();

    public string Path { get; } = path;

    public bool HandlesChildPaths => false;

    /// <summary>Ends a confirmation that is still waiting for the user.</summary>
    public void Close()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.Request.InterfaceAsString != AgentInterface)
        {
            context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        try
        {
            switch (context.Request.MemberAsString)
            {
                case "Release":
                    ReplyEmpty(context);
                    break;

                case "Cancel":
                    Close();
                    ReplyEmpty(context);
                    break;

                case "RequestPinCode":
                case "RequestPasskey":
                    prompt.Unsupported("Entering a PIN is not supported");
                    context.ReplyError(RejectedError, "The deck cannot enter a PIN");
                    break;

                case "DisplayPinCode":
                {
                    Reader reader = context.Request.GetBodyReader();
                    reader.ReadObjectPathAsString();
                    prompt.ShowCode(reader.ReadString());
                    ReplyEmpty(context);
                    break;
                }

                case "DisplayPasskey":
                {
                    Reader reader = context.Request.GetBodyReader();
                    reader.ReadObjectPathAsString();
                    prompt.ShowCode(FormatPasskey(reader.ReadUInt32()));
                    ReplyEmpty(context);
                    break;
                }

                case "RequestConfirmation":
                {
                    Reader reader = context.Request.GetBodyReader();
                    string device = reader.ReadObjectPathAsString();
                    string code = FormatPasskey(reader.ReadUInt32());

                    if (device != devicePath)
                    {
                        context.ReplyError(RejectedError, "Not the device being paired");
                        break;
                    }

                    // The answer depends on the user. The read loop must keep running meanwhile,
                    // because the reply to Pair arrives through it, so the call is answered later.
                    context.DisposesAsynchronously = true;
                    _ = ConfirmLaterAsync(context, code);
                    break;
                }

                case "RequestAuthorization":
                case "AuthorizeService":
                {
                    // The user started this pairing by pressing the device, which is the
                    // authorization; anything for another device is refused.
                    Reader reader = context.Request.GetBodyReader();
                    string device = reader.ReadObjectPathAsString();

                    if (device == devicePath)
                    {
                        ReplyEmpty(context);
                    }
                    else
                    {
                        context.ReplyError(RejectedError, "Not the device being paired");
                    }

                    break;
                }

                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth: the pairing agent failed on {context.Request.MemberAsString}: {ex.Message}");

            if (!context.ReplySent && !context.DisposesAsynchronously)
            {
                context.ReplyError(RejectedError, ex.Message);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task ConfirmLaterAsync(MethodContext context, string code)
    {
        try
        {
            bool accepted = await prompt.ConfirmAsync(code, _cancel.Token).ConfigureAwait(false);

            if (accepted)
            {
                ReplyEmpty(context);
            }
            else
            {
                context.ReplyError(RejectedError, "Rejected on the deck");
            }
        }
        catch (OperationCanceledException)
        {
            context.ReplyError(CanceledError, "Cancelled");
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth: the pairing confirmation failed: {ex.Message}");
            context.ReplyError(RejectedError, ex.Message);
        }
        finally
        {
            context.Dispose();
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected)
        {
            return;
        }

        using MessageWriter writer = context.CreateReplyWriter(null!);
        context.Reply(writer.CreateMessage());
    }

    /// <summary>BlueZ passkeys are six digits, shown with leading zeros.</summary>
    private static string FormatPasskey(uint passkey) => passkey.ToString("D6", CultureInfo.InvariantCulture);
}
