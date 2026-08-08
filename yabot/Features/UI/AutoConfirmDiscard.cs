using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using YABOT.FeaturesSetup;
using System;

namespace YABOT.Features.UI
{
    // Shift-click Discard (or Lower Quality) in an item's right-click menu and the confirmation
    // prompt answers itself.
    //
    // Rather than trying to work out after the fact which SelectYesno belongs to a discard, this hooks
    // the two AgentInventoryContext calls the menu entries make. That reads Shift at the moment the
    // entry is actually clicked, and it can't confuse an unrelated prompt for a discard, because the
    // arming only happens inside those calls. The prompt opens on the same click, so a short window is
    // plenty - it exists only so a discard that somehow opens no prompt doesn't leave us armed.
    public unsafe class AutoConfirmDiscard : BaseFeature
    {
        public override string Name => "Auto-Confirm Discard";

        public override string Description =>
            "Hold Shift while clicking Discard or Lower Quality in an item's right-click menu and the " +
            "confirmation prompt is answered Yes for you. Without Shift the prompt behaves normally.";

        public override FeatureType FeatureType => FeatureType.UI;

        // How long after the click we'll accept a prompt as belonging to it.
        private const double ArmedWindowSeconds = 2;

        private Hook<AgentInventoryContext.Delegates.DiscardItem>? discardItemHook;
        private Hook<AgentInventoryContext.Delegates.LowerItemQuality>? lowerItemQualityHook;

        private DateTime? armedUntil;

        public override void Enable()
        {
            armedUntil = null;

            discardItemHook ??= Svc.Hook.HookFromAddress<AgentInventoryContext.Delegates.DiscardItem>(
                AgentInventoryContext.MemberFunctionPointers.DiscardItem, DiscardItemDetour);

            lowerItemQualityHook ??= Svc.Hook.HookFromAddress<AgentInventoryContext.Delegates.LowerItemQuality>(
                AgentInventoryContext.MemberFunctionPointers.LowerItemQuality, LowerItemQualityDetour);

            discardItemHook.Enable();
            lowerItemQualityHook.Enable();

            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesNo);
            base.Enable();
        }

        public override void Disable()
        {
            Svc.AddonLifecycle.UnregisterListener(OnSelectYesNo);
            discardItemHook?.Disable();
            lowerItemQualityHook?.Disable();
            armedUntil = null;
            base.Disable();
        }

        public override void Dispose()
        {
            discardItemHook?.Dispose();
            lowerItemQualityHook?.Dispose();
            discardItemHook = null;
            lowerItemQualityHook = null;
            base.Dispose();
        }

        private void DiscardItemDetour(AgentInventoryContext* agent, InventoryItem* itemSlot, InventoryType inventory, int slot, uint addonId, int position)
        {
            ArmIfShiftHeld("Discard");
            discardItemHook!.Original(agent, itemSlot, inventory, slot, addonId, position);
        }

        private void LowerItemQualityDetour(AgentInventoryContext* agent, InventoryItem* itemSlot, InventoryType inventory, int slot, uint addonId)
        {
            ArmIfShiftHeld("Lower Quality");
            lowerItemQualityHook!.Original(agent, itemSlot, inventory, slot, addonId);
        }

        private void ArmIfShiftHeld(string what)
        {
            try
            {
                if (!Svc.KeyState[VirtualKey.SHIFT]) return;
                armedUntil = DateTime.Now.AddSeconds(ArmedWindowSeconds);
                Log($"{what} clicked with Shift held, will confirm the next prompt");
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "AutoConfirmDiscard.ArmIfShiftHeld");
            }
        }

        private void OnSelectYesNo(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (armedUntil is not { } until) return;

                // Consume the arming either way - if this prompt isn't ours, the click that armed us
                // didn't produce one and we shouldn't confirm something later by mistake.
                armedUntil = null;
                if (DateTime.Now > until) return;

                // Yes() force-enables the button first, which a plain callback wouldn't - some of these
                // prompts hand back a disabled Yes for a moment.
                new AddonMaster.SelectYesno(args.Addon.Address).Yes();
                Log("confirmed discard prompt");
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "AutoConfirmDiscard.OnSelectYesNo");
            }
        }
    }
}
