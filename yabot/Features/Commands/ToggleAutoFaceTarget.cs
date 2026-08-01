using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YABOT.Features.Commands
{
    public unsafe class ToggleAutoFaceTarget : CommandFeature
    {
        public override string Name => "Toggle Auto Face Target";
        public override string Command { get; set; } = "/yautofacetarget";
        public override string[] Alias => new[] { "/yaft" };
        public override string Description => "Toggles the 'Automatically face target when using action' game setting (and BossmodReborn's smart character orientation). Can also turn it off automatically while you are in combat with specific monsters, restoring it afterwards.";

        public class Configs : FeatureConfig
        {
            public bool AutoDisableInCombat = false;
            public List<string> MonsterNames = new();
        }

        public Configs Config { get; private set; } = null!;

        // Set while we have auto-disabled the setting; restored when no listed monster remains on the
        // enmity list. Only ever set when the setting was ON, so restoring always means turning it back on.
        private bool _suppressed;

        private string _newMonster = string.Empty;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            if (_suppressed)
            {
                Apply(true);
                _suppressed = false;
            }
            SaveConfig(Config);
            base.Disable();
        }

        protected override void OnCommand(List<string> args)
        {
            bool? newValue = args.Count > 0 ? args[0] switch
            {
                "on" => true,
                "off" => false,
                _ => null,
            } : null;

            if (newValue == null)
            {
                if (!Svc.GameConfig.TryGet(UiControlOption.AutoFaceTargetOnAction, out bool current))
                {
                    Svc.Chat.PrintError("[YABOT] Failed to read AutoFaceTargetOnAction setting.");
                    return;
                }
                newValue = !current;
            }

            // A manual toggle takes over from any pending auto-restore.
            _suppressed = false;

            Apply(newValue.Value);
        }

        private static void Apply(bool value)
        {
            Svc.GameConfig.Set(UiControlOption.AutoFaceTargetOnAction, value ? 1u : 0u);
            Svc.Chat.Print($"[YABOT] Auto face target: {(value ? "ON" : "OFF")}");

            ToggleBmrSmartOrientation(value);
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                if (!Config.AutoDisableInCombat || Config.MonsterNames.Count == 0)
                {
                    RestoreIfSuppressed();
                    return;
                }

                if (Svc.Objects.LocalPlayer is null) return;

                if (!EnmityListHasListedMonster())
                {
                    RestoreIfSuppressed();
                    return;
                }

                if (_suppressed) return;

                // Only suppress when the setting is currently ON - otherwise there is nothing to restore.
                if (Svc.GameConfig.TryGet(UiControlOption.AutoFaceTargetOnAction, out bool current) && current)
                {
                    Apply(false);
                    _suppressed = true;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] update failed");
            }
        }

        private void RestoreIfSuppressed()
        {
            if (!_suppressed) return;
            Apply(true);
            _suppressed = false;
        }

        private bool EnmityListHasListedMonster()
        {
            ref var hater = ref UIState.Instance()->Hater;
            var count = Math.Min(hater.HaterCount, hater.Haters.Length);
            for (var i = 0; i < count; i++)
            {
                var name = hater.Haters[i].NameString;
                if (Config.MonsterNames.Any(m => name.Equals(m, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (ImGui.Checkbox("Turn off while in combat with listed monsters", ref Config.AutoDisableInCombat))
                hasChanged = true;
            ImGui.TextDisabled("Turned off when a listed monster is on your enmity list, restored when none remain.");

            int? toRemove = null;
            for (var i = 0; i < Config.MonsterNames.Count; i++)
            {
                if (ImGuiComponents.IconButton($"##yaft_rm_{i}", FontAwesomeIcon.TrashAlt))
                    toRemove = i;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove");
                ImGui.SameLine();
                ImGui.TextUnformatted(Config.MonsterNames[i]);
            }
            if (toRemove.HasValue)
            {
                Config.MonsterNames.RemoveAt(toRemove.Value);
                hasChanged = true;
            }

            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##yaft_new", "Monster name", ref _newMonster, 64);
            ImGui.SameLine();
            var trimmed = _newMonster.Trim();
            var canAdd = trimmed.Length > 0
                && !Config.MonsterNames.Any(s => s.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (!canAdd) ImGui.BeginDisabled();
            if (ImGuiComponents.IconButton("##yaft_add", FontAwesomeIcon.Plus))
            {
                Config.MonsterNames.Add(trimmed);
                _newMonster = string.Empty;
                hasChanged = true;
            }
            if (!canAdd) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add monster");
        };

        // BossmodReborn's "Smart character orientation" replaces the base-game auto-face-target option,
        // so keep it in sync. BMR exposes a console-command IPC that sets a config field and fires its
        // Modified event - the same path the in-game checkbox uses.
        private static void ToggleBmrSmartOrientation(bool enabled)
        {
            try
            {
                ICallGateSubscriber<List<string>, bool, List<string>> cfg =
                    Svc.PluginInterface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration");

                cfg.InvokeFunc(new List<string> { "SmartRotationConfig", "Enabled", enabled ? "true" : "false" }, true);
                Svc.Chat.Print($"[YABOT] BossmodReborn smart orientation: {(enabled ? "ON" : "OFF")}");
            }
            catch (Exception)
            {
                // BossmodReborn not installed/loaded - nothing to sync.
            }
        }
    }
}
