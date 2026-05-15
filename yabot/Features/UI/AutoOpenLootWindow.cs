using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class AutoOpenLootWindow : BaseFeature
    {
        public override string Name => "Auto Open Loot Window";

        public override string Description => "Opens the Need/Greed window automatically when items are added to be rolled on.";

        public override FeatureType FeatureType => FeatureType.UI;

        private int lastNumItems;
        private byte cutsceneThrottle;
        private bool pendingOpen;

        public override void Enable()
        {
            lastNumItems = 0;
            cutsceneThrottle = 0;
            pendingOpen = false;
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            base.Disable();
        }

        private void OnFrameworkUpdate(IFramework _)
        {
            try
            {
                var agentModule = AgentModule.Instance();
                if (agentModule == null) return;

                var agent = agentModule->GetAgentByInternalId(AgentId.Loot);
                if (agent == null) return;

                var loot = (AgentLoot*)agent;
                var current = loot->NumItems;

                if (current > lastNumItems)
                {
                    pendingOpen = true;
                    cutsceneThrottle = 0;
                }
                else if (current == 0)
                {
                    pendingOpen = false;
                    cutsceneThrottle = 0;
                }

                lastNumItems = current;

                if (!pendingOpen) return;

                if (IsInCutscene())
                {
                    cutsceneThrottle = 0;
                    return;
                }

                if (++cutsceneThrottle <= 10) return;

                pendingOpen = false;
                cutsceneThrottle = 0;
                TryOpenWindow(agent, loot);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "AutoOpenLootWindow.OnFrameworkUpdate");
                pendingOpen = false;
            }
        }

        private void TryOpenWindow(AgentInterface* agent, AgentLoot* loot)
        {
            var existing = (AtkUnitBase*)Svc.GameGui.GetAddonByName("NeedGreed").Address;
            if (existing != null && existing->IsVisible) return;
            if (loot->NumItems <= 0) return;

            agent->Show();
        }

        private static bool IsInCutscene()
            => Svc.Condition[ConditionFlag.WatchingCutscene]
            || Svc.Condition[ConditionFlag.WatchingCutscene78]
            || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent];
    }
}
