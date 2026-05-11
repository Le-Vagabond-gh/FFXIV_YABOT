# Don't Forget

Bundle of "things you always forget to do" automations, ported from the standalone `dontforget` plugin. All sub-features are individually toggleable in the config tree.

Sub-features:
- **Auto Peloton / Sprint** - applied after moving for 2+ seconds (Peloton on Phys Ranged, Sprint on anyone, skipped if a speed buff is already up).
- **Auto Summon Pet** - Summoner→Carbuncle, Scholar→Fairy, when standing still. Optional "summon in combat after death" gate (only re-summons if raised within the last 15 seconds).
- **Auto Tank Stance** - PLD/WAR/DRK/GNB stance auto-applied when standing still without it.
- **Auto Gathering Buffs** - MIN/BTN/FSH: Prospect/Triangulate, Sneak, and Truth of Mountains/Forests/Oceans (Truth only on home world). 2-second cooldown to avoid toggle spam.
- **Auto Gysahl Greens** - re-feeds your chocobo when its timer drops below 15 minutes (off by default).
- **Chocobo Stance Keeper** - enforces a chosen Free/Attacker/Defender/Healer stance on the chocobo companion.
- **Auto Switch Gatherer** - listens for "Unable to gather. Current class not set to Miner/Botanist" chat errors and switches to the matching gearset.

## Chat command

`/yabot dontforget [option] [on|off|toggle]` toggles individual sub-features without opening the config window.

- `/yabot dontforget` or `/yabot dontforget list` - print all options and their current state.
- `/yabot dontforget peloton` - flip the `Peloton` option.
- `/yabot dontforget autosprint off` - set explicitly.

Option names match the config field names (case-insensitive): `scholar`, `summoner`, `summonincombatafterdeath`, `tankstance`, `gatheringbuffs`, `autoswitchgatherer`, `peloton`, `autosprint`, `autogysahlgreens`. `ChocoboStanceOption` is not exposed (multi-valued, use the config UI).

Originally a standalone plugin (`ffxiv_dontforget`); folded into YABOT.
