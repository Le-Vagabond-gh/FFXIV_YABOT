using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using YABOT.FeaturesSetup;
using System;

namespace YABOT.Features.Actions
{
    public sealed unsafe class AutoPoseOnDraw : BaseFeature
    {
        public override string Name => "Auto-Pose After Drawing Weapon";

        public override string Description =>
            "When you draw your weapon outside of combat, waits a configurable delay and then runs /cpose " +
            "to switch to the alternate battle stance. Cancelled if you sheathe or enter combat before it fires.";

        public override FeatureType FeatureType => FeatureType.Actions;

        public class Configs : FeatureConfig
        {
            public float DelaySeconds = 2.0f;
        }

        public Configs Config { get; private set; } = null!;

        // Null until the first framework tick after enabling, so enabling the feature with the
        // weapon already drawn doesn't count as a draw.
        private bool? lastDrawn;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            lastDrawn = null;
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= OnFrameworkUpdate;
            base.Disable();
        }

        private static bool IsWeaponDrawn()
        {
            var chara = (Character*)(Svc.Objects.LocalPlayer?.Address ?? 0);
            return chara != null && chara->IsWeaponDrawn;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                var drawn = IsWeaponDrawn();
                if (lastDrawn is null || drawn == lastDrawn)
                {
                    lastDrawn = drawn;
                    return;
                }
                lastDrawn = drawn;

                if (!drawn)
                {
                    // Sheathed again - drop any pending pose change.
                    TaskManager.Abort();
                    return;
                }

                if (Svc.Condition[ConditionFlag.InCombat]) return;

                TaskManager.Abort();
                TaskManager.EnqueueDelay((int)(Config.DelaySeconds * 1000));
                TaskManager.Enqueue(() =>
                {
                    // Combat may have started (or the weapon been sheathed) during the delay.
                    if (!IsWeaponDrawn() || Svc.Condition[ConditionFlag.InCombat]) return;
                    Chat.SendMessage("/cpose");
                });
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[YABOT][AutoPoseOnDraw] Error in framework update");
            }
        }

        public override bool UseAutoConfig => false;

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            ImGui.PushItemWidth(300);
            if (ImGui.SliderFloat("Delay before /cpose (seconds)", ref Config.DelaySeconds, 0.5f, 10f, "%.1f"))
                hasChanged = true;
        };
    }
}
