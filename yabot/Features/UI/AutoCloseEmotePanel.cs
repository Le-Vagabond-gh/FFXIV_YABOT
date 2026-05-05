using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using YABOT.UI;
using System;
using System.Numerics;

namespace YABOT.Features.UI
{
    public unsafe class AutoCloseEmotePanel : Feature
    {
        public override string Name => "Auto-Close Emote Panel";

        public override string Description => "Automatically closes the emote panel after selecting an emote. A checkbox is shown inside the emote panel to toggle this on or off.";

        public override FeatureType FeatureType => FeatureType.UI;

        internal Overlays OverlayWindow = null!;

        private bool AutoCloseEnabled = true;

        public override void Enable()
        {
            OverlayWindow = new(this);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "Emote", OnEmoteEvent);
            base.Enable();
        }

        private void OnEmoteEvent(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (!AutoCloseEnabled) return;

                if (args is AddonReceiveEventArgs a && (AtkEventType)a.AtkEventType is AtkEventType.ListItemClick)
                {
                    TaskManager.EnqueueDelay(100);
                    TaskManager.Enqueue(() => CloseEmotePanel());
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "AutoCloseEmotePanel");
            }
        }

        private void CloseEmotePanel()
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("Emote").Address;
                if (addon != null && addon->IsVisible)
                    addon->Close(true);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "AutoCloseEmotePanel");
            }
        }

        public override bool DrawConditions()
        {
            return Svc.GameGui.GetAddonByName("Emote") != IntPtr.Zero;
        }

        public override void Draw()
        {
            try
            {
                var addonPtr = Svc.GameGui.GetAddonByName("Emote");
                if (addonPtr == IntPtr.Zero) return;

                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon == null || !addon->IsVisible || !addon->IsFullyLoaded()) return;

                var rootNode = addon->RootNode;
                if (rootNode == null) return;

                var position = AtkResNodeHelper.GetNodePosition(rootNode);
                var scale = AtkResNodeHelper.GetNodeScale(rootNode);
                var addonSize = new Vector2(rootNode->Width, rootNode->Height) * scale;

                var checkboxPos = new Vector2(position.X + 14 * scale.X, position.Y + addonSize.Y - 38 * scale.Y);

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(checkboxPos);

                ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
                var oldSize = ImGui.GetFont().Scale;
                ImGui.GetFont().Scale *= scale.X * 0.75f;
                ImGui.PushFont(ImGui.GetFont());
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
                ImGui.Begin("###AutoCloseEmoteCheckbox", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNavFocus
                    | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);

                if (ImGui.Checkbox("Auto-close", ref AutoCloseEnabled))
                {
                }

                ImGui.End();
                ImGui.PopStyleVar(2);
                ImGui.GetFont().Scale = oldSize;
                ImGui.PopFont();
                ImGui.PopStyleColor();
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "AutoCloseEmotePanel.Draw");
            }
        }

        public override void Disable()
        {
            TaskManager.Abort();
            Svc.AddonLifecycle.UnregisterListener(OnEmoteEvent);
            P.Ws.RemoveWindow(OverlayWindow);
            OverlayWindow = null!;
            base.Disable();
        }
    }
}
