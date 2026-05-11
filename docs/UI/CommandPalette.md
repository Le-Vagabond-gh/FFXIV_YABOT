# Command Palette

Adds a small button to the server info bar (top-right) that drops down a searchable list of every slash command currently registered by your installed plugins, plus any custom shortcuts you've saved. Click a command to run it - the dropdown closes automatically.

- **Click** the server info bar entry: open the dropdown (anchored under the click). Click again to close it.
- **Shift-click** the entry: open settings (filter mode, lists, shortcuts, behaviour toggles).
- **Star icon (☆ / ★)** in front of a command: pin it to the top of the dropdown. Favourites are sorted before everything else, then by plugin, then alphabetically.
- **Right-click** a command in the dropdown: run it, copy it, toggle its favourite, or add/remove it from the active list.
- **Click a long favourite description**: toggle between the trimmed preview and the full text. The preview cuts at the first newline if there is one, otherwise at ~200 characters. Non-favourite descriptions are always shown in full.
- **"Display non-favourited commands"** checkbox next to the filter box: when unchecked, the dropdown shows only favourites and shortcuts.
- **Click outside / focus another window**: dropdown auto-closes.

The filter box matches against the command, the owning plugin's assembly name, the help text, and shortcut labels.

## Custom shortcuts

Sub-commands like `/li w` (world change) are parsed by the game or the owning plugin, so they aren't enumerable through the Dalamud command API. To get one-click access to them, add a **custom shortcut** in settings:

- A shortcut is a `(label, command line)` pair. The label is what appears in the dropdown; the command line is what's actually sent (verbatim) through `CommandManager.ProcessCommand`.
- Shortcuts always appear at the top of the dropdown, above favourites.
- Right-click a shortcut to copy the command or run it from the menu.
- Edit shortcuts in place in the settings table. Use the row's `x` button to delete one.

## Display options

- **Hide commands flagged `ShowInHelp=false`** - many plugins register hidden debug / internal commands. On by default.
- **Hide YABOT's own commands** - off by default.

## Caveats

- Plugins that hide sub-commands behind a single root (e.g. `/pdr <something>`) only register the root, so the dropdown can only invoke the root.
- Some commands need arguments and won't do anything useful on a bare click. Save those as custom shortcuts instead.
