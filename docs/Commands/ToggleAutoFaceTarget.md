# Toggle Auto Face Target

Slash command (`/yautofacetarget` / `/yaft`) that toggles the in-game "Automatically face target when using action" setting. Pass `on` / `off` to set explicitly, or no argument to toggle.

If BossmodReborn is installed, its "Smart character orientation" tweak (Settings > Action tweaks > Smart character orientation > Enabled) is kept in sync with the same value, since it replaces the base-game option. This is done through BossmodReborn's `BossMod.Configuration` IPC (the same code path as its config checkbox); if BossmodReborn is not loaded, the command silently skips it.
