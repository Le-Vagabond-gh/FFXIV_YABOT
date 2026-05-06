using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using YABOT.UI;
using System;
using System.Numerics;

namespace YABOT.Features.UI
{
    public unsafe class ArmourySortButton : Feature
    {
        public override string Name => "Armoury Chest Sort Button";

        public override string Description =>
            "Adds a dice icon button to the bottom-left of the armoury chest panel. " +
            "Clicking it runs a preset /isort sequence that sorts the armoury, main hand and off hand by category and stats.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Run on job change")]
            public bool RunOnJobChange = false;
        }

        public Configs Config { get; private set; } = null!;

        internal Overlays OverlayWindow = null!;

        private const int StepDelayMs = 300;
        private const int ExecuteDelayMs = 1500;
        private const int JobChangeInitialDelayMs = 800;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            OverlayWindow = new(this);
            Svc.ClientState.ClassJobChanged += OnClassJobChanged;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.ClientState.ClassJobChanged -= OnClassJobChanged;
            TaskManager.Abort();
            if (OverlayWindow != null)
            {
                P.Ws.RemoveWindow(OverlayWindow);
                OverlayWindow = null!;
            }
            base.Disable();
        }

        private void OnClassJobChanged(uint classJobId)
        {
            if (!Config.RunOnJobChange) return;

            TaskManager.Abort();
            TaskManager.EnqueueDelay(JobChangeInitialDelayMs);
            QueueSortSequence();
        }

        public override bool DrawConditions()
        {
            return Svc.GameGui.GetAddonByName("ArmouryBoard") != IntPtr.Zero;
        }

        public override void Draw()
        {
            try
            {
                var addonPtr = Svc.GameGui.GetAddonByName("ArmouryBoard");
                if (addonPtr == IntPtr.Zero) return;

                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon == null || !addon->IsVisible || !addon->IsFullyLoaded()) return;

                var rootNode = addon->RootNode;
                if (rootNode == null) return;

                var position = AtkResNodeHelper.GetNodePosition(rootNode);
                var scale = AtkResNodeHelper.GetNodeScale(rootNode);
                var addonSize = new Vector2(rootNode->Width, rootNode->Height) * scale;

                var buttonPos = new Vector2(position.X + 19 * scale.X, position.Y + addonSize.Y - 48 * scale.Y);

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(buttonPos);

                ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
                var oldFontScale = ImGui.GetFont().Scale;
                ImGui.GetFont().Scale *= scale.X * 0.85f;
                ImGui.PushFont(ImGui.GetFont());
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
                ImGui.Begin("###YABOT_ArmourySortButton", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNavFocus
                    | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);

                if (ImGuiComponents.IconButton(FontAwesomeIcon.Dice))
                {
                    TaskManager.Abort();
                    QueueSortSequence();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Sort armoury, main hand and off hand (/isort)");

                ImGui.End();
                ImGui.PopStyleVar(2);
                ImGui.GetFont().Scale = oldFontScale;
                ImGui.PopFont();
                ImGui.PopStyleColor();
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "ArmourySortButton.Draw");
            }
        }

        private void QueueSortSequence()
        {
            EnqueueCmd("/isort condition armoury category");
            EnqueueCmd("/isort condition armoury lv asc");
            EnqueueCmd("/isort condition armoury ilv asc");
            EnqueueCmd("/isort condition armoury perception asc");
            EnqueueCmd("/isort condition armoury gathering asc");
            EnqueueCmd("/isort condition armoury control asc");
            EnqueueCmd("/isort condition armoury craftsmanship asc");
            EnqueueCmd("/isort execute armoury");
            TaskManager.EnqueueDelay(ExecuteDelayMs);

            EnqueueCmd("/isort condition mh category asc");
            EnqueueCmd("/isort execute mh");
            TaskManager.EnqueueDelay(ExecuteDelayMs);

            EnqueueCmd("/isort condition oh category asc");
            EnqueueCmd("/isort execute oh");
        }

        private void EnqueueCmd(string cmd)
        {
            TaskManager.Enqueue(() =>
            {
                try
                {
                    Chat.SendMessage(cmd);
                }
                catch (Exception e)
                {
                    Svc.Log.Error(e, $"ArmourySortButton: failed to send '{cmd}'");
                }
            });
            TaskManager.EnqueueDelay(StepDelayMs);
        }
    }
}
