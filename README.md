# DRMoudles

Custom modules for the DailyRoutines (FFXIV / Dalamud) plugin.

## Modules

### OccultPotNotifier
Tracks the Occult Crescent magic-pot bunny FATEs (north `1976` / south `1977`, alternating on a 30-minute cycle) and warns before respawn.

- Countdown shown on the server info bar, an overlay, or hidden. Clicking it flags the next pot on the map.
- Advance alert (default 5 min): TTS, in-game notification, and/or chat forward to a selectable channel with a `<flag>` link.
- Optional online sync/report via the community tracker (`infi.ovh`), keyed by data center + an active FATE. Falls back to local observation when off.

### AutoInviteToParty
Sends a party invite to anyone whose chat message matches a keyword/regex. Modeled on [Bluefissure/Inviter](https://github.com/Bluefissure/Inviter).

- Hooks `RaptureLogModule.AddMsgSourceEntry`; invites through `InfoProxyPartyInvite`.
- Configurable pattern (default regex `111|求组队`), listened channels (default: shout), and rate limit.
- Skips when the party is full, you are not the leader, or the target is already in the party.
- Toggle with `/pdr autoinvite [on|off|toggle]`.

## Build

Needs the .NET SDK and the DailyRoutines dev assemblies at `%APPDATA%\XIVLauncherCN\pluginConfigs\DailyRoutines\Dev\`.

```
dotnet build OccultPotNotifier.sln -c Release
dotnet build AutoInviteToParty.sln -c Release
```

Output: `bin/Release/<Module>.dll`.

## Install

Load the DLL through the DailyRoutines local-module loader, then enable the module in-game.
