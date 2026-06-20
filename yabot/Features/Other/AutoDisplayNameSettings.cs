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

        public override string Description => "Automatically changes the display name setting for other PCs based on whether you are in a city, in a duty, in a deep dungeon, in a large-scale duty (alliance raids, Eureka, Bozja, Occult Crescent), or outside.";

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
            public DisplayNameType InDeepDungeons = DisplayNameType.Always;
            public DisplayNameType InLargeScaleDuty = DisplayNameType.Always;
        }

        public Configs Config { get; set; } = null!;

        public override bool UseAutoConfig => false;

        // In deep dungeons the matched players are party members, so they obey the Party
        // nameplate option, not Other. We override it on entry and restore it on exit.
        private uint? savedPartyDisp;

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

            var inDeepDungeon = (int)Config.InDeepDungeons;
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.Combo("In Deep Dungeons", ref inDeepDungeon, DisplayNameLabels, DisplayNameLabels.Length))
            {
                Config.InDeepDungeons = (DisplayNameType)inDeepDungeon;
                hasChanged = true;
                ApplyForCurrentZone();
            }

            var inLargeScale = (int)Config.InLargeScaleDuty;
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.Combo("In Large-Scale Duty (Alliance Raids, Eureka, Bozja, Occult Crescent)", ref inLargeScale, DisplayNameLabels, DisplayNameLabels.Length))
            {
                Config.InLargeScaleDuty = (DisplayNameType)inLargeScale;
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
                var intendedUse = Player.TerritoryIntendedUseEnum;
                var inDeepDungeon = intendedUse == TerritoryIntendedUseEnum.Deep_Dungeon;
                if (intendedUse == TerritoryIntendedUseEnum.Alliance_Raid
                    || intendedUse == TerritoryIntendedUseEnum.Eureka
                    || intendedUse == TerritoryIntendedUseEnum.Bozja
                    || intendedUse == TerritoryIntendedUseEnum.Occult_Crescent)
                    desired = Config.InLargeScaleDuty;
                else if (inDeepDungeon)
                    desired = Config.InDeepDungeons;
                else if (Player.IsInDuty)
                    desired = Config.InDuty;
                else if (intendedUse == TerritoryIntendedUseEnum.City_Area)
                    desired = Config.InCities;
                else
                    desired = Config.OutsideCities;
                Svc.GameConfig.Set(UiConfigOption.NamePlateDispTypeOther, (uint)desired);

                if (inDeepDungeon)
                {
                    if (savedPartyDisp == null && Svc.GameConfig.TryGet(UiConfigOption.NamePlateDispTypeParty, out uint current))
                        savedPartyDisp = current;
                    Svc.GameConfig.Set(UiConfigOption.NamePlateDispTypeParty, (uint)desired);
                }
                else
                {
                    RestorePartyDisp();
                }
            }
            catch { }
        }

        private void RestorePartyDisp()
        {
            if (savedPartyDisp is uint saved)
            {
                Svc.GameConfig.Set(UiConfigOption.NamePlateDispTypeParty, saved);
                savedPartyDisp = null;
            }
        }

        public override void Disable()
        {
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
            RestorePartyDisp();
            SaveConfig(Config);
            base.Disable();
        }
    }
}
