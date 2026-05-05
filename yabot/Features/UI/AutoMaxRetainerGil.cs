using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class AutoMaxRetainerGil : Feature
    {
        public override string Name => "Auto-Fill Max Retainer Gil";

        public override string Description => "Automatically fills the gil transfer field with the maximum amount when withdrawing gil from a retainer.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Bank", OnBankSetup);
            base.Enable();
        }

        private void OnBankSetup(AddonEvent type, AddonArgs args)
        {
            TaskManager.EnqueueDelay(100);
            TaskManager.Enqueue(() => SetMaxValue());
        }

        private void SetMaxValue()
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("Bank").Address;
                if (addon == null || !addon->IsVisible)
                    return;

                for (var i = 0; i < addon->UldManager.NodeListCount; i++)
                {
                    var node = addon->UldManager.NodeList[i];
                    if (node == null || node->Type < (NodeType)1000)
                        continue;

                    var componentNode = (AtkComponentNode*)node;
                    if (componentNode->Component->GetComponentType() != ComponentType.NumericInput)
                        continue;

                    var numericInput = (AtkComponentNumericInput*)componentNode->Component;
                    var max = numericInput->Data.Max;
                    if (max > 0)
                        numericInput->SetValue(max);
                    break;
                }
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "AutoMaxRetainerGil");
            }
        }

        public override void Disable()
        {
            TaskManager.Abort();
            Svc.AddonLifecycle.UnregisterListener(OnBankSetup);
            base.Disable();
        }
    }
}
