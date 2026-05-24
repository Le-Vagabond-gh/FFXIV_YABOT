using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using Dalamud.Game.ClientState.Conditions;
using System;

namespace YABOT.Features.OccultCrescent
{
    public unsafe class AutoSwitchOccultGearset : BaseFeature
    {
        public override string Name => "Auto-Switch Occult Gearset";

        public override string Description => "Automatically switches to a gearset whose name contains \"occult\" when entering Occult Crescent, and restores the previous gearset when leaving.";

        public override FeatureType FeatureType => FeatureType.OccultCrescent;

        public override bool UseAutoConfig => false;

        private int targetGearsetId = -1;
        private int previousGearsetId = -1;
        private bool wasInOccultCrescent;

        public override void Enable()
        {
            targetGearsetId = -1;
            previousGearsetId = -1;
            wasInOccultCrescent = false;
            Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
            base.Enable();
        }

        private void OnTerritoryChanged(uint territoryId)
        {
            if (ZoneHelper.IsOccultCrescent(territoryId))
            {
                var freshEntry = !wasInOccultCrescent;
                wasInOccultCrescent = true;
                targetGearsetId = -1;
                TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51]);
                TaskManager.Enqueue(() => Svc.Objects.LocalPlayer != null);
                TaskManager.Enqueue(() => PlayerState.Instance() != null && PlayerState.Instance()->IsLoaded);
                if (freshEntry)
                    TaskManager.Enqueue(SavePreviousGearset);
                TaskManager.Enqueue(FindTargetGearset);
                TaskManager.Enqueue(TrySwitchGearset);
            }
            else if (wasInOccultCrescent)
            {
                wasInOccultCrescent = false;
                TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51]);
                TaskManager.Enqueue(() => Svc.Objects.LocalPlayer != null);
                TaskManager.Enqueue(() => PlayerState.Instance() != null && PlayerState.Instance()->IsLoaded);
                TaskManager.Enqueue(TryRestoreGearset);
            }
        }

        private bool SavePreviousGearset()
        {
            previousGearsetId = RaptureGearsetModule.Instance()->CurrentGearsetIndex;
            return true;
        }

        private bool FindTargetGearset()
        {
            var player = Svc.Objects.LocalPlayer;
            if (player == null) return false;

            var gearsetModule = RaptureGearsetModule.Instance();
            for (var i = 0; i < 100; i++)
            {
                var gearset = gearsetModule->GetGearset(i);
                if (gearset == null) continue;
                if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                if (gearset->Id != i) continue;

                var name = gearset->NameString;
                if (name.Contains("occult", StringComparison.OrdinalIgnoreCase)
                    && gearset->ClassJob == player.ClassJob.RowId)
                {
                    targetGearsetId = gearset->Id;
                    return true;
                }
            }

            targetGearsetId = -1;
            return true;
        }

        private bool TrySwitchGearset()
        {
            if (targetGearsetId < 0) return true;

            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule->CurrentGearsetIndex == targetGearsetId) return true;

            var result = gearsetModule->EquipGearset(targetGearsetId);
            if (result != targetGearsetId) return false;

            return true;
        }

        private bool TryRestoreGearset()
        {
            if (previousGearsetId < 0) return true;

            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule->CurrentGearsetIndex == previousGearsetId) return true;

            var result = gearsetModule->EquipGearset(previousGearsetId);
            if (result != previousGearsetId) return false;

            previousGearsetId = -1;
            return true;
        }

        public override void Disable()
        {
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
            base.Disable();
        }
    }
}
