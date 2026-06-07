# Auto-Interject

While inside a deep dungeon, this automatically uses **Interject** on your current target whenever it casts an interruptible spell whose name is in your watch list (default contains **Malice**). Like the auto-heal feature, it only does anything while `EventFramework.GetInstanceContentDeepDungeon()` reports you are in a deep dungeon.

## What it does

On every `Framework.Update` tick it reads the current target's live `CastInfo`. If the target is casting, the cast is flagged **interruptible**, and the cast's action name matches any name in your watch list (case-insensitive), it fires Interject (action **7538**) on that target.

`Interject` is a tank role action available to any tank job at level 18, so the feature is only useful when you are playing a tank (or carrying the role action) in the deep dungeon.

## Cooldowns and retrying

`UseAction` can report success and still be silently dropped under an animation lock, so its return value isn't trusted. The use is re-attempted every frame while the conditions hold and `GetActionStatus` reports Interject is usable (returns 0); once it goes on its 30s recast the status flips non-zero and the retry stops on its own. A 2-second anti-double-fire debounce (the same one the auto-heal regen ability uses) guards the gap before the recast registers.

## Options

- **Watch list** - the spell names that trigger Interject (case-insensitive, matched exactly). Type a name and press the **+** button to add one; use the trash button to remove. The list starts with **Malice**.
