using System.Linq;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class CommandPanelAdjustments : BaseFeature
    {
        public override string Name => "Command Panel Adjustments";

        public override string Description =>
            "Cleans up the visual noise on the Command Panel (the small hotbar window opened via /commandpanel): hide the slot highlights, focus border, and panel background.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Hide cursor highlight on hovered slot")]
            public bool HideHighlighting = true;

            [FeatureConfigOption("Hide focus border around the panel")]
            public bool HideFocusBorder = true;

            [FeatureConfigOption("Hide panel background")]
            public bool HidePanelBackground = false;

            [FeatureConfigOption("Hide frames on empty slots")]
            public bool HideEmptySlots = false;

            [FeatureConfigOption("Move close and settings buttons over the slot area")]
            public bool MoveButtons = false;
        }

        public Configs Config { get; private set; } = null!;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "QuickPanel", OnQuickPanelUpdate);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "QuickPanel", OnQuickPanelUpdate);
            base.Enable();
        }

        public override void Disable()
        {
            // Revert mods to the addon while it's still alive.
            var existing = (AtkUnitBase*)Svc.GameGui.GetAddonByName("QuickPanel").Address;
            if (existing != null) ApplyAdjustments(existing, restoreDefaults: true);

            Svc.AddonLifecycle.UnregisterListener(OnQuickPanelUpdate);
            SaveConfig(Config);
            base.Disable();
        }

        private void OnQuickPanelUpdate(AddonEvent type, AddonArgs args)
        {
            try
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (addon == null) return;
                ApplyAdjustments(addon, restoreDefaults: false);
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "CommandPanelAdjustments");
            }
        }

        private void ApplyAdjustments(AtkUnitBase* addon, bool restoreDefaults)
        {
            var windowComponent = (AtkComponentWindow*)addon->GetComponentByNodeId(45);
            if (windowComponent == null) return;

            var highlightNode = windowComponent->GetNodeById(10);
            if (highlightNode != null)
                highlightNode->ToggleVisibility(restoreDefaults || !Config.HideHighlighting);

            // Always toggle the focus border visibility - when the addon isn't focused the game
            // already keeps it hidden, so this only has an effect when it was about to show.
            var focusBorderNode = windowComponent->GetNodeById(8);
            if (focusBorderNode != null && !restoreDefaults && Config.HideFocusBorder)
                focusBorderNode->ToggleVisibility(false);

            var backgroundNode = windowComponent->GetNodeById(9);
            if (backgroundNode != null)
                backgroundNode->ToggleVisibility(restoreDefaults || !Config.HidePanelBackground);

            var panelBackgroundNode = addon->GetNodeById(44);
            if (panelBackgroundNode != null)
                panelBackgroundNode->ToggleVisibility(restoreDefaults || !Config.HidePanelBackground);

            foreach (uint index in Enumerable.Range(19, 25))
            {
                var componentDragDrop = (AtkComponentDragDrop*)addon->GetComponentByNodeId(index);
                if (componentDragDrop == null) continue;

                var iconComponentNode = componentDragDrop->GetNodeById(2);
                if (iconComponentNode == null) continue;

                var slotFrameNode = componentDragDrop->GetNodeById(3);
                if (slotFrameNode == null) continue;

                var visible = restoreDefaults
                    ? !iconComponentNode->IsVisible()
                    : !Config.HideEmptySlots && !iconComponentNode->IsVisible();
                slotFrameNode->ToggleVisibility(visible);
            }

            var closeButtonNode = windowComponent->GetNodeById(6);
            var settingsButtonNode = addon->GetNodeById(2);
            if (closeButtonNode != null && settingsButtonNode != null)
            {
                if (!restoreDefaults && Config.MoveButtons)
                {
                    closeButtonNode->SetPositionFloat(234f, 37f);
                    settingsButtonNode->SetPositionFloat(206f, 32f);
                }
                else
                {
                    closeButtonNode->SetPositionFloat(258f, 10f);
                    settingsButtonNode->SetPositionFloat(232f, 6f);
                }
            }
        }
    }
}
