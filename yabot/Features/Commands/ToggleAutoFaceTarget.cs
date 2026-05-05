using Dalamud.Game.Config;
using ECommons.DalamudServices;
using System.Collections.Generic;

namespace YABOT.Features.Commands
{
    public class ToggleAutoFaceTarget : CommandFeature
    {
        public override string Name => "Toggle Auto Face Target";
        public override string Command { get; set; } = "/yautofacetarget";
        public override string[] Alias => new[] { "/yaft" };
        public override string Description => "Toggles the 'Automatically face target when using action' game setting.";

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
        }
    }
}
