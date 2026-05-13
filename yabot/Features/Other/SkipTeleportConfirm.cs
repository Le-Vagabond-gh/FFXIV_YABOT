using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.Other
{
    public unsafe class SkipTeleportConfirm : Feature
    {
        public override string Name => "Skip Teleport Confirm";

        public override string Description =>
            "Automatically clicks Yes on the teleport confirmation prompt that opens when clicking an aetheryte icon on the world map.";

        public override FeatureType FeatureType => FeatureType.Other;

        // The teleport callback is registered with EventKind = 1 on the Map agent.
        private const ulong TeleportEventKind = 1;

        public override void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesNo);
            base.Enable();
        }

        public override void Disable()
        {
            Svc.AddonLifecycle.UnregisterListener(OnSelectYesNo);
            base.Disable();
        }

        private static void OnSelectYesNo(AddonEvent type, AddonArgs args)
        {
            try
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (addon == null) return;
                if (!IsTeleportPrompt(addon)) return;

                Callback.Fire(addon, true, 0);
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "SkipTeleportConfirm");
            }
        }

        // Mirrors VanillaPlus's AtkUnitBase.GetCallbackHandlerInfo() - the SelectYesno's
        // registered callback handler is recorded in RaptureAtkModule's AddonCallbackMapping.
        // For a teleport confirm, the registered handler is AgentMap with EventKind 1.
        private static bool IsTeleportPrompt(AtkUnitBase* addon)
        {
            var atkModule = RaptureAtkModule.Instance();
            if (atkModule == null) return false;

            if (!atkModule->AddonCallbackMapping.TryGetValue(addon->Id, out var entry, false)) return false;
            if (entry.AgentInterface == null) return false;
            if (entry.EventKind != TeleportEventKind) return false;

            var mapAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Map);
            return entry.AgentInterface == mapAgent;
        }
    }
}
