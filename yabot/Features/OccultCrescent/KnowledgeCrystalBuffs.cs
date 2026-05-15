using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using YABOT.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static ECommons.GenericHelpers;

namespace YABOT.Features.OccultCrescent
{
    public unsafe class KnowledgeCrystalBuffs : BaseFeature
    {
        public override string Name => "Knowledge Crystal Buffs";
        public override string Description => "Shows a button near Knowledge Crystals to apply all available phantom job buffs. Also available as /ykcb command.";
        public override FeatureType FeatureType => FeatureType.OccultCrescent;

        public override IEnumerable<(string Command, string Aliases, string Description)> CommandReferences =>
            new[] { ("/ykcb", "/ycrystalbuffs", "Apply all available phantom job buffs at a Knowledge Crystal in Occult Crescent.") };

        private struct CrystalBuff
        {
            public byte JobRowId;
            public uint ActionId;
            public byte RequiredLevel;
            public uint StatusId;
            public string JobName;
            public string ActionName;
        }

        // MKDSupportJob column layout:
        // 0: Name, 1: NameShort, 2: NameFemale, 3: Description, 4: NameEnglish
        // 5: LevelMax, 6: JobIndex
        // 7+: pairs of (LevelUnlock[i], Action[i]) for i=0..4
        private const int ColName = 0;
        private const int ColActionsStart = 7;

        private readonly List<CrystalBuff> crystalBuffs = new();
        private CrystalBuff? inquiringMind;
        private CrystalBuff? occultTreasuresight;
        private uint treasuresightIconId;
        private CrystalBuff? geomancerSuspend;
        private uint suspendIconId;
        private bool dataLoaded;
        private Overlays Overlay = null!;

        private bool treasuresightRunning;
        private byte treasuresightOriginalJob;
        private bool suspendRunning;
        private byte suspendOriginalJob;

        public class Configs : FeatureConfig
        {
            public Vector2 WindowPos = new(-1, -1);
            public Vector2 TreasuresightWindowPos = new(-1, -1);
            public Vector2 SuspendWindowPos = new(-1, -1);
            public bool ShowTreasuresightButton = false;
            public bool ShowSuspendButton = false;
        }

        public Configs Config { get; private set; } = null!;
        public override bool UseAutoConfig => false;

        private static readonly Dictionary<string, string> CrystalActionToBuffName = new()
        {
            { "Pray", "Enduring Fortitude" },
            { "Counterstance", "Fleetfooted" },
            { "Romeo's Ballad", "Romeo's Ballad" },
            { "Quickstep", "Quicker Step" },
        };

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Overlay = new(this);
            Svc.Commands.AddHandler("/ykcb", new CommandInfo(OnCommand) { ShowInHelp = false });
            Svc.Commands.AddHandler("/ycrystalbuffs", new CommandInfo(OnCommand) { ShowInHelp = false });
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            P.Ws.RemoveWindow(Overlay);
            Overlay = null!;
            Svc.Commands.RemoveHandler("/ykcb");
            Svc.Commands.RemoveHandler("/ycrystalbuffs");
            base.Disable();
        }

        private const uint KnowledgeCrystalBaseId = 2007457;

        private bool IsNearAetheryte()
        {
            if (Player.Object == null) return false;
            return Svc.Objects.Any(x =>
                x.Name.ToString().Contains("Aetheryte", StringComparison.OrdinalIgnoreCase) &&
                Vector3.Distance(x.Position, Player.Object.Position) < 20f);
        }

        private bool IsNearKnowledgeCrystal()
        {
            if (Player.Object == null) return false;
            return Svc.Objects.Any(x =>
            {
                if (x.ObjectKind != ObjectKind.EventObj) return false;
                if (x.Name.ToString().Length > 0) return false;
                if (Vector3.Distance(x.Position, Player.Object.Position) > 5f) return false;
                var baseObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)x.Address;
                return baseObj->BaseId == KnowledgeCrystalBaseId;
            });
        }

        private bool ShowCrystalOverlay()
        {
            return !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]
                && IsNearAetheryte()
                && IsNearKnowledgeCrystal();
        }

        private bool ShowTreasuresightOverlay()
        {
            return Config.ShowTreasuresightButton && occultTreasuresight.HasValue;
        }

        private bool ShowSuspendOverlay()
        {
            return Config.ShowSuspendButton && geomancerSuspend.HasValue;
        }

        public override bool DrawConditions()
        {
            if (!ZoneHelper.IsOccultCrescent()) return false;
            try { EnsureDataLoaded(); } catch { }
            return ShowCrystalOverlay() || ShowTreasuresightOverlay() || ShowSuspendOverlay();
        }

        public override void Draw()
        {
            try
            {
                EnsureDataLoaded();

                if (ShowCrystalOverlay())
                    DrawCrystalBuffsWindow();

                if (ShowTreasuresightOverlay())
                    DrawPhantomActionWindow(
                        "###OccultTreasuresightOverlay",
                        "Treasuresight",
                        Config.TreasuresightWindowPos,
                        p => Config.TreasuresightWindowPos = p,
                        treasuresightIconId,
                        treasuresightRunning,
                        "Cast Occult Treasuresight\n(switches to Phantom Freelancer and back)\nHold Shift to drag",
                        () => ExecutePhantomAction(occultTreasuresight!.Value, remountAfter: true,
                            r => treasuresightRunning = r, j => treasuresightOriginalJob = j,
                            "Phantom Freelancer", "Occult Treasuresight"),
                        () => CancelPhantomAction(treasuresightOriginalJob, r => treasuresightRunning = r));

                if (ShowSuspendOverlay())
                    DrawPhantomActionWindow(
                        "###GeomancerSuspendOverlay",
                        "Suspend",
                        Config.SuspendWindowPos,
                        p => Config.SuspendWindowPos = p,
                        suspendIconId,
                        suspendRunning,
                        "Cast Suspend\n(switches to Phantom Geomancer and back)\nHold Shift to drag",
                        () => ExecutePhantomAction(geomancerSuspend!.Value, remountAfter: false,
                            r => suspendRunning = r, j => suspendOriginalJob = j,
                            "Phantom Geomancer", "Suspend"),
                        () => CancelPhantomAction(suspendOriginalJob, r => suspendRunning = r));
            }
            catch { }
        }

        private void DrawCrystalBuffsWindow()
        {
            ImGuiHelpers.ForceNextWindowMainViewport();
            if (Config.WindowPos.X >= 0 && Config.WindowPos.Y >= 0)
                ImGui.SetNextWindowPos(Config.WindowPos, ImGuiCond.Once);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0.7f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
            var shiftHeld = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            if (!shiftHeld)
                flags |= ImGuiWindowFlags.NoMove;

            ImGui.Begin("###KnowledgeCrystalBuffsOverlay", flags);

            if (TaskManager.IsBusy)
            {
                ImGui.TextUnformatted("Applying buffs...");
            }
            else
            {
                var missingCount = GetMissingBuffCount();
                var label = missingCount > 0
                    ? $"Apply Crystal Buffs ({missingCount} missing)"
                    : "Apply Crystal Buffs";
                if (ImGui.Button(label))
                    ExecuteBuffSequence();
            }

            Config.WindowPos = ImGui.GetWindowPos();
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }

        private void DrawPhantomActionWindow(
            string windowId,
            string fallbackLabel,
            Vector2 currentPos,
            System.Action<Vector2> savePos,
            uint iconId,
            bool isRunning,
            string idleTooltip,
            System.Action onActivate,
            System.Action onCancel)
        {
            ImGuiHelpers.ForceNextWindowMainViewport();
            if (currentPos.X >= 0 && currentPos.Y >= 0)
                ImGui.SetNextWindowPos(currentPos, ImGuiCond.Once);

            var shiftHeld = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, shiftHeld ? 0.7f : 0.5f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
            if (!shiftHeld)
                flags |= ImGuiWindowFlags.NoMove;

            ImGui.Begin(windowId, flags);

            var iconSize = ImGui.GetFontSize() * 2.2f;
            var clicked = false;
            if (iconId != 0 && ThreadLoadImageHandler.TryGetIconTextureWrap(iconId, false, out var tex) && tex != null)
            {
                var cursorStart = ImGui.GetCursorScreenPos();
                ImGui.Image(tex.Handle, new Vector2(iconSize, iconSize));
                var iconMax = cursorStart + new Vector2(iconSize, iconSize);
                if (isRunning)
                {
                    DrawMarchingAnts(cursorStart, iconMax, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 0.95f)));
                }
                else if (TaskManager.IsBusy)
                {
                    ImGui.GetWindowDrawList().AddRectFilled(cursorStart, iconMax, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.5f)));
                }
                if (ImGui.IsItemHovered())
                {
                    var tooltip = isRunning
                        ? "Click to cancel\n(returns to original phantom job)"
                        : TaskManager.IsBusy
                            ? "Busy..."
                            : idleTooltip;
                    ImGui.SetTooltip(tooltip);
                    if (!shiftHeld && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        clicked = true;
                }
            }
            else
            {
                clicked = ImGui.Button(fallbackLabel, new Vector2(iconSize + 40, iconSize));
            }

            if (clicked)
            {
                if (isRunning)
                    onCancel();
                else if (!TaskManager.IsBusy)
                    onActivate();
            }

            savePos(ImGui.GetWindowPos());
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }

        private void OnCommand(string cmd, string argStr)
        {
            try
            {
                var args = argStr.Trim().ToLower();

                if (args == "scan")
                {
                    ScanNearbyObjects();
                    return;
                }

                EnsureDataLoaded();

                if (TaskManager.IsBusy)
                {
                    Svc.Chat.PrintError("[YABOT] Crystal buffs already in progress.");
                    return;
                }

                if (!ZoneHelper.IsOccultCrescent())
                {
                    Svc.Chat.PrintError("[YABOT] Not in Occult Crescent.");
                    return;
                }

                if (crystalBuffs.Count == 0)
                {
                    Svc.Chat.PrintError("[YABOT] No crystal buff data loaded.");
                    return;
                }

                ExecuteBuffSequence();
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "KnowledgeCrystalBuffs command failed");
                Svc.Chat.PrintError($"[YABOT] Error: {ex.Message}");
            }
        }

        private void ScanNearbyObjects()
        {
            if (Player.Object == null) return;
            var nearby = Svc.Objects
                .Where(x => Vector3.Distance(x.Position, Player.Object.Position) < 15f)
                .OrderBy(x => Vector3.Distance(x.Position, Player.Object.Position));

            foreach (var obj in nearby)
            {
                var baseObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                Svc.Log.Info($"[KCB Scan] {obj.ObjectKind} name='{obj.Name}' baseId={baseObj->BaseId} entityId={obj.EntityId:X} dist={Vector3.Distance(obj.Position, Player.Object.Position):F1}");
            }
            Svc.Chat.Print("[YABOT] Scanned nearby objects - check /xllog for results.");
        }

        private const float BuffRefreshThresholdSeconds = 15f * 60f;

        private static bool BuffNeedsRefresh(CrystalBuff b, Dalamud.Game.ClientState.Statuses.StatusList? statuses)
        {
            if (b.StatusId == 0 || statuses == null) return true;
            foreach (var s in statuses)
            {
                if (s.StatusId == b.StatusId)
                    return s.RemainingTime < BuffRefreshThresholdSeconds;
            }
            return true;
        }

        private int GetMissingBuffCount()
        {
            if (crystalBuffs.Count == 0) return 0;
            var playerStatuses = Svc.Objects.LocalPlayer?.StatusList;
            return crystalBuffs.Count(b => BuffNeedsRefresh(b, playerStatuses));
        }

        private void ExecuteBuffSequence()
        {
            var state = PublicContentOccultCrescent.GetState();
            if (state == null) return;

            var playerStatuses = Svc.Objects.LocalPlayer?.StatusList;
            var missingBuffs = crystalBuffs.Where(b => BuffNeedsRefresh(b, playerStatuses)).ToList();

            if (missingBuffs.Count == 0) return;

            var originalJob = state->CurrentSupportJob;

            if (inquiringMind.HasValue && missingBuffs.Count > 1)
            {
                var im = inquiringMind.Value;
                if (im.JobRowId < 16)
                {
                    var freelancerLevel = state->SupportJobLevels[im.JobRowId];
                    if (freelancerLevel >= im.RequiredLevel)
                    {
                        QueueSwitchAndAction(im);
                        QueueSwitchBack(originalJob);
                        return;
                    }
                }
            }

            foreach (var buff in missingBuffs)
            {
                if (buff.JobRowId >= 16) continue;
                var jobLevel = state->SupportJobLevels[buff.JobRowId];
                if (jobLevel < buff.RequiredLevel) continue;
                QueueSwitchAndAction(buff);
            }
            QueueSwitchBack(originalJob);
        }

        private void EnsureDataLoaded()
        {
            if (dataLoaded && crystalBuffs.Count > 0) return;

            crystalBuffs.Clear();
            inquiringMind = null;

            var buffStatusIds = new Dictionary<string, uint>();
            var targetBuffNames = CrystalActionToBuffName.Values.ToHashSet();
            for (uint i = 1; i < 5000; i++)
            {
                if (!TryGetRawRow("Status", i, out RawRow statusRow)) continue;
                var statusName = statusRow.ReadColumn(0)?.ToString() ?? "";
                if (targetBuffNames.Contains(statusName) && !buffStatusIds.ContainsKey(statusName))
                    buffStatusIds[statusName] = i;
                if (buffStatusIds.Count == targetBuffNames.Count) break;
            }

            for (byte jobRow = 0; jobRow < 30; jobRow++)
            {
                if (!TryGetRawRow("MKDSupportJob", jobRow, out RawRow job)) continue;

                var jobName = job.ReadColumn(ColName)?.ToString() ?? "";
                if (string.IsNullOrEmpty(jobName)) continue;

                for (int a = 0; a < 5; a++)
                {
                    uint actionId;
                    byte requiredLevel;
                    try
                    {
                        requiredLevel = Convert.ToByte(job.ReadColumn(ColActionsStart + a * 2));
                        actionId = Convert.ToUInt32(job.ReadColumn(ColActionsStart + a * 2 + 1));
                    }
                    catch { continue; }
                    if (actionId == 0) continue;

                    if (!TryGetRawRow("Action", actionId, out RawRow action)) continue;
                    var actionName = action.ReadColumn(0)?.ToString() ?? "";

                    if (CrystalActionToBuffName.TryGetValue(actionName, out var buffName))
                    {
                        buffStatusIds.TryGetValue(buffName, out var statusId);
                        crystalBuffs.Add(new CrystalBuff
                        {
                            JobRowId = jobRow,
                            ActionId = actionId,
                            RequiredLevel = requiredLevel,
                            StatusId = statusId,
                            JobName = jobName,
                            ActionName = actionName,
                        });
                    }
                    else if (actionName == "Inquiring Mind")
                    {
                        inquiringMind = new CrystalBuff
                        {
                            JobRowId = jobRow,
                            ActionId = actionId,
                            RequiredLevel = requiredLevel,
                            StatusId = 0,
                            JobName = jobName,
                            ActionName = actionName,
                        };
                    }
                    else if (actionName == "Occult Treasuresight")
                    {
                        occultTreasuresight = new CrystalBuff
                        {
                            JobRowId = jobRow,
                            ActionId = actionId,
                            RequiredLevel = requiredLevel,
                            StatusId = 0,
                            JobName = jobName,
                            ActionName = actionName,
                        };
                        try
                        {
                            var actionSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                            treasuresightIconId = actionSheet.GetRow(actionId).Icon;
                        }
                        catch { treasuresightIconId = 0; }
                    }
                    else if (actionName == "Suspend")
                    {
                        geomancerSuspend = new CrystalBuff
                        {
                            JobRowId = jobRow,
                            ActionId = actionId,
                            RequiredLevel = requiredLevel,
                            StatusId = 0,
                            JobName = jobName,
                            ActionName = actionName,
                        };
                        try
                        {
                            var actionSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                            suspendIconId = actionSheet.GetRow(actionId).Icon;
                        }
                        catch { suspendIconId = 0; }
                    }
                }
            }

            dataLoaded = true;
            Svc.Log.Info($"[KnowledgeCrystalBuffs] Loaded {crystalBuffs.Count} crystal buffs, Inquiring Mind: {(inquiringMind.HasValue ? "found" : "not found")}, Occult Treasuresight: {(occultTreasuresight.HasValue ? $"found (icon {treasuresightIconId})" : "not found")}, Suspend: {(geomancerSuspend.HasValue ? $"found (icon {suspendIconId})" : "not found")}");
        }

        private void QueueSwitchAndAction(CrystalBuff buff)
        {
            var actionId = buff.ActionId;

            TaskManager.EnqueueDelay(800);
            QueueSupportJobSwitch(buff.JobRowId);
            TaskManager.EnqueueDelay(600);
            TaskManager.Enqueue(() =>
            {
                try
                {
                    var am = ActionManager.Instance();
                    if (am->GetActionStatus(ActionType.Action, actionId) != 0) return false;
                    am->UseAction(ActionType.Action, actionId);
                    return true;
                }
                catch { return (bool?)true; }
            }, ResilientConfig(ActionCooldownWaitMs));
        }

        private void QueueSwitchBack(byte originalJob)
        {
            TaskManager.EnqueueDelay(1000);
            QueueSupportJobSwitch(originalJob);
        }

        private const uint MountRouletteGeneralActionId = 9;

        private const int ActionCooldownWaitMs = 65000;

        private static TaskManagerConfiguration ResilientConfig(int timeLimitMs) => new()
        {
            TimeLimitMS = timeLimitMs,
            AbortOnTimeout = false,
            AbortOnError = false,
        };

        private void ExecutePhantomAction(
            CrystalBuff action,
            bool remountAfter,
            System.Action<bool> setRunning,
            System.Action<byte> setOriginalJob,
            string jobDisplayName,
            string actionDisplayName)
        {
            var state = PublicContentOccultCrescent.GetState();
            if (state == null) return;
            if (action.JobRowId >= 16) return;

            var jobLevel = state->SupportJobLevels[action.JobRowId];
            if (jobLevel < action.RequiredLevel)
            {
                Svc.Chat.PrintError($"[YABOT] {jobDisplayName} level {action.RequiredLevel} required for {actionDisplayName} (currently {jobLevel}).");
                return;
            }

            var originalJob = state->CurrentSupportJob;
            var needsSwitch = originalJob != action.JobRowId;
            var actionId = action.ActionId;
            var targetJobId = action.JobRowId;

            var wasMounted = Player.Object != null
                && Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted];

            setRunning(true);
            setOriginalJob(originalJob);

            if (wasMounted)
                QueueDismount();

            if (needsSwitch)
            {
                TaskManager.EnqueueDelay(800);
                QueueSupportJobSwitch(targetJobId);
                TaskManager.EnqueueDelay(600);
            }
            else
            {
                TaskManager.EnqueueDelay(wasMounted ? 400 : 0);
            }

            TaskManager.Enqueue(() =>
            {
                try
                {
                    var am = ActionManager.Instance();
                    if (am->GetActionStatus(ActionType.Action, actionId) != 0) return false;
                    am->UseAction(ActionType.Action, actionId);
                    return true;
                }
                catch { return (bool?)true; }
            }, ResilientConfig(ActionCooldownWaitMs));

            if (needsSwitch)
            {
                TaskManager.EnqueueDelay(1000);
                QueueSupportJobSwitch(originalJob);
            }

            if (wasMounted && remountAfter)
                QueueMountRoulette();

            TaskManager.Enqueue(() => { setRunning(false); return true; }, ResilientConfig(1000));
        }

        private void CancelPhantomAction(byte revertTo, System.Action<bool> setRunning)
        {
            TaskManager.Abort();
            setRunning(false);
            QueueSupportJobSwitch(revertTo);
        }

        private static void DrawMarchingAnts(Vector2 min, Vector2 max, uint color, float thickness = 2f, float dashLength = 6f, float speed = 18f)
        {
            var dl = ImGui.GetWindowDrawList();
            var period = dashLength * 2f;
            var phase = (float)((ImGui.GetTime() * speed) % period);

            Vector2[] corners =
            {
                min,
                new Vector2(max.X, min.Y),
                max,
                new Vector2(min.X, max.Y),
                min,
            };

            var t = -phase;
            for (var i = 0; i < 4; i++)
            {
                var a = corners[i];
                var b = corners[i + 1];
                var dir = b - a;
                var len = dir.Length();
                if (len < 0.001f) continue;
                var unit = dir / len;

                while (t < len)
                {
                    var dashStart = Math.Max(0f, t);
                    var dashEnd = Math.Min(len, t + dashLength);
                    if (dashEnd > dashStart)
                        dl.AddLine(a + unit * dashStart, a + unit * dashEnd, color, thickness);
                    t += period;
                }
                t -= len;
            }
        }

        private void QueueSupportJobSwitch(byte jobId)
        {
            TaskManager.Enqueue(() =>
            {
                try
                {
                    var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance()->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.MKDSupportJobList);
                    ((FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMKDSupportJobList*)agent)->ChangeSupportJob(jobId);
                    return true;
                }
                catch { return true; }
            }, ResilientConfig(5000));
        }

        private void QueueDismount()
        {
            TaskManager.Enqueue(() =>
            {
                try
                {
                    if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted]) return true;
                    var am = ActionManager.Instance();
                    if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0) return false;
                    am->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId, 0xE0000000, 0, 0, 0, null);
                    return true;
                }
                catch { return (bool?)true; }
            }, ResilientConfig(3000));
            TaskManager.Enqueue(() =>
            {
                try { return !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted]; }
                catch { return (bool?)true; }
            }, ResilientConfig(3000));
        }

        private void QueueMountRoulette()
        {
            TaskManager.EnqueueDelay(800);
            TaskManager.Enqueue(() =>
            {
                try
                {
                    if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted]) return true;
                    if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]) return true;
                    var am = ActionManager.Instance();
                    if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0) return false;
                    am->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId);
                    return true;
                }
                catch { return (bool?)true; }
            }, ResilientConfig(5000));
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            var showTs = Config.ShowTreasuresightButton;
            if (ImGui.Checkbox("Show Occult Treasuresight button while in Occult Crescent", ref showTs))
            {
                Config.ShowTreasuresightButton = showTs;
                hasChanged = true;
            }
            ImGui.TextDisabled("Clicking the icon switches to Phantom Freelancer, casts Occult Treasuresight, and reverts to the original job.\nHold Shift while hovering the icon to reposition the overlay.");

            ImGui.Spacing();

            var showSuspend = Config.ShowSuspendButton;
            if (ImGui.Checkbox("Show Suspend button while in Occult Crescent", ref showSuspend))
            {
                Config.ShowSuspendButton = showSuspend;
                hasChanged = true;
            }
            ImGui.TextDisabled("Clicking the icon switches to Phantom Geomancer, casts Suspend, and reverts to the original job.\nDoes not remount afterwards. Hold Shift while hovering the icon to reposition the overlay.");
        };
    }
}
