using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using YABOT.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace YABOT.Features.CosmicExploration
{
    // Self-contained port of Ice's Cosmic Exploration collectable masterpiece rotation
    // (ICE/Scheduler/Tasks/Task_Gather.cs -> CollectableGather, and
    //  ICE/Utilities/GatheringHelper/GatheringUtil.cs action tables).
    // Only the rotation engine is ported - no mission detection, node pathing or turn-in -
    // so it works on any Cosmic gathering node, including planets ICE doesn't support yet.
    public unsafe class CosmicCollectableRotation : BaseFeature
    {
        public override string Name => "Cosmic Collectable Auto-Rotation";

        public override string Description =>
            "Adds a toggle button to the collectable gathering window (GatheringMasterpiece). " +
            "While enabled, it automatically plays the optimal collectable rotation " +
            "(Scrutiny / Meticulous / Brazen, integrity recovery, then Collect) - ported from " +
            "Ice's Cosmic Exploration, for Cosmic gathering nodes (including planets ICE doesn't support yet). " +
            "Drive to the node yourself and open it; this only handles the masterpiece minigame.";

        public override FeatureType FeatureType => FeatureType.CosmicExploration;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Start with auto-rotation ON when a collectable window opens")]
            public bool AutoRunByDefault = false;

            [FeatureConfigOption("Action delay (ms)", IntMin = 100, IntMax = 2000, EditorSize = 300)]
            public int ActionDelayMs = 300;
        }

        public Configs Config { get; private set; } = null!;

        internal Overlays OverlayWindow = null!;

        private bool autoRun;

        // Stuck-detector (ported from ICE): if collectability stalls, fall through to Collect.
        private int _lastCollectability = -1;
        private DateTime _lastCollectProgress = DateTime.MinValue;

        // Baseline duty-action charge count captured when a new collectable window opens.
        private uint _buffCount;
        private uint _trackedItemId;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            autoRun = Config.AutoRunByDefault;
            OverlayWindow = new(this);
            Svc.Framework.Update += RotationTick;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= RotationTick;
            TaskManager.Abort();
            if (OverlayWindow != null)
            {
                P.Ws.RemoveWindow(OverlayWindow);
                OverlayWindow = null!;
            }
            base.Disable();
        }

        public override bool DrawConditions()
            => Svc.GameGui.GetAddonByName("GatheringMasterpiece") != IntPtr.Zero;

        public override void Draw()
        {
            try
            {
                var addonPtr = Svc.GameGui.GetAddonByName("GatheringMasterpiece");
                if (addonPtr == IntPtr.Zero) return;

                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon == null || !addon->IsVisible || !addon->IsFullyLoaded()) return;

                var rootNode = addon->RootNode;
                if (rootNode == null) return;

                var position = AtkResNodeHelper.GetNodePosition(rootNode);
                var scale = AtkResNodeHelper.GetNodeScale(rootNode);
                var addonSize = new Vector2(rootNode->Width, rootNode->Height) * scale;

                var buttonPos = new Vector2(position.X + addonSize.X - 42 * scale.X, position.Y + addonSize.Y - 41 * scale.Y);

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(buttonPos);

                ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
                try
                {
                    ImGui.Begin("###YABOT_CosmicCollectableButton", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNavFocus
                        | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);

                    // SetWindowFontScale scales every font rendered in this window, including the icon
                    // font ImGuiComponents.IconButton pushes internally - so the button itself resizes.
                    // (Mutating ImGui.GetFont().Scale only affects the default font, not the icon glyph.)
                    ImGui.SetWindowFontScale(scale.X * 0.80f);

                    // Capture the toggle state once: the click handler below flips autoRun, so reading
                    // it again for the matching PopStyleColor would unbalance ImGui's style stack.
                    var isOn = autoRun;
                    if (isOn) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                    if (ImGuiComponents.IconButton(isOn ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play))
                    {
                        autoRun = !autoRun;
                        if (!autoRun) TaskManager.Abort();
                    }
                    if (isOn) ImGui.PopStyleColor();

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(autoRun
                            ? "Cosmic collectable auto-rotation: ON (click to stop)"
                            : "Cosmic collectable auto-rotation: OFF (click to start)");

                    ImGui.End();
                }
                finally
                {
                    ImGui.PopStyleVar(2);
                    ImGui.PopStyleColor();
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CosmicCollectableRotation.Draw");
            }
        }

        private void RotationTick(IFramework framework)
        {
            if (!autoRun) return;

            try
            {
                if (!GenericHelpers.TryGetAddonMaster<GatheringMasterpiece>("GatheringMasterpiece", out var collectable)
                 || !collectable.IsAddonReady)
                {
                    // Window closed: reset per-node tracking.
                    _trackedItemId = 0;
                    _lastCollectability = -1;
                    _lastCollectProgress = DateTime.MinValue;
                    return;
                }

                // Only wait out an in-flight action animation. Do NOT gate on Player.IsBusy: gathering
                // itself counts as "occupied", which would block the rotation entirely.
                if (Svc.Condition[ConditionFlag.ExecutingGatheringAction])
                    return;

                // Capture the baseline duty-action charges when a new collectable item window appears.
                if (_trackedItemId != collectable.ItemID)
                {
                    _trackedItemId = collectable.ItemID;
                    _buffCount = CollectStandardCharges();
                    _lastCollectability = -1;
                    _lastCollectProgress = DateTime.MinValue;
                }

                if (!EzThrottler.Throttle("YABOT_CosmicCollectable", Config.ActionDelayMs))
                    return;

                CollectableGather(collectable);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CosmicCollectableRotation.RotationTick");
            }
        }

        // Ported verbatim from ICE's Task_Gather.CollectableGather, minus mission/quantity logic.
        private void CollectableGather(GatheringMasterpiece collectable)
        {
            var integrity = collectable.CurrentIntegrity;
            var collect_Current = collectable.CurrentCollectability;
            var collect_Max = collectable.MaxCollectability;
            var collect_highGrade = collectable.HighCollectability;
            bool missingDur = integrity < collectable.TotalIntegrity;

            // Track collectability progress to detect stuck rotations.
            if (collect_Current != _lastCollectability)
            {
                _lastCollectability = collect_Current;
                _lastCollectProgress = DateTime.Now;
            }
            bool isStuck = _lastCollectProgress != DateTime.MinValue
                        && (DateTime.Now - _lastCollectProgress).TotalSeconds > 5;

            if (integrity > 1 && collect_Current < collect_highGrade && !isStuck)
            {
                if (!HasStatusId(3911 /* Collector's High Standard */))
                {
                    var currentCharge = CollectStandardCharges();

                    if (currentCharge != 0 && currentCharge >= _buffCount)
                    {
                        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 27);
                    }
                    else if (CanUseCollectableAction("Scrutiny"))
                    {
                        UseCollectableBuff("Scrutiny");
                    }
                    else
                    {
                        UseCollectableAction("Meticulous");
                    }
                }
                else
                {
                    // We have the buff. Pick the best gain for what's left.
                    var collect_Missing = collect_Max - collect_Current;
                    var meticulousPower = collectable.MeticulousPower;

                    if (CanUseCollectableAction("Scrutiny"))
                    {
                        UseCollectableBuff("Scrutiny");
                    }
                    else if (collect_Missing <= meticulousPower)
                    {
                        UseCollectableAction("Meticulous");
                    }
                    else
                    {
                        UseCollectableAction("Brazen");
                    }
                }
            }
            else
            {
                // Reset progress tracking when we start collecting.
                _lastCollectability = -1;
                _lastCollectProgress = DateTime.MinValue;

                if (integrity < collectable.TotalIntegrity && CanUseCollectableAction("BonusIntegrityChance", missingDur))
                {
                    if (EzThrottler.Throttle("YABOT_CosmicIntegrityBonus"))
                        UseCollectableAction("BonusIntegrityChance");
                }
                else if (CanUseCollectableAction("BonusIntegrity", missingDur))
                {
                    if (EzThrottler.Throttle("YABOT_CosmicIntegrityBonus"))
                        UseCollectableAction("BonusIntegrity");
                }
                else
                {
                    UseCollectableAction("Collect");
                }
            }
        }

        private bool CanUseCollectableAction(string action, bool missingDur = false)
        {
            var info = CollectableBuffs[action];
            bool hasStatus = HasStatusId(info.StatusId);
            bool hasGp = GetGp() >= info.RequiredGp;

            return action switch
            {
                "Scrutiny" => !hasStatus && hasGp,
                "BonusIntegrityChance" => hasStatus && missingDur,
                "BonusIntegrity" => hasGp && missingDur && GetGp() >= 300,
                _ => false,
            };
        }

        private void UseCollectableBuff(string action)
        {
            var actionId = CollectableBuffs[action].ClassAction[(uint)Player.Job];
            if (EzThrottler.Throttle("YABOT_CosmicBuff", 100))
                ActionManager.Instance()->UseAction(ActionType.Action, actionId);
        }

        private void UseCollectableAction(string action)
        {
            var actionId = CollectableActions[action].ClassAction[(uint)Player.Job];
            ActionManager.Instance()->UseAction(ActionType.Action, actionId);
        }

        private static uint CollectStandardCharges()
        {
            try
            {
                var dam = DutyActionManager.GetInstanceIfReady();
                if (dam != null)
                    return (uint)(dam->CurCharges[1] + dam->CurCharges[0]);

                return 0;
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CosmicCollectableRotation.CollectStandardCharges");
                return 0;
            }
        }

        private static uint GetGp() => Player.Object?.CurrentGp ?? 0;

        private static bool HasStatusId(uint id) => id != 0 && Player.Status.Any(s => s.StatusId == id);

        private sealed class GatherAction
        {
            public Dictionary<uint, uint> ClassAction = new();
            public uint StatusId;
            public uint RequiredGp;
        }

        // ClassAction keys: 16 = Miner (MIN), 17 = Botanist (BTN). IDs from ICE GatheringUtil.
        private static readonly Dictionary<string, GatherAction> CollectableBuffs = new()
        {
            ["Scrutiny"] = new() { ClassAction = { [16] = 22185, [17] = 22189 }, StatusId = 757, RequiredGp = 200 },
            ["BonusIntegrity"] = new() { ClassAction = { [16] = 232, [17] = 215 }, StatusId = 0, RequiredGp = 300 },
            ["BonusIntegrityChance"] = new() { ClassAction = { [16] = 26521, [17] = 26522 }, StatusId = 2765, RequiredGp = 0 },
        };

        private static readonly Dictionary<string, GatherAction> CollectableActions = new()
        {
            ["Scour"] = new() { ClassAction = { [16] = 22182, [17] = 22186 } },
            ["Brazen"] = new() { ClassAction = { [16] = 22183, [17] = 22187 } },
            ["Meticulous"] = new() { ClassAction = { [16] = 22184, [17] = 22188 } },
            ["BonusIntegrity"] = new() { ClassAction = { [16] = 232, [17] = 215 }, RequiredGp = 300 },
            ["BonusIntegrityChance"] = new() { ClassAction = { [16] = 26521, [17] = 26522 }, StatusId = 2765 },
            ["Collect"] = new() { ClassAction = { [16] = 240, [17] = 815 } },
        };
    }
}
