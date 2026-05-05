# Fancy Loading Screens

Replaces the FFXIV loading-screen image with the destination zone's concept art (the larger painted scenery shots from the lobby tour). Skips InstanceContent (dungeons / raids etc) which have their own loading visuals.

Adapted from [Dalamud.LoadingImage](https://github.com/MapleHinata/Dalamud.LoadingImage) by goat / Maple. The original used a byte-signature hook on the game's territory-change function; this port reads `GameMain.NextTerritoryTypeId` from `_LocationTitle.PreSetup` instead, so there's no sig to maintain across patches.

**Caveat:** the concept art textures are 16:9-sized; they don't fill ultrawide displays. The default layout values (scale, offset) are tuned for 16:9. If you're on something else and want to nudge them, the sliders in the config tree expose the same Width/Height/Scale/X/Y values the original plugin used.
