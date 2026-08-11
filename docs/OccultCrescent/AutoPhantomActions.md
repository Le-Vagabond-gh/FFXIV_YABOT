# Auto Phantom Actions

While in Occult Crescent, in combat and with an enemy targeted, automatically fires your current phantom job's duty actions from a hardcoded list:

- **Damage actions** (Deadly Blow, Iainuki, cannons, Occult Comet, spell blades, Fuma Shuriken, Hellfire, the Predict and Dance cycles, etc.) are used on cooldown, with range/line-of-sight checks so they don't whiff. Self-centered attacks only fire when the target is inside their effect radius.
- **Debuffs** (Occult Slowga, Occult Mage Masher, Pilfer Weapon, Mesmerize, Occult Libra) are only applied when the target doesn't already carry the debuff, no matter who put it there. For Occult Libra that means any of the four elemental weaknesses, so a Libra someone else in your party already landed isn't wasted by recasting it. As a backstop it also won't be reapplied to the same target more often than roughly every 110 seconds.
- **Buffs** (Phantom Aim, Offensive Aria, Hero's Rime, Battle Bell, Occult Quick, Magic Shell, Defend, Phantom Guard, etc.) are only used when the buff isn't already active on you. Offensive Aria and Hero's Rime block each other since they can't stack.

Heals, resurrections, movement abilities, and out-of-combat utility (Occult Falcon, Vigilance, Steal...) are never used. The Phantom Necromancer spells that inflict Doom on you (Deep Freeze, Hell Wind, Chaos Drive, Doomsday) are never used by default either - only Drain Touch is. An optional setting enables them when playing Gunbreaker: they only fire when you are above 95% HP and a heal is guaranteed to cleanse the Doom by topping you back to full - either Aurora's regen is on you (the feature casts Aurora on you first when it isn't), or Catharsis of Corundum is about to expire within the Doom window and its expiration heal will do the job. While the setting is active, Heart of Corundum is also kept on cooldown so those Catharsis windows keep cycling.

Actions with no effect on bosses are skipped against boss (level "??") targets: Occult Missile (pure %HP damage) and the hard crowd control debuffs (Occult Slowga, Mesmerize). Attacks whose instant-kill bonus fizzles on bosses (Iainuki, Phantom Fire, Finisher) still fire for their normal damage.

Actions that share a cooldown (like the phantom summoner's summons or the cannoneer's cannon pairs) follow the duty action panel: when the panel's big main button shows one of them, that one is used, so rotate the panel to choose which one the feature casts. A shared group the main button isn't part of still fires through its first action. If the main button holds an action the feature never uses (like Earthen Wall), its shared cooldown is left untouched for manual use.

Actions with a cast time are skipped while you are moving. At most one action is fired every 700ms so the feature doesn't fight your own inputs. Works alongside WrathCombo's auto-rotation.

Notes:

- **Occult Toad** (Black Mage) and **Starfall** (Oracle prophecy) are never used - Toad is only worth spending on select targets rather than every trash mob, and Starfall costs up to 90% of your own max HP. Both are left for you to fire manually.
- **Zeninage** (Samurai) consumes one Occult Coffer from your inventory per use, so it is only fired at boss (level "??") targets and never spent on trash.
- **Occult Jump** (Dragoon) is included and leaps you onto the target.
