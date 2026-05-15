using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class SelectNextLootItem : BaseFeature
    {
        public override string Name => "Auto Select Next Loot Item";

        public override string Description =>
            "When the Need/Greed window opens, automatically highlights the first item you haven't rolled on. After you roll (Need/Greed/Pass), it advances to the next item.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "NeedGreed", OnNeedGreedSetup);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "NeedGreed", OnNeedGreedEvent);
            base.Enable();
        }

        public override void Disable()
        {
            Svc.AddonLifecycle.UnregisterListener(OnNeedGreedSetup);
            Svc.AddonLifecycle.UnregisterListener(OnNeedGreedEvent);
            base.Disable();
        }

        private static void OnNeedGreedSetup(AddonEvent type, AddonArgs args)
        {
            try
            {
                var addon = (AddonNeedGreed*)args.Addon.Address;
                if (addon == null) return;

                for (var i = 0; i < addon->NumItems; i++)
                {
                    ref var item = ref addon->Items[i];
                    if (item.Roll == 0 && item.ItemId != 0)
                    {
                        SelectItem(addon, i);
                        break;
                    }
                }
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "SelectNextLootItem: setup");
            }
        }

        private static void OnNeedGreedEvent(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (args is not AddonReceiveEventArgs eventArgs) return;

                if ((AtkEventType)eventArgs.AtkEventType != AtkEventType.ButtonClick) return;

                var addon = (AddonNeedGreed*)eventArgs.Addon.Address;
                if (addon == null) return;

                var buttonType = (ButtonType)eventArgs.EventParam;
                var selected = addon->SelectedItemIndex;
                if (selected < 0 || selected >= addon->NumItems) return;

                switch (buttonType)
                {
                    case ButtonType.Need:
                    case ButtonType.Greed:
                        break;
                    case ButtonType.Pass:
                        // Don't auto-advance if we're passing on something we already rolled on -
                        // user is intentionally going back through items.
                        ref var current = ref addon->Items[selected];
                        if (current.Roll != 0 || current.ItemId == 0) return;
                        break;
                    default:
                        return;
                }

                var next = selected + 1;
                if (next < addon->NumItems) SelectItem(addon, next);
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "SelectNextLootItem: event");
            }
        }

        private static void SelectItem(AddonNeedGreed* addon, int index)
        {
            var eventData = new AtkEventData();
            eventData.ListItemData.SelectedIndex = index;
            addon->ReceiveEvent(AtkEventType.ListItemClick, 0, null, &eventData);
        }

        // Other button types (Greed Only, Loot Recipient) intentionally fall through and are ignored.
        private enum ButtonType : uint
        {
            Need = 0,
            Greed = 1,
            Pass = 2,
        }
    }
}
