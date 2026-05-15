using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using YABOT.Features;
using System;
using System.Linq;
using System.Numerics;

namespace YABOT.UI
{
    internal class Overlays : Window
    {
        private BaseFeature Feature { get; set; }

        public Overlays(BaseFeature t) : base($"###Overlay{t.Name}",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.AlwaysAutoResize, true)
        {
            this.Position = new Vector2(0, 0);
            Feature = t;
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
            DisableWindowSounds = true;
            this.SizeConstraints = new WindowSizeConstraints()
            {
                MaximumSize = new Vector2(0, 0),
            };
            if (P.Ws.Windows.Any(x => x.WindowName == this.WindowName))
            {
                P.Ws.RemoveWindow(P.Ws.Windows.First(x => x.WindowName == this.WindowName));
            }
            P.Ws.AddWindow(this);
        }

        public override void Draw()
        {
            try
            {
                Feature.Draw();
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"Error in overlay Draw() for feature {Feature.Name}");
            }
        }

        public override bool DrawConditions() => Feature.Enabled && Feature.DrawConditions();
    }
}
