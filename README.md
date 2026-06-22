# RPFramework

A multiplayer roleplay toolkit for FINAL FANTASY XIV, built on [Dalamud](https://github.com/goatcorp/Dalamud).

> **Disclaimer:** This project was built with the help of [Claude](https://claude.com/claude-code).

Run a tabletop-style campaign inside the game: custom character sheets, dice, turn-based initiative, skills, companions, shared inventories, and synchronized music - all kept in sync across your party in real time through a relay server.

## Features

- **RP Hub** (`/rphub`) - Connect to the server, create or join campaigns, set your active campaign, and manage members. Roles: Owner (DM), Co-DM, Member.
- **RP Character** (`/rpcharacter`) - One window for your whole character: Profile, Stats, Skills, Equipment, and an active Companion tab.
- **Character Sheet** (`/rpstats`, `/rpsheet`, `/rpcs`) - A fully customisable stat sheet. The DM designs the template (numbers, bars, checkboxes, tooltips) and publishes it to everyone.
- **Dice** (`/rpdice`) - Roll any die with stat modifiers, specializations, advantage/disadvantage, and automatic AP-exhaustion penalties. Quick-roll from chat with `/rpdice d20`.
- **Skills** (`/rpskills`, `/rpsk`) - Active abilities and live passive effects tied to your sheet, with cooldowns and durations that tick on turn end. DMs can author private skills and embed passives into tradeable gear.
- **Companions & NPCs** (`/rpnpc`) - Build full side characters with their own sheets and skills. Share them as import/export codes.
- **Initiative** (`/rpini`) - Joinable turn-order trackers. Run several encounters at once; everyone sees the same live state.
- **BGM** (`/rpbgm`) - Synchronized music rooms from YouTube links. The server prepares the audio, so no extra software is needed on your machine and everyone hears the same track at the same position.
- **Inventory** (`/rpinventory`, `/rpinv`) - Server-synced roleplay item bags scoped to your campaign, including DM-shared inventories. Trade items or share a whole inventory with another member.

## Installation

RPFramework is distributed via a custom Dalamud plugin repository.

1. Open Dalamud Settings (`/xlsettings`) -> **Experimental**
2. Under **Custom Plugin Repositories**, add:
   ```
   https://raw.githubusercontent.com/ZeroTheScyther/RPFramework/master/repo.json
   ```
3. Click **+**, then **Save**
4. Open the Plugin Installer (`/xlplugins`), search for **RPFramework**, and install
5. Open `/rpsettings`, enter your relay server URL, and connect

## Relay Server

Multiplayer features need a running **RPFrameworkServer** - an ASP.NET Core SignalR server included in this repo under `RPFrameworkServer/`. Host it yourself or use a shared community instance, then set its URL in `/rpsettings`.

## Building from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Dalamud (run XIVLauncher at least once).

```bash
~/.dotnet/dotnet build RPFramework/RPFramework.csproj --configuration Release
```

Output lands at `RPFramework/bin/Release/RPFramework.dll`. To load it as a dev plugin, add that path under Dalamud Settings -> **Experimental** -> **Dev Plugin Locations**.

## License

[AGPL-3.0](LICENSE.md)
