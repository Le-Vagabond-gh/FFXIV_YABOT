# Gearset Orphan Cleanup

Watches every gearset's item list and reacts to changes no matter what made them - the game's own "Update Gearset" action, [Stylist](https://github.com/NightmareXIV/Stylist) (which writes gearset memory directly instead of calling the native update function), IPC, or macros.

When a gearset drops a piece of gear and that item is no longer registered to **any** gearset - the same "in a gear set" state the inventory icon shows - the orphan is pulled out of the armoury chest and dropped into the first free inventory slot, scanning bag 1 to bag 4 and slot 0 upward (top-left first), instead of the game's default last-bag/last-slot placement. Gear you still use in another gearset is left alone.

Only gear removed by an actual gearset change is considered - it does not sweep your whole armoury for unused items. Because a direct-memory editor like Stylist writes the gearset entry immediately but shuffles the physical gear over the following frames, a detected orphan stays queued for a short window (~15s) and is moved as soon as it lands in the armoury.

If you use Stylist's own "move items" / "unmove items" options, Stylist may already relocate the old gear (to the default slot) before this feature sees it. Turn those off in Stylist to let this feature handle placement.

Optionally announces each moved item in the chat log.
