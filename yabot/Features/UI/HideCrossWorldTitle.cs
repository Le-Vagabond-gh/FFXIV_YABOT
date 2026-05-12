using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public class HideCrossWorldTitle : Feature
    {
        public override string Name => "Hide Cross-World Title";

        public override string Description =>
            "Hides or replaces the \"Wanderer\" / \"Traveler\" indicator the game auto-applies to nameplates of players visiting from another world or data center.";

        public override FeatureType FeatureType => FeatureType.UI;

        public enum DisplayMode
        {
            Hide = 0,
            GlobeIcon = 1,
        }

        private static readonly string[] DisplayModeLabels = { "Hide", "Globe icon" };

        public class Configs : FeatureConfig
        {
            public bool HideWanderer = true;
            public bool HideTraveler = true;
            public DisplayMode Mode = DisplayMode.Hide;
        }

        public Configs Config { get; set; } = null!;

        public override bool UseAutoConfig => false;

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            var mode = (int)Config.Mode;
            ImGui.TextUnformatted("Replace with");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.Combo("##HideCrossWorldTitle_Mode", ref mode, DisplayModeLabels, DisplayModeLabels.Length))
            {
                Config.Mode = (DisplayMode)mode;
                hasChanged = true;
                Service.NamePlateGui.RequestRedraw();
            }
            if (ImGui.Checkbox("Apply to \"Wanderer\" (visitors from another data center)", ref Config.HideWanderer))
            {
                hasChanged = true;
                Service.NamePlateGui.RequestRedraw();
            }
            if (ImGui.Checkbox("Apply to \"Traveler\" (visitors from another world on your data center)", ref Config.HideTraveler))
            {
                hasChanged = true;
                Service.NamePlateGui.RequestRedraw();
            }
        };

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Service.NamePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
            Service.NamePlateGui.RequestRedraw();
            base.Enable();
        }

        public override void Disable()
        {
            Service.NamePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;
            Service.NamePlateGui.RequestRedraw();
            SaveConfig(Config);
            base.Disable();
        }

        private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
        {
            try
            {
                foreach (var handler in handlers)
                {
                    if (handler.NamePlateKind != NamePlateKind.PlayerCharacter) continue;

                    var pc = handler.PlayerCharacter;
                    if (pc == null) continue;

                    var homeId = pc.HomeWorld.RowId;
                    var currentId = pc.CurrentWorld.RowId;
                    if (homeId == 0 || currentId == 0) continue;
                    if (homeId == currentId) continue;

                    var sameDc = false;
                    try
                    {
                        var home = pc.HomeWorld.ValueNullable;
                        var current = pc.CurrentWorld.ValueNullable;
                        if (home.HasValue && current.HasValue)
                        {
                            sameDc = home.Value.DataCenter.RowId == current.Value.DataCenter.RowId;
                        }
                    }
                    catch { }

                    var shouldApply = sameDc ? Config.HideTraveler : Config.HideWanderer;
                    if (!shouldApply) continue;

                    if (Config.Mode == DisplayMode.GlobeIcon)
                    {
                        handler.FreeCompanyTag = new SeString(new TextPayload(" "), new IconPayload(BitmapFontIcon.CrossWorld));
                    }
                    else
                    {
                        handler.RemoveFreeCompanyTag();
                    }
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "HideCrossWorldTitle: error processing nameplate update");
            }
        }
    }
}
