# OfDungeonsDeep: Target Window Background

[OfDungeonsDeep](https://github.com/NotNite/OfDungeonsDeep) shows a target details window (portrait, HP, auto-attack, aggro type, floor range, vulnerabilities, abilities, and notes) for the mob you're targeting inside a deep dungeon. That window always renders with the default opaque ImGui background and border, which can sit awkwardly over the game.

This tweak changes that background. A radio picks the mode:

- **Remove background** - OR's `NoBackground` into the window flags, so the portrait and stats float over the game with no box or border at all.
- **Set transparency** - leaves the background drawn but dims it to a chosen opacity via the window's `BgAlpha`, exposed as a **Background opacity** slider (0.0 - 1.0) that only appears in this mode.

Because it's a plugin mod that only matters inside deep dungeons, it appears under both the **Plugin Mods** and **Deep Dungeons** categories in the YABOT window (via `BaseFeature.AdditionalCategories`).

## How it works

OfDungeonsDeep's target window only ever toggles `NoMove`/`NoResize` in its own `PreDraw`, so the `NoBackground` flag / `BgAlpha` this tweak sets persist frame to frame - no per-frame draw wrapper is needed (unlike `BossModTransparentHints`, which wraps its window because it also suppresses content). The live `Window` instance is cached and the current mode/opacity is re-applied each frame on the framework tick, so changes to the radio or slider take effect immediately. When the tweak is disabled it restores the original flags and `BgAlpha`.

OfDungeonsDeep doesn't expose its `WindowSystem` statically, so the window is located through its public field chain - `Plugin.Controller` (static) → `WindowController` → `TargetDataWindow` (which derives from Dalamud's `Window`) - via reflection. Because `Controller` is assigned asynchronously after the bestiary data finishes loading, and the window instance is recreated whenever OfDungeonsDeep reloads, the lookup is retried on the framework tick until it succeeds and the cache is dropped whenever the installed-plugins set changes.

Requires OfDungeonsDeep to be installed and loaded; reload YABOT after installing it.
