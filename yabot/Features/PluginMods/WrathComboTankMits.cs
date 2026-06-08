using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using ECommons.DalamudServices;
using ECommons.Reflection;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YABOT.Features.PluginMods
{
    public class WrathComboTankMits : PluginModFeature
    {
        public override string Name => "WrathCombo: Tank Mitigations Toggle";

        public override string Description =>
            "Toggle WrathCombo's tank mitigations (PLD/WAR/DRK/GNB) on or off in one go. For each tank " +
            "it flips both the Simple rotation's mit option and the Advanced Mitigation feature, so it " +
            "works whichever one you run. Note this only flips the on/off switch for the whole category - " +
            "the individual Advanced Mitigation skill options still need to be set up in WrathCombo first. " +
            "Use /ymits [on|off|toggle] or the buttons below. Enable this tweak to register the command.";

        public override string RequiredPluginName => PluginName;

        public override bool UseAutoConfig => false;

        public override IEnumerable<(string Command, string Aliases, string Description)> CommandReferences =>
            new[] { ("/ymits", "[on|off|toggle]", "Toggle WrathCombo's tank mitigations (no WrathCombo modification needed).") };

        private const string PluginName = "WrathCombo";
        private const string ConfigType = "WrathCombo.Core.Configuration";
        private const string ServiceType = "WrathCombo.Services.Service";
        private const string PresetStorageType = "WrathCombo.Core.PresetStorage";
        private const string DictMember = "CustomIntValues";
        private const string ConfigMember = "Configuration";

        // Per-tank mit wiring. WrathCombo's naming is inconsistent, hence the table:
        // - GNB/PLD use *_MitOptions, WAR uses *_MitsOptions, DRK uses *_SimpleMitigation
        // - the "include" radio value is 0 everywhere except DRK, where it's inverted (On = 1)
        // - the Advanced Mitigation feature preset is *_Mitigation, except GNB which is GNB_Mit_Advanced
        private readonly record struct TankMit(string Job, string StKey, string AoeKey, int IncludeValue, int AdvancedPreset);

        private static readonly TankMit[] Tanks =
        {
            new("GNB", "GNB_ST_MitOptions",       "GNB_AoE_MitOptions",       0, 7700),
            new("PLD", "PLD_ST_MitOptions",       "PLD_AoE_MitOptions",       0, 11086),
            new("WAR", "WAR_ST_MitsOptions",      "WAR_AoE_MitsOptions",      0, 18131),
            new("DRK", "DRK_ST_SimpleMitigation", "DRK_AoE_SimpleMitigation", 1, 5300),
        };

        public override void Enable()
        {
            Svc.Commands.AddHandler("/ymits", new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle WrathCombo's tank mitigations: /ymits [on|off|toggle]",
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

                Svc.Chat.Print($"[YABOT] WrathCombo tank mitigations: {(newMitsEnabled ? "ON" : "OFF")}");
            }
            catch (ArgumentException ex)
            {
                Svc.Chat.PrintError($"[YABOT] {ex.Message}");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "WrathComboTankMits command failed");
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
                Svc.Log.Warning($"[WrathComboTankMits] reflection read failed: {ex.Message}");
                return null;
            }
        }

        // Use the first tank as the reference for the current on/off state - the command keeps
        // all tanks in sync, so any one of them is representative.
        private static bool TryReadMitState(out bool mitsEnabled)
        {
            mitsEnabled = false;
            var dict = GetCustomIntValues();
            if (dict == null) return false;

            var t = Tanks[0];
            dict.TryGetValue(t.StKey, out var stVal);
            mitsEnabled = stVal == t.IncludeValue;
            return true;
        }

        private static bool TryWriteMitState(bool mitsEnabled)
        {
            var dict = GetCustomIntValues();
            if (dict == null) return false;

            foreach (var t in Tanks)
            {
                var val = mitsEnabled ? t.IncludeValue : 1 - t.IncludeValue;
                dict[t.StKey] = val;
                dict[t.AoeKey] = val;
            }

            try
            {
                if (TryGetWrath(out var pl))
                {
                    // Also flip each tank's Advanced Mitigation feature - this Saves and notifies IPC on its own.
                    foreach (var t in Tanks)
                        TrySetAdvancedMit(pl, t.AdvancedPreset, mitsEnabled);

                    var configInstance = pl.GetStaticFoP<object>(ServiceType, ConfigMember);
                    configInstance?.Call("Save", null, Array.Empty<object>());
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboTankMits] Save failed: {ex.Message}");
            }

            return true;
        }

        // Enable/disable an Advanced Mitigation preset via WrathCombo's PresetStorage helpers,
        // which handle parents, conflicts and saving for us.
        private static void TrySetAdvancedMit(object pl, int preset, bool enabled)
        {
            try
            {
                var type = pl.GetType().Assembly.GetType(PresetStorageType);
                var name = enabled ? "EnablePreset" : "DisablePreset";
                var method = type?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == name
                        && m.GetParameters() is { Length: 2 } p
                        && p[0].ParameterType == typeof(int));
                method?.Invoke(null, new object?[] { preset, null });
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboTankMits] advanced-mit {(enabled ? "enable" : "disable")} failed: {ex.Message}");
            }
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

            ImGui.TextDisabled("Covers all four tanks (PLD/WAR/DRK/GNB). For each, flips the Simple rotation's\n\"Include/Exclude Mitigations\" radio (ST + AoE) and the Advanced Mitigation feature.\nOnly flips the category on/off - the individual skill options still need setting up in WrathCombo.");
        };
    }
}
