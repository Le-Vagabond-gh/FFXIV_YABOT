using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System;

namespace YABOT.Features.DeepDungeons
{
    public unsafe class HideDeepDungeonStatus : BaseFeature
    {
        public override string Name => "Hide Deep Dungeon Status Panel";

        public override string Description =>
            "Entering a deep dungeon (Palace of the Dead, Heaven-on-High, Eureka Orthos) automatically pops open the Deep Dungeon Status panel (aetherpool level plus held pomanders/magicite). This closes it once on entry - handy when the Pomander List overlay already shows that information. The panel is hidden through its own agent, exactly like clicking the X, so you can reopen it any time with the character-panel shortcut (C by default).";

        public override FeatureType FeatureType => FeatureType.DeepDungeons;

        // The panel only auto-pops once, when you first enter the dungeon. Floor descents reload the
        // zone (firing TerritoryChanged + recreating the addon) but stay within Deep_Dungeon-use
        // territories and do NOT auto-pop, so a territory-scoped gate would wrongly hide the first
        // manual open on each floor. Scope it to the duty instead: re-arm only when we land back in a
        // non-deep-dungeon zone (i.e. we've left), so we hide the entry popup once and never touch a
        // manual reopen on any later floor.
        private bool _hiddenThisDuty;

        public override void Enable()
        {
            Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "DeepDungeonStatus", OnStatusSetup);
            base.Enable();
        }

        public override void Disable()
        {
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
            Svc.AddonLifecycle.UnregisterListener(OnStatusSetup);
            base.Disable();
        }

        private void OnTerritoryChanged(uint territory)
        {
            if (!ZoneHelper.IsDeepDungeon(territory))
                _hiddenThisDuty = false;
        }

        private void OnStatusSetup(AddonEvent type, AddonArgs args)
        {
            if (_hiddenThisDuty) return;
            _hiddenThisDuty = true;

            // Defer a frame so the game's own show flow finishes first - hiding mid-show can leave
            // the agent re-activated and desynced (which is exactly what breaks reopening). On zone-in
            // the loading fade covers the single frame the panel is visible.
            TaskManager.Enqueue(HideViaAgent);
        }

        // Hide through the agent rather than addon->Close so the agent's open-state stays consistent;
        // the character-panel shortcut toggles the agent, so a desynced state is what prevents reopening.
        private void HideViaAgent()
        {
            try
            {
                var agentModule = AgentModule.Instance();
                if (agentModule == null) return;

                var agent = agentModule->GetAgentByInternalId(AgentId.DeepDungeonStatus);
                if (agent != null && agent->IsAgentActive())
                    agent->Hide();
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] failed to hide DeepDungeonStatus");
            }
        }
    }
}
