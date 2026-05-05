using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using ECommons.DalamudServices;
using ECommons.Reflection;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;

namespace YABOT.Features.Commands
{
    public class WrathComboGnbMits : Feature
    {
        public override string Name => "WrathCombo: GNB Simple Mitigations Toggle";

        public override string Description =>
            "Toggle WrathCombo's GNB simple-rotation mitigations (Heart of Stone/Corundum, Aurora, etc.) on or off " +
            "without modifying WrathCombo. Reflects directly into WrathCombo's live config dictionary, so changes take " +
            "effect immediately. Use /ymits [on|off|toggle] or the buttons below.";

        public override FeatureType FeatureType => FeatureType.Commands;

        public override bool UseAutoConfig => false;

        public override IEnumerable<(string Command, string Aliases, string Description)> CommandReferences =>
            new[] { ("/ymits", "[on|off|toggle]", "Toggle WrathCombo's GNB simple mitigations (no WrathCombo modification needed).") };

        private const string PluginName = "WrathCombo";
        private const string ConfigType = "WrathCombo.Core.Configuration";
        private const string ServiceType = "WrathCombo.Services.Service";
        private const string DictMember = "CustomIntValues";
        private const string ConfigMember = "Configuration";
        private const string StKey = "GNB_ST_MitOptions";
        private const string AoeKey = "GNB_AoE_MitOptions";

        public override void Enable()
        {
            Svc.Commands.AddHandler("/ymits", new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle WrathCombo's GNB simple mitigations: /ymits [on|off|toggle]",
                ShowInHelp = true,
            });
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Commands.RemoveHandler("/ymits");
            base.Disable();
        }

        private void OnCommand(string cmd, string argStr)
        {
            try
            {
                var arg = argStr.Trim().ToLowerInvariant();

                if (!TryReadMitState(out var currentMitsEnabled))
                {
                    Svc.Chat.PrintError("[YABOT] WrathCombo not loaded or config layout changed - cannot toggle mits.");
                    return;
                }

                bool newMitsEnabled = arg switch
                {
                    "on" => true,
                    "off" => false,
                    "toggle" or "" => !currentMitsEnabled,
                    _ => throw new ArgumentException($"Unknown arg '{arg}'. Use: /ymits [on|off|toggle]"),
                };

                if (!TryWriteMitState(newMitsEnabled))
                {
                    Svc.Chat.PrintError("[YABOT] Failed to update WrathCombo mit options.");
                    return;
                }

                Svc.Chat.Print($"[YABOT] WrathCombo GNB Simple Mitigations: {(newMitsEnabled ? "ON" : "OFF")}");
            }
            catch (ArgumentException ex)
            {
                Svc.Chat.PrintError($"[YABOT] {ex.Message}");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "WrathComboGnbMits command failed");
                Svc.Chat.PrintError($"[YABOT] Error: {ex.Message}");
            }
        }

        private static bool TryGetWrath(out object plugin) =>
            DalamudReflector.TryGetDalamudPlugin(PluginName, out plugin, suppressErrors: true, ignoreCache: false);

        private static Dictionary<string, int>? GetCustomIntValues()
        {
            if (!TryGetWrath(out var pl)) return null;
            try
            {
                return pl.GetStaticFoP<Dictionary<string, int>>(ConfigType, DictMember);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboGnbMits] reflection read failed: {ex.Message}");
                return null;
            }
        }

        private static bool TryReadMitState(out bool mitsEnabled)
        {
            mitsEnabled = false;
            var dict = GetCustomIntValues();
            if (dict == null) return false;

            dict.TryGetValue(StKey, out var stVal);
            mitsEnabled = stVal != 1;
            return true;
        }

        private static bool TryWriteMitState(bool mitsEnabled)
        {
            var dict = GetCustomIntValues();
            if (dict == null) return false;

            var val = mitsEnabled ? 0 : 1;
            dict[StKey] = val;
            dict[AoeKey] = val;

            try
            {
                if (TryGetWrath(out var pl))
                {
                    var configInstance = pl.GetStaticFoP<object>(ServiceType, ConfigMember);
                    configInstance?.Call("Save", null, Array.Empty<object>());
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboGnbMits] Save failed: {ex.Message}");
            }

            return true;
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (!TryReadMitState(out var mitsEnabled))
            {
                ImGui.TextDisabled("WrathCombo is not loaded.");
                return;
            }

            ImGui.Text("Current state:");
            ImGui.SameLine();
            ImGui.TextColored(
                mitsEnabled ? new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f)
                            : new System.Numerics.Vector4(0.9f, 0.4f, 0.4f, 1f),
                mitsEnabled ? "Mits ON (Include)" : "Mits OFF (Exclude)");

            if (ImGui.Button(mitsEnabled ? "Turn Mits OFF" : "Turn Mits ON"))
            {
                TryWriteMitState(!mitsEnabled);
            }

            ImGui.TextDisabled("Affects both ST and AoE simple rotations. Equivalent to clicking the\n\"Include/Exclude Simple Mitigations\" radio in WrathCombo's GNB Simple config.");
        };
    }
}
