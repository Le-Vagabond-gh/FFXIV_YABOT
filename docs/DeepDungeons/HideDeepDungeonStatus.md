# Hide Deep Dungeon Status Panel

Entering a deep dungeon (Palace of the Dead, Heaven-on-High, or Eureka Orthos) makes the game automatically pop open the **Deep Dungeon Status** panel - the window showing your aetherpool level and the pomanders/magicite you currently hold. When you run the [Pomander List](PomanderList.md) overlay that information is already on screen, so this panel is just clutter.

This feature closes the panel once when the game opens it on entry. You can reopen it whenever you like with the character-panel shortcut (`C` by default, the same key that opens it in a deep dungeon) and it stays open - only the initial auto-popup is dismissed.

## How it works

The panel is hidden through `AgentDeepDungeonStatus.Hide()` (obtained via `AgentModule.GetAgentByInternalId(AgentId.DeepDungeonStatus)`), **not** by force-closing the addon. Force-closing the addon (`AtkUnitBase.Close`) leaves the agent thinking the panel is still open, which desyncs its toggle state and is what prevents the `C` shortcut from reopening it. Hiding via the agent is equivalent to clicking the window's X button, so the agent's open-state stays correct and the shortcut works normally.

It listens for the `PostSetup` event on the `DeepDungeonStatus` addon via `IAddonLifecycle` and gates the hide on a **duty-scoped** flag. The panel only auto-pops once, when you first enter the dungeon. Floor descents reload the zone (which fires `ClientState.TerritoryChanged` and recreates the addon) but stay within `Deep_Dungeon` intended-use territories and do *not* auto-pop the panel - so the first fresh `PostSetup` on a later floor is your *manual* open. A territory-scoped flag would therefore wrongly hide that manual open on every floor.

Instead the flag is re-armed only when `TerritoryChanged` lands in a non-deep-dungeon zone (`ZoneHelper.IsDeepDungeon`, intended use `31`) - i.e. when you have left the dungeon entirely. That hides the entry popup exactly once per run and never touches a manual reopen on any floor. The hide is deferred one frame so the game's own show flow finishes first (hiding mid-show can re-activate and desync the agent); on zone-in the loading fade covers that single frame.
