using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using ECommons.DalamudServices;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;

namespace YABOT.Features.PluginMods
{
    public class WrathComboTankRotationMode : PluginModFeature
    {
        public override string Name => "WrathCombo: Tank Simple/Advanced Toggle";

        public override string Description =>
            "Switch every tank (PLD/WAR/DRK/GNB) between WrathCombo's Simple and Advanced rotations in one go, " +
            "for both single target and AoE. Enabling one mode automatically disables the other (they conflict). " +
            "Advanced still uses whatever sub-options you've set up in WrathCombo. " +
            "Use /yrot [simple|advanced|toggle] or the buttons below. Enable this tweak to register the command.";

        public override string RequiredPluginName => WrathComboInterop.PluginName;

        public override bool UseAutoConfig => false;

        public override IEnumerable<(string Command, string Aliases, string Description)> CommandReferences =>
            new[] { ("/yrot", "[simple|advanced|toggle]", "Switch all tanks between Simple and Advanced WrathCombo rotations.") };

        // Simple/Advanced ST + AoE preset ids per tank, straight from WrathCombo's CustomComboPreset enum.
        private readonly record struct TankRotation(string Job, int SimpleSt, int SimpleAoe, int AdvancedSt, int AdvancedAoe);

        private static readonly TankRotation[] Tanks =
        {
            new("GNB", 7001,  7002,  7003,  7200),
            new("PLD", 11000, 11001, 11002, 11015),
            new("WAR", 18000, 18001, 18002, 18016),
            new("DRK", 5001,  5002,  5010,  5050),
        };

        public override void Enable()
        {
            Svc.Commands.AddHandler("/yrot", new CommandInfo(OnCommand)
            {
                HelpMessage = "Switch all tanks between Simple and Advanced rotations: /yrot [simple|advanced|toggle]",
                ShowInHelp = true,
            });
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Commands.RemoveHandler("/yrot");
            base.Disable();
        }

        private void OnCommand(string cmd, string argStr)
        {
            try
            {
                var arg = argStr.Trim().ToLowerInvariant();

                if (!WrathComboInterop.TryGetWrath(out _))
                {
                    Svc.Chat.PrintError("[YABOT] WrathCombo not loaded - cannot switch rotations.");
                    return;
                }

                bool advanced = arg switch
                {
                    "advanced" or "adv" => true,
                    "simple" => false,
                    "toggle" or "" => !IsAdvancedActive(),
                    _ => throw new ArgumentException($"Unknown arg '{arg}'. Use: /yrot [simple|advanced|toggle]"),
                };

                ApplyMode(advanced);
                Svc.Chat.Print($"[YABOT] All tanks set to {(advanced ? "Advanced" : "Simple")} rotations.");
            }
            catch (ArgumentException ex)
            {
                Svc.Chat.PrintError($"[YABOT] {ex.Message}");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "WrathComboTankRotationMode command failed");
                Svc.Chat.PrintError($"[YABOT] Error: {ex.Message}");
            }
        }

        // Use the first tank's Advanced ST as the reference - the toggle keeps every tank in sync.
        private static bool IsAdvancedActive() =>
            WrathComboInterop.IsPresetEnabled(Tanks[0].AdvancedSt);

        private static void ApplyMode(bool advanced)
        {
            foreach (var t in Tanks)
            {
                // Enabling a mode auto-disables its conflicting counterpart, but disable it
                // explicitly too so we don't depend on WrathCombo's conflict metadata.
                WrathComboInterop.SetPreset(t.AdvancedSt, advanced);
                WrathComboInterop.SetPreset(t.AdvancedAoe, advanced);
                WrathComboInterop.SetPreset(t.SimpleSt, !advanced);
                WrathComboInterop.SetPreset(t.SimpleAoe, !advanced);
            }
            WrathComboInterop.SaveConfig();
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (!WrathComboInterop.TryGetWrath(out _))
            {
                ImGui.TextDisabled("WrathCombo is not loaded.");
                return;
            }

            var advanced = IsAdvancedActive();

            ImGui.Text("Current mode:");
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.7f, 0.9f, 1f), advanced ? "Advanced" : "Simple");

            if (ImGui.Button("Switch all tanks to Simple"))
                ApplyMode(false);
            ImGui.SameLine();
            if (ImGui.Button("Switch all tanks to Advanced"))
                ApplyMode(true);

            ImGui.TextDisabled("Covers all four tanks (PLD/WAR/DRK/GNB), ST + AoE. Enabling one mode\ndisables the other. Advanced uses whatever sub-options you've set in WrathCombo.");
        };
    }
}
