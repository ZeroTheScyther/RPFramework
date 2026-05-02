# RPFramework

A roleplay toolkit plugin for FINAL FANTASY XIV, built on [Dalamud](https://github.com/goatcorp/Dalamud).

## Features

### RP Hub (`/rphub`)
The central panel for server connection, party management, and player interactions.

- Connect to a relay server and create or join parties with a display name and password
- Be a member of multiple parties simultaneously
- Roles: **Owner (DM)** - full control; **Co-DM** - shared initiative/template control; **Member** - roll and end own turn
- Right-click any online party member to view their character sheet, their skills, or offer them an item
- Start combat initiative for the party from here

### RP Character Sheet (`/rpstats`, `/rpsheet`, `/rpcs`)
A fully customisable stat sheet driven by a template the DM publishes to the whole party.

- Field types: **Number** (integer stat with optional D&D-style modifier), **Bar** (current/max pair, e.g. HP and AP), **Checkbox** (proficiency or boolean flag)
- DMs open the template editor (✏ icon), add/remove/reorder groups and fields, set per-field tooltips, and hit **Publish** to push to all party members instantly
- Mark a Number field as the initiative stat; mark Bars as HP and AP so the system knows which pools are which
- Right-click a party member in RPHUB to view their sheet or skills in a read-only window

### RP Dice (`/rpdice`)
Roll any standard die or a custom size. Results appear in chat with a full breakdown.

- Select a stat modifier (Number field) and/or a specialization (Checkbox field)
- Advantage / Disadvantage: roll twice and keep the higher or lower result
- Proficiency in a specialization grants advantage; proficiency cancels disadvantage and you roll once normally
- **AP Exhaustion** - automatic penalty when your AP bar is low: ≤40% → −1, ≤30% → −2, ≤20% → −4, ≤10% → −5
- Quick roll from chat: `/rpdice d20`, `/rpdice d6`, `/rpdice d100`, etc.

### RP Skills (`/rpskills`, `/rpsk`)
Define active abilities and passive effects tied to your character's sheet fields.

- **Active skills** - manually triggered; the system tracks cooldowns and durations (honour-based)
- **Passive skills** - fire automatically on turn end or whenever all conditions are met
- Conditions check any sheet field with <, ≤, =, ≥, or > (percentage mode for bars)
- Effects apply +, −, or = to a target field when the passive fires
- Cooldowns and durations tick down automatically when you end your turn in RPINITIATIVE

### RP Initiative (`/rpini`)
Live turn-order tracker for combat encounters. All party members see the same state.

- DM clicks **Start Initiative** in RPHUB; each player rolls d24 + initiative stat modifier
- Combatants sorted by total roll, ties broken by raw die result
- HP and AP visible when the DM enables **Show HP / AP**
- **End Turn** advances to the next combatant and ticks skill cooldowns and passive effects
- DMs can end anyone's turn and close the round with **End Combat**
- Combatants at 0 HP or AP are shown greyed-out with a strikethrough

### RP BGM (`/rpbgm`)
Synchronized music player - share atmospheric audio with party members in real time.

- Create or join a room; add songs from YouTube URLs
- Owner controls: play, pause, stop, seek, previous/next, loop modes (None/Single/All)
- Members automatically follow the owner's playback state with latency compensation
- DMs and Co-DMs in the same party automatically have room owner privileges
- Audio cached locally - no re-downloads; clear the cache from Settings

### RP Inventory (`/rpinventory`, `/rpinv`)
Custom item bags separate from your real FFXIV inventory, for items that don't exist in-game.

- Create multiple named bags; each item has a name, description, FFXIV icon ID, and quantity
- Track a Gil balance per bag
- Right-click a party member in RPHUB to offer them an item (transfer or send a copy)
- **Shared bags** - invite another player to a bag; all participants see live updates synced through the server

### Settings (`/rpsettings`)
- Configure the relay server URL and connect/disconnect
- View and clear the local BGM audio cache

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

Multiplayer features (trading, shared bags, BGM sync, parties, initiative) require a running instance of **RPFrameworkServer** - an ASP.NET Core SignalR server included in this repository under `RPFrameworkServer/`.

You can host it yourself or use a shared community instance. Set the server URL in `/rpsettings` and click **Connect** while logged in to FFXIV.

## Building from Source

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- XIVLauncher with Dalamud installed and run at least once

### Build

```bash
~/.dotnet/dotnet build RPFramework/RPFramework.csproj --configuration Release
```

The output will be at `RPFramework/bin/Release/RPFramework.dll`.

### Loading as a dev plugin

1. Open Dalamud Settings → **Experimental**
2. Add the full path to `RPFramework.dll` under **Dev Plugin Locations**
3. Open the Plugin Installer → **Dev Tools → Installed Dev Plugins** and enable it

## License

[AGPL-3.0](LICENSE.md)
