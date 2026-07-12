# Auto-Interject

While inside a deep dungeon, this automatically interrupts your current target whenever it casts an interruptible spell whose name is in your watch list (default contains **Malice**). Like the auto-heal feature, it only does anything while `EventFramework.GetInstanceContentDeepDungeon()` reports you are in a deep dungeon.

## What it does

On every `Framework.Update` tick it reads the current target's live `CastInfo`. If the target is casting, the cast is flagged **interruptible**, and the cast's action name matches any name in your watch list (case-insensitive), it fires whichever interrupt role action your current job can use on that target.

It tries each interrupt in turn and fires the first one that's available:

- **Interject** (action **7538**) - tank role action, level 18+
- **Head Graze** (action **7551**) - physical ranged DPS role action (BRD/MCH/DNC), level 24+

An action the current job can't use returns a non-zero `GetActionStatus` and is skipped rather than blocking the others, so the feature works on any job that has an interrupt. Jobs with no interrupt (casters, healers, melee) simply never find an available action and the feature no-ops.

## Cooldowns and retrying

`UseAction` can report success and still be silently dropped under an animation lock, so its return value isn't trusted. The use is re-attempted every frame while the conditions hold and `GetActionStatus` reports the interrupt is usable (returns 0); once it goes on its 30s recast the status flips non-zero and the retry stops on its own. A 2-second anti-double-fire debounce (the same one the auto-heal regen ability uses) guards the gap before the recast registers.

## Options

- **Watch list** - the spell names that trigger the interrupt (case-insensitive, matched exactly). Type a name and press the **+** button to add one; use the trash button to remove. The list starts with **Malice**.
