# Hide Cross-World Title

Hides or replaces the `«Wanderer»` and `«Traveler»` indicators the game automatically puts on nameplates of players visiting from another data center or another world on your data center.

The game writes those indicators into the free-company-tag slot of the nameplate (the line below the name where the FC tag would normally appear), so this feature acts on that slot.

Each kind can be toggled independently:

- **Wanderer**: visitors from a different data center
- **Traveler**: visitors from a different world on your own data center

The "Replace with" setting picks what shows up:

- **Hide**: clears the slot entirely
- **Globe icon**: shows the game's built-in cross-world globe icon in place of the text

The feature uses Dalamud's `INamePlateGui` cooperative nameplate API, so it composes cleanly with other nameplate plugins (e.g. PartyIcons) - it only touches the FC-tag field, leaving names, titles, icons, and other modifications untouched.
