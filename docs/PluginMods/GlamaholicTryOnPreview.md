# Glamaholic: Try On Item List On Hover

When you edit a plate in [Glamaholic](https://github.com/caitlyn-gg/Glamaholic) and click a slot, it opens a scrolling list of every item that fits that slot. Normally you'd try items on one at a time by hand. This tweak makes hovering an entry in that list automatically try the item on - as long as the game's Fitting Room ("Try On") window is already open - so you can flip through looks by moving the cursor down the list.

Items are tried on undyed and layer onto whatever is already in the Fitting Room, exactly like the game's own right-click "Try On". The "Save/Delete Outfit" toggle is left untouched, so you can preview pieces over your current glamour.

## Options

- **Hover delay before trying on (ms)** (default 100) - how long the cursor must rest on an entry before it's tried on. Higher values stop fast scrolling from firing a burst of try-ons; lower values feel snappier.

Glamaholic must be installed and loaded; reload YABOT after installing it.

<!--
IMPLEMENTATION NOTE (reusable technique - not user-facing)

This feature reads which row the cursor is over inside *another plugin's* ImGui list without
editing that plugin, forking it, or using IPC. It works because every Dalamud plugin renders into
the same shared ImGui context, so the global hovered-id is visible to us too. The trick, general to
any "react to what the user hovers in plugin X's list" problem:

  1. Each frame (subscribe to UiBuilder.Draw - no window/overlay needed, we draw nothing), read the
     shared context: ImGui.GetCurrentContext() -> HoveredIdPreviousFrame (fully resolved, so draw
     order doesn't matter) and HoveredWindow.
  2. Identify the target list by HoveredWindow.Name. ImGui names a BeginChild("foo") window
     "<parent>/foo_<hex>", so a substring match on the child's str_id ("item search" for Glamaholic)
     pins down exactly the list you care about and rejects everything else.
  3. The hovered-id is an opaque hash, but ImGui's hash (ImHashStr) is deterministic: a reflected
     CRC32 (poly 0xEDB88320) seeded by the enclosing id-stack entry. Glamaholic's selectables are
     Selectable($"##{rowId}") with no intervening PushID, so the seed is just HoveredWindow.ID.
     Reproduce ImHashStr in C#, hash "##{rowId}" for every candidate row (here: all equippable
     items, cached per-seed into an id->row map), and look the hovered-id up. That maps the hash
     back to the actual item id. See GlamaholicTryOnPreview.ResolveHoveredRow / ImHashStr.
  4. Act on it - here, AgentTryon.TryOn(0, rowId, 0, 0).

The only coupling to Glamaholic internals is two string constants: the child str_id ("item search")
and the label format ("##{rowId}"). If a future Glamaholic version restructures that popup the
feature just goes quiet (no match) rather than misbehaving, and re-pointing it is a one-line change.

The ImHashStr "###" reset rule (triple-hash resets the running crc to the seed) is handled but
never triggers here because the labels use "##".
-->
