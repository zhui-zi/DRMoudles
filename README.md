# DRMoudles

Custom modules for the DailyRoutines (FFXIV / Dalamud) plugin.

## Modules

### OccultPotNotifier — 新月岛 魔法罐助手
Tracks the Occult Crescent magic-pot bunny FATEs (north `1976` / south `1977`, alternating on a 30-minute cycle) and overlays treasure locations.

**Respawn tracker**
- Countdown shown on the server info bar, an overlay, or hidden. Clicking it flags the next pot on the map.
- Advance alert (default 5 min): TTS, in-game notification, and/or chat forward to a selectable channel with a `<flag>` link.
- Optional online sync/report via the community tracker (`infi.ovh`), keyed by data center + an active FATE. Falls back to local observation when off.

**Map markers** (ported from [Infiziert90/EurekaTrackerAutoPopper](https://github.com/Infiziert90/EurekaTrackerAutoPopper))
- Marks bronze/silver chests, north/south pots, reroll, and bunny spots on the map and minimap.
- Fast-switch overlay docked to the map window for toggling each marker set on the fly.
- While carrying the lure pot (撒娇罐), automatically shows the configured marker set and draws a circle at the nearest buried-treasure spot.

### AutoInviteToParty
Sends a party invite to anyone whose chat message matches a keyword/regex. Modeled on [Bluefissure/Inviter](https://github.com/Bluefissure/Inviter).

- Hooks `RaptureLogModule.AddMsgSourceEntry`; invites through `InfoProxyPartyInvite`.
- Configurable pattern (default regex `111|求组队`), listened channels (default: shout), and rate limit.
- Skips when the party is full, you are not the leader, or the target is already in the party.
- Toggle with `/pdr autoinvite [on|off|toggle]`.

## Install

These are single-file source modules — no DLL build required. Drop the `.cs` file into a `DailyRoutines.ModulesPublic` checkout (namespace `DailyRoutines.ModulesPublic`), then enable the module in-game.

- [`OccultPotNotifier/OccultPotNotifier.cs`](OccultPotNotifier/OccultPotNotifier.cs)
- [`AutoInviteToParty/AutoInviteToParty.cs`](AutoInviteToParty/AutoInviteToParty.cs)
