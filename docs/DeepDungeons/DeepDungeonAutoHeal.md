# Auto-Heal (Potions & Regen)

While inside a deep dungeon, this keeps you alive by automatically using the dungeon's emergency consumables and any self-regen ability your job has. Everything is driven from the live `InstanceContentDeepDungeon` struct (via `EventFramework.GetInstanceContentDeepDungeon()`), so the feature does nothing at all outside a deep dungeon, and picks the right potions from `dd->DeepDungeonId`.

## What it does

- **Auto-use HP potion** - drinks the dungeon's instant-heal potion when your HP drops below the configured percentage (default 30%). The potion is chosen per dungeon: **Max-Potion** (item 13637) in Palace of the Dead, **Super-Potion** (23167) in Heaven-on-High.
- **Auto-use regen potion** - drinks the dungeon's HP-regen potion below a higher percentage (default 60%): **Sustaining Potion** (20309) in PotD, **Empyrean Potion** (23163) in HoH. Both grant the **Rehabilitation** regen for 30s; it is not re-drunk while that regen is already ticking (detected by status name, with a 30s use-debounce as a locale-proof backstop that also avoids stacking over a manual drink).
- **Auto-use regen ability** - on jobs that have a self-targeted regen oGCD, keeps it up while you are in combat, recasting on yourself whenever its buff isn't already active: **Gunbreaker - Aurora** (action 16151, status 1835) and **Warrior - Equilibrium** (action 3552, status 2681). It is gated to in-combat so charges aren't burned while exploring between packs.

Eureka Orthos has no equivalent potions, so the potion options simply do nothing there; the regen ability still runs.

## Cooldowns and retrying

Every use goes through `ActionManager.GetActionStatus`, which returns non-zero whenever a GCD, animation lock, or recast would block the action. The feature checks this on every `Framework.Update` tick, so a blocked use isn't skipped - it is simply retried on the next frame until the game accepts it. A short 2-second debounce after each potion/ability use covers the one-or-two-frame gap before the recast registers, preventing an accidental double-use; the regen potion uses a 30-second debounce matching its buff duration.

The HP potion and regen potion are independent items on their own recasts, so a low-HP emergency drink can fire even while the regen potion is on cooldown. Priorities fall out naturally from the two thresholds: below 30% you get both an instant heal and (if available) a regen, below 60% just the regen.

## Options

- **Auto-use HP potion** + **below this HP %** (1-99, default 30)
- **Auto-use regen potion** + **below this HP %** (1-99, default 60)
- **Auto-use regen ability (Aurora / Equilibrium)** (default on)
