using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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
    public class CommandPalette : Feature
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
            public bool ShowNonFavourites = true;
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
                | ImGuiWindowFlags.NoDocking
                | ImGuiWindowFlags.NoSavedSettings;

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
                    // (the server info bar lives in the top-right of the screen).
                    ImGui.SetNextWindowPos(requestedAnchor.Value, ImGuiCond.Always, new Vector2(1f, 0f));
                    ImGui.SetNextWindowSize(new Vector2(720, 360), ImGuiCond.Appearing);
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

                if (!parent.Config.ShowNonFavourites)
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

            private void DrawFilterRow()
            {
                var checkboxLabel = "Display non-favourited commands";
                var checkboxWidth = ImGui.CalcTextSize(checkboxLabel).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
                var available = ImGui.GetContentRegionAvail().X;
                var filterWidth = Math.Max(120f, available - checkboxWidth - ImGui.GetStyle().ItemSpacing.X);

                ImGui.SetNextItemWidth(filterWidth);
                ImGui.InputTextWithHint("##filter", "Filter commands, plugins, shortcuts or descriptions...", ref filterText, 256);
                ImGui.SameLine();

                var show = parent.Config.ShowNonFavourites;
                if (ImGui.Checkbox(checkboxLabel, ref show))
                {
                    parent.Config.ShowNonFavourites = show;
                    parent.Save();
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

                if (!ImGui.BeginTable("YABOT_CommandPaletteTable", 4, tableFlags))
                    return;

                // Star, command and plugin columns auto-fit to their content (WidthFixed with init width 0).
                // Description uses WidthStretch so it takes the remaining space - with the default window
                // width of 720, that's around 350px. The table scrolls internally with a sticky header row.
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 0f);
                ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 0f);
                ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 0f);
                ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableHeadersRow();

                DrawShortcutRows(shortcutRows);
                DrawCommandRows(commandRows);

                ImGui.EndTable();
            }

            private void DrawShortcutRows((CustomShortcut Shortcut, int Index)[] rows)
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

                    // Description column - shows the user-defined label.
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(string.IsNullOrEmpty(sc.Label) ? "(no label)" : sc.Label);
                }
            }

            private void DrawCommandRows((string Command, IReadOnlyCommandInfo Info, string Assembly)[] rows)
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

                    // ----- Description column -----
                    ImGui.TableNextColumn();
                    DrawDescription(cmd, info.HelpMessage, isFav);
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
                : base("Command Palette Settings###YABOT_CommandPaletteSettings", ImGuiWindowFlags.AlwaysAutoResize)
            {
                this.parent = parent;
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(380, 0),
                    MaximumSize = new Vector2(440, 1000),
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

                if (cfg.Shortcuts.Count > 0
                    && ImGui.BeginTable("##shortcuts_table", 3,
                           ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
                {
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 160f);
                    ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 0f);
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
                        if (ImGui.SmallButton($"^##sc_up_{i}")) moveUp = i;
                        if (atTop) ImGui.EndDisabled();
                        if (ImGui.IsItemHovered() && !atTop) ImGui.SetTooltip("Move up");

                        ImGui.SameLine();
                        if (atBottom) ImGui.BeginDisabled();
                        if (ImGui.SmallButton($"v##sc_down_{i}")) moveDown = i;
                        if (atBottom) ImGui.EndDisabled();
                        if (ImGui.IsItemHovered() && !atBottom) ImGui.SetTooltip("Move down");

                        ImGui.SameLine();
                        if (ImGui.SmallButton($"x##sc_rm_{i}"))
                            toRemove = i;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove");
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
