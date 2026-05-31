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
            "Inside a deep dungeon, automatically drinks the dungeon's HP potion (Max-Potion in PotD, Super-Potion in HoH) when HP drops below a threshold, drinks the regen potion (Sustaining Potion / Empyrean Potion) below a higher threshold, and - on jobs that have one - keeps a self-targeted regen ability up (Gunbreaker's Aurora, Warrior's Equilibrium). Each use is retried every frame until it goes through, so a GCD or animation lock just delays it rather than skipping it.";

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
        // 1 = Palace of the Dead, 2 = Heaven-on-High. Eureka Orthos (3) ships no equivalent
        // consumables, so potions are simply skipped there (the regen ability still runs).
        private const uint MaxPotion = 13637;        // PotD - instant HP
        private const uint SustainingPotion = 20309; // PotD - Rehabilitation regen
        private const uint SuperPotion = 23167;      // HoH  - instant HP
        private const uint EmpyreanPotion = 23163;   // HoH  - Rehabilitation regen

        // Self-regen oGCDs that grant a maintainable HoT buff, keyed by ClassJob row id.
        // Both PotD/HoH potions and these abilities are fire-and-forget survivability.
        private static readonly (uint Job, uint Action, uint Status)[] RegenAbilities =
        {
            (37, 16151, 1835), // Gunbreaker - Aurora     -> Aurora
            (21, 3552, 2681),  // Warrior    - Equilibrium -> Equilibrium
        };

        // Both regen potions grant "Rehabilitation" for 30s. The status sheet reuses that name across
        // many potency tiers (different ids per item level), so we gate on the name rather than a
        // single id - and pair it with a 30s use-debounce as a locale-proof fallback that also avoids
        // re-drinking over a manual one.
        private const double RegenBuffSeconds = 30.0;
        private const string RehabilitationStatus = "Rehabilitation";

        // Short anti-double-fire window: GetActionStatus keeps reading 0 for a frame or two after
        // UseAction before the recast registers, so without this we could fire twice in that gap.
        private const double AttemptDebounce = 2.0;

        private DateTime _lastHpPotUse = DateTime.MinValue;
        private DateTime _lastRegenPotUse = DateTime.MinValue;
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
                    TryUseItem(am, pots.Value.Heal, ref _lastHpPotUse, AttemptDebounce, now);

                // Regen potion under the higher threshold, unless Rehabilitation is already ticking.
                if (Config.UseRegenPotion && pots.HasValue && hpPct < Config.RegenPotionThreshold
                    && (now - _lastRegenPotUse).TotalSeconds >= RegenBuffSeconds
                    && !PlayerHasStatusNamed(player, RehabilitationStatus))
                    TryUseItem(am, pots.Value.Regen, ref _lastRegenPotUse, RegenBuffSeconds, now);

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
            _ => null,
        };

        // Fire an item if it's usable right now and we're past our debounce. GetActionStatus == 0
        // means the game *should* accept it, but it can still reject the use (e.g. mid-GCD /
        // animation lock); UseAction returns false in that case. We only stamp the debounce timer on
        // an accepted use - a rejection leaves it untouched so we re-attempt next frame instead of
        // locking ourselves out for the whole debounce window while still under threshold.
        private static void TryUseItem(ActionManager* am, uint itemId, ref DateTime lastUse, double debounce, DateTime now)
        {
            if ((now - lastUse).TotalSeconds < debounce) return;
            if (am->GetActionStatus(ActionType.Item, itemId) != 0) return;

            if (am->UseAction(ActionType.Item, itemId, 0xE0000000, 0xFFFF))
                lastUse = now;
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
                ImGui.SetTooltip("Drink Max-Potion (Palace of the Dead) / Super-Potion (Heaven-on-High)\nwhen HP drops below the threshold below.");
            ImGui.Indent();
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("below this HP %##hppot", ref Config.HpPotionThreshold, 1, 99)) hasChanged = true;
            ImGui.Unindent();

            if (ImGui.Checkbox("Auto-use regen potion", ref Config.UseRegenPotion)) hasChanged = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drink Sustaining Potion (PotD) / Empyrean Potion (HoH) when HP drops\nbelow the threshold, unless the Rehabilitation regen is already active.");
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
