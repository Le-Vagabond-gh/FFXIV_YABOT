# Fancy Loading Screens

Replaces the FFXIV loading-screen image with the destination zone's concept art (the larger painted scenery shots from the lobby tour). Skips InstanceContent (dungeons / raids etc) which have their own loading visuals.

Adapted from [Dalamud.LoadingImage](https://github.com/MapleHinata/Dalamud.LoadingImage) by goat / Maple, updated for Dalamud API 15 (Lumina sheet definition, addon-lifecycle listeners, ECommons services). The original plugin's byte-signature hook on the territory-change function is preserved verbatim - it captures the destination terri ID as a function argument, which is the only race-free signal we found. A polling-based replacement reading `GameMain.NextTerritoryTypeId` from `_LocationTitle.PreDraw` was attempted but produced incorrect / stuck textures across zone changes (the field's update timing relative to PreDraw is inconsistent). The sig is the maintenance burden but for now it's the right tradeoff; revisit when CS exposes a higher-level handler we can hook directly.

**Caveat:** the concept art textures are 16:9-sized; they don't fill ultrawide displays. The default layout values (scale, offset) are tuned for 16:9. If you're on something else and want to nudge them, the sliders in the config tree expose the same Width/Height/Scale/X/Y values the original plugin used.
