using Dalamud.Game.Config;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;

namespace YABOT.Features.Commands
{
    public class ToggleAutoFaceTarget : CommandFeature
    {
        public override string Name => "Toggle Auto Face Target";
        public override string Command { get; set; } = "/yautofacetarget";
        public override string[] Alias => new[] { "/yaft" };
        public override string Description => "Toggles the 'Automatically face target when using action' game setting (and BossmodReborn's smart character orientation).";

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

            Svc.GameConfig.Set(UiControlOption.AutoFaceTargetOnAction, newValue.Value ? 1u : 0u);
            Svc.Chat.Print($"[YABOT] Auto face target: {(newValue.Value ? "ON" : "OFF")}");

            ToggleBmrSmartOrientation(newValue.Value);
        }

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
