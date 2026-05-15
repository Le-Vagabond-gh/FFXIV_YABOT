using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using System.Runtime.InteropServices;
using YABOT.FeaturesSetup;
using LSeStringBuilder = Lumina.Text.SeStringBuilder;

namespace YABOT.Features.UI
{
    public unsafe class ChatPlayerTooltip : BaseFeature
    {
        public override string Name => "Chat Player Tooltip";

        public override string Description =>
            "Hovering a player name in chat shows a tooltip with their name and home world. Useful for spotting visitors from other worlds/data centers without right-clicking.";

        public override FeatureType FeatureType => FeatureType.UI;

        private static readonly string[] AddonNames =
        {
            "ChatLogPanel_0",
            "ChatLogPanel_1",
            "ChatLogPanel_2",
            "ChatLogPanel_3",
        };

        private bool tooltipActive;
        private ushort activeTooltipAddonId;

        public override void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, AddonNames, OnPreReceiveEvent);
            base.Enable();
        }

        public override void Disable()
        {
            Svc.AddonLifecycle.UnregisterListener(OnPreReceiveEvent);
            HideTooltip();
            base.Disable();
        }

        private void OnPreReceiveEvent(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (args is not AddonReceiveEventArgs eventArgs) return;

                var eventType = (AtkEventType)eventArgs.AtkEventType;
                switch (eventType)
                {
                    case AtkEventType.LinkMouseOver:
                        HandleLinkMouseOver(eventArgs);
                        break;
                    case AtkEventType.LinkMouseOut:
                        HideTooltip();
                        break;
                }
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "ChatPlayerTooltip");
            }
        }

        private void HandleLinkMouseOver(AddonReceiveEventArgs eventArgs)
        {
            if (eventArgs.AtkEventData == nint.Zero) return;

            var eventData = (AtkEventData*)eventArgs.AtkEventData;
            var linkData = eventData->LinkData;
            if (linkData == null) return;

            if ((LinkMacroPayloadType)linkData->LinkType != LinkMacroPayloadType.Character) return;
            if (linkData->Payload == null) return;

            var payloadSpan = new ReadOnlySeStringSpan(linkData->Payload);
            var enumerator = payloadSpan.GetEnumerator();
            if (!enumerator.MoveNext()) return;
            var payload = enumerator.Current;

            if (!payload.TryGetExpression(out _, out _, out var worldExpression, out _, out var nameExpression)) return;
            if (!worldExpression.TryGetUInt(out var worldId) || worldId == 0) return;
            if (!nameExpression.TryGetString(out var playerName)) return;
            if (!Svc.Data.GetExcelSheet<World>().TryGetRow(worldId, out var worldData)) return;

            var addon = (AtkUnitBase*)eventArgs.Addon.Address;
            if (addon == null) return;

            var tooltipString = new LSeStringBuilder()
                .Append(playerName)
                .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                .Append(worldData.Name)
                .ToReadOnlySeString();

            ShowTooltip(addon->Id, tooltipString);
        }

        private void ShowTooltip(ushort addonId, ReadOnlySeString tooltipString)
        {
            // ShowTooltip wants a null-terminated CStringPointer. Copy the bytes into a
            // null-terminated stack buffer so the pointer survives the call.
            var span = tooltipString.Data.Span;
            var buffer = stackalloc byte[span.Length + 1];
            var dest = new System.Span<byte>(buffer, span.Length + 1);
            span.CopyTo(dest);
            dest[span.Length] = 0;

            AtkStage.Instance()->TooltipManager.ShowTooltip(addonId, null, buffer);
            activeTooltipAddonId = addonId;
            tooltipActive = true;
        }

        private void HideTooltip()
        {
            if (!tooltipActive) return;
            AtkStage.Instance()->TooltipManager.HideTooltip(activeTooltipAddonId);
            tooltipActive = false;
            activeTooltipAddonId = 0;
        }
    }
}
