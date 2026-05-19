# BossMod: Transparent Text Hints Window Background

BossMod's `TrishaMode` setting makes the radar window background transparent so the radar floats over the game without a black box around it. But the equivalent option does not exist for the separate text-hints window (enabled via BossMod's "Show text hints in separate window" option) - it always renders with the default opaque ImGui background.

This tweak removes that background by replacing BossMod's hints window with a wrapper that ORs `NoBackground` into the window flags every frame. The original window is restored when the tweak is disabled.

Additionally, the wrapper hides the "Cur: Inactive" line that BossMod renders when no encounter is active. It does this by detecting that `StateMachine.ActiveState == null` and temporarily flipping `BossModuleConfig.ShowMechanicTimers` to `false` for the duration of that single Draw call, so `BossModule.Draw` skips the `StateMachine.Draw()` invocation entirely. No bossmod logic is duplicated and the field is restored immediately after.

Requires BossMod's "Show text hints in separate window" option to be enabled (under "Boss Modules and Radar" in BossMod's settings). Re-applies automatically if BossMod is reloaded.
