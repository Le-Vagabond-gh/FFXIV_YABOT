using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
    // Jobs holding Occult Libra reveal the target's elemental weakness first and then spend their
    // shared elemental cooldown on the spell that weakness boosts - see PlanElemental.
    public unsafe class AutoPhantomActions : BaseFeature
    {
        public override string Name => "Auto Phantom Actions";

        public override string Description =>
            "In Occult Crescent, automatically uses your phantom job's duty actions while in combat with an enemy targeted: " +
            "damage actions on cooldown, debuffs when the target isn't already afflicted, buffs when the buff isn't already active. " +
            "Heals and utility actions are never used, and neither are Occult Toad or Starfall - they are left for you " +
            "to place manually. Zeninage is only used on bosses so it doesn't spend your Occult Coffers on trash.\n\n" +
            "Occult Libra is cast first to reveal the target's elemental weakness, and the elemental spell matching that " +
            "weakness is then the one used - taking priority over whichever action sits on the panel's main button.";

        public override FeatureType FeatureType => FeatureType.OccultCrescent;
        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Use self-Doom Necromancer spells (GNB)", HelpText =
                "Deep Freeze, Hell Wind, Chaos Drive and Doomsday afflict you with Doom, which is only " +
                "cleansed by getting healed back to full HP within 10 seconds.\n\n" +
                "With this enabled they are used when you are above 95% HP and a heal is guaranteed to " +
                "cover the Doom: either Aurora's regen is on you (Aurora is cast on you first when it " +
                "isn't), or Catharsis of Corundum will expire within the Doom window and its expiration " +
                "heal tops you back to full.\n\n" +
                "Heart of Corundum is also kept on cooldown so a Catharsis window cycles regularly.")]
            public bool UseDoomSpellsWithAurora = false;
        }

        public Configs Config { get; private set; } = null!;

        private enum Kind
        {
            Damage,      // fire on cooldown at the current enemy target
            Debuff,      // fire unless the target already has one of the statuses
            Buff,        // fire unless the player already has one of the statuses
        }

        // NotOnBosses: skipped against boss-rank targets (level "??"), where the action's effect
        // (instant kill, %HP damage, hard crowd control) doesn't work "with some exceptions".
        // BossesOnly: the inverse - the action costs a limited resource, so it's held for
        // boss-rank targets instead of being spent on trash.
        // SelfDoom: afflicts the caster with Doom (fatal unless healed back to full HP within
        // 10s) - only used with the Aurora config option, while Aurora's regen is on the player.
        private readonly record struct Entry(Kind Kind, uint[] Statuses, bool NotOnBosses = false, bool BossesOnly = false, bool SelfDoom = false);

        private static readonly uint[] None = Array.Empty<uint>();

        // Keyed by action ID. Statuses = blocking statuses (on target for debuffs, on self for buffs).
        private static readonly Dictionary<uint, Entry> Actions = new()
        {
            // --- Damage: Phantom Berserker / Monk / Samurai / Time Mage ---
            [41592] = new(Kind.Damage, None), // Rage (cone + grants Pent-up Rage)
            [41594] = new(Kind.Damage, None), // Deadly Blow
            [41595] = new(Kind.Damage, None), // Phantom Kick
            [41596] = new(Kind.Damage, None), // Occult Counter (only ready right after a parry)
            [41605] = new(Kind.Damage, None), // Iainuki (kill proc fizzles on bosses, potency still applies)
            [41606] = new(Kind.Damage, None, BossesOnly: true), // Zeninage (consumes an Occult Coffer per use - held for bosses)
            [41623] = new(Kind.Damage, None), // Occult Comet (8s cast)
            // --- Damage: Phantom Cannoneer ---
            [41626] = new(Kind.Damage, None), // Phantom Fire (kill proc fizzles on bosses, potency still applies)
            [41627] = new(Kind.Damage, None), // Holy Cannon
            [41628] = new(Kind.Damage, None), // Dark Cannon
            [41629] = new(Kind.Damage, None), // Shock Cannon
            [41630] = new(Kind.Damage, None), // Silver Cannon
            // --- Damage: Phantom Oracle (Predict cycle; Blessing excluded - pure heal) ---
            [41636] = new(Kind.Damage, None), // Predict
            [41637] = new(Kind.Damage, None), // Phantom Judgment
            [41638] = new(Kind.Damage, None), // Cleansing
            // Starfall (41640) deliberately excluded: costs up to 90% of your own max HP, too
            // expensive to spend on trash - left for manual use.
            // --- Damage: Phantom Mystic Knight / Gladiator ---
            [46591] = new(Kind.Damage, None), // Sundering Spellblade
            [46592] = new(Kind.Damage, None), // Holy Spellblade
            [46593] = new(Kind.Damage, None), // Blazing Spellblade
            [46594] = new(Kind.Damage, None), // Finisher (kill proc fizzles on bosses, potency still applies)
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
            // SelfDoom spells: too dangerous to fire unconditionally; only used with the Aurora
            // config option, while Aurora's regen guarantees getting healed back to full.
            [49098] = new(Kind.Damage, None, SelfDoom: true), // Deep Freeze
            [49099] = new(Kind.Damage, None, SelfDoom: true), // Hell Wind
            [49100] = new(Kind.Damage, None, SelfDoom: true), // Chaos Drive
            [49101] = new(Kind.Damage, None, SelfDoom: true), // Doomsday (consumes 10% max HP; dispel bonus fizzles on bosses)

            // --- Debuffs (blocked while the target already has the status, from any source) ---
            [41621] = new(Kind.Debuff, new uint[] { 427, 1568 }, NotOnBosses: true), // Occult Slowga -> Slow+
            [41624] = new(Kind.Debuff, new uint[] { 4259 }),      // Occult Mage Masher
            [41649] = new(Kind.Debuff, new uint[] { 4279 }),      // Pilfer Weapon -> Weapon Pilfered
            [46605] = new(Kind.Debuff, new uint[] { 4802 }, NotOnBosses: true), // Mesmerize -> Mesmerized
            // Occult Toad (49075) deliberately excluded: automating it means it goes off on every
            // piece of trash, while it's only worth using on select targets - left for manual use.
            // Occult Libra applies whichever of the four elemental weaknesses the target actually has,
            // so gate on all of them - that also picks up a Libra cast by anyone else in the party.
            [49094] = new(Kind.Debuff, new uint[] { 5322, 5323, 5324, 5325 }), // Fire/Ice/Lightning/Wind Weakness

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

        // Aurora (GNB, action 16151): heal over time that restores the player to full HP, which
        // is what dispels the self-inflicted Doom. Status 1835 is the standard buff, 2065 an
        // alternate id the game uses in some contexts.
        private const uint AuroraActionId = 16151;
        private static readonly uint[] AuroraStatuses = { 1835, 2065 };

        // Catharsis of Corundum (GNB, from Heart of Corundum): heals ~30% max HP when the effect
        // expires (or when HP drops below 50%). If it expires within the Doom window, that heal
        // tops the player back to full and cleanses the Doom, so it covers a Doom spell the same
        // way Aurora does. One second of safety margin against Doom's 10s timer.
        private static readonly uint[] CatharsisStatuses = { 2685, 4296 };
        private const float DoomDurationSeconds = 10f;

        private static bool CatharsisCoversDoom(IPlayerCharacter player) =>
            player.StatusList.Any(s => CatharsisStatuses.Contains(s.StatusId)
                                       && s.RemainingTime > 0
                                       && s.RemainingTime <= DoomDurationSeconds - 1);

        // Heart of Corundum (GNB, action 25758) grants Catharsis of Corundum; kept on cooldown
        // (25s recast, 20s Catharsis) while the self-Doom option is active so a Catharsis expiry
        // window cycles regularly.
        private const uint HeartOfCorundumActionId = 25758;

        private static bool HasSelfDoomDutyAction(DutyActionManager* dam, int slots)
        {
            for (var i = 0; i < slots; i++)
                if (Actions.TryGetValue(dam->ActionId[i], out var entry) && entry.SelfDoom)
                    return true;
            return false;
        }

        // The weakness statuses above are the primary gate; this timer is a second one, kept for two
        // cases the status check can't cover. The status takes a server round-trip to land, which can
        // outrun the 700ms use debounce and let a second cast slip out; and if an enemy ever turns out
        // to have no elemental affinity at all, no status appears and the status check would never
        // block. Threshold sits slightly under Libra's 120s duration.
        private const uint LibraActionId = 49094;
        private const double LibraReapplySeconds = 110;
        private readonly Dictionary<ulong, DateTime> libraApplied = new();
        private bool wasInCombat;

        // Libra's weakness statuses, mapped to the element ids the Action sheet's Aspect column
        // uses (1 fire, 2 ice, 3 wind, 5 lightning). Matching a revealed weakness to a spell goes
        // through these instead of a hardcoded per-job spell list, so it covers every phantom job
        // with aspected spells - red mage casts its own Libra, black mage / summoner / necromancer
        // pick up a weakness applied by anyone else in the party.
        private static readonly Dictionary<uint, byte> WeaknessAspects = new()
        {
            [5322] = 1, // Fire Weakness
            [5323] = 2, // Ice Weakness
            [5324] = 5, // Lightning Weakness
            [5325] = 3, // Wind Weakness
        };

        private static bool IsWeaknessAspect(byte aspect) => WeaknessAspects.ContainsValue(aspect);

        private static byte WeaknessAspect(IBattleNpc target)
        {
            foreach (var status in target.StatusList)
                if (WeaknessAspects.TryGetValue(status.StatusId, out var aspect))
                    return aspect;
            return 0;
        }

        private static bool HasSlotAction(DutyActionManager* dam, int slots, uint actionId)
        {
            for (var i = 0; i < slots; i++)
                if (dam->ActionId[i] == actionId) return true;
            return false;
        }

        // How long to wait for the weakness status after Libra was used, before concluding the
        // target has no elemental affinity at all. Covers the server round-trip.
        private const double LibraPendingSeconds = 2;

        // Priority = the action that must win this tick regardless of the panel's main button;
        // HoldAspected = don't spend the shared elemental cooldown yet.
        private readonly record struct ElementalPlan(uint Priority, bool HoldAspected);

        // A phantom job's elemental spells all share one cooldown group (red mage's Fire/Blizzard/
        // Thunder II are group 83, summoner's Hellfire/Judgment Bolt/Thunderstorm group 85), so
        // only one of them goes off per cycle and it should be the one the weakness boosts. While
        // the weakness is still unknown Libra goes first, and the aspected spells hold so the
        // cooldown isn't burned on an unboosted element - unless Libra already landed and nothing
        // came back, which means the target has no affinity and the hold would block damage
        // forever.
        private ElementalPlan PlanElemental(DutyActionManager* dam, int slots, IBattleNpc target, DateTime now)
        {
            var aspect = WeaknessAspect(target);

            if (aspect == 0)
            {
                if (!HasSlotAction(dam, slots, LibraActionId)) return default;
                var pending = !libraApplied.TryGetValue(target.GameObjectId, out var applied)
                              || (now - applied).TotalSeconds < LibraPendingSeconds;
                return new ElementalPlan(LibraActionId, pending);
            }

            for (var i = 0; i < slots; i++)
            {
                var id = dam->ActionId[i];
                if (id != 0 && Actions.ContainsKey(id) && GetSheetInfo(id).Aspect == aspect)
                    return new ElementalPlan(id, false);
            }
            return default; // weakness revealed, but no spell of that element slotted
        }

        // Sheet data resolved once per action (cast time for the moving check, range semantics,
        // icon for matching the duty action panel's buttons, name for logging, cooldown group
        // for shared-cooldown handling, aspect for elemental weakness matching).
        private readonly record struct SheetInfo(bool HasCastTime, bool CanTargetHostile, byte EffectRange, ushort Icon, string Name, byte CooldownGroup, byte Aspect);
        private readonly Dictionary<uint, SheetInfo> sheetCache = new();

        // Attempt throttle + post-use debounce so we don't spam UseAction every frame.
        private static readonly TimeSpan AttemptInterval = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan UseDebounce = TimeSpan.FromMilliseconds(700);
        private DateTime nextAttempt = DateTime.MinValue;

        // Last main-button action id written to the debug log.
        private uint lastLoggedSelected = uint.MaxValue;

        // Priority order: the elemental plan's action (Libra, or the weakness-matching spell) ahead
        // of everything, then the panel's main-button action, then the remaining slots. Within a
        // shared-cooldown group this makes the rotated-in action win - once it fires, the whole
        // group is on cooldown, so the others never race it.
        private static int BuildOrder(DutyActionManager* dam, int slots, uint selected, uint priority, Span<uint> order)
        {
            var count = 0;
            if (priority != 0) order[count++] = priority;
            if (selected != 0 && selected != priority) order[count++] = selected;
            for (var i = 0; i < slots; i++)
            {
                var id = dam->ActionId[i];
                if (id != 0 && id != selected && id != priority) order[count++] = id;
            }
            return count;
        }

        // GetActionStatus is unreliable for duty actions: it can stay 582 while the action is off
        // cooldown and perfectly usable. The duty action system tracks the real cooldown in
        // DutyActionManager's recast entries; those aren't reliably parallel to the action slots,
        // so match them by the ActionId each RecastDetail records instead of by slot position.
        // A recast entry only records the group member that was actually cast (verified in-game:
        // casting Thunderstorm leaves Judgment Bolt/Hellfire without an entry), so an active
        // entry also covers every action sharing its sheet cooldown group.
        private bool IsOnCooldown(DutyActionManager* dam, uint actionId)
        {
            var group = GetSheetInfo(actionId).CooldownGroup;
            for (var r = 0; r < 5; r++)
            {
                ref var rc = ref dam->Recast[r];
                if (!rc.IsActive || rc.ActionId == 0 || rc.Elapsed >= rc.Total) continue;
                if (rc.ActionId == actionId) return true;
                if (group != 0 && GetSheetInfo(rc.ActionId).CooldownGroup == group) return true;
            }
            return false;
        }

        // Within a shared-cooldown group only one member is ever attempted, to avoid the
        // action-queue race described at the call site. The representative is the panel's
        // main-button action when it belongs to the group (the player's rotation choice wins),
        // otherwise the first group member in slot order. Groups are independent per pair -
        // e.g. cannoneer has Holy+Silver in one group and Dark+Shock in another, so the pair
        // without the main button still fires through its first slot. Group numbers are reused
        // across jobs, so only the current slots are compared, never the whole action table.
        private bool IsGroupRepresentative(DutyActionManager* dam, int slots, uint actionId, uint selected, uint priority)
        {
            var group = GetSheetInfo(actionId).CooldownGroup;
            if (group == 0) return true;
            // The weakness-matching spell outranks the main button: the group only fires once per
            // cooldown either way, and the boosted element is always the better spend.
            if (priority != 0 && GetSheetInfo(priority).CooldownGroup == group)
                return actionId == priority;
            if (selected != 0 && GetSheetInfo(selected).CooldownGroup == group)
                return actionId == selected;
            for (var i = 0; i < slots; i++)
            {
                var id = dam->ActionId[i];
                if (id != 0 && GetSheetInfo(id).CooldownGroup == group)
                    return id == actionId;
            }
            return true;
        }

        // Action name for log messages (falls back to the raw id for unknown rows).
        private string ActionName(uint actionId) => actionId == 0 ? "none" : GetSheetInfo(actionId).Name;

        // The duty action panel (_ActionContents) shows one enlarged "main" button the player can
        // rotate actions into. In Occult Crescent the panel is the subtree under res node 16: one
        // button res node per action (IDs 24/26/28/30/32), each wrapping a DragDrop component with
        // the action's icon - the main button's res node is at scale 1.0, the small ones at ~0.7,
        // locked/hidden ones lose their Visible flag. Nothing else tracks the rotation
        // (AgentMKDSupportJob's SelectedAction and DutyActionManager.GetDutyActionId(0) both
        // don't), so the selection is read straight off the addon: the visible DragDrop whose
        // icon matches a duty action and whose accumulated scale is largest is the main button.
        // Visibility must be checked up the whole parent chain: the regular two-slot duty action
        // layout (subtree under res node 2) also contains DragDrop nodes at scale 1.0 and is
        // merely invisible while the phantom layout is shown.
        private uint GetSelectedAction(DutyActionManager* dam, int slots)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("_ActionContents").Address;
            if (addon == null || !addon->IsVisible) return 0;

            // Icon -> action for the current duty actions (each action has a distinct icon).
            Span<uint> candidateIds = stackalloc uint[5];
            Span<ushort> candidateIcons = stackalloc ushort[5];
            var candidates = 0;
            for (var s = 0; s < slots; s++)
            {
                var id = dam->ActionId[s];
                if (id == 0) continue;
                var icon = GetSheetInfo(id).Icon;
                if (icon == 0) continue;
                candidateIds[candidates] = id;
                candidateIcons[candidates] = icon;
                candidates++;
            }
            if (candidates == 0) return 0;

            var bestScale = 0f;
            var bestAction = 0u;
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null || (ushort)node->Type < 1000) continue; // components only

                var component = ((AtkComponentNode*)node)->Component;
                if (component == null) continue;
                var objectInfo = (AtkUldComponentInfo*)component->UldManager.Objects;
                if (objectInfo == null || objectInfo->ComponentType != ComponentType.DragDrop) continue;

                var iconComponent = ((AtkComponentDragDrop*)component)->AtkComponentIcon;
                if (iconComponent == null) continue;

                var actionId = 0u;
                for (var c = 0; c < candidates; c++)
                {
                    if (candidateIcons[c] == iconComponent->IconId)
                    {
                        actionId = candidateIds[c];
                        break;
                    }
                }
                if (actionId == 0) continue;

                // The per-button res node carries the 0.7/1.0 scale; accumulating up the parent
                // chain picks it up regardless of how deep the DragDrop node is nested under it,
                // and any invisible ancestor disqualifies the button (hidden layout/locked slot).
                var scale = 1f;
                var visible = true;
                for (var n = (AtkResNode*)node; n != null; n = n->ParentNode)
                {
                    if ((n->NodeFlags & NodeFlags.Visible) == 0)
                    {
                        visible = false;
                        break;
                    }
                    scale *= n->ScaleX;
                }
                if (!visible) continue;

                if (scale > bestScale)
                {
                    bestScale = scale;
                    bestAction = actionId;
                }
            }

            return bestAction;
        }

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            libraApplied.Clear();
            wasInCombat = false;
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
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

                // Libra's per-target timers only mean anything within the fight they were recorded in,
                // so drop them when combat ends. Keeps the map from growing all session and stops a
                // recycled GameObjectId from inheriting a stale timestamp. Checked ahead of the zone
                // gate so leaving Occult Crescent mid-session doesn't strand the entries.
                var inCombat = Svc.Condition[ConditionFlag.InCombat];
                if (wasInCombat && !inCombat) libraApplied.Clear();
                wasInCombat = inCombat;

                if (!ZoneHelper.IsOccultCrescent()) return;
                if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return;

                var dam = DutyActionManager.GetInstanceIfReady();
                if (dam == null || !dam->ActionsPresent) return;

                var slots = Math.Min((int)dam->NumValidSlots, 5);

                // Try the panel's main-button action first so that within a shared-cooldown group
                // (e.g. the phantom summoner spells) the one the player rotated in wins - once it
                // fires, the whole group is on cooldown, so the others never race it.
                var selected = GetSelectedAction(dam, slots);
                if (selected != lastLoggedSelected)
                {
                    lastLoggedSelected = selected;
                    Log($"duty action panel main button: {ActionName(selected)}");
                }

                if (!inCombat) return;
                if (Svc.Condition[ConditionFlag.Mounted]) return;

                if (Svc.Objects.LocalPlayer is not { } player || player.IsDead || player.CurrentHp == 0) return;
                if (player.IsCasting) return; // can't use actions mid-cast

                // Only act with a live, targetable enemy selected. SubKind 5 = enemy battle NPC,
                // compared as byte to stay version-proof against enum member renames.
                if (Svc.Targets.Target is not IBattleNpc target) return;
                if ((byte)target.SubKind != 5) return;
                if (target.IsDead || target.CurrentHp == 0 || !target.IsTargetable) return;

                var am = ActionManager.Instance();

                // Keep Heart of Corundum rolling ahead of duty actions while the self-Doom
                // option is active and the phantom job actually has a self-Doom action, so
                // Catharsis of Corundum expiry windows keep cycling. GetActionStatus filters
                // out non-GNB players and the ability being on cooldown.
                if (Config.UseDoomSpellsWithAurora
                    && HasSelfDoomDutyAction(dam, slots)
                    && am->GetActionStatus(ActionType.Action, HeartOfCorundumActionId) == 0
                    && am->UseAction(ActionType.Action, HeartOfCorundumActionId, player.GameObjectId))
                {
                    Log("used Heart of Corundum");
                    nextAttempt = now + UseDebounce;
                    return;
                }

                var plan = PlanElemental(dam, slots, target, now);

                Span<uint> order = stackalloc uint[7];
                var count = BuildOrder(dam, slots, selected, plan.Priority, order);

                for (var i = 0; i < count; i++)
                {
                    var actionId = order[i];
                    if (!Actions.TryGetValue(actionId, out var entry)) continue;
                    if (IsOnCooldown(dam, actionId)) continue;

                    var info = GetSheetInfo(actionId);

                    // Hold aspected spells until Libra has revealed which element to spend the
                    // shared cooldown on (Libra itself is unaspected, so it still gets through).
                    if (plan.HoldAspected && IsWeaknessAspect(info.Aspect)) continue;

                    // A hardcast would be cancelled instantly while moving - skip until stationary.
                    if (info.HasCastTime && AgentMap.Instance()->IsPlayerMoving) continue;

                    if (!Eligible(entry, actionId, info, player, target)) continue;

                    // Within a shared-cooldown group, only ever attempt one member (see
                    // IsGroupRepresentative). Attempting the other members too (as the code
                    // originally did, in slot order) is what cast the wrong spell: an attempt
                    // landing in the game's action-queue window (~0.5s before the shared
                    // cooldown ends) queues THAT member, which then fires at ready ahead of
                    // the intended one. When the main button holds an action this feature
                    // never uses (e.g. Earthen Wall), its group is left alone for manual use.
                    if (!IsGroupRepresentative(dam, slots, actionId, selected, plan.Priority))
                        continue;

                    // Self-Doom spells only fire while Aurora's regen is on the player (opt-in),
                    // and only from 95%+ HP: the spell costs 10% max HP on top, and the Doom is
                    // only cleansed by getting back to FULL HP within 10s - from lower HP one
                    // Aurora may not make it. When Aurora isn't up, cast it on self first - the
                    // spell follows next tick. Checked last so Aurora is only burned when the
                    // spell would actually fire.
                    if (entry.SelfDoom)
                    {
                        if (!Config.UseDoomSpellsWithAurora) continue;
                        if (player.CurrentHp * 100u < player.MaxHp * 95u) continue;
                        if (!HasAnyStatus(player, AuroraStatuses) && !CatharsisCoversDoom(player))
                        {
                            // GetActionStatus filters out non-GNB players and spent charges.
                            if (am->GetActionStatus(ActionType.Action, AuroraActionId) != 0) continue;
                            if (am->UseAction(ActionType.Action, AuroraActionId, player.GameObjectId))
                            {
                                Log($"used Aurora ahead of {ActionName(actionId)}");
                                nextAttempt = now + UseDebounce;
                                return; // one action per tick
                            }
                            continue;
                        }
                    }

                    var targetId = entry.Kind == Kind.Buff ? player.GameObjectId : target.GameObjectId;
                    if (am->UseAction(ActionType.Action, actionId, targetId))
                    {
                        var tag = actionId == plan.Priority && actionId != LibraActionId ? " [weakness match]" : "";
                        Log($"used {ActionName(actionId)}{tag} (main button {ActionName(selected)})");
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
            if (entry.BossesOnly && !IsBoss(target)) return false;

            switch (entry.Kind)
            {
                case Kind.Buff:
                    return !HasAnyStatus(player, entry.Statuses);

                case Kind.Debuff:
                    if (HasAnyStatus(target, entry.Statuses)) return false;
                    // Libra additionally honours its own timer - see LibraReapplySeconds.
                    if (actionId == LibraActionId
                        && libraApplied.TryGetValue(target.GameObjectId, out var applied)
                        && (DateTime.Now - applied).TotalSeconds < LibraReapplySeconds)
                        return false;
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

            var info = new SheetInfo(false, true, 0, 0, actionId.ToString(), 0, 0);
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row))
                info = new SheetInfo(row.Cast100ms > 0, row.CanTargetHostile, row.EffectRange, row.Icon, row.Name.ExtractText(), row.CooldownGroup, row.Aspect);

            sheetCache[actionId] = info;
            return info;
        }
    }
}
