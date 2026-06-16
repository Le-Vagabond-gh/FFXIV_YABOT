using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using YABOT.FeaturesSetup;
using System;
using System.Linq;

namespace YABOT.Features.DeepDungeons
{
    // Deep dungeon survival autopilot: drinks the dungeon-specific HP and regen potions on low HP,
    // and keeps a job's self-regen oGCD (GNB Aurora / WAR Equilibrium) up. Every action is gated on
    // ActionManager.GetActionStatus, which is non-zero while a GCD/animation lock or recast blocks it,
    // so the per-frame Framework.Update loop is itself the "retry until it goes through" mechanism:
    // we simply re-attempt next frame until the game lets it through.
    public unsafe class DeepDungeonAutoHeal : BaseFeature
    {
        public override string Name => "Auto-Heal (Potions & Regen)";

        public override string Description =>
            "Inside a deep dungeon, automatically drinks the dungeon's HP potion (Max-Potion in PotD, Super-Potion in HoH, Hyper-Potion in Eureka Orthos, Ultra-Potion in Pilgrim's Traverse) when HP drops below a threshold, drinks the regen potion (Sustaining / Empyrean / Orthos / Pilgrim's Potion) below a higher threshold, and - on jobs that have one - keeps a self-targeted regen ability up (Gunbreaker's Aurora, Warrior's Equilibrium). Each use is retried every frame until it goes through, so a GCD or animation lock just delays it rather than skipping it.";

        public override FeatureType FeatureType => FeatureType.DeepDungeons;

        public class Configs : FeatureConfig
        {
            public bool UseHpPotion = true;
            public int HpPotionThreshold = 30;
            public bool UseRegenPotion = true;
            public int RegenPotionThreshold = 60;
            public bool UseRegenAbility = true;
        }

        public Configs Config { get; private set; } = null!;

        // Deep-dungeon-exclusive potions, keyed by DeepDungeon sheet row (dd->DeepDungeonId):
        // 1 = Palace of the Dead, 2 = Heaven-on-High, 3 = Eureka Orthos, 4 = Pilgrim's Traverse.
        // Quality is resolved at use time, so the plain (NQ) id is fine even for the HQ-only
        // Hyper-Potion / Ultra-Potion.
        private const uint MaxPotion = 13637;        // PotD - instant HP
        private const uint SustainingPotion = 20309; // PotD - Rehabilitation regen
        private const uint SuperPotion = 23167;      // HoH  - instant HP
        private const uint EmpyreanPotion = 23163;   // HoH  - Rehabilitation regen
        private const uint HyperPotion = 38956;      // EO   - instant HP
        private const uint OrthosPotion = 38944;     // EO   - Rehabilitation regen
        private const uint UltraPotion = 47701;      // PT   - instant HP
        private const uint PilgrimsPotion = 47102;   // PT   - Rehabilitation regen

        // Self-regen oGCDs that grant a maintainable HoT buff, keyed by ClassJob row id.
        // Both PotD/HoH potions and these abilities are fire-and-forget survivability.
        private static readonly (uint Job, uint Action, uint Status)[] RegenAbilities =
        {
            (37, 16151, 1835), // Gunbreaker - Aurora     -> Aurora
            (21, 3552, 2681),  // Warrior    - Equilibrium -> Equilibrium
        };

        // Both regen potions grant "Rehabilitation" for 30s. The status sheet reuses that name across
        // many potency tiers (different ids per item level), so we gate on the name rather than a
        // single id - that gate alone keeps us from re-drinking the regen while it's already ticking.
        private const string RehabilitationStatus = "Rehabilitation";

        // Anti-double-fire window for the regen *ability* (long-cooldown oGCD, no inventory to watch).
        private const double AttemptDebounce = 2.0;

        // Potions are fired insistently: we re-attempt every frame until the item actually goes on
        // recast (GetActionStatus != 0), which is the only solid proof the drink landed - UseAction can
        // return true and still be silently dropped ("it can not go through sometimes"), so we never
        // trust its return. The one thing we guard against is the 1-2 frame gap where the recast hasn't
        // registered yet: if our fire already dropped the inventory count we hold off for this short
        // window so we don't chug a second potion in that gap, then resume hammering if nothing landed.
        private const double PotionRetryGrace = 0.5;

        // Potions can be High Quality (the ones you stockpile/loot often are). The item and action APIs
        // address an HQ item as id + 1,000,000; ask for the plain NQ id while you only hold HQ and the
        // game answers "you do not have that item" (LogMessage 583) - which is exactly what we observed
        // for the HQ Super-Potion. So we resolve the quality we actually carry before using.
        private const uint HqOffset = 1_000_000;

        // Per-item in-flight tracking for the insistent retry above.
        private struct ItemUse
        {
            public int CountAtUse;   // inventory count when we last fired (0 = nothing in flight)
            public DateTime FiredAt;
        }

        private ItemUse _hpPot;
        private ItemUse _regenPot;
        private DateTime _lastAbilityUse = DateTime.MinValue;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
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
                if (Svc.Objects.LocalPlayer is not { } player) return;
                if (player.MaxHp == 0) return;

                var dd = EventFramework.Instance()->GetInstanceContentDeepDungeon();
                if (dd == null) return; // only ever act inside a deep dungeon

                var am = ActionManager.Instance();
                var now = DateTime.Now;
                var hpPct = 100f * player.CurrentHp / player.MaxHp;

                var pots = PotionsFor(dd->DeepDungeonId);

                // HP potion under the low threshold (independent item, fires regardless of combat).
                if (Config.UseHpPotion && pots.HasValue && hpPct < Config.HpPotionThreshold)
                    TryUseItem(am, pots.Value.Heal, ref _hpPot, now);

                // Regen potion under the higher threshold, unless Rehabilitation is already ticking.
                if (Config.UseRegenPotion && pots.HasValue && hpPct < Config.RegenPotionThreshold
                    && !PlayerHasStatusNamed(player, RehabilitationStatus))
                    TryUseItem(am, pots.Value.Regen, ref _regenPot, now);

                // Self-regen ability: keep it up while in combat (so charges aren't burned exploring).
                if (Config.UseRegenAbility && Svc.Condition[ConditionFlag.InCombat])
                    TryUseRegenAbility(am, player, now);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] update failed");
            }
        }

        private static (uint Heal, uint Regen)? PotionsFor(byte deepDungeonId) => deepDungeonId switch
        {
            1 => (MaxPotion, SustainingPotion),
            2 => (SuperPotion, EmpyreanPotion),
            3 => (HyperPotion, OrthosPotion),
            4 => (UltraPotion, PilgrimsPotion),
            _ => null,
        };

        // Drink an item, retrying every frame until it actually goes on recast. UseAction can return
        // true while the use is silently dropped, so the only signals we trust are GetActionStatus
        // flipping to non-zero (on recast = it landed) and the inventory count dropping. Everything
        // else just means "keep trying".
        private static void TryUseItem(ActionManager* am, uint itemId, ref ItemUse use, DateTime now)
        {
            var inv = InventoryManager.Instance();
            if (inv == null) return;

            // Resolve the quality we actually carry. Prefer NQ; fall back to HQ (addressed as id + 1M).
            // GetInventoryItemCount counts a single quality, so we have to ask for each.
            var nq = inv->GetInventoryItemCount(itemId, false);
            var hq = inv->GetInventoryItemCount(itemId, true);
            var useId = nq > 0 ? itemId : (hq > 0 ? itemId + HqOffset : 0);
            var count = nq > 0 ? nq : hq;
            if (useId == 0) return; // none in inventory - nothing to drink

            // On recast -> the drink landed. Reset and stop until the item is usable again.
            if (am->GetActionStatus(ActionType.Item, useId) != 0) { use.CountAtUse = 0; return; }

            // Our fire already dropped the count -> it landed; the recast just hasn't registered this
            // frame yet. Don't chug a second one in that gap.
            if (use.CountAtUse > 0 && count < use.CountAtUse) { use.CountAtUse = 0; return; }

            // Just fired and nothing has landed yet: give the queued use a brief moment to resolve so a
            // single use doesn't become two drinks. Past that with no count drop, it was dropped - retry.
            if (use.CountAtUse > 0 && (now - use.FiredAt).TotalSeconds < PotionRetryGrace) return;

            // Be insistent: fire (again) until it goes on recast. We only record the fire so the guards
            // above can run - we never treat the boolean as proof it worked.
            if (am->UseAction(ActionType.Item, useId, 0xE0000000, 0xFFFF))
            {
                use.CountAtUse = count;
                use.FiredAt = now;
            }
        }

        private void TryUseRegenAbility(ActionManager* am, Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, DateTime now)
        {
            foreach (var (job, action, status) in RegenAbilities)
            {
                if (player.ClassJob.RowId != job) continue;
                if (player.StatusList.Any(s => s.StatusId == status)) break; // already up
                if ((now - _lastAbilityUse).TotalSeconds < AttemptDebounce) break;
                if (am->GetActionStatus(ActionType.Action, action) != 0) break; // on cooldown / blocked

                // Only debounce on an accepted use; a rejection retries next frame.
                if (am->UseAction(ActionType.Action, action, player.GameObjectId)) // self-target
                    _lastAbilityUse = now;
                break;
            }
        }

        private static bool PlayerHasStatusNamed(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, string statusName)
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>();
            foreach (var s in player.StatusList)
            {
                if (s.StatusId == 0) continue;
                if (sheet.TryGetRow(s.StatusId, out var row)
                    && row.Name.ToString().Equals(statusName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (ImGui.Checkbox("Auto-use HP potion", ref Config.UseHpPotion)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drink Max-Potion (PotD) / Super-Potion (HoH) / Hyper-Potion (Eureka Orthos) /\nUltra-Potion (Pilgrim's Traverse) when HP drops below the threshold below.");
            ImGui.Indent();
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("below this HP %##hppot", ref Config.HpPotionThreshold, 1, 99)) hasChanged = true;
            ImGui.Unindent();

            if (ImGui.Checkbox("Auto-use regen potion", ref Config.UseRegenPotion)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drink Sustaining (PotD) / Empyrean (HoH) / Orthos (EO) / Pilgrim's Potion (PT)\nwhen HP drops below the threshold, unless the Rehabilitation regen is already active.");
            ImGui.Indent();
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("below this HP %##regenpot", ref Config.RegenPotionThreshold, 1, 99)) hasChanged = true;
            ImGui.Unindent();

            if (ImGui.Checkbox("Auto-use regen ability (Aurora / Equilibrium)", ref Config.UseRegenAbility)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Keep a self-regen oGCD up while in combat:\nGunbreaker's Aurora, Warrior's Equilibrium.\nCast on yourself whenever its buff isn't already active.");
        };
    }
}
