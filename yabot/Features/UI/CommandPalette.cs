using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using ECommons.Automation;
using ECommons.DalamudServices;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YABOT.Features.UI
{
    public class CommandPalette : BaseFeature
    {
        public override string Name => "Command Palette";

        public override string Description =>
            "Adds a button to the server info bar that opens a dropdown of every slash command registered by installed plugins, plus your own custom shortcuts (e.g. '/li w '). Click to run, star to pin, shift-click the entry for settings.";

        public override FeatureType FeatureType => FeatureType.UI;

        public class CustomShortcut
        {
            public string Label = string.Empty;
            public string Command = string.Empty;
        }

        public class Configs : FeatureConfig
        {
            public HashSet<string> Favourites = new(StringComparer.OrdinalIgnoreCase);
            public List<CustomShortcut> Shortcuts = new();
            public bool HideHelpHidden = true;
            public bool HideYabot = false;
            public bool FavouritesOnly = false;
            public bool ShowDescription = true;
        }

        public Configs Config { get; private set; } = null!;

        private DropdownWindow? dropdown;
        private SettingsWindow? settingsWindow;
        private IDtrBarEntry? dtrEntry;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            // JSON deserialization loses the StringComparer; rebuild with case-insensitive matching.
            Config.Favourites = new HashSet<string>(Config.Favourites, StringComparer.OrdinalIgnoreCase);
            Config.Shortcuts ??= new List<CustomShortcut>();

            dropdown = new DropdownWindow(this);
            settingsWindow = new SettingsWindow(this);
            P.Ws.AddWindow(dropdown);
            P.Ws.AddWindow(settingsWindow);

            dtrEntry = Service.DtrBar.Get("YABOT.CommandPalette");
            dtrEntry.Text = new SeString(new TextPayload(" ☰ "));
            dtrEntry.Tooltip = new SeString(new TextPayload("Click: command dropdown\nShift-click: settings"));
            dtrEntry.OnClick = _ =>
            {
                if (ImGui.GetIO().KeyShift)
                {
                    settingsWindow.IsOpen = !settingsWindow.IsOpen;
                    return;
                }

                if (dropdown.IsOpen)
                    dropdown.IsOpen = false;
                else
                    dropdown.ShowAt(ImGui.GetIO().MousePos);
            };

            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);

            dtrEntry?.Remove();
            dtrEntry = null;

            if (dropdown != null) P.Ws.RemoveWindow(dropdown);
            if (settingsWindow != null) P.Ws.RemoveWindow(settingsWindow);
            dropdown = null;
            settingsWindow = null;

            base.Disable();
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (ImGui.Button("Open Settings") && settingsWindow != null)
                settingsWindow.IsOpen = true;

            ImGui.TextDisabled($"Favourites: {Config.Favourites.Count} - Shortcuts: {Config.Shortcuts.Count}");
            ImGui.TextDisabled("☰ in the server info bar - click to open the dropdown, shift-click for settings.");
        };

        internal void Save() => SaveConfig(Config);

        internal IEnumerable<(string Command, IReadOnlyCommandInfo Info, string Assembly)> EnumerateVisibleCommands()
        {
            foreach (var kvp in Service.CommandManager.Commands)
            {
                var cmd = kvp.Key;
                var info = kvp.Value;

                if (Config.HideHelpHidden && !info.ShowInHelp) continue;

                var asm = info.Handler.Method.DeclaringType?.Assembly.GetName().Name ?? "(unknown)";
                if (Config.HideYabot && string.Equals(asm, "yabot", StringComparison.OrdinalIgnoreCase)) continue;

                yield return (cmd, info, asm);
            }
        }

        // ----- DropdownWindow -----

        public class DropdownWindow : Window
        {
            private const int FavDescriptionTrimAt = 200;

            private readonly CommandPalette parent;
            private readonly HashSet<string> expandedDescriptions = new(StringComparer.OrdinalIgnoreCase);
            private string filterText = string.Empty;
            private Vector2? requestedAnchor;
            private int appearingFrames;

            private const ImGuiWindowFlags BaseFlags =
                ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoDocking;

            public DropdownWindow(CommandPalette parent)
                : base("###YABOT_CommandPaletteDropdown", BaseFlags)
            {
                this.parent = parent;
                RespectCloseHotkey = true;
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(400, 150),
                    MaximumSize = new Vector2(1400, 900),
                };
            }

            public void ShowAt(Vector2 mousePos)
            {
                filterText = string.Empty;
                requestedAnchor = mousePos;
                appearingFrames = 2;
                IsOpen = true;
            }

            public override void PreDraw()
            {
                if (requestedAnchor.HasValue)
                {
                    // Anchor the top-right corner of the dropdown at the click point so it grows down and to the left
                    // (the server info bar lives in the top-right of the screen). Size persists across
                    // openings via ImGui's ini file - FirstUseEver only seeds the initial value.
                    ImGui.SetNextWindowPos(requestedAnchor.Value, ImGuiCond.Always, new Vector2(1f, 0f));
                    ImGui.SetNextWindowSize(new Vector2(720, 360), ImGuiCond.FirstUseEver);
                    requestedAnchor = null;
                }
            }

            public override void Draw()
            {
                DrawFilterRow();
                ImGui.Separator();

                var shortcutRows = parent.Config.Shortcuts
                    .Select((s, idx) => (Shortcut: s, Index: idx))
                    .Where(t => string.IsNullOrEmpty(filterText)
                                || t.Shortcut.Label.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                                || t.Shortcut.Command.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                IEnumerable<(string Command, IReadOnlyCommandInfo Info, string Assembly)> commandQuery =
                    parent.EnumerateVisibleCommands()
                        .Where(r => string.IsNullOrEmpty(filterText)
                                    || r.Command.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                                    || (r.Info.HelpMessage ?? string.Empty).Contains(filterText, StringComparison.OrdinalIgnoreCase)
                                    || r.Assembly.Contains(filterText, StringComparison.OrdinalIgnoreCase));

                // "★ only" restricts the default view, but an active search looks across everything -
                // otherwise you couldn't find a non-favourited command without first toggling it off.
                if (parent.Config.FavouritesOnly && string.IsNullOrEmpty(filterText))
                    commandQuery = commandQuery.Where(r => parent.Config.Favourites.Contains(r.Command));

                var commandRows = commandQuery
                    .OrderByDescending(r => parent.Config.Favourites.Contains(r.Command))
                    .ThenBy(r => r.Assembly, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Command, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (shortcutRows.Length == 0 && commandRows.Length == 0)
                {
                    ImGui.TextDisabled("No commands match.");
                }
                else
                {
                    DrawTable(shortcutRows, commandRows);
                }

                // Auto-close when focus moves elsewhere (after the first couple of frames so the
                // window has a chance to actually receive focus).
                if (appearingFrames > 0)
                {
                    appearingFrames--;
                }
                else if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
                {
                    IsOpen = false;
                }
            }

            // Approximate width of the description column, used to grow/shrink the window from
            // the right edge when the user toggles "Desc.".
            private const float DescriptionColumnApproxWidth = 300f;

            private void DrawFilterRow()
            {
                const string favOnlyLabel = "★ only";
                const string descLabel = "Desc.";

                var style = ImGui.GetStyle();
                var frameHeight = ImGui.GetFrameHeight();
                var clearWidth = frameHeight; // square IconButton
                var favWidth = ImGui.CalcTextSize(favOnlyLabel).X + frameHeight + style.ItemInnerSpacing.X;
                var descWidth = ImGui.CalcTextSize(descLabel).X + frameHeight + style.ItemInnerSpacing.X;
                var available = ImGui.GetContentRegionAvail().X;
                var filterWidth = Math.Max(120f, available - clearWidth - favWidth - descWidth - style.ItemSpacing.X * 3f);

                ImGui.SetNextItemWidth(filterWidth);
                ImGui.InputTextWithHint("##filter", "Filter commands, plugins, shortcuts or descriptions...", ref filterText, 256);

                ImGui.SameLine(0, style.ItemInnerSpacing.X);
                var hasText = !string.IsNullOrEmpty(filterText);
                if (!hasText) ImGui.BeginDisabled();
                if (ImGuiComponents.IconButton("##filter_clear", FontAwesomeIcon.Times))
                    filterText = string.Empty;
                if (!hasText) ImGui.EndDisabled();
                if (hasText && ImGui.IsItemHovered()) ImGui.SetTooltip("Clear filter");

                ImGui.SameLine();
                var favOnly = parent.Config.FavouritesOnly;
                if (ImGui.Checkbox(favOnlyLabel, ref favOnly))
                {
                    parent.Config.FavouritesOnly = favOnly;
                    parent.Save();
                }

                ImGui.SameLine();
                var showDesc = parent.Config.ShowDescription;
                if (ImGui.Checkbox(descLabel, ref showDesc))
                {
                    parent.Config.ShowDescription = showDesc;
                    parent.Save();

                    // Grow / shrink the window from the left edge (right edge stays put), matching
                    // the dropdown's top-right anchor.
                    var pos = ImGui.GetWindowPos();
                    var size = ImGui.GetWindowSize();
                    var requested = showDesc ? DescriptionColumnApproxWidth : -DescriptionColumnApproxWidth;
                    var newWidth = Math.Max(280f, size.X + requested);
                    var actualDelta = newWidth - size.X;
                    ImGui.SetWindowPos(new Vector2(pos.X - actualDelta, pos.Y));
                    ImGui.SetWindowSize(new Vector2(newWidth, size.Y));
                }
            }

            private void DrawTable(
                (CustomShortcut Shortcut, int Index)[] shortcutRows,
                (string Command, IReadOnlyCommandInfo Info, string Assembly)[] commandRows)
            {
                const ImGuiTableFlags tableFlags =
                    ImGuiTableFlags.Borders
                    | ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.Resizable
                    | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.SizingFixedFit;

                var includeDesc = parent.Config.ShowDescription;
                var columnCount = includeDesc ? 4 : 3;
                // Different table id per layout so saved column widths don't bleed between 3- and 4-column modes.
                var tableId = includeDesc ? "YABOT_CommandPaletteTable" : "YABOT_CommandPaletteTable_NoDesc";

                if (!ImGui.BeginTable(tableId, columnCount, tableFlags))
                    return;

                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 0f);
                ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 0f);
                if (includeDesc)
                {
                    ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 0f);
                    ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch, 1f);
                }
                else
                {
                    ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch, 1f);
                }
                ImGui.TableHeadersRow();

                DrawShortcutRows(shortcutRows, includeDesc);
                DrawCommandRows(commandRows, includeDesc);

                ImGui.EndTable();
            }

            private void DrawShortcutRows((CustomShortcut Shortcut, int Index)[] rows, bool includeDesc)
            {
                foreach (var (sc, idx) in rows)
                {
                    ImGui.TableNextRow();

                    // Star column - shortcuts are implicitly pinned, no toggle.
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled("›");

                    // Command column - the actual slash command that fires when clicked.
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{sc.Command}##sc_run_{idx}"))
                    {
                        RunCommand(sc.Command);
                        IsOpen = false;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{sc.Label}\nRight-click for options");

                    if (ImGui.BeginPopupContextItem($"##sc_ctx_{idx}"))
                    {
                        if (ImGui.MenuItem("Run"))
                        {
                            RunCommand(sc.Command);
                            IsOpen = false;
                        }
                        if (ImGui.MenuItem("Copy command"))
                            ImGui.SetClipboardText(sc.Command);
                        ImGui.EndPopup();
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled("(shortcut)");

                    if (includeDesc)
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(string.IsNullOrEmpty(sc.Label) ? "(no label)" : sc.Label);
                    }
                }
            }

            private void DrawCommandRows((string Command, IReadOnlyCommandInfo Info, string Assembly)[] rows, bool includeDesc)
            {
                var favColour = new Vector4(1f, 0.82f, 0.2f, 1f);

                foreach (var (cmd, info, asm) in rows)
                {
                    ImGui.TableNextRow();

                    var isFav = parent.Config.Favourites.Contains(cmd);

                    // ----- Star column -----
                    ImGui.TableNextColumn();
                    if (isFav) ImGui.PushStyleColor(ImGuiCol.Text, favColour);
                    if (ImGui.SmallButton($"{(isFav ? "★" : "☆")}##fav_{cmd}"))
                        ToggleFavourite(cmd);
                    if (isFav) ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(isFav ? "Unstar (remove from favourites)" : "Star (pin to top of list)");

                    // ----- Command column -----
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{cmd}##run_{cmd}"))
                    {
                        RunCommand(cmd);
                        IsOpen = false;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Click to run\nRight-click for options");

                    if (ImGui.BeginPopupContextItem($"##ctx_{cmd}"))
                    {
                        if (ImGui.MenuItem("Run"))
                        {
                            RunCommand(cmd);
                            IsOpen = false;
                        }
                        if (ImGui.MenuItem("Copy to clipboard"))
                            ImGui.SetClipboardText(cmd);
                        ImGui.Separator();
                        if (ImGui.MenuItem(isFav ? "Remove from favourites" : "Mark as favourite"))
                            ToggleFavourite(cmd);
                        ImGui.EndPopup();
                    }

                    // ----- Plugin column -----
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(asm);

                    if (includeDesc)
                    {
                        ImGui.TableNextColumn();
                        DrawDescription(cmd, info.HelpMessage, isFav);
                    }
                }
            }

            private void DrawDescription(string cmd, string? helpMessage, bool isFav)
            {
                var fullDesc = string.IsNullOrEmpty(helpMessage) ? "(no description)" : helpMessage;

                string? trimmed = null;
                if (isFav)
                {
                    var newlineIdx = fullDesc.IndexOfAny(new[] { '\n', '\r' });
                    if (newlineIdx > 0)
                        trimmed = fullDesc.Substring(0, newlineIdx).TrimEnd();
                    else if (fullDesc.Length > FavDescriptionTrimAt)
                        trimmed = fullDesc.Substring(0, FavDescriptionTrimAt).TrimEnd();
                }

                var canTrim = trimmed != null;
                var isExpanded = expandedDescriptions.Contains(cmd);
                var displayDesc = (canTrim && !isExpanded) ? trimmed + " ..." : fullDesc;

                ImGui.TextWrapped(displayDesc);

                if (!canTrim) return;

                if (ImGui.IsItemClicked())
                {
                    if (isExpanded) expandedDescriptions.Remove(cmd);
                    else expandedDescriptions.Add(cmd);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isExpanded ? "Click to collapse description" : "Click to show full description");
            }

            private static void RunCommand(string cmd)
            {
                // Route through the game's chat box entry so built-in game commands (/dice, /em, /li, ...)
                // work too - Dalamud's CommandManager.ProcessCommand only knows about plugin-registered
                // commands. Plugin commands still work because Dalamud hooks ProcessChatBoxEntry.
                try { Chat.SendMessage(cmd); }
                catch (Exception ex) { Svc.Log.Error(ex, $"CommandPalette: failed to run {cmd}"); }
            }

            private void ToggleFavourite(string cmd)
            {
                if (parent.Config.Favourites.Contains(cmd))
                    parent.Config.Favourites.Remove(cmd);
                else
                    parent.Config.Favourites.Add(cmd);
                parent.Save();
            }
        }

        // ----- SettingsWindow (shortcuts + display options) -----

        public class SettingsWindow : Window
        {
            private readonly CommandPalette parent;
            private string newShortcutLabel = string.Empty;
            private string newShortcutCommand = string.Empty;

            public SettingsWindow(CommandPalette parent)
                : base("Command Palette Settings###YABOT_CommandPaletteSettings")
            {
                this.parent = parent;
                Size = new Vector2(560, 420);
                SizeCondition = ImGuiCond.FirstUseEver;
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(460, 240),
                    MaximumSize = new Vector2(1200, 1500),
                };
            }

            public override void Draw()
            {
                var cfg = parent.Config;
                var changed = false;

                if (ImGui.Checkbox("Hide commands flagged ShowInHelp=false", ref cfg.HideHelpHidden)) changed = true;
                if (ImGui.Checkbox("Hide YABOT's own commands", ref cfg.HideYabot)) changed = true;

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Custom shortcuts");
                DrawShortcutsSection(cfg, ref changed);

                if (changed) parent.Save();
            }

            private void DrawShortcutsSection(Configs cfg, ref bool changed)
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextDisabled("Saved snippets that appear at the top of the dropdown. Useful for things like '/li w ' that the game parses sub-commands for.");
                ImGui.PopTextWrapPos();

                // Reserve a fixed width for the actions column (3 IconButtons + spacing) so the
                // stretch column (Command) shrinks instead of pushing the table past the window.
                var actionsColumnWidth = ImGui.GetFrameHeight() * 3 + ImGui.GetStyle().ItemSpacing.X * 2 + 8f;

                if (cfg.Shortcuts.Count > 0
                    && ImGui.BeginTable("##shortcuts_table", 3,
                           ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch, 0.35f);
                    ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 0.65f);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, actionsColumnWidth);
                    ImGui.TableHeadersRow();

                    int? toRemove = null;
                    int? moveUp = null;
                    int? moveDown = null;
                    for (var i = 0; i < cfg.Shortcuts.Count; i++)
                    {
                        var sc = cfg.Shortcuts[i];

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputText($"##sc_label_{i}", ref sc.Label, 64))
                            changed = true;

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputText($"##sc_cmd_{i}", ref sc.Command, 256))
                            changed = true;

                        ImGui.TableNextColumn();
                        var atTop = i == 0;
                        var atBottom = i == cfg.Shortcuts.Count - 1;

                        if (atTop) ImGui.BeginDisabled();
                        if (ImGuiComponents.IconButton($"##sc_up_{i}", FontAwesomeIcon.ArrowUp)) moveUp = i;
                        if (atTop) ImGui.EndDisabled();
                        if (ImGui.IsItemHovered() && !atTop) ImGui.SetTooltip("Move up");

                        ImGui.SameLine();
                        if (atBottom) ImGui.BeginDisabled();
                        if (ImGuiComponents.IconButton($"##sc_down_{i}", FontAwesomeIcon.ArrowDown)) moveDown = i;
                        if (atBottom) ImGui.EndDisabled();
                        if (ImGui.IsItemHovered() && !atBottom) ImGui.SetTooltip("Move down");

                        ImGui.SameLine();
                        if (ImGuiComponents.IconButton($"##sc_rm_{i}", FontAwesomeIcon.TrashAlt))
                            toRemove = i;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete shortcut");
                    }

                    if (moveUp.HasValue)
                    {
                        var i = moveUp.Value;
                        (cfg.Shortcuts[i - 1], cfg.Shortcuts[i]) = (cfg.Shortcuts[i], cfg.Shortcuts[i - 1]);
                        changed = true;
                    }
                    else if (moveDown.HasValue)
                    {
                        var i = moveDown.Value;
                        (cfg.Shortcuts[i + 1], cfg.Shortcuts[i]) = (cfg.Shortcuts[i], cfg.Shortcuts[i + 1]);
                        changed = true;
                    }

                    if (toRemove.HasValue)
                    {
                        cfg.Shortcuts.RemoveAt(toRemove.Value);
                        changed = true;
                    }

                    ImGui.EndTable();
                }

                ImGui.Spacing();
                ImGui.SetNextItemWidth(160f);
                ImGui.InputTextWithHint("##new_label", "Label (e.g. World change)", ref newShortcutLabel, 64);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 110f);
                ImGui.InputTextWithHint("##new_cmd", "Command (e.g. /li w )", ref newShortcutCommand, 256);
                ImGui.SameLine();
                var canAdd = !string.IsNullOrWhiteSpace(newShortcutLabel) && !string.IsNullOrWhiteSpace(newShortcutCommand);
                if (!canAdd) ImGui.BeginDisabled();
                if (ImGui.Button("Add##sc_add"))
                {
                    cfg.Shortcuts.Add(new CommandPalette.CustomShortcut
                    {
                        Label = newShortcutLabel.Trim(),
                        Command = newShortcutCommand,
                    });
                    newShortcutLabel = string.Empty;
                    newShortcutCommand = string.Empty;
                    changed = true;
                }
                if (!canAdd) ImGui.EndDisabled();
            }
        }
    }
}
