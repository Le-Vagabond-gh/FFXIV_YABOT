# Pomander List

While inside a deep dungeon (Palace of the Dead, Heaven-on-High, or Eureka Orthos), shows a small overlay listing the pomanders and magicite/demiclones you currently hold. Each row shows the in-game icon, the canonical name ("Pomander of X", "X Magicite", or "X Demiclone"), a two-word effect summary, and the quantity (`x2`, `x3`...). Clicking a row uses that pomander/stone immediately - the same as right-clicking it in the in-game pomander menu. Pomanders already active on the current floor are highlighted in green, and stay listed even once the last one is consumed so the active marker doesn't disappear mid-floor. Pomander of Strength and Pomander of Steel grant ordinary player buffs rather than a floor-wide effect, so their green "active" highlight is driven by the actual buff (Damage Up, status 687; Vulnerability Down, status 1100) instead of the struct flag. Hold Shift to drag the window to reposition it; the **Lock panel** option disables Shift-to-move so the window stays put (and clicks can't accidentally reposition it).

The dungeon contents and current counts come from `EventFramework.GetInstanceContentDeepDungeon()` (the live `InstanceContentDeepDungeon` struct - `Items[16]` for pomanders, `Magicite[3]` for magic stones/demiclones). The slot-to-name mapping is taken from the `DeepDungeon` Excel sheet's `PomanderSlot`/`MagiciteSlot` arrays - magic stones for PotD/HoH (DeepDungeonType 1) and demiclones for Eureka Orthos (DeepDungeonType 2). Clicks call the engine's `UsePomander(slot)` / `UseStone(slot)` directly.

## Pickup flash

When a pomander/magicite count goes up (you pick one up), its row text briefly pulses blue->white and a trio of bouncing arrows (`>>>`, the rotated game icon, painted to the draw list) animate to the left of the row to draw the eye. Per-slot count tracking keys the flash so duplicate magicite only light up the slot that was actually filled. On entering/re-entering a dungeon there is a short prime grace window during which the saved stock settling in (counts arriving `0` then jumping to their real values) updates the baseline silently instead of flashing every row.

## Capped coffers

The game prints "You return the <item> to the coffer. You cannot carry any more of that item." when a coffer holds a pomander you're already at max on. The overlay listens for that line, identifies the pomander from the dungeon's own `PomanderSlot` mapping, and flashes its row with blue arrows so you know which one to spend to make room. The **When a coffer holds a pomander you're already at max on** radio chooses what else happens: **Do nothing** (just the flash), **Use one automatically** to free a slot, or **Ask with a mid-screen Yes/No prompt** before using one.

## Status line

A status line sits at the top of the overlay with two optional segments drawn left-to-right: the Beacon of Passage progress on the left and the respawn timer on its right.

### Passage progress

The **Show passage progress** option shows how close the floor is to opening the Beacon of Passage (`Passage  70%`), turning green once it's open (`Passage  100%`). This reads `dd->PassageProgress` from the live `InstanceContentDeepDungeon` struct directly - it fills `0-10` as the floor is cleared and reads `>=11` once the beacon spawns - which is more robust than NecroLens' approach of scraping the `DeepDungeonMap` addon's key-icon part ID (that only works while the map window is open). The percentage is `min(raw, 10) * 10`, shown green when open.

### Respawn timer

The **Show respawn timer** option estimates when mobs next respawn on the current floor (`mm:ss`, tinted amber in the final few seconds). The game struct exposes no respawn countdown, so this is dead-reckoned the same way NecroLens does: each 10-floor set has a fixed respawn interval (see `DeepDungeonRespawn`), and the clock is anchored to when the player entered the floor - detected by watching `dd->Floor` change. Because the anchor is floor entry, enabling the overlay mid-floor makes the first cycle read early until the next floor transition re-anchors it.

Both segments are suppressed on boss/transition floors (every 10th) and Eureka Orthos' floor 99, which have neither a passage nor respawns - you advance via the boss there (`DeepDungeonRespawn.IsBossFloor`). When the panel is right-aligned, the whole status line hugs the right edge as a group, keeping passage progress to the left of the timer.
