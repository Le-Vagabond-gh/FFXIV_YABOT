using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class HideDutyActionPanelBackground : BaseFeature
    {
        public override string Name => "Hide Duty Action Panel Background";

        public override string Description =>
            "Hides the brown wooden frame behind the Duty Action panel (the _ActionContents bar), " +
            "so its slots float over the game instead of sitting in an opaque panel. The action slots " +
            "themselves are untouched, and the frame is restored when the feature is disabled.";

        public override FeatureType FeatureType => FeatureType.UI;

        private const string AddonName = "_ActionContents";

        public override void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonUpdate);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonUpdate);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, AddonName, OnAddonUpdate);

            // Bar may already be open when the feature is toggled on.
            var existing = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
            if (existing != null) SetBackgroundVisible(existing, false);

            base.Enable();
        }

        public override void Disable()
        {
            Svc.AddonLifecycle.UnregisterListener(OnAddonUpdate);

            // Restore the frame while the addon is still alive.
            var existing = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
            if (existing != null) SetBackgroundVisible(existing, true);

            base.Disable();
        }

        private void OnAddonUpdate(AddonEvent type, AddonArgs args)
        {
            try
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (addon == null) return;
                SetBackgroundVisible(addon, false);
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, Name);
            }
        }

        // The brown frame is rendered by the addon's NineGrid background node(s); the action icons are
        // component/image nodes, so toggling the top-level NineGrid nodes hides only the frame. We only
        // touch the addon's direct children to avoid hitting any per-slot frames nested in components.
        private static void SetBackgroundVisible(AtkUnitBase* addon, bool visible)
        {
            var nodeList = addon->UldManager.NodeList;
            if (nodeList == null) return;

            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = nodeList[i];
                if (node == null) continue;
                if (node->Type == NodeType.NineGrid)
                    node->ToggleVisibility(visible);
            }
        }
    }
}
