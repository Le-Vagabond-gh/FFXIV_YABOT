using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using YABOT.UI;
using System;
using System.Numerics;

namespace YABOT.Features.OccultCrescent
{
    public unsafe class OccultChestLabels : BaseFeature
    {
        public override string Name => "Occult Chest Labels";

        public override string Description =>
            "Draws floating labels over treasure coffers in range while in the Occult Crescent (NecroLens-style). " +
            "Works in any Horn - no chest map needed.";

        public override FeatureType FeatureType => FeatureType.OccultCrescent;
        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Show bronze coffers")]
            public bool ShowBronze = true;

            [FeatureConfigOption("Show silver coffers")]
            public bool ShowSilver = true;

            [FeatureConfigOption("Show unrecognized coffer types")]
            public bool ShowUnknown = true;

            [FeatureConfigOption("Show distance in label")]
            public bool ShowDistance = true;

            [FeatureConfigOption("Max distance (yalms)", IntMin = 20, IntMax = 200, EditorSize = 200)]
            public int MaxDistance = 120;

            [FeatureConfigOption("Show chest dot")]
            public bool ShowDot = true;

            [FeatureConfigOption("Dot radius", IntMin = 2, IntMax = 12, EditorSize = 200)]
            public int DotRadius = 4;
        }

        public Configs Config { get; private set; } = null!;
        private Overlays Overlay = null!;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Overlay = new(this);
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            if (Overlay != null)
            {
                P.Ws.RemoveWindow(Overlay);
                Overlay = null!;
            }
            base.Disable();
        }

        public override bool DrawConditions() =>
            ZoneHelper.IsOccultCrescent() && Player.Object != null;

        public override void Draw()
        {
            try
            {
                var drawList = ImGui.GetBackgroundDrawList();
                var playerPos = Player.Object.Position;
                var outline = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.9f));

                foreach (var obj in Svc.Objects)
                {
                    if (obj.ObjectKind != ObjectKind.Treasure) continue;

                    var distance = Vector3.Distance(playerPos, obj.Position);
                    if (distance > Config.MaxDistance) continue;

                    // Unopened Occult chests report IsTargetable == false through Dalamud's wrapper,
                    // so visibility must be checked via RenderFlags + Treasure.State instead.
                    var treasure = (FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure*)obj.Address;
                    if (treasure->RenderFlags != 0) continue;
                    if (treasure->State != FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureState.Unopened) continue;

                    var kind = OccultChestHelper.Classify(obj.BaseId);
                    switch (kind)
                    {
                        case OccultChestKind.Bronze when !Config.ShowBronze: continue;
                        case OccultChestKind.Silver when !Config.ShowSilver: continue;
                        case OccultChestKind.Unknown when !Config.ShowUnknown: continue;
                    }

                    if (!Svc.GameGui.WorldToScreen(obj.Position, out var screenPos)) continue;

                    var color = ImGui.GetColorU32(new Vector4(OccultChestHelper.GetColor(kind), 1f));

                    if (Config.ShowDot)
                    {
                        drawList.AddCircleFilled(screenPos, Config.DotRadius + 1f, outline);
                        drawList.AddCircleFilled(screenPos, Config.DotRadius, color);
                    }

                    var label = OccultChestHelper.GetLabel(kind);
                    if (Config.ShowDistance)
                        label += $" {distance:0}y";

                    var textSize = ImGui.CalcTextSize(label);
                    var textPos = new Vector2(screenPos.X - textSize.X / 2f, screenPos.Y + textSize.Y / 2f);
                    drawList.AddText(textPos + Vector2.One, outline, label);
                    drawList.AddText(textPos, color, label);
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] Draw failed");
            }
        }
    }
}
