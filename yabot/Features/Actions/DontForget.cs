using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using YABOT.FeaturesSetup;
using System;
using System.Linq;
using System.Reflection;

namespace YABOT.Features.Actions
{
    public unsafe class DontForget : BaseFeature
    {
        public override string Name => "Don't Forget";

        public override string Description =>
            "Bundle of \"things you always forget to do\" automations: auto Peloton/Sprint when moving, auto-summon Carbuncle/Fairy, auto tank stance, auto gathering buffs, auto Gysahl Greens, chocobo stance keeper, and auto-switch gatherer class on wrong-node errors.";

        public override FeatureType FeatureType => FeatureType.Actions;

        public override bool UseAutoConfig => false;

        public class Configs : FeatureConfig
        {
            public bool Scholar = true;
            public bool Summoner = true;
            public bool SummonInCombatAfterDeath = true;
            public bool TankStance = true;
            public bool GatheringBuffs = true;
            public bool AutoSwitchGatherer = true;
            public bool Peloton = true;
            public bool AutoSprint = true;
            public bool AutoGysahlGreens = false;
            public byte ChocoboStanceOption = 0;
        }

        public Configs Config { get; private set; } = null!;

        // Action / status IDs
        private const uint SummonFairy = 17215;
        private const uint SummonCarbuncle = 25798;
        private const uint Peloton = 7557;
        private const uint Sprint = 4;
        private const uint GysahlGreens = 4868;
        private const uint IronWill = 28, Defiance = 48, Grit = 3629, RoyalGuard = 16142;
        private const uint IronWillStatus = 79, DefianceStatus = 91, GritStatus = 743, RoyalGuardStatus = 1833;
        private const uint Sneak = 304, Prospect = 227, Triangulate = 210;
        private const uint TruthOfMountains = 238, TruthOfForests = 221, TruthOfOceans = 7911;
        private const uint SneakStatus = 47, ProspectStatus = 225, TriangulateStatus = 217;
        private const uint TruthOfMountainsStatus = 222, TruthOfForestsStatus = 221, TruthOfOceansStatus = 1173;
        private const uint PelotonStatus = 1199;
        private const uint SprintStatus1 = 50, SprintStatus2 = 4398;

        // ChocoboStance dropdown
        public static readonly string[] StanceLabels = { "Disabled", "Free Stance", "Attacker Stance", "Defender Stance", "Healer Stance" };
        public static readonly byte[] StanceIds = { 0, 4, 5, 6, 7 };
        private static int StanceIdToIndex(byte id)
        {
            for (var i = 0; i < StanceIds.Length; i++) if (StanceIds[i] == id) return i;
            return 0;
        }

        // Runtime state
        private DateTime lastGatheringAction = DateTime.MinValue;
        private DateTime lastGysahlUse = DateTime.MinValue;
        private DateTime demiSummonLastSeen = DateTime.MinValue;
        private DateTime movementStartTime = DateTime.MinValue;
        private DateTime lastStanceAction = DateTime.MinValue;
        private DateTime lastTankStanceAction = DateTime.MinValue;
        private DateTime playerRaisedTimestamp = DateTime.MinValue;
        private bool wasUnconscious;
        private bool wasInCombat;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.Framework.Update += OnFrameworkUpdate;
            Svc.Chat.ChatMessage += OnChatMessage;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= OnFrameworkUpdate;
            Svc.Chat.ChatMessage -= OnChatMessage;
            base.Disable();
        }

        private static bool IsInGPose()
        {
            if (Svc.ClientState.IsGPosing) return true;
            if (Svc.GameGui.GetAddonByName("GPoseHud").Address != nint.Zero) return true;
            if (Svc.GameGui.GetAddonByName("BannerEditor").Address != nint.Zero) return true;
            return false;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                if (IsInGPose()) return;

                var player = Svc.Objects.LocalPlayer;
                if (player == null) return;

                var am = ActionManager.Instance();
                var isInCombat = Svc.Condition[ConditionFlag.InCombat];
                var isUnconscious = Svc.Condition[ConditionFlag.Unconscious];

                if (wasInCombat && !isInCombat)
                    playerRaisedTimestamp = DateTime.MinValue;

                if (wasUnconscious && !isUnconscious)
                {
                    playerRaisedTimestamp = DateTime.Now;
                    demiSummonLastSeen = DateTime.MinValue;
                }

                wasInCombat = isInCombat;
                wasUnconscious = isUnconscious;

                // Combat-summoning gate: only allow if recently raised
                var combatSummonAllowed = !isInCombat
                    || (Config.SummonInCombatAfterDeath
                        && playerRaisedTimestamp != DateTime.MinValue
                        && (DateTime.Now - playerRaisedTimestamp).TotalSeconds <= 15);

                var isMoving = AgentMap.Instance()->IsPlayerMoving;

                if (isMoving)
                {
                    if (movementStartTime == DateTime.MinValue) movementStartTime = DateTime.Now;
                }
                else
                {
                    movementStartTime = DateTime.MinValue;
                }

                var movingForTwoSeconds = movementStartTime != DateTime.MinValue
                    && (DateTime.Now - movementStartTime).TotalSeconds >= 2;

                var hasPeloton = player.StatusList.Any(x => x.StatusId == PelotonStatus);
                var hasSprint = player.StatusList.Any(x => x.StatusId == SprintStatus1 || x.StatusId == SprintStatus2);

                if (combatSummonAllowed && Config.Peloton && movingForTwoSeconds
                    && am->GetActionStatus(ActionType.Action, Peloton) == 0 && !hasPeloton)
                {
                    am->UseAction(ActionType.Action, Peloton);
                }

                if (combatSummonAllowed && Config.AutoSprint && movingForTwoSeconds
                    && am->GetActionStatus(ActionType.GeneralAction, Sprint) == 0
                    && !hasPeloton && !hasSprint)
                {
                    am->UseAction(ActionType.GeneralAction, Sprint);
                }

                if (!isMoving && combatSummonAllowed)
                {
                    var classJobId = player.ClassJob.RowId;
                    var playerId = player.GameObjectId;

                    var petNames = new[] { "Carbuncle", "Eos", "Selene", "Ifrit-Egi", "Titan-Egi", "Garuda-Egi" };
                    var demiNames = new[] { "Demi-Bahamut", "Demi-Phoenix", "Solar Bahamut", "Seraph" };

                    var hasPet = Svc.Objects.Any(o =>
                        o.OwnerId == playerId
                        && o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                        && o.IsValid()
                        && (petNames.Contains(o.Name.ToString()) || o.Name.ToString().Contains("Carbuncle")));

                    var hasDemi = Svc.Objects.Any(o =>
                        o.OwnerId == playerId
                        && o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                        && o.IsValid()
                        && demiNames.Contains(o.Name.ToString()));

                    if (hasDemi) demiSummonLastSeen = DateTime.Now;

                    var demiGracePeriod = demiSummonLastSeen != DateTime.MinValue
                        && (DateTime.Now - demiSummonLastSeen).TotalSeconds < 3;

                    if (!hasPet && !demiGracePeriod)
                    {
                        if (classJobId == 28 && Config.Scholar
                            && am->GetActionStatus(ActionType.Action, SummonFairy) == 0)
                            am->UseAction(ActionType.Action, SummonFairy);
                        else if (classJobId == 27 && Config.Summoner
                            && am->GetActionStatus(ActionType.Action, SummonCarbuncle) == 0)
                            am->UseAction(ActionType.Action, SummonCarbuncle);
                    }

                    // Auto tank stance, with 2-second cooldown to prevent runaway recasts
                    if (Config.TankStance && (DateTime.Now - lastTankStanceAction).TotalSeconds >= 2)
                    {
                        var statuses = player.StatusList;
                        var fired = false;
                        switch (classJobId)
                        {
                            case 19 when !statuses.Any(x => x.StatusId == IronWillStatus)
                                          && am->GetActionStatus(ActionType.Action, IronWill) == 0:
                                am->UseAction(ActionType.Action, IronWill); fired = true; break;
                            case 21 when !statuses.Any(x => x.StatusId == DefianceStatus)
                                          && am->GetActionStatus(ActionType.Action, Defiance) == 0:
                                am->UseAction(ActionType.Action, Defiance); fired = true; break;
                            case 32 when !statuses.Any(x => x.StatusId == GritStatus)
                                          && am->GetActionStatus(ActionType.Action, Grit) == 0:
                                am->UseAction(ActionType.Action, Grit); fired = true; break;
                            case 37 when !statuses.Any(x => x.StatusId == RoyalGuardStatus)
                                          && am->GetActionStatus(ActionType.Action, RoyalGuard) == 0:
                                am->UseAction(ActionType.Action, RoyalGuard); fired = true; break;
                        }
                        if (fired) lastTankStanceAction = DateTime.Now;
                    }

                    // Auto gathering buffs (MIN/BTN/FSH) when standing still, with 2-second cooldown
                    if (Config.GatheringBuffs && (classJobId is 16 or 17 or 18)
                        && (DateTime.Now - lastGatheringAction).TotalSeconds >= 2)
                    {
                        var statuses = player.StatusList;
                        var isOnHomeWorld = player.HomeWorld.RowId == player.CurrentWorld.RowId;

                        (uint statusId, uint actionId)[] buffs = classJobId switch
                        {
                            16 => isOnHomeWorld
                                ? new[] { (ProspectStatus, Prospect), (SneakStatus, Sneak), (TruthOfMountainsStatus, TruthOfMountains) }
                                : new[] { (ProspectStatus, Prospect), (SneakStatus, Sneak) },
                            17 => isOnHomeWorld
                                ? new[] { (TriangulateStatus, Triangulate), (SneakStatus, Sneak), (TruthOfForestsStatus, TruthOfForests) }
                                : new[] { (TriangulateStatus, Triangulate), (SneakStatus, Sneak) },
                            18 => isOnHomeWorld
                                ? new[] { (TruthOfOceansStatus, TruthOfOceans) }
                                : Array.Empty<(uint, uint)>(),
                            _ => Array.Empty<(uint, uint)>(),
                        };

                        foreach (var (statusId, actionId) in buffs)
                        {
                            if (!statuses.Any(x => x.StatusId == statusId)
                                && am->GetActionStatus(ActionType.Action, actionId) == 0)
                            {
                                am->UseAction(ActionType.Action, actionId);
                                lastGatheringAction = DateTime.Now;
                                break;
                            }
                        }
                    }
                }

                // Chocobo stance keeper
                UpdateChocoboStance();

                // Auto Gysahl Greens (chocobo timer < 15 min, not in combat, with 5-second cooldown)
                if (Config.AutoGysahlGreens && !isInCombat && (DateTime.Now - lastGysahlUse).TotalSeconds >= 5)
                {
                    var timeLeft = UIState.Instance()->Buddy.CompanionInfo.TimeLeft;
                    if (timeLeft > 0 && timeLeft < 900
                        && am->GetActionStatus(ActionType.Item, GysahlGreens) == 0)
                    {
                        am->UseAction(ActionType.Item, GysahlGreens, 0xE0000000, 0xFFFF);
                        lastGysahlUse = DateTime.Now;
                    }
                }
            }
            catch { }
        }

        private void UpdateChocoboStance()
        {
            try
            {
                if (Config.ChocoboStanceOption == 0) return;

                var info = &UIState.Instance()->Buddy.CompanionInfo;
                if (info->TimeLeft <= 0) return;

                if (info->ActiveCommand == Config.ChocoboStanceOption) return;
                if ((DateTime.Now - lastStanceAction).TotalSeconds < 2) return;

                var am = ActionManager.Instance();
                if (am->GetActionStatus(ActionType.BuddyAction, Config.ChocoboStanceOption) == 0)
                {
                    am->UseAction(ActionType.BuddyAction, Config.ChocoboStanceOption);
                    lastStanceAction = DateTime.Now;
                }
            }
            catch { }
        }

        // Auto-switch gatherer class on "wrong class" chat error
        private void OnChatMessage(IHandleableChatMessage chatMessage)
        {
            try
            {
                if (IsInGPose()) return;
                if (!Config.AutoSwitchGatherer) return;

                // Type 2108 = "Unable to X. Current class not set to X" gathering errors
                if ((int)chatMessage.LogKind != 2108) return;

                var text = chatMessage.Message.TextValue.ToLowerInvariant();

                if (text.Contains("miner"))
                    SwitchToGearset(16);
                else if (text.Contains("botanist"))
                    SwitchToGearset(17);
            }
            catch { }
        }

        public void HandleSubCommand(string args)
        {
            args = (args ?? string.Empty).Trim();

            var boolFields = typeof(Configs).GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(bool))
                .ToList();

            if (args.Length == 0 || args.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                Svc.Chat.Print("[YABOT] Don't Forget options:");
                foreach (var f in boolFields)
                {
                    var v = (bool)f.GetValue(Config)!;
                    Svc.Chat.Print($"  {f.Name.ToLowerInvariant()}: {(v ? "ON" : "OFF")}");
                }
                return;
            }

            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var optionName = parts[0];
            var setTo = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : null;

            var field = boolFields.FirstOrDefault(f =>
                f.Name.Equals(optionName, StringComparison.OrdinalIgnoreCase));

            if (field == null)
            {
                var names = string.Join(", ", boolFields.Select(f => f.Name.ToLowerInvariant()));
                Svc.Chat.PrintError($"[YABOT] Unknown Don't Forget option '{optionName}'. Available: {names}");
                return;
            }

            var current = (bool)field.GetValue(Config)!;
            bool newVal = setTo switch
            {
                "on" or "true" or "1" => true,
                "off" or "false" or "0" => false,
                null or "" or "toggle" => !current,
                _ => current,
            };

            if (setTo != null && setTo != "toggle" && newVal == current && setTo is not ("on" or "off" or "true" or "false" or "0" or "1"))
            {
                Svc.Chat.PrintError($"[YABOT] Unknown value '{setTo}'. Use on/off/toggle.");
                return;
            }

            field.SetValue(Config, newVal);
            SaveConfig(Config);

            Svc.Chat.Print($"[YABOT] Don't Forget - {field.Name}: {(newVal ? "ON" : "OFF")}");
        }

        private void SwitchToGearset(byte classJobId)
        {
            var module = RaptureGearsetModule.Instance();
            for (var i = 0; i < 100; i++)
            {
                var gs = module->GetGearset(i);
                if (gs == null) continue;
                if (!gs->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                if (gs->ClassJob == classJobId)
                {
                    Svc.Log.Info($"[YABOT][DontForget] Switching to gearset {i + 1} for class {classJobId}");
                    module->EquipGearset(i);
                    return;
                }
            }
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (ImGui.Checkbox("Phys Ranged - Auto Peloton", ref Config.Peloton)) hasChanged = true;
            if (ImGui.Checkbox("Auto Sprint", ref Config.AutoSprint)) hasChanged = true;

            if (ImGui.Checkbox("Auto Gysahl Greens (< 15 min)", ref Config.AutoGysahlGreens)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Automatically use Gysahl Greens when your chocobo\ncompanion's timer falls below 15 minutes.");

            if (ImGui.Checkbox("Scholar - Summon Fairy", ref Config.Scholar)) hasChanged = true;
            if (ImGui.Checkbox("Summoner - Summon Carbuncle", ref Config.Summoner)) hasChanged = true;

            if (ImGui.Checkbox("Tank - Auto Tank Stance", ref Config.TankStance)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Automatically enables tank stance when standing still.");

            if (ImGui.Checkbox("Gatherer - Auto Buffs", ref Config.GatheringBuffs)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Auto Prospect/Triangulate, Sneak, and Truth of Mountains/Forests/Oceans\nwhen standing still on Miner, Botanist, or Fisher.");

            if (ImGui.Checkbox("Gatherer - Auto Switch Class", ref Config.AutoSwitchGatherer)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Switch to Miner or Botanist gearset when you try\nto gather from the wrong node type.");

            if (ImGui.Checkbox("Summon in Combat (After Death Only)", ref Config.SummonInCombatAfterDeath)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When enabled, pets auto-summon in combat only if\nyou were raised within the last 15 seconds.");

            ImGui.Text("Chocobo Stance");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            var idx = StanceIdToIndex(Config.ChocoboStanceOption);
            if (ImGui.Combo("##ChocoboStance", ref idx, StanceLabels, StanceLabels.Length))
            {
                Config.ChocoboStanceOption = StanceIds[idx];
                hasChanged = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Enforces the selected chocobo companion stance.\nReapplies after summon or death.");
        };
    }
}
