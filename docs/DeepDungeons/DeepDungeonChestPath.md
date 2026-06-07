# Deep Dungeon Chest Paths

While inside a deep dungeon, this draws a path from you to nearby coffers and to the beacon of passage, so you can see at a glance where the loot and the exit are. Pathfinding is delegated to the **vnavmesh** plugin over IPC - vnavmesh builds the floor's navmesh reliably; this feature only requests paths and draws them. **Your character is never moved.**

## What it does

- **Coffer paths** - detects bronze / silver / gold coffers from their object `DataId` (NecroLens tables) and draws a **dotted** line in the coffer's tier color (bronze / silver / gold) to each one in range, with a dot and a walk-time estimate at the coffer.
- **Beacon of passage** - draws a **teal** line with an animated flow toward the stairs to the next floor, so the exit direction is obvious. If more than one distinct beacon shows up on a floor it's a trap floor, so the passage line is suppressed for the rest of that floor.

## Behaviour

- **Detection** is by `DataId`, so it catches coffers regardless of object kind (bronze coffers are `Treasure`, silver/gold are `EventObj`).
- **Cached until the floor changes** - a coffer's location is remembered the first time it's seen and the path keeps being drawn even after the coffer unloads from the object table (when you move far away). The cache is wiped on a floor change. A cached coffer is dropped early only when it disappears from the table while you're standing right next to it (≈5y) - i.e. you opened it.
- **In combat** everything is hidden and pathfinding stops (the cache is kept, so paths reappear when combat ends).
- **Under the ETA cutoff** (≈6s walk) a coffer is hidden and stops being recomputed - you're basically on top of it. It reappears if you move away.
- **Throttling** - nearby coffers are rescanned ~twice a second; a path is only recomputed when you've moved a few yalms, and the previous path stays drawn until the new one resolves (no flicker). The nearest few in-range coffers are pathed (capped).

## Requirements

Requires the **vnavmesh** plugin to be installed and enabled (it computes the path). If it isn't detected, nothing draws and the config panel says so. vnavmesh needs its navmesh built for the floor (it auto-builds, and may lag a second on a fresh floor).

## Walk-time estimate

The estimate is the actual navmesh path length divided by run speed, so it reflects the real route around walls rather than straight-line distance. Run speed defaults to ~6 yalms/s and is adjustable in case you sprint.

## Options

- **Bronze / Silver / Gold coffers** - per-tier toggles.
- **Beacon of passage** - teal path to the stairs (toggle).
- **Max paths** - how many of the nearest coffers to path at once.
- **Line thickness** - the passage line uses this; coffer lines draw at half.
- **Opacity %**.
- **Show walk-time estimate** + **Walk speed (yalms/s)**.
