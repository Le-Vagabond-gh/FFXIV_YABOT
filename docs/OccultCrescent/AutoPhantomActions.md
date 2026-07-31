# Auto Phantom Actions

While in Occult Crescent, in combat and with an enemy targeted, automatically fires your current phantom job's duty actions from a hardcoded list:

- **Damage actions** (Deadly Blow, Iainuki, cannons, Occult Comet, spell blades, Fuma Shuriken, Hellfire, Doomsday, the Predict and Dance cycles, etc.) are used on cooldown, with range/line-of-sight checks so they don't whiff. Self-centered attacks only fire when the target is inside their effect radius.
- **Debuffs** (Occult Slowga, Occult Mage Masher, Pilfer Weapon, Mesmerize, Occult Toad, Occult Libra) are only applied when the target doesn't already carry the debuff. Occult Libra is reapplied to each target roughly every 110 seconds.
- **Buffs** (Phantom Aim, Offensive Aria, Hero's Rime, Battle Bell, Occult Quick, Magic Shell, Defend, Phantom Guard, etc.) are only used when the buff isn't already active on you. Offensive Aria and Hero's Rime block each other since they can't stack.

Heals, resurrections, movement abilities, and out-of-combat utility (Zeninage, Occult Falcon, Vigilance, Steal...) are never used.

Actions whose effects don't work on bosses are skipped against boss (level "??") targets: the instant-kill and %HP attacks (Iainuki, Phantom Fire, Finisher, Occult Missile, Doomsday) and the hard crowd control debuffs (Occult Toad, Occult Slowga, Mesmerize).

Actions with a cast time are skipped while you are moving. At most one action is fired every 700ms so the feature doesn't fight your own inputs. Works alongside WrathCombo's auto-rotation.

Notes:

- **Starfall** (Oracle prophecy) is included and deals up to 90% of your own max HP when it fires.
- **Occult Jump** (Dragoon) is included and leaps you onto the target.
- **Doomsday** (Necromancer) consumes 10% of your max HP per use.
