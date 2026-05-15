using Dalamud.Game.Config;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Dalamud.Bindings.ImGui;
using YABOT.FeaturesSetup;

namespace YABOT.Features.Other
{
    public class AutoDisplayNameSettings : BaseFeature
    {
        public override string Name => "Auto Display Name Settings";

        public override string Description => "Automatically changes the display name setting for other PCs based on whether you are in a city, in a duty, or outside.";

        public override FeatureType FeatureType => FeatureType.Other;

        public enum DisplayNameType
        {
            Always = 0,
            DuringBattle = 1,
            WhenTargeted = 2,
            Never = 3,
        }

        private static readonly string[] DisplayNameLabels = { "Always", "During Battle", "When Targeted", "Never" };

        public class Configs : FeatureConfig
        {
            public DisplayNameType InCities = DisplayNameType.Always;
            public DisplayNameType OutsideCities = DisplayNameType.Always;
            public DisplayNameType InDuty = DisplayNameType.Always;
        }

        public Configs Config { get; set; } = null!;

        public override bool UseAutoConfig => false;

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            var inCity = (int)Config.InCities;
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.Combo("In Cities", ref inCity, DisplayNameLabels, DisplayNameLabels.Length))
            {
                Config.InCities = (DisplayNameType)inCity;
                hasChanged = true;
                ApplyForCurrentZone();
            }

            var outside = (int)Config.OutsideCities;
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.Combo("Outside of Cities", ref outside, DisplayNameLabels, DisplayNameLabels.Length))
            {
                Config.OutsideCities = (DisplayNameType)outside;
                hasChanged = true;
                ApplyForCurrentZone();
            }

            var inDuty = (int)Config.InDuty;
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.Combo("In Duty", ref inDuty, DisplayNameLabels, DisplayNameLabels.Length))
            {
                Config.InDuty = (DisplayNameType)inDuty;
                hasChanged = true;
                ApplyForCurrentZone();
            }
        };

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
            ApplyForCurrentZone();
            base.Enable();
        }

        private void OnTerritoryChanged(uint territoryId)
        {
            ApplyForCurrentZone();
        }

        private void ApplyForCurrentZone()
        {
            try
            {
                DisplayNameType desired;
                if (Player.IsInDuty)
                    desired = Config.InDuty;
                else if (Player.TerritoryIntendedUseEnum == TerritoryIntendedUseEnum.City_Area)
                    desired = Config.InCities;
                else
                    desired = Config.OutsideCities;
                Svc.GameConfig.Set(UiConfigOption.NamePlateDispTypeOther, (uint)desired);
            }
            catch { }
        }

        public override void Disable()
        {
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
            SaveConfig(Config);
            base.Disable();
        }
    }
}
