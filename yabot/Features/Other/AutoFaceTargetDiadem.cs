using Dalamud.Game.Config;
using ECommons.DalamudServices;
using YABOT.FeaturesSetup;

namespace YABOT.Features.Other
{
    public class AutoFaceTargetDiadem : BaseFeature
    {
        public override string Name => "Auto Face Target in Diadem";

        public override string Description => "Automatically enables 'Auto Face Target' when entering The Diadem and restores the previous setting when leaving.";

        public override FeatureType FeatureType => FeatureType.Other;

        private const ushort DiademTerritoryId = 939;

        private bool wasInDiadem;
        private bool previousValue;

        public override void Enable()
        {
            wasInDiadem = Svc.ClientState.TerritoryType == DiademTerritoryId;
            if (wasInDiadem)
                EnableAutoFaceTarget();

            Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
            base.Enable();
        }

        private void OnTerritoryChanged(uint territoryId)
        {
            if (territoryId == DiademTerritoryId)
            {
                EnableAutoFaceTarget();
                wasInDiadem = true;
            }
            else if (wasInDiadem)
            {
                RestoreAutoFaceTarget();
                wasInDiadem = false;
            }
        }

        private void EnableAutoFaceTarget()
        {
            if (Svc.GameConfig.TryGet(UiControlOption.AutoFaceTargetOnAction, out bool current))
            {
                previousValue = current;
                if (!current)
                {
                    Svc.GameConfig.Set(UiControlOption.AutoFaceTargetOnAction, 1u);
                    Svc.Chat.Print("[YABOT] Auto face target enabled for The Diadem.");
                }
            }
        }

        private void RestoreAutoFaceTarget()
        {
            if (!previousValue)
            {
                Svc.GameConfig.Set(UiControlOption.AutoFaceTargetOnAction, 0u);
                Svc.Chat.Print("[YABOT] Auto face target restored to previous setting.");
            }
        }

        public override void Disable()
        {
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;

            if (wasInDiadem)
                RestoreAutoFaceTarget();

            base.Disable();
        }
    }
}
