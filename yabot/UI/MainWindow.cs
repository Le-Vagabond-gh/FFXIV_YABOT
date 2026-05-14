using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using YABOT.Features;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YABOT.UI;

internal class MainWindow : Window
{
    public string OpenWindow { get; private set; } = AllTweaks;

    public const string AllTweaks = "All Tweaks";
    public const string EnabledLabel = "Enabled";

    private static readonly (string Label, FeatureType? Type)[] Categories =
    {
        (AllTweaks, null),
        (EnabledLabel, null),
        ("Actions", FeatureType.Actions),
        ("Chat", FeatureType.Chat),
        ("Commands", FeatureType.Commands),
        ("Other", FeatureType.Other),
        ("Targets", FeatureType.Targets),
        ("UI", FeatureType.UI),
        ("Occult Crescent", FeatureType.OccultCrescent),
        ("Plugin Mods", FeatureType.PluginMods),
    };

    public MainWindow() : base($"YABOT {typeof(MainWindow).Assembly.GetName().Version}###YABOTMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(650, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Flags = ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = false;
    }

    private string searchString = string.Empty;
    private readonly List<BaseFeature> filteredFeatures = new();

    public override void Draw()
    {
        var region = ImGui.GetContentRegionAvail();
        var topLeftSideHeight = region.Y;

        if (!ImGui.BeginTable("###YABOTTableContainer", 2, ImGuiTableFlags.Resizable)) return;

        try
        {
            ImGui.TableSetupColumn("###LeftColumn", ImGuiTableColumnFlags.WidthFixed, ImGui.GetWindowWidth() / 3);
            ImGui.TableNextColumn();

            var regionSize = ImGui.GetContentRegionAvail();
            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.0f, 0.5f));
            if (ImGui.BeginChild("###YABOTLeft", regionSize with { Y = topLeftSideHeight }, false, ImGuiWindowFlags.NoDecoration))
            {
                ImGui.TextWrapped(Punchline);
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                foreach (var (label, type) in Categories)
                {
                    if (type.HasValue && !P.Features.Any(f => f.FeatureType == type.Value))
                        continue;

                    if (ImGui.Selectable(label, OpenWindow == label))
                        OpenWindow = label;
                }

                ImGui.SetCursorPosY(ImGui.GetContentRegionMax().Y - 45f);
                ImGui.Separator();
                ImGui.TextUnformatted("Search");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("###FeatureSearch", ref searchString, 500))
                {
                    filteredFeatures.Clear();
                    if (searchString.Length > 0)
                    {
                        foreach (var feature in P.Features)
                        {
                            if (feature.FeatureType == FeatureType.Commands) continue;

                            if (feature.Description.Contains(searchString, StringComparison.CurrentCultureIgnoreCase) ||
                                feature.Name.Contains(searchString, StringComparison.CurrentCultureIgnoreCase))
                                filteredFeatures.Add(feature);
                        }
                    }
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();

            ImGui.TableNextColumn();
            if (ImGui.BeginChild("###YABOTRight", Vector2.Zero, false,
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse))
            {
                if (filteredFeatures.Count > 0)
                {
                    DrawFeatures(filteredFeatures.ToArray(), header: "Search Results");
                }
                else if (OpenWindow == AllTweaks)
                {
                    DrawCommandTable(P.Features);
                    DrawFeatures(P.Features.ToArray(), header: AllTweaks);
                }
                else if (OpenWindow == EnabledLabel)
                {
                    var enabledFeatures = P.Features.Where(f => f.Enabled).ToArray();
                    DrawFeatures(enabledFeatures, header: EnabledLabel);
                }
                else
                {
                    var match = Categories.FirstOrDefault(c => c.Label == OpenWindow);
                    if (match.Type is FeatureType type)
                    {
                        if (type == FeatureType.Commands)
                        {
                            DrawCommandTable(P.Features);
                            var commandTabFeatures = P.Features
                                .Where(x => x.FeatureType == type || x.CommandReferences.Any())
                                .ToArray();
                            DrawFeatures(commandTabFeatures, header: "");
                        }
                        else if (type == FeatureType.PluginMods)
                        {
                            DrawPluginModsHeader();
                            DrawFeatures(P.Features.Where(x => x.FeatureType == type).ToArray(), header: match.Label);
                        }
                        else
                        {
                            DrawFeatures(P.Features.Where(x => x.FeatureType == type).ToArray(), header: match.Label);
                        }
                    }
                }
            }
            ImGui.EndChild();
        }
        catch (Exception ex)
        {
            ex.Log();
        }

        ImGui.EndTable();
    }

    private static void DrawPluginModsHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
        ImGui.TextWrapped(
            "These tweaks reach into other plugins to alter their behaviour. They can stop working - or behave unexpectedly - when those plugins are updated. If a tweak misbehaves after a plugin update, disable it here and report it on the YABOT repo.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawCommandTable(IEnumerable<BaseFeature> features)
    {
        var rows = features.SelectMany(f => f.CommandReferences).OrderBy(r => r.Command).ToArray();
        if (rows.Length == 0) return;

        ImGuiEx.LineCentered("featureHeaderCommands", () => ImGui.Text("Commands"));
        ImGui.Separator();

        if (!ImGui.BeginTable("###YABOTCommandsTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Aliases / Args", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch, 150f);
        ImGui.TableHeadersRow();

        foreach (var (cmd, aliases, desc) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextWrapped(cmd);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(aliases);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(desc);
        }

        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static void DrawFeatures(IEnumerable<BaseFeature> features, string header)
    {
        var list = features as BaseFeature[] ?? features.ToArray();
        if (list.Length == 0) return;

        if (!string.IsNullOrEmpty(header))
        {
            ImGuiEx.LineCentered($"featureHeader_{header}", () => ImGui.Text(header));
            ImGui.Separator();
        }

        foreach (var feature in list)
        {
            // CommandFeatures are documented in the command table; they don't have a checkbox toggle.
            if (feature is CommandFeature) continue;

            using (ImRaii.Disabled(feature.FeatureDisabled))
            {
                var enabled = feature.Enabled;
                if (ImGui.Checkbox($"###{feature.Name}", ref enabled))
                {
                    if (enabled)
                    {
                        try
                        {
                            feature.Enable();
                            if (feature.Enabled) Config.EnabledFeatures.Add(feature.GetType().Name);
                        }
                        catch (Exception ex) { Svc.Log.Error(ex, $"Failed to enable {feature.Name}"); }
                    }
                    else
                    {
                        try
                        {
                            feature.Disable();
                            Config.EnabledFeatures.RemoveAll(x => x == feature.GetType().Name);
                        }
                        catch (Exception ex) { Svc.Log.Error(ex, $"Failed to disable {feature.Name}"); }
                    }
                    Config.Save();
                }
                ImGui.SameLine();
                var changed = false;
                feature.DrawConfig(ref changed);
                ImGui.Spacing();
                ImGui.TextWrapped(feature.Description);
            }

            if (feature.FeatureDisabled)
                ImGuiEx.Text(ImGuiColors.DalamudRed, $"Disabled - Reason: {feature.DisabledReason}");

            ImGui.Separator();
        }
    }
}
