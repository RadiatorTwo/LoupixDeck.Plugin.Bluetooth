# LoupixDeck.Plugin.Bluetooth

Shows known Bluetooth devices on a Loupedeck device and connects or disconnects them with a press.

```text
WH-1000XM5
Connected · 82 %
```

On Linux the plugin talks to BlueZ (`org.bluez`) over D-Bus directly. It never runs or parses
`bluetoothctl`. New devices can be paired from the device folder.

The first version supports **Linux with BlueZ** and needs LoupixDeck with Plugin SDK 1.31 or newer.
The plugin is built on a platform-neutral backend interface (`IBluetoothBackend`). On other
platforms it loads, offers no commands, and reports on the Plugins page that the platform is not
supported yet. A Windows backend may follow once its connect and disconnect paths have been
verified on real hardware.

## What it does

- **Adapter:** turn the adapter on, off, or toggle it. The button follows the adapter through the
  states `Off`, `On` and `Unavailable`. When rfkill blocks the radio, the button shows
  *Bluetooth is blocked*.
- **Devices:** connect, disconnect, toggle, trust or untrust a paired device.
  - The button shows the device's name and its state.
  - It also shows a battery ring with the percentage, when BlueZ reports the level (`org.bluez.Battery1`).
- **Discovery:** looks for devices nearby for a limited time (30 s by default), then stops on its
  own. The button shows `Idle`, `Discovering` or `Unavailable`. The devices it finds appear in the
  device folder, where they can be paired.
- **Device slots:** ten favorite slots (`Bluetooth.Device01` … `Bluetooth.Device10`).
  - Each slot's button state follows its device: `Disconnected`, `Connecting`, `Connected`,
    `Disconnecting`, `Failed` and `Unavailable`. Each state can have its own design.
  - A press toggles the device.
- **Device folder:** a touch folder that lists the paired devices, connected ones first.
  - Each tile shows the device's type icon, its state and its battery level.
  - A press on a device connects or disconnects it.
  - The first tile runs a discovery with a countdown. While it runs, the devices it finds are listed
    after the paired ones, with their signal strength. With *Show paired devices only* turned off,
    they stay listed after the discovery ends.
  - A press on a found device pairs it (see below).

Everything updates from BlueZ's D-Bus signals (`InterfacesAdded`, `InterfacesRemoved` and
`PropertiesChanged`).
- Bursts of changes, such as RSSI updates during a discovery, are combined into a single redraw.
- A battery level that appears after the device does is picked up the same way.
- If BlueZ restarts, the plugin rebuilds its state by itself.
- If BlueZ is missing, the buttons show `Unavailable` and nothing crashes.

## Choosing a device

Buttons store a device as `<adapter address>/<device address>`, for example
`00:1A:7D:DA:71:13/AC:80:0A:11:22:33`. They never store the D-Bus object path. A bare device
address also works and matches the device on any adapter.

The command menu goes from device to action:

```text
Bluetooth
  Toggle Adapter
  Discover Devices
  Devices
    WH-1000XM5 · Headset · Connected · 82 %
      WH-1000XM5 — Toggle
      WH-1000XM5 — Connect
      WH-1000XM5 — Disconnect
      WH-1000XM5 — Trust
      WH-1000XM5 — Add to Favorites
  Device Slots
```

Each leaf is named `device — action`, and a button dropped from it uses that name as its caption.
**Add to Favorites** fills the device slots without typing ids. **Device Slots** lists each slot
with the device it currently shows.

## Why slots for the button states

The host binds button states to a command name, not to its parameters. If two **Toggle Device**
buttons for different devices carried states, they would share one state. Each slot is therefore a
command of its own that takes its device from the favorites list.

The device commands show their state through their value instead: name, state text and battery
ring.

## Connecting states

BlueZ only reports `Connected` as true or false. `Connecting` and `Disconnecting` are kept by the
plugin while its own call runs, and are then reconciled with what BlueZ reports.
- A call that fails or times out shows `Failed` for a few seconds, along with the reason, for
  example *Adapter is off* or *Timed out*.
- Optionally, the plugin retries a failed connection once.
- Optionally, it marks a successfully connected device as trusted.

## Pairing a new device

1. Put the device into pairing mode.
2. Open the device folder and press **Discover**.
3. Press the device once it appears (*Press to pair*).

The plugin pairs the device, marks it as trusted and connects it. While it does, the folder shows
the pairing instead of the list:

| The device | What the folder shows |
|---|---|
| Headphones, speakers, mice, gamepads | nothing to do; *Pairing*, then the result |
| Phones, computers | the code; press **Confirm** if it matches the device, otherwise **Reject** |
| Keyboards | the code; type it on the keyboard and press Enter |
| Old devices with a fixed PIN | not supported (the deck cannot type); the pairing ends with a message |

**Cancel** aborts a running pairing. After about 90 s it ends on its own. Found devices without a
name are not listed; BlueZ reports many anonymous BLE advertisers.

For the pairing, the plugin registers a BlueZ agent (`org.bluez.Agent1`, capability
`DisplayYesNo`) for itself only. It never becomes the default agent, so the desktop's own
Bluetooth dialogs keep working. The agent is removed as soon as the pairing ends, whatever the
outcome.

## Using it with audio

The plugin has no dependency on the Audio/PipeWire plugin. To switch the audio output when a
headset connects, put both steps in a macro: connect the device, then select the output device.

## Not included

- Entering a PIN on the deck, which only very old devices need.
- Removing (unpairing) devices, and anything that needs root.

## Build

```bash
dotnet build -c Release
loupix pack          # validates plugin.json and writes the store files to dist/
loupix install       # copies the plugin into the LoupixDeck plugins folder
```

To copy the plugin by hand, copy these files from `bin/Release/` into
`~/.config/LoupixDeck/plugins/bluetooth/` and restart LoupixDeck:
- `LoupixDeck.Plugin.Bluetooth.dll`
- `LoupixDeck.Plugin.Bluetooth.deps.json`
- `Tmds.DBus.Protocol.dll`
- `strings.*.json`
- `plugin.json`

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Favorite devices | empty | The devices behind the device slots, in slot order. |
| Hidden devices | empty | Devices kept out of the command menu and the device folder. |
| Show paired devices only | on | Off: found devices stay listed after the discovery ends. |
| Preferred adapter | empty | The adapter used when a button names none. Empty uses the first adapter. |
| Connection timeout (s) | 20 | How long connecting or disconnecting may take (5 to 120). |
| Retry a failed connection once | off | Tries a failed or timed-out connection a second time. |
| Trust connected devices | off | Marks a device as trusted after the deck connected it. |
| Discovery duration (s) | 30 | How long a discovery started from the deck runs (5 to 300). |

**List devices** prints every adapter and paired device with the id the text fields accept.
**Check BlueZ** shows whether BlueZ is reachable, the adapter state and the last error.
