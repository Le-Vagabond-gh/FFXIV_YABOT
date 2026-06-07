using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YABOT.Features.DeepDungeons
{
    // Deep-dungeon-only auto-interrupt: when the current target is casting one of the watched spells
    // (default "Malice") and that cast is interruptible, fire Interject. Same insistent per-frame model
    // as the auto-heal feature - we re-attempt on every Framework.Update until the action goes on recast
    // (GetActionStatus != 0), so a GCD/animation lock just delays it rather than dropping it.
    public unsafe class DeepDungeonAutoInterject : BaseFeature
    {
        public override string Name => "Auto-Interject";

        public override string Description =>
            "Inside a deep dungeon, automatically uses Interject on your current target when it casts an interruptible spell whose name is in your watch list (default \"Malice\"). Retried every frame until it lands, so an animation lock just delays it. Requires Interject - any tank job (or the tank role action) at level 18+.";

        public override FeatureType FeatureType => FeatureType.DeepDungeons;

        public class Configs : FeatureConfig
        {
            // Seeded with "Malice" on first run (see Enable), not here: a field initializer would be
            // re-appended to the saved list on every load (Newtonsoft populates the existing instance).
            public List<string> SpellNames = new();
        }

        public Configs Config { get; private set; } = null!;

        private const uint Interject = 7538; // tank role action - interrupts an interruptible cast

        // Anti-double-fire window: Interject is a long-cooldown oGCD with nothing in inventory to watch,
        // so we mirror the auto-heal regen-ability debounce rather than tracking an item count.
        private const double AttemptDebounce = 2.0;
        private DateTime _lastUse = DateTime.MinValue;

        private string _newSpell = string.Empty;

        public override void Enable()
        {
            Config = LoadConfig<Configs>();
            if (Config == null)
                Config = new Configs { SpellNames = { "Malice" } }; // first run default
            else
                DedupeSpellNames(); // clean up any duplicates from older saves
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        // Keep the watch list unique (case-insensitive) and free of blanks, preserving order.
        private void DedupeSpellNames()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Config.SpellNames.RemoveAll(s => string.IsNullOrWhiteSpace(s) || !seen.Add(s.Trim()));
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
                if (Svc.Objects.LocalPlayer is null) return;
                if (EventFramework.Instance()->GetInstanceContentDeepDungeon() == null) return; // only inside a deep dungeon
                if (Svc.Targets.Target is not { } target) return;

                var chara = (BattleChara*)target.Address;
                if (chara == null) return;

                ref var cast = ref chara->CastInfo;
                if (!cast.IsCasting || !cast.Interruptible) return;
                if (!CastMatches(cast.ActionId)) return;

                var now = DateTime.Now;
                if ((now - _lastUse).TotalSeconds < AttemptDebounce) return;

                var am = ActionManager.Instance();
                if (am->GetActionStatus(ActionType.Action, Interject) != 0) return; // on cooldown / unavailable

                // Only debounce on an accepted use; a rejection retries next frame.
                if (am->UseAction(ActionType.Action, Interject, target.GameObjectId))
                    _lastUse = now;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] update failed");
            }
        }

        private bool CastMatches(uint actionId)
        {
            if (Config.SpellNames.Count == 0) return false;
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (!sheet.TryGetRow(actionId, out var row)) return false;
            var name = row.Name.ToString();
            return Config.SpellNames.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase));
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            ImGui.TextDisabled("Interject is used on your target when it casts an interruptible spell named below.");

            int? toRemove = null;
            for (var i = 0; i < Config.SpellNames.Count; i++)
            {
                if (ImGuiComponents.IconButton($"##interject_rm_{i}", FontAwesomeIcon.TrashAlt))
                    toRemove = i;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove");
                ImGui.SameLine();
                ImGui.TextUnformatted(Config.SpellNames[i]);
            }
            if (toRemove.HasValue)
            {
                Config.SpellNames.RemoveAt(toRemove.Value);
                hasChanged = true;
            }

            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##interject_new", "Spell name (e.g. Malice)", ref _newSpell, 64);
            ImGui.SameLine();
            var trimmed = _newSpell.Trim();
            var canAdd = trimmed.Length > 0
                && !Config.SpellNames.Any(s => s.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (!canAdd) ImGui.BeginDisabled();
            if (ImGuiComponents.IconButton("##interject_add", FontAwesomeIcon.Plus))
            {
                Config.SpellNames.Add(trimmed);
                _newSpell = string.Empty;
                hasChanged = true;
            }
            if (!canAdd) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add spell");
        };
    }
}
