using System.Collections.Generic;
using Dalamud.Game.Gui.NamePlate;
using YABOT.FeaturesSetup;

namespace YABOT.Features.Other
{
    public class HideDeadEnemyNamePlates : BaseFeature
    {
        public override string Name => "Hide Dead Enemy Nameplates";

        public override string Description =>
            "Hides nameplates and markers on enemy NPCs that have already been killed, reducing visual clutter after pulls.";

        public override FeatureType FeatureType => FeatureType.Other;

        public override void Enable()
        {
            Service.NamePlateGui.OnDataUpdate += OnNamePlateUpdate;
            Service.NamePlateGui.RequestRedraw();
            base.Enable();
        }

        public override void Disable()
        {
            Service.NamePlateGui.OnDataUpdate -= OnNamePlateUpdate;
            Service.NamePlateGui.RequestRedraw();
            base.Disable();
        }

        private static void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
        {
            foreach (var handler in handlers)
            {
                if (handler.NamePlateKind != NamePlateKind.BattleNpcEnemy) continue;
                if (handler.GameObject is not { IsDead: true }) continue;

                handler.VisibilityFlags = 0;
                handler.MarkerIconId = 0;
            }
        }
    }
}
