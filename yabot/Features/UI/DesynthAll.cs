using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;
using Callback = ECommons.Automation.Callback;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using YABOT.UI;
using System;
using System.Collections.Generic;
using System.Numerics;
using static ECommons.GenericHelpers;
using Category = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentSalvage.SalvageItemCategory;

namespace YABOT.Features.UI
{
    // ported from PandorasBox's Desynth feature, minus its unused list item hook and the YesAlready lock
    public unsafe class DesynthAll : BaseFeature
    {
        public override string Name => "Desynth All";

        public override string Description =>
            "Adds buttons to the desynthesis window: one desynths everything in the selected category, " +
            "the other desynths everything in Inventory: Equipment then Inventory: Items & Housing. " +
            "The window closes when it's done. Nothing is filtered out. " +
            "(Disclaimer: YABOT takes no responsibility for the loss of any Ultimate weapons or other rare items. Please use responsibly.)";

        public override FeatureType FeatureType => FeatureType.UI;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Show \"Desynth All\" button (selected category)")]
            public bool ShowDesynthAll = true;

            [FeatureConfigOption("Show \"Desynth Inventory Equipment / Items + Housing\" button")]
            public bool ShowDesynthInventory = true;
        }

        public Configs Config { get; private set; } = null!;

        internal Overlays OverlayWindow = null!;

        private readonly Queue<Category> pending = new();

        private const string SelectorAddon = "SalvageItemSelector";

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            OverlayWindow = new(this);
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            TaskManager.Abort();
            pending.Clear();
            if (OverlayWindow != null)
            {
                P.Ws.RemoveWindow(OverlayWindow);
                OverlayWindow = null!;
            }
            base.Disable();
        }

        public override bool DrawConditions() => TryGetAddonByName<AddonSalvageItemSelector>(SelectorAddon, out _);

        public override void Draw()
        {
            try
            {
                if (!TryGetAddonByName<AddonSalvageItemSelector>(SelectorAddon, out var addon))
                    return;

                var node = addon->AtkUnitBase.GetNodeById(6);
                if (node == null)
                    return;

                var position = AtkResNodeHelper.GetNodePosition(node);
                var scale = AtkResNodeHelper.GetNodeScale(node);
                var size = new Vector2(node->Width, node->Height) * scale;

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(position + size with { Y = 0 } + new Vector2(12f, 0));

                ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
                var oldFontScale = ImGui.GetFont().Scale;
                ImGui.GetFont().Scale *= scale.X;
                ImGui.PushFont(ImGui.GetFont());
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f, 3f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("###YABOT_DesynthAll", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNavFocus
                    | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings);

                if (Svc.Condition[ConditionFlag.Mounted])
                {
                    ImGui.Text("You are mounted, please dismount");
                }
                else if (TaskManager.IsBusy)
                {
                    if (ImGui.Button("Desynthing. Click to Abort."))
                    {
                        TaskManager.Abort();
                        pending.Clear();
                    }
                }
                else
                {
                    if (Config.ShowDesynthAll && ImGui.Button("Desynth All"))
                        Start(AgentSalvage.Instance()->SelectedCategory);
                    if (Config.ShowDesynthInventory && ImGui.Button("Desynth Inventory Equipment / Items + Housing"))
                        Start(Category.InventoryEquipment, Category.InventoryHousing);
                }

                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.GetFont().Scale = oldFontScale;
                ImGui.PopFont();
                ImGui.PopStyleColor();
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "DesynthAll.Draw");
            }
        }

        private void Start(params Category[] categories)
        {
            pending.Clear();
            foreach (var c in categories)
                pending.Enqueue(c);
            TaskManager.Enqueue(Step, "Desynth step");
        }

        private void Step()
        {
            try
            {
                if (!TryGetAddonByName<AddonSalvageItemSelector>(SelectorAddon, out var addon))
                {
                    pending.Clear();
                    return;
                }

                if (pending.Count == 0)
                {
                    addon->AtkUnitBase.Close(true);
                    return;
                }

                // same category switch AutoDuty uses: write the agent's category and rebuild the list,
                // then give the addon a moment to pick up the new item count
                var agent = AgentSalvage.Instance();
                if (agent->SelectedCategory != pending.Peek())
                {
                    agent->SelectedCategory = pending.Peek();
                    agent->ItemListRefresh(true);
                    TaskManager.EnqueueDelay(500);
                }
                else if (agent->ItemCount > 0)
                {
                    TaskManager.Enqueue(DesynthFirst, "Desynth first item");
                    TaskManager.EnqueueWithTimeout(ConfirmDesynth, 2000, "Confirm desynth");
                    TaskManager.EnqueueWithTimeout(CloseResults, 9000, "Close results");
                    TaskManager.EnqueueDelay(500);
                }
                else
                {
                    pending.Dequeue();
                }

                TaskManager.Enqueue(Step, "Desynth step");
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "DesynthAll.Step");
                pending.Clear();
                TaskManager.Abort();
            }
        }

        private bool? DesynthFirst()
        {
            if (Svc.Condition[ConditionFlag.Occupied]) return false;
            if (!TryGetAddonByName<AtkUnitBase>(SelectorAddon, out var addon)) return null;
            // callback 12 desynths index N of the agent's list, so re-check the agent is still on the
            // category we're working through (dropdown can change between Step and here), abort otherwise
            var agent = AgentSalvage.Instance();
            if (pending.Count == 0 || agent->SelectedCategory != pending.Peek() || agent->ItemCount == 0)
            {
                pending.Clear();
                return null;
            }
            Callback.Fire(addon, false, 12, 0);
            return true;
        }

        private static bool? ConfirmDesynth()
        {
            if (Svc.Condition[ConditionFlag.Occupied]) return false;
            if (!TryGetAddonByName<AtkUnitBase>("SalvageDialog", out var addon) || !addon->IsVisible) return false;
            Callback.Fire(addon, false, 0, false);
            return Svc.Condition[ConditionFlag.Occupied39];
        }

        private static bool? CloseResults()
        {
            if (Svc.Condition[ConditionFlag.Occupied]) return false;
            if (!TryGetAddonByName<AtkUnitBase>("SalvageResult", out var addon) || !addon->IsVisible) return false;
            addon->Close(true);
            return true;
        }
    }
}
