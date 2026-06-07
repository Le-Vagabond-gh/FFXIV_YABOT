# Auto-Heal (Potions & Regen)

While inside a deep dungeon, this keeps you alive by automatically using the dungeon's emergency consumables and any self-regen ability your job has. Everything is driven from the live `InstanceContentDeepDungeon` struct (via `EventFramework.GetInstanceContentDeepDungeon()`), so the feature does nothing at all outside a deep dungeon, and picks the right potions from `dd->DeepDungeonId`.

## What it does

- **Auto-use HP potion** - drinks the dungeon's instant-heal potion when your HP drops below the configured percentage (default 30%). The potion is chosen per dungeon: **Max-Potion** (item 13637) in Palace of the Dead, **Super-Potion** (23167) in Heaven-on-High.
- **Auto-use regen potion** - drinks the dungeon's HP-regen potion below a higher percentage (default 60%): **Sustaining Potion** (20309) in PotD, **Empyrean Potion** (23163) in HoH. Both grant the **Rehabilitation** regen for 30s; it is not re-drunk while that regen is already ticking (detected by status name, which also avoids stacking over a manual drink).
- **Auto-use regen ability** - on jobs that have a self-targeted regen oGCD, keeps it up while you are in combat, recasting on yourself whenever its buff isn't already active: **Gunbreaker - Aurora** (action 16151, status 1835) and **Warrior - Equilibrium** (action 3552, status 2681). It is gated to in-combat so charges aren't burned while exploring between packs.

Eureka Orthos has no equivalent potions, so the potion options simply do nothing there; the regen ability still runs.

## Quality (HQ) handling

The potions you carry may be **High Quality**, and the game's item/action API addresses an HQ item as `itemId + 1,000,000`. Asking to use the plain NQ id while you only hold HQ is rejected with "You do not have that item" (`GetActionStatus` returns LogMessage 583), and `GetInventoryItemCount` counts a single quality at a time. So before each use the feature checks the NQ count, then the HQ count, and addresses whichever quality you actually hold (preferring NQ). This is what lets it drink an HQ-only stock of Super-Potions.

## Cooldowns and retrying

Potions are fired **insistently**: on every `Framework.Update` tick, while the item is usable (`GetActionStatus` returns 0) and you are below the threshold, the feature re-attempts the drink. `UseAction` can report success and still be silently dropped under an animation lock ("it doesn't always go through"), so its return value is never trusted as proof. Instead the feature watches two real signals - the item going on recast (`GetActionStatus` flips to non-zero) and the inventory count dropping - to know the drink actually landed and stop. The only thing that pauses the retry is a brief 0.5s window after a fire whose count drop hasn't registered yet, so a single queued use doesn't turn into two drinks. The regen ability (an oGCD with no inventory to watch) keeps a simple 2-second anti-double-fire debounce.

The HP potion and regen potion are independent items on their own recasts, so a low-HP emergency drink can fire even while the regen potion is on cooldown. Priorities fall out naturally from the two thresholds: below 30% you get both an instant heal and (if available) a regen, below 60% just the regen.

## Options

- **Auto-use HP potion** + **below this HP %** (1-99, default 30)
- **Auto-use regen potion** + **below this HP %** (1-99, default 60)
- **Auto-use regen ability (Aurora / Equilibrium)** (default on)
