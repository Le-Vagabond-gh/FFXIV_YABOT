using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YABOT.Features.OccultCrescent
{
    // Auto-fires phantom job duty actions while in combat in Occult Crescent. The action list is
    // hardcoded (OC-specific, the set never grows outside patches): damage actions on cooldown,
    // debuffs when the target doesn't already carry the debuff, buffs when the buff isn't already
    // active on the player. Heals, movement and out-of-combat utility are deliberately excluded.
    public unsafe class AutoPhantomActions : BaseFeature
    {
        public override string Name => "Auto Phantom Actions";

        public override string Description =>
            "In Occult Crescent, automatically uses your phantom job's duty actions while in combat with an enemy targeted: " +
            "damage actions on cooldown, debuffs when the target isn't already afflicted, buffs when the buff isn't already active. " +
            "Heals and utility actions are never used.";

        public override FeatureType FeatureType => FeatureType.OccultCrescent;

        private enum Kind
        {
            Damage,      // fire on cooldown at the current enemy target
            Debuff,      // fire unless the target already has one of the statuses
            Buff,        // fire unless the player already has one of the statuses
        }

        // NotOnBosses: skipped against boss-rank targets (level "??"), where the action's effect
        // (instant kill, %HP damage, hard crowd control) doesn't work "with some exceptions".
        private readonly record struct Entry(Kind Kind, uint[] Statuses, bool NotOnBosses = false);

        private static readonly uint[] None = Array.Empty<uint>();

        // Keyed by action ID. Statuses = blocking statuses (on target for debuffs, on self for buffs).
        private static readonly Dictionary<uint, Entry> Actions = new()
        {
            // --- Damage: Phantom Berserker / Monk / Samurai / Time Mage ---
            [41592] = new(Kind.Damage, None), // Rage (cone + grants Pent-up Rage)
            [41594] = new(Kind.Damage, None), // Deadly Blow
            [41595] = new(Kind.Damage, None), // Phantom Kick
            [41596] = new(Kind.Damage, None), // Occult Counter (only ready right after a parry)
            [41605] = new(Kind.Damage, None, NotOnBosses: true), // Iainuki (10% instant kill)
            [41623] = new(Kind.Damage, None), // Occult Comet (8s cast)
            // --- Damage: Phantom Cannoneer ---
            [41626] = new(Kind.Damage, None, NotOnBosses: true), // Phantom Fire (5% instant kill)
            [41627] = new(Kind.Damage, None), // Holy Cannon
            [41628] = new(Kind.Damage, None), // Dark Cannon
            [41629] = new(Kind.Damage, None), // Shock Cannon
            [41630] = new(Kind.Damage, None), // Silver Cannon
            // --- Damage: Phantom Oracle (Predict cycle; Blessing excluded - pure heal) ---
            [41636] = new(Kind.Damage, None), // Predict
            [41637] = new(Kind.Damage, None), // Phantom Judgment
            [41638] = new(Kind.Damage, None), // Cleansing
            [41640] = new(Kind.Damage, None), // Starfall (hits self for up to 90% max HP)
            // --- Damage: Phantom Mystic Knight / Gladiator ---
            [46591] = new(Kind.Damage, None), // Sundering Spellblade
            [46592] = new(Kind.Damage, None), // Holy Spellblade
            [46593] = new(Kind.Damage, None), // Blazing Spellblade
            [46594] = new(Kind.Damage, None, NotOnBosses: true), // Finisher (25% instant kill)
            [46596] = new(Kind.Damage, None), // Long Reach
            [46597] = new(Kind.Damage, None), // Bladeblitz
            // --- Damage: Phantom Dancer (Dance cycle) ---
            [46598] = new(Kind.Damage, None), // Dance
            [46599] = new(Kind.Damage, None), // Phantom Sword Dance
            [46600] = new(Kind.Damage, None), // Tempting Tango
            [46601] = new(Kind.Damage, None), // Jitterbug
            [46602] = new(Kind.Damage, None), // Mystery Waltz
            // --- Damage: Phantom Ninja / White Mage / Black Mage ---
            [49062] = new(Kind.Damage, None), // Fuma Shuriken
            [49064] = new(Kind.Damage, None), // Lightning Scroll
            [49065] = new(Kind.Damage, None), // Flame Scroll
            [49071] = new(Kind.Damage, None), // Occult Holy
            [49072] = new(Kind.Damage, None), // Occult Fire III
            [49073] = new(Kind.Damage, None), // Occult Blizzard III
            [49074] = new(Kind.Damage, None), // Occult Thunder III
            [49076] = new(Kind.Damage, None), // Occult Flare
            // --- Damage: Phantom Dragoon / Summoner ---
            [49077] = new(Kind.Damage, None), // Occult Jump (leaps to the target)
            [49079] = new(Kind.Damage, None), // Lance
            [49080] = new(Kind.Damage, None), // Hellfire (4s cast)
            [49081] = new(Kind.Damage, None), // Judgment Bolt (4s cast)
            [49083] = new(Kind.Damage, None), // Thunderstorm (4s cast)
            [49084] = new(Kind.Damage, None), // Megaflare (6s cast)
            // --- Damage: Phantom Blue Mage / Red Mage / Necromancer ---
            [49085] = new(Kind.Damage, None), // Occult Aero
            [49089] = new(Kind.Damage, None), // Occult Aero II (replaces Occult Aero in the slot at higher job level)
            [49091] = new(Kind.Damage, None), // Occult Aero III (replaces Occult Aero II)
            [49086] = new(Kind.Damage, None, NotOnBosses: true), // Occult Missile (%HP damage)
            [49087] = new(Kind.Damage, None), // Occult Aqua Breath
            [49092] = new(Kind.Damage, None), // Occult Fire II
            [49095] = new(Kind.Damage, None), // Occult Blizzard II
            [49096] = new(Kind.Damage, None), // Occult Thunder II
            [49097] = new(Kind.Damage, None), // Drain Touch
            [49098] = new(Kind.Damage, None), // Deep Freeze
            [49099] = new(Kind.Damage, None), // Hell Wind
            [49100] = new(Kind.Damage, None), // Chaos Drive
            [49101] = new(Kind.Damage, None, NotOnBosses: true), // Doomsday (consumes 10% max HP)

            // --- Debuffs (blocked while the target already has the status, from any source) ---
            [41621] = new(Kind.Debuff, new uint[] { 427, 1568 }, NotOnBosses: true), // Occult Slowga -> Slow+
            [41624] = new(Kind.Debuff, new uint[] { 4259 }),      // Occult Mage Masher
            [41649] = new(Kind.Debuff, new uint[] { 4279 }),      // Pilfer Weapon -> Weapon Pilfered
            [46605] = new(Kind.Debuff, new uint[] { 4802 }, NotOnBosses: true), // Mesmerize -> Mesmerized
            [49075] = new(Kind.Debuff, new uint[] { 5317 }, NotOnBosses: true), // Occult Toad
            [49094] = new(Kind.Debuff, None),                     // Occult Libra (hidden status - internal timer below)

            // --- Buffs (blocked while the player already has the status, from any source) ---
            [41588] = new(Kind.Buff, new uint[] { 4231 }),        // Phantom Guard
            [41597] = new(Kind.Buff, new uint[] { 4238 }),        // Counterstance
            [41599] = new(Kind.Buff, new uint[] { 4240, 4241 }),  // Phantom Aim / Deadly Phantom Aim
            [41608] = new(Kind.Buff, new uint[] { 4247, 4249 }),  // Offensive Aria (blocked by Hero's Rime too - can't stack)
            [41610] = new(Kind.Buff, new uint[] { 4249, 4247 }),  // Hero's Rime (blocked by Offensive Aria too)
            [41611] = new(Kind.Buff, new uint[] { 4251 }),        // Battle Bell
            [41625] = new(Kind.Buff, new uint[] { 4260 }),        // Occult Quick
            [46590] = new(Kind.Buff, new uint[] { 4788 }),        // Magic Shell
            [46595] = new(Kind.Buff, new uint[] { 4792 }),        // Defend
            [46603] = new(Kind.Buff, new uint[] { 4798 }),        // Quickstep
            [46604] = new(Kind.Buff, new uint[] { 4800 }),        // Steadfast Stance
            [49063] = new(Kind.Buff, new uint[] { 5327 }),        // Smoke
            [49066] = new(Kind.Buff, new uint[] { 4873 }),        // Image
            // Occult Mighty Guard (49088) deliberately excluded: defensive cooldown, not worth burning automatically.
        };

        // Occult Libra applies no visible status, so reapplication is gated by a per-target timer
        // slightly under its 120s duration.
        private const uint LibraActionId = 49094;
        private const double LibraReapplySeconds = 110;
        private readonly Dictionary<ulong, DateTime> libraApplied = new();

        // Sheet data resolved once per action (cast time for the moving check, range semantics).
        private readonly record struct SheetInfo(bool HasCastTime, bool CanTargetHostile, byte EffectRange);
        private readonly Dictionary<uint, SheetInfo> sheetCache = new();

        // Attempt throttle + post-use debounce so we don't spam UseAction every frame.
        private static readonly TimeSpan AttemptInterval = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan UseDebounce = TimeSpan.FromMilliseconds(700);
        private DateTime nextAttempt = DateTime.MinValue;

        public override void Enable()
        {
            libraApplied.Clear();
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            base.Disable();
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                var now = DateTime.Now;
                if (now < nextAttempt) return;
                nextAttempt = now + AttemptInterval;

                if (!ZoneHelper.IsOccultCrescent()) return;
                if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return;
                if (!Svc.Condition[ConditionFlag.InCombat]) return;
                if (Svc.Condition[ConditionFlag.Mounted]) return;

                if (Svc.Objects.LocalPlayer is not { } player || player.IsDead || player.CurrentHp == 0) return;
                if (player.IsCasting) return; // can't use actions mid-cast

                // Only act with a live, targetable enemy selected. SubKind 5 = enemy battle NPC,
                // compared as byte to stay version-proof against enum member renames.
                if (Svc.Targets.Target is not IBattleNpc target) return;
                if ((byte)target.SubKind != 5) return;
                if (target.IsDead || target.CurrentHp == 0 || !target.IsTargetable) return;

                var dam = DutyActionManager.GetInstanceIfReady();
                if (dam == null || !dam->ActionsPresent) return;

                var am = ActionManager.Instance();
                var slots = Math.Min((int)dam->NumValidSlots, 5);

                for (var i = 0; i < slots; i++)
                {
                    var actionId = dam->ActionId[i];
                    if (actionId == 0 || !Actions.TryGetValue(actionId, out var entry)) continue;

                    // GetActionStatus is unreliable for duty actions: it can stay 582 while the
                    // action is off cooldown and perfectly usable. The duty action system tracks
                    // the real cooldown in DutyActionManager's recast entries; those aren't
                    // reliably parallel to the action slots, so match them by the ActionId each
                    // RecastDetail records instead of by slot position.
                    var onCooldown = false;
                    for (var r = 0; r < 5; r++)
                    {
                        ref var rc = ref dam->Recast[r];
                        if (rc.ActionId == actionId && rc.IsActive && rc.Elapsed < rc.Total)
                        {
                            onCooldown = true;
                            break;
                        }
                    }
                    if (onCooldown) continue;

                    var info = GetSheetInfo(actionId);

                    // A hardcast would be cancelled instantly while moving - skip until stationary.
                    if (info.HasCastTime && AgentMap.Instance()->IsPlayerMoving) continue;

                    if (!Eligible(entry, actionId, info, player, target)) continue;

                    var targetId = entry.Kind == Kind.Buff ? player.GameObjectId : target.GameObjectId;
                    if (am->UseAction(ActionType.Action, actionId, targetId))
                    {
                        Log($"used {actionId}");
                        if (actionId == LibraActionId)
                            libraApplied[target.GameObjectId] = now;
                        nextAttempt = now + UseDebounce;
                        return; // one action per tick
                    }
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] update failed");
            }
        }

        // Boss detection matching the level "??" / "(c)??" targets. Occult Crescent bosses
        // (e.g. Regnant Chimera) carry BNpcBase rank 1 - normal field trash is rank 0; ranks
        // 2 (boss) and 6 (raid boss) are included for completeness.
        private static HashSet<uint>? bossBaseIds;

        private static bool IsBoss(IBattleNpc target)
        {
            bossBaseIds ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.BNpcBase>()
                .Where(x => x.Rank is 1 or 2 or 6)
                .Select(x => x.RowId)
                .ToHashSet();
            return bossBaseIds.Contains(target.BaseId);
        }

        private bool Eligible(Entry entry, uint actionId, SheetInfo info, IPlayerCharacter player, IBattleNpc target)
        {
            if (entry.NotOnBosses && IsBoss(target)) return false;

            switch (entry.Kind)
            {
                case Kind.Buff:
                    return !HasAnyStatus(player, entry.Statuses);

                case Kind.Debuff:
                    if (actionId == LibraActionId)
                    {
                        if (libraApplied.TryGetValue(target.GameObjectId, out var applied)
                            && (DateTime.Now - applied).TotalSeconds < LibraReapplySeconds)
                            return false;
                    }
                    else if (HasAnyStatus(target, entry.Statuses))
                    {
                        return false;
                    }
                    return InRange(actionId, player, target);

                case Kind.Damage:
                    if (info.CanTargetHostile)
                        return InRange(actionId, player, target);
                    // Self-centered attack (cone/point-blank AoE): require the target inside the
                    // effect radius so it doesn't whiff. Radius 0 = enabler action (Rage, Predict,
                    // Dance) - no range requirement.
                    if (info.EffectRange > 0)
                        return Vector3.Distance(player.Position, target.Position) <= info.EffectRange;
                    return true;

                default:
                    return false;
            }
        }

        private static bool InRange(uint actionId, IGameObject source, IGameObject target)
        {
            return ActionManager.GetActionInRangeOrLoS(
                actionId,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)source.Address,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address) == 0;
        }

        private static bool HasAnyStatus(IGameObject obj, uint[] statusIds)
        {
            if (statusIds.Length == 0 || obj is not IBattleChara chara) return false;
            return chara.StatusList.Any(s => statusIds.Contains(s.StatusId));
        }

        private SheetInfo GetSheetInfo(uint actionId)
        {
            if (sheetCache.TryGetValue(actionId, out var cached)) return cached;

            var info = new SheetInfo(false, true, 0);
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row))
                info = new SheetInfo(row.Cast100ms > 0, row.CanTargetHostile, row.EffectRange);

            sheetCache[actionId] = info;
            return info;
        }
    }
}
