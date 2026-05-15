using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class AutoConfirmRepair : BaseFeature
    {
        public override string Name => "Auto-Confirm Repair Complete";

        public override string Description => "Automatically closes the Repair Progress window after repairing all items at an NPC.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "RepairAuto", OnRepairAutoRefresh);
            base.Enable();
        }

        private void OnRepairAutoRefresh(AddonEvent type, AddonArgs args)
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("RepairAuto").Address;
                if (addon == null || !addon->IsVisible)
                    return;

                if (addon->AtkValuesCount == 0 || addon->AtkValues[0].UInt != 1)
                    return;

                TaskManager.EnqueueDelay(1000);
                TaskManager.Enqueue(() => CloseRepairWindows());
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "AutoConfirmRepair");
            }
        }

        private void CloseRepairWindows()
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("RepairAuto").Address;
            if (addon != null && addon->IsVisible)
            {
                var node = addon->GetNodeById(13);
                if (node != null)
                {
                    var evt = (AtkEvent*)node->AtkEventManager.Event;
                    if (evt != null)
                        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, node->AtkEventManager.Event);
                }
            }

            var repair = (AtkUnitBase*)Svc.GameGui.GetAddonByName("Repair").Address;
            if (repair != null && repair->IsVisible)
                repair->Close(true);
        }

        public override void Disable()
        {
            TaskManager.Abort();
            Svc.AddonLifecycle.UnregisterListener(OnRepairAutoRefresh);
            base.Disable();
        }
    }
}
