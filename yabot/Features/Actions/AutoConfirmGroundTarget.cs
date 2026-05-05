using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using YABOT.FeaturesSetup;
using System;
using Dalamud.Plugin.Services;

namespace YABOT.Features.Actions
{
    public unsafe class AutoConfirmGroundTarget : Feature
    {
        public override string Name => "Auto-Confirm Ground Targets";

        public override string Description => "Automatically confirms ground-targeted actions (like Sacred Soil, Asylum, etc.) at the cursor position after a brief delay. Waymarks are skipped.";

        public override FeatureType FeatureType => FeatureType.Actions;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Delay before confirming (seconds)", FloatMin = 0f, FloatMax = 2f, FloatIncrements = 0.05f, EditorSize = 300)]
            public float DelaySeconds = 0.25f;
        }

        public Configs Config { get; private set; } = null!;

        private DateTime _groundTargetStartTime = DateTime.MinValue;
        private uint _lastGroundTargetActionId = 0;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.Framework.Update += RunFeature;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Framework.Update -= RunFeature;
            SaveConfig(Config);
            base.Disable();
        }

        private void RunFeature(IFramework framework)
        {
            try
            {
                var actionManager = ActionManager.Instance();
                if (actionManager == null)
                    return;

                var currentActionId = actionManager->AreaTargetingActionId;

                if (actionManager->AreaTargetingActionType == ActionType.FieldMarker)
                    return;

                if (currentActionId != 0)
                {
                    if (currentActionId != _lastGroundTargetActionId)
                    {
                        _groundTargetStartTime = DateTime.Now;
                        _lastGroundTargetActionId = currentActionId;
                    }

                    if ((DateTime.Now - _groundTargetStartTime).TotalMilliseconds >= Config.DelaySeconds * 1000)
                    {
                        actionManager->AreaTargetingExecuteAtCursor = true;
                    }
                }
                else
                {
                    _lastGroundTargetActionId = 0;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"[AutoConfirmGroundTarget] Error: {ex.Message}");
            }
        }
    }
}
