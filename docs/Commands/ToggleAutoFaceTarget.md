# Toggle Auto Face Target

Slash command (`/yautofacetarget` / `/yaft`) that toggles the in-game "Automatically face target when using action" setting. Pass `on` / `off` to set explicitly, or no argument to toggle.

If BossmodReborn is installed, its "Smart character orientation" tweak (Settings > Action tweaks > Smart character orientation > Enabled) is kept in sync with the same value, since it replaces the base-game option. This is done through BossmodReborn's `BossMod.Configuration` IPC (the same code path as its config checkbox); if BossmodReborn is not loaded, the command silently skips it.

## Automatic toggle in combat

The feature config offers an optional monster watch list: while any monster whose name is on the list appears on your enmity list, auto face target is turned off, then turned back on once none of them remain (combat ended, monsters died, or you left the area). The automatic toggle only kicks in if the setting was on to begin with, and a manual `/yaft` while it is active cancels the pending restore. Monster names are matched case-insensitively.
