using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class AutoFillHalfStackSplit : BaseFeature
    {
        public override string Name => "Auto-Fill Half Stack on Split";

        public override string Description => "When the 'Remove how many from stack?' dialog opens, pre-fills the count with half the stack, rounded up.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "InputNumeric", OnInputNumericSetup);
            base.Enable();
        }

        private void OnInputNumericSetup(AddonEvent type, AddonArgs args)
        {
            TaskManager.EnqueueDelay(100);
            TaskManager.Enqueue(() => SetHalfValue());
        }

        private void SetHalfValue()
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("InputNumeric").Address;
                if (addon == null || !addon->IsVisible)
                    return;

                if (!HasRemoveStackPrompt(addon))
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
                    if (max > 1)
                        numericInput->SetValue((max + 1) / 2);
                    break;
                }
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "AutoFillHalfStackSplit");
            }
        }

        private static bool HasRemoveStackPrompt(AtkUnitBase* addon)
        {
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null || node->Type != NodeType.Text)
                    continue;

                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (text.Contains("Remove how many from stack", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public override void Disable()
        {
            TaskManager.Abort();
            Svc.AddonLifecycle.UnregisterListener(OnInputNumericSetup);
            base.Disable();
        }
    }
}
