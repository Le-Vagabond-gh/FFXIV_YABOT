# Auto-Pose After Drawing Weapon

When you draw your weapon **outside of combat**, the plugin waits a configurable delay (default 2 seconds) and then runs `/cpose`, switching your character to the alternate battle stance pose.

It triggers on the weapon being drawn, whatever caused it - the draw/sheathe keybind or the `/draw` command.

The pending pose change is cancelled if, before the delay elapses, you:

- sheathe your weapon again, or
- enter combat.

Drawing the weapon *while already in combat* never triggers it. Tune the delay with the slider in the feature's settings.
