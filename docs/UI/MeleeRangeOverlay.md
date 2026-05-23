# Melee Range Overlay

Shows a small coloured circle on screen indicating whether your current target is within melee weapon-skill range, in line of sight, and being faced. The check uses the game's own function (`ActionManager.GetActionInRangeOrLoS`) against Standard Attack (action 7), so the range matches your equipped weapon - 3y with a melee weapon equipped - and the same call covers LoS and the "must face target" requirement.

The overlay is **only displayed on melee jobs** (tanks and melee DPS - ClassJob role 1 or 2). On ranged DPS jobs and healers it stays hidden, since melee range isn't a concern there.

It also hides automatically while you're in an NPC dialogue, quest event, or cutscene (any of the `OccupiedInQuestEvent` / `OccupiedInEvent` / `OccupiedInCutSceneEvent` condition flags), so it doesn't clutter the screen during conversations.

- **Green** circle: target is within range, in line of sight, and being faced.
- **Red** circle: target is out of range, out of line of sight, or not being faced.
- **No target**: the circle is hidden, or shown in grey - selectable via a radio option in the feature settings.

Hold Shift and drag the overlay to reposition it. The size of the circle is configurable (default 12 px).
