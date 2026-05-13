# VanillaPlus Ports

These features are ports of code from [MidoriKami/VanillaPlus](https://github.com/MidoriKami/VanillaPlus), licensed under AGPL-3.0 (same as YABOT). Each section credits the original author(s) as listed in the VanillaPlus source.

The ports replace VanillaPlus's `GameModification` lifecycle and KamiToolKit native UI helpers with YABOT's `Feature` base class and direct Dalamud / FFXIVClientStructs APIs. Optional KamiToolKit-only flourishes (custom text nodes, timeline-animated overlays) are left out; the core behavior is preserved.

## Chat Player Tooltip
*Original author: anqied*

Hovering a player-name link in chat shows a tooltip with their character name plus a cross-world icon and their home world. Useful for spotting visitors from another data center / world without right-clicking.

## Command Panel Adjustments
*Original author: Pixis Lepus*

Cleans up the visual noise on the Command Panel (the small hotbar opened via `/commandpanel`):

- hide cursor highlight on hovered slot
- hide focus border around the panel
- hide panel background
- hide frames on empty slots
- move close / settings buttons over the slot area

The configurable background tint from VanillaPlus is not ported - the boolean toggles cover the visible-noise use case.

## Resource Bars as Percentages
*Original author: Zeffuro*

Replaces the raw HP/MP/GP/CP numbers on the player's parameter widget (top-right) with percentages. Each resource can be toggled independently. The `%` sign and decimal precision are configurable, and the partial-decimals option keeps maxed values clean ("100" instead of "100.00").

Only the parameter widget is ported.

## Wondrous Tails Probabilities
*Original author: MidoriKami; solver by Daemitris*

Adds line-completion probabilities (1/2/3 line chances, plus the shuffle-average baseline) to the Wondrous Tails (Khloe's Journal) window. The probability text is appended to the existing help text on the right side of the addon; values are color-coded by how they compare to a random-shuffle baseline so you can see at a glance whether a Second Chance reshuffle is likely to help.

## Auto Select Next Loot Item
*Original author: MidoriKami*

When the Need/Greed window opens, automatically highlights the first item you haven't rolled on. After you roll (Need / Greed / Pass), the highlight advances to the next item, so you can roll without clicking each item first.

## Better Quest Map Link
*Original author: MidoriKami*

Clicking a quest's map link no longer forces the map view to the quest's flag. The map opens normally, centered on your current position, so you can pan around without fighting the game's auto-snap. Hooks `AgentMap.OpenMap`; if the requested map is a quest-log map for a zone other than your current one, the type is rewritten to `Centered` and the call is re-issued.

Cosmic Exploration zones (`TerritoryIntendedUse = 60`) are excluded because their quest links are normally useful as-is.

## Hide Dead Enemy Nameplates
*Original author: nebel*

Hides nameplates and markers on enemy NPCs that have already been killed, reducing visual clutter after big pulls. Uses Dalamud's cooperative `INamePlateGui.OnDataUpdate` event, so it composes cleanly with other nameplate plugins.

## Skip Teleport Confirm
*Original author: MidoriKami*

Automatically clicks Yes on the teleport confirmation prompt that opens when you click an aetheryte icon on the world map. Mirrors VanillaPlus's `GetCallbackHandlerInfo` check: the SelectYesno's registered callback handler is looked up in `RaptureAtkModule.AddonCallbackMapping` (keyed by the dialog's addon Id), and we only confirm when the handler is `AgentMap` with `EventKind == 1`. Any other Yes/No dialog (even one that pops up over the map) is left alone.
