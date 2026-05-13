using System.Globalization;
using System.Text;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class ResourceBarPercentages : Feature
    {
        public override string Name => "Resource Bars as Percentages";

        public override string Description =>
            "Replaces the raw HP/MP/GP/CP numbers on the player's parameter widget (top-right) with percentages. Maxed-out values stay readable; the percentage sign and decimal precision are configurable.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Show HP as percentage")]
            public bool HpEnabled = true;

            [FeatureConfigOption("Show MP as percentage")]
            public bool MpEnabled = true;

            [FeatureConfigOption("Show GP as percentage (gatherer)")]
            public bool GpEnabled = true;

            [FeatureConfigOption("Show CP as percentage (crafter)")]
            public bool CpEnabled = true;

            [FeatureConfigOption("Append \"%\" sign")]
            public bool PercentageSignEnabled = true;

            [FeatureConfigOption("Decimal places", IntMin = 0, IntMax = 2, EditorSize = 150)]
            public int DecimalPlaces = 0;

            [FeatureConfigOption("Only show decimals below 100%")]
            public bool ShowDecimalsBelowHundredOnly = false;

            [FeatureConfigOption("Show raw value on hover")]
            public bool ShowRawValueTooltip = true;
        }

        public Configs Config { get; private set; } = null!;

        private IAddonEventHandle? hpOverHandle;
        private IAddonEventHandle? hpOutHandle;
        private IAddonEventHandle? mpOverHandle;
        private IAddonEventHandle? mpOutHandle;
        private bool tooltipActive;
        private ushort tooltipAddonId;
        private nint hpNodeAddr;
        private nint mpNodeAddr;
        private NodeFlags hpOriginalFlags;
        private NodeFlags mpOriginalFlags;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_ParameterWidget", OnParameterDraw);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_ParameterWidget", OnParameterDraw);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_ParameterWidget", OnParameterSetup);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_ParameterWidget", OnParameterFinalize);

            // Widget is already open at login - attach now too.
            var existing = (AddonParameterWidget*)Svc.GameGui.GetAddonByName("_ParameterWidget").Address;
            if (existing != null) RegisterHoverEvents(existing);

            base.Enable();
        }

        public override void Disable()
        {
            Svc.AddonLifecycle.UnregisterListener(OnParameterDraw);
            Svc.AddonLifecycle.UnregisterListener(OnParameterSetup);
            Svc.AddonLifecycle.UnregisterListener(OnParameterFinalize);

            UnregisterHoverEvents();
            HideTooltip();

            // Restore raw numbers on the addon while it's still around.
            RestoreNativeText();

            SaveConfig(Config);
            base.Disable();
        }

        private void OnParameterDraw(AddonEvent type, AddonArgs args)
        {
            try
            {
                var addon = (AddonParameterWidget*)args.Addon.Address;
                if (addon == null) return;
                if (Svc.Objects.LocalPlayer is not { } player) return;

                addon->HealthAmount->SetText(FormatHp(player.CurrentHp, player.MaxHp, Config.HpEnabled));

                var active = GetActiveResource(player);
                addon->ManaAmount->SetText(Format(active.Current, active.Max, active.Enabled));
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, "ResourceBarPercentages");
            }
        }

        private void OnParameterSetup(AddonEvent type, AddonArgs args)
            => RegisterHoverEvents((AddonParameterWidget*)args.Addon.Address);

        private void OnParameterFinalize(AddonEvent type, AddonArgs args)
        {
            UnregisterHoverEvents();
            HideTooltip();
        }

        private void RegisterHoverEvents(AddonParameterWidget* addon)
        {
            UnregisterHoverEvents();
            if (addon == null) return;

            var atkAddon = (AtkUnitBase*)addon;
            var hpNode = (AtkResNode*)(addon->HealthGaugeBar != null ? addon->HealthGaugeBar->OwnerNode : null);
            var mpNode = (AtkResNode*)(addon->ManaGaugeBar != null ? addon->ManaGaugeBar->OwnerNode : null);

            var collisionDirty = false;

            if (hpNode != null)
            {
                hpNodeAddr = (nint)hpNode;
                hpOriginalFlags = hpNode->NodeFlags;
                hpNode->NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;
                hpOverHandle = Svc.AddonEventManager.AddEvent((nint)atkAddon, hpNodeAddr, AddonEventType.MouseOver, OnHpHover);
                hpOutHandle = Svc.AddonEventManager.AddEvent((nint)atkAddon, hpNodeAddr, AddonEventType.MouseOut, OnHoverOut);
                collisionDirty = true;
            }
            if (mpNode != null)
            {
                mpNodeAddr = (nint)mpNode;
                mpOriginalFlags = mpNode->NodeFlags;
                mpNode->NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;
                mpOverHandle = Svc.AddonEventManager.AddEvent((nint)atkAddon, mpNodeAddr, AddonEventType.MouseOver, OnMpHover);
                mpOutHandle = Svc.AddonEventManager.AddEvent((nint)atkAddon, mpNodeAddr, AddonEventType.MouseOut, OnHoverOut);
                collisionDirty = true;
            }

            if (collisionDirty) atkAddon->UpdateCollisionNodeList(false);
        }

        private void UnregisterHoverEvents()
        {
            if (hpOverHandle != null) { Svc.AddonEventManager.RemoveEvent(hpOverHandle); hpOverHandle = null; }
            if (hpOutHandle != null) { Svc.AddonEventManager.RemoveEvent(hpOutHandle); hpOutHandle = null; }
            if (mpOverHandle != null) { Svc.AddonEventManager.RemoveEvent(mpOverHandle); mpOverHandle = null; }
            if (mpOutHandle != null) { Svc.AddonEventManager.RemoveEvent(mpOutHandle); mpOutHandle = null; }

            // Restore the original node flags so we don't leave the widget interactive.
            if (hpNodeAddr != 0)
            {
                var hpNode = (AtkResNode*)hpNodeAddr;
                hpNode->NodeFlags = hpOriginalFlags;
                hpNodeAddr = 0;
            }
            if (mpNodeAddr != 0)
            {
                var mpNode = (AtkResNode*)mpNodeAddr;
                mpNode->NodeFlags = mpOriginalFlags;
                mpNodeAddr = 0;
            }
        }

        private void OnHpHover(AddonEventType _, AddonEventData data)
        {
            if (!Config.ShowRawValueTooltip || !Config.HpEnabled) return;
            if (Svc.Objects.LocalPlayer is not { } player) return;
            ShowTooltip(data.AddonPointer, data.NodeTargetPointer, $"{player.CurrentHp:N0} / {player.MaxHp:N0}");
        }

        private void OnMpHover(AddonEventType _, AddonEventData data)
        {
            if (!Config.ShowRawValueTooltip) return;
            if (Svc.Objects.LocalPlayer is not { } player) return;
            var active = GetActiveResource(player);
            if (!active.Enabled) return;
            ShowTooltip(data.AddonPointer, data.NodeTargetPointer, $"{active.Current:N0} / {active.Max:N0}");
        }

        private void OnHoverOut(AddonEventType _, AddonEventData data)
            => HideTooltip();

        private void ShowTooltip(nint addonPtr, nint nodePtr, string text)
        {
            var addon = (AtkUnitBase*)addonPtr;
            var node = (AtkResNode*)nodePtr;
            if (addon == null || node == null) return;

            var byteCount = Encoding.UTF8.GetByteCount(text);
            var buffer = stackalloc byte[byteCount + 1];
            var span = new System.Span<byte>(buffer, byteCount + 1);
            Encoding.UTF8.GetBytes(text, span);
            span[byteCount] = 0;

            AtkStage.Instance()->TooltipManager.ShowTooltip(addon->Id, node, buffer);
            tooltipAddonId = addon->Id;
            tooltipActive = true;
        }

        private void HideTooltip()
        {
            if (!tooltipActive) return;
            AtkStage.Instance()->TooltipManager.HideTooltip(tooltipAddonId);
            tooltipActive = false;
            tooltipAddonId = 0;
        }

        private void RestoreNativeText()
        {
            var addon = (AddonParameterWidget*)Svc.GameGui.GetAddonByName("_ParameterWidget").Address;
            if (addon == null) return;
            if (Svc.Objects.LocalPlayer is not { } player) return;

            addon->HealthAmount->SetText(player.CurrentHp.ToString());
            var active = GetActiveResource(player);
            addon->ManaAmount->SetText(active.Current.ToString());
        }

        private record struct ActiveResource(uint Current, uint Max, bool Enabled);

        private ActiveResource GetActiveResource(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
        {
            if (player.MaxMp > 0) return new ActiveResource(player.CurrentMp, player.MaxMp, Config.MpEnabled);
            if (player.MaxGp > 0) return new ActiveResource(player.CurrentGp, player.MaxGp, Config.GpEnabled);
            if (player.MaxCp > 0) return new ActiveResource(player.CurrentCp, player.MaxCp, Config.CpEnabled);
            return new ActiveResource(0, 0, false);
        }

        // Some fights (e.g. Construct 7) report a tiny MaxHp - keep the raw value in that case
        // so the percentage doesn't look broken.
        private string FormatHp(uint current, uint max, bool enabled)
            => Format(current, max, enabled && max >= 50);

        private string Format(uint current, uint max, bool enabled)
        {
            if (!enabled) return current.ToString();
            if (max == 0) return "0" + (Config.PercentageSignEnabled ? "%" : "");

            var percentage = current / (float)max * 100f;
            var sign = Config.PercentageSignEnabled ? "%" : "";
            var format = Config.ShowDecimalsBelowHundredOnly && percentage >= 100f ? "F0" : $"F{Config.DecimalPlaces}";
            return percentage.ToString(format, CultureInfo.InvariantCulture) + sign;
        }
    }
}
