# Gearset Titles

Assign a title to each of your gearsets. Whenever you switch to a gearset, its title is applied automatically.

It doesn't matter what did the switching - the gearset window, a hotbar slot, a `/gearset change` macro or another plugin all work the same way, because the feature reacts to the gearset switch itself rather than to one particular command.

## Assigning titles

Each existing gearset gets a dropdown listing every title your character has unlocked, with a search box for finding one quickly. Three special entries sit at the top of every dropdown:

- **(use default)** - this gearset has no title of its own and follows the default setting below.
- **(no title)** - switching to this gearset removes your title.
- Anything else is a specific title.

## Settings

- **Default title** - what gearsets marked *(use default)* apply. Set it to **(leave my title alone)** to have those gearsets change nothing, **(no title)** to have them clear your title, or pick a title to use everywhere you haven't chosen something specific.
- **Announce the title change in chat** - prints a line naming the title that was applied. Off by default, since gearset switching is frequent.

## Notes

The title changes as part of the switch, including when you re-equip the gearset you're already on. If you set a title by hand afterwards it stays until your next gearset switch.

A switch the game refuses (in combat, or with gear locked in a duty) leaves your title alone. Logging in doesn't change your title either, since nothing is equipping a gearset.

Titles you haven't unlocked are skipped rather than applied. If the dropdown says the title list isn't loaded yet, open the game's own title window once (Character -> Title) and reopen the settings.
