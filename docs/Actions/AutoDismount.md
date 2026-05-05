# Auto-Dismount on Blocked Action

When you press an action that's unavailable while mounted (status 579 - e.g. attacking, gathering, interacting), the plugin auto-dismounts instead of letting the action fail. Skips FATE vehicles / cosmic mechs and steps aside when vnavmesh is actively pathing so it doesn't fight other movement plugins.

Originally a standalone plugin (`ffxiv_autodismount`); folded into YABOT.
