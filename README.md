# RPFramework

A roleplay toolkit plugin for FINAL FANTASY XIV, built on [Dalamud](https://github.com/goatcorp/Dalamud).

## Features

### RP Inventory (`/rpinv` or `/rpinventory`)
A custom item bag system separate from your real FFXIV inventory, designed for roleplay items that don't exist in-game.

- Create and manage multiple named bags
- Add custom items with names, descriptions, amounts, and game icons
- Right-click items to edit, delete, trade, or send a copy
- Right-click bag tabs to rename, delete, share, or dissolve
- Track a Gil balance displayed at the bottom of the inventory
- Trade items directly to other RPFramework users via the relay server (item removed from your inventory, or send a copy to keep it)
- Share bags with other players for collaborative access — all participants see live updates

### RP BGM (`/rpbgm`)
A synchronized music player that lets a group of players listen to the same YouTube audio together in real time.

- Create or join a named room
- Build a shared playlist from YouTube URLs (audio cached locally, no re-downloads)
- Owner/Admin controls: play, pause, stop, seek, previous/next, loop modes (None/Single/All)
- Members automatically follow the room owner's playback state with latency compensation
- Promote members to Admin to share transport controls
- BGM cache stored in your Dalamud config folder — clear it any time from Settings

### Settings
Open via the gear icon in Dalamud or `/xlsettings` → plugin config.

- Configure the relay server URL
- Connect/disconnect from the relay server
- View and clear the BGM audio cache

## Installation

RPFramework is distributed via a custom Dalamud plugin repository.

1. Open Dalamud Settings (`/xlsettings`) → **Experimental**
2. Under **Custom Plugin Repositories**, add:
   ```
   https://raw.githubusercontent.com/ZeroTheScyther/RPFramework/master/repo.json
   ```
3. Click the **+** button, then **Save**
4. Open the Plugin Installer (`/xlplugins`), search for **RPFramework**, and install

## Relay Server

Multiplayer features (trading, shared bags, BGM sync) require a running instance of **RPFrameworkServer** — an ASP.NET Core SignalR server included in this repository under `RPFrameworkServer/`.

You can host it yourself or use a shared community instance. Set the server URL in the plugin's Settings window and click **Connect** while logged in to FFXIV.

## Building from Source

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- XIVLauncher with Dalamud installed and run at least once

### Build

```bash
~/.dotnet/dotnet build RPFramework/RPFramework.csproj --configuration Release
```

The output will be at `RPFramework/bin/x64/Release/RPFramework/`.

### Loading as a dev plugin

1. Open Dalamud Settings → **Experimental**
2. Add the full path to `RPFramework.dll` under **Dev Plugin Locations**
3. Open the Plugin Installer → **Dev Tools → Installed Dev Plugins** and enable it

## License

[AGPL-3.0](LICENSE.md)
