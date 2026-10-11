using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Bluetooth;

public sealed class BluetoothPlugin : LoupixPlugin
{
    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "bluetooth",
        Name = "Bluetooth",
        Version = new Version(1, 0, 0),
        SdkVersion = SdkInfo.Version,
        Author = "",
        Description = ""
    };

    public override void Initialize(IPluginHost host)
    {
    }

    public override IEnumerable<IPluginCommand> GetCommands() => [];
}
