# Hide Duty Action Panel Background

Hides the brown wooden frame behind the Duty Action panel (the `_ActionContents` bar), so its slots float over the game instead of sitting in an opaque panel.

The brown frame is drawn by the addon's `NineGrid` background node(s); the action icons themselves are component/image nodes. The feature toggles off only the top-level `NineGrid` nodes of the addon, so the slots and any per-slot frames nested inside the slot components are left untouched.

The frame is re-applied whenever the panel is recreated or refreshed (the game otherwise redraws it), and is restored when the feature is disabled.
