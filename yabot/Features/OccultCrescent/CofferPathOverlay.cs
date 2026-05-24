using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using YABOT.UI;
using System;
using System.Numerics;
using MapSheet = Lumina.Excel.Sheets.Map;

namespace YABOT.Features.OccultCrescent
{
    public unsafe class CofferPathOverlay : BaseFeature
    {
        public override string Name => "Occult Crescent Coffer Path Overlay";

        public override string Description =>
            "When the area map is open in South Horn, overlays the two community minimap-chest farming paths (red NW loop, blue E/S loop). " +
            "Bronze chests are drawn as copper dots, silver chests as lighter dots. Path order matches the console wiki's Coffer Path image.";

        public override FeatureType FeatureType => FeatureType.OccultCrescent;
        public override bool UseAutoConfig => true;

        private const uint SouthHornTerritoryId = 1252;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Show red path (NW loop)")]
            public bool ShowRed = true;

            [FeatureConfigOption("Show blue path (E/S loop)")]
            public bool ShowBlue = true;

            [FeatureConfigOption("Line thickness", IntMin = 1, IntMax = 8, EditorSize = 200)]
            public int LineThickness = 3;

            [FeatureConfigOption("Show chest dots")]
            public bool ShowDots = true;

            [FeatureConfigOption("Dot radius", IntMin = 2, IntMax = 12, EditorSize = 200)]
            public int DotRadius = 4;

            [FeatureConfigOption("Path opacity (%)", IntMin = 20, IntMax = 100, EditorSize = 200)]
            public int OpacityPct = 90;
        }

        public Configs Config { get; private set; } = null!;
        private Overlays Overlay = null!;

        // Match the wiki image's exact line colors (R 249 G 50 B 40 / R 36 G 98 B 130).
        private static readonly Vector3 RedRgb = new(249f / 255f, 50f / 255f, 40f / 255f);
        private static readonly Vector3 BlueRgb = new(36f / 255f, 98f / 255f, 130f / 255f);
        private static readonly Vector3 BronzeRgb = new(0.722f, 0.451f, 0.200f);
        private static readonly Vector3 SilverRgb = new(0.831f, 0.835f, 0.847f);
        private static readonly Vector3 BlackRgb = Vector3.Zero;

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
            Svc.ClientState.TerritoryType == SouthHornTerritoryId
            && Player.Object != null
            && Svc.GameGui.GetAddonByName("AreaMap") != IntPtr.Zero;

        public override void Draw()
        {
            try
            {
                if (!TryGetTransform(out var ctx)) return;

                var palette = BuildPalette(Config.OpacityPct / 100f);
                var drawList = ImGui.GetForegroundDrawList();
                drawList.PushClipRect(ctx.ClipCenter - ctx.ClipHalfSize, ctx.ClipCenter + ctx.ClipHalfSize, true);
                try
                {
                    if (Config.ShowRed) DrawPath(drawList, RedPath, palette.Red, palette, ctx);
                    if (Config.ShowBlue) DrawPath(drawList, BluePath, palette.Blue, palette, ctx);
                }
                finally
                {
                    drawList.PopClipRect();
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] Draw failed");
            }
        }

        private void DrawPath(ImDrawListPtr drawList, ChestNode[] path, uint lineColor, in Palette palette, in TransformCtx ctx)
        {
            if (path.Length < 2) return;

            Span<Vector2> pts = stackalloc Vector2[path.Length];
            for (var i = 0; i < path.Length; i++)
                pts[i] = WorldToScreen(path[i].Pos, ctx);

            for (var i = 1; i < path.Length; i++)
                drawList.AddLine(pts[i - 1], pts[i], lineColor, Config.LineThickness);

            if (!Config.ShowDots) return;

            var radius = (float)Config.DotRadius;
            for (var i = 0; i < path.Length; i++)
            {
                var silver = path[i].Rarity == Rarity.Silver;
                var fill = silver ? palette.Silver : palette.Bronze;
                var size = silver ? radius + 1f : radius;
                drawList.AddCircleFilled(pts[i], size + 1f, palette.Outline);
                drawList.AddCircleFilled(pts[i], size, fill);
            }
        }

        private static Palette BuildPalette(float alpha)
        {
            var a = Math.Clamp(alpha, 0.2f, 1f);
            return new Palette(
                Red:     ImGui.GetColorU32(new Vector4(RedRgb,    a)),
                Blue:    ImGui.GetColorU32(new Vector4(BlueRgb,   a)),
                Bronze:  ImGui.GetColorU32(new Vector4(BronzeRgb, a)),
                Silver:  ImGui.GetColorU32(new Vector4(SilverRgb, a)),
                Outline: ImGui.GetColorU32(new Vector4(BlackRgb,  a)));
        }

        private readonly record struct Palette(uint Red, uint Blue, uint Bronze, uint Silver, uint Outline);

        private readonly record struct TransformCtx(
            Vector2 PlayerScreen, Vector2 PlayerTex,
            Vector2 ClipCenter, Vector2 ClipHalfSize,
            float Scale,
            float SheetSizeFactor, float SheetOffsetX, float SheetOffsetY);

        // World -> screen via player-marker calibration: the game renders PlayerCone at the right
        // on-screen position for the current zoom/pan, so we measure that and place chests
        // relative to it. Lags one frame during fast drags but is otherwise positionally accurate
        // and avoids needing to understand the AtkComponentMap MapOffset semantics.
        private static Vector2 WorldToScreen(Vector3 world, in TransformCtx ctx)
        {
            var texX = (world.X + ctx.SheetOffsetX) * ctx.SheetSizeFactor;
            var texY = (world.Z + ctx.SheetOffsetY) * ctx.SheetSizeFactor;
            return new Vector2(
                ctx.PlayerScreen.X + (texX - ctx.PlayerTex.X) * ctx.Scale,
                ctx.PlayerScreen.Y + (texY - ctx.PlayerTex.Y) * ctx.Scale);
        }

        private bool TryGetTransform(out TransformCtx ctx)
        {
            ctx = default;
            var addonPtr = Svc.GameGui.GetAddonByName("AreaMap");
            if (addonPtr == IntPtr.Zero) return false;
            var addon = (AddonAreaMap*)addonPtr.Address;
            if (addon == null || !addon->IsVisible || !addon->IsFullyLoaded()) return false;

            var comp = addon->ComponentMap;
            if (comp == null || comp->OwnerNode == null || comp->PlayerCone == null) return false;
            if (Player.Object == null) return false;

            var agent = AgentMap.Instance();
            if (agent == null) return false;
            if (!Svc.Data.GetExcelSheet<MapSheet>().TryGetRow(agent->CurrentMapId, out var row))
                return false;

            // PlayerCone is the player-marker arrow. Its rotation pivot (OriginX/OriginY) is where
            // the game logically places the player; the geometric center of the node doesn't match
            // because the arrow texture isn't symmetric vertically.
            var cone = (AtkResNode*)comp->PlayerCone;
            var coneScale = AtkResNodeHelper.GetNodeScale(cone);
            var conePos = AtkResNodeHelper.GetNodePosition(cone);
            var playerScreen = conePos + new Vector2(cone->OriginX, cone->OriginY) * coneScale;

            // ComponentMap's owner node bounds the visible map widget rectangle - used to clip the
            // overlay so it can't bleed onto the addon chrome around the map.
            var owner = (AtkResNode*)comp->OwnerNode;
            var ownerScale = AtkResNodeHelper.GetNodeScale(owner);
            var ownerPos = AtkResNodeHelper.GetNodePosition(owner);
            var ownerSize = new Vector2(owner->Width, owner->Height) * ownerScale;

            var sizeFactor = row.SizeFactor / 100f;
            var playerWorld = Player.Object.Position;
            var playerTex = new Vector2(
                (playerWorld.X + row.OffsetX) * sizeFactor,
                (playerWorld.Z + row.OffsetY) * sizeFactor);

            ctx = new TransformCtx(
                PlayerScreen: playerScreen,
                PlayerTex:    playerTex,
                ClipCenter:   ownerPos + ownerSize * 0.5f,
                ClipHalfSize: ownerSize * 0.5f,
                Scale:        comp->MapScale * coneScale.X,
                SheetSizeFactor: sizeFactor,
                SheetOffsetX:    row.OffsetX,
                SheetOffsetY:    row.OffsetY);
            return true;
        }

        private enum Rarity { Bronze, Silver }
        private readonly record struct ChestNode(Vector3 Pos, Rarity Rarity);

        // Source: EurekaTrackerAutoPopper OccultChests.cs TreasurePosition[1252].
        // Visit order matches the wiki's Coffer Path image, captured by clicking each marker in order.
        private static readonly ChestNode[] RedPath =
        [
            new(new Vector3( 617.090f,  66.300f, -703.883f), Rarity.Bronze),
            new(new Vector3( 490.410f,  62.455f, -590.570f), Rarity.Bronze),
            new(new Vector3( 386.923f,  96.788f, -451.377f), Rarity.Bronze),
            new(new Vector3( 381.735f,  22.171f, -743.648f), Rarity.Bronze),
            new(new Vector3( 142.107f,  16.403f, -574.060f), Rarity.Bronze),
            new(new Vector3(-118.975f,   4.990f, -708.461f), Rarity.Bronze),
            new(new Vector3(-451.682f,   2.975f, -775.570f), Rarity.Bronze),
            new(new Vector3(-585.290f,   4.990f, -864.836f), Rarity.Bronze),
            new(new Vector3(-729.427f,   4.990f, -724.819f), Rarity.Bronze),
            new(new Vector3(-825.162f,   2.975f, -832.273f), Rarity.Silver),
            new(new Vector3(-884.123f,   3.799f, -682.033f), Rarity.Bronze),
            new(new Vector3(-661.707f,   2.975f, -579.492f), Rarity.Bronze),
            new(new Vector3(-491.020f,   2.975f, -529.595f), Rarity.Bronze),
            new(new Vector3(-140.459f,  22.354f, -414.267f), Rarity.Bronze),
            new(new Vector3(-343.160f,  52.323f, -382.132f), Rarity.Bronze),
            new(new Vector3(-487.114f,  98.527f, -205.463f), Rarity.Bronze),
            new(new Vector3(-444.114f,  90.684f,   26.230f), Rarity.Bronze),
            new(new Vector3(-394.888f, 106.737f,  175.433f), Rarity.Bronze),
            new(new Vector3(-713.802f,  62.058f,  192.615f), Rarity.Bronze),
            new(new Vector3(-756.832f,  76.554f,   97.368f), Rarity.Bronze),
            new(new Vector3(-682.795f, 135.607f, -195.270f), Rarity.Silver),
            new(new Vector3(-729.915f, 116.533f,  -79.057f), Rarity.Bronze),
            new(new Vector3(-856.962f,  68.833f,  -93.156f), Rarity.Bronze),
            new(new Vector3(-798.245f, 105.577f, -310.567f), Rarity.Silver),
            new(new Vector3(-767.452f, 115.618f, -235.004f), Rarity.Bronze),
            new(new Vector3(-680.537f, 104.845f, -354.788f), Rarity.Bronze),
        ];

        private static readonly ChestNode[] BluePath =
        [
            new(new Vector3( 666.529f,  79.118f, -480.369f), Rarity.Bronze),
            new(new Vector3( 870.664f,  95.689f, -388.357f), Rarity.Bronze),
            new(new Vector3( 779.019f,  96.086f, -256.245f), Rarity.Bronze),
            new(new Vector3( 770.748f, 107.988f, -143.572f), Rarity.Silver),
            new(new Vector3( 726.284f, 108.141f,  -67.918f), Rarity.Bronze),
            new(new Vector3( 475.730f,  95.994f,  -87.083f), Rarity.Bronze),
            new(new Vector3( 609.613f, 107.988f,  117.266f), Rarity.Bronze),
            new(new Vector3( 788.876f, 120.378f,  109.392f), Rarity.Bronze),
            new(new Vector3( 826.688f, 121.996f,  434.989f), Rarity.Bronze),
            new(new Vector3( 869.291f, 109.972f,  581.201f), Rarity.Bronze),
            new(new Vector3( 835.080f,  69.993f,  699.092f), Rarity.Bronze),
            new(new Vector3( 697.322f,  69.993f,  597.925f), Rarity.Silver),
            new(new Vector3( 596.460f,  70.298f,  622.766f), Rarity.Bronze),
            new(new Vector3( 433.707f,  70.298f,  683.528f), Rarity.Bronze),
            new(new Vector3( 294.880f,  56.077f,  640.223f), Rarity.Bronze),
            new(new Vector3( 140.978f,  55.985f,  770.992f), Rarity.Bronze),
            new(new Vector3(  35.721f,  65.110f,  648.951f), Rarity.Bronze),
            new(new Vector3( 256.153f,  73.167f,  492.363f), Rarity.Bronze),
            new(new Vector3( 471.183f,  70.298f,  530.022f), Rarity.Bronze),
            new(new Vector3( 642.969f,  69.993f,  407.797f), Rarity.Bronze),
            new(new Vector3( 517.754f,  67.887f,  236.133f), Rarity.Silver),
            new(new Vector3( 277.790f, 103.776f,  241.901f), Rarity.Bronze),
            new(new Vector3( 245.594f, 109.117f,  -18.174f), Rarity.Bronze),
            new(new Vector3( 354.116f,  95.659f, -288.930f), Rarity.Bronze),
            new(new Vector3(  55.283f, 111.314f, -289.082f), Rarity.Bronze),
            new(new Vector3(-158.648f,  98.619f, -132.738f), Rarity.Bronze),
            new(new Vector3( -25.681f, 102.220f,  150.164f), Rarity.Bronze),
            new(new Vector3(-256.886f, 120.989f,  125.078f), Rarity.Bronze),
            new(new Vector3(-401.663f,  85.038f,  332.540f), Rarity.Bronze),
            new(new Vector3(-283.986f, 115.984f,  377.035f), Rarity.Silver),
            new(new Vector3(   8.987f, 103.197f,  426.963f), Rarity.Bronze),
            new(new Vector3(-197.192f,  74.906f,  618.341f), Rarity.Bronze),
            new(new Vector3(-225.025f,  74.998f,  804.990f), Rarity.Bronze),
            new(new Vector3(-372.671f,  74.998f,  527.428f), Rarity.Bronze),
            new(new Vector3(-550.134f, 106.981f,  627.741f), Rarity.Bronze),
            new(new Vector3(-600.275f, 138.994f,  802.640f), Rarity.Bronze),
            new(new Vector3(-645.686f, 202.991f,  710.170f), Rarity.Silver),
            new(new Vector3(-716.152f, 170.977f,  794.430f), Rarity.Bronze),
            new(new Vector3(-676.417f, 170.977f,  640.375f), Rarity.Bronze),
            new(new Vector3(-784.756f, 138.994f,  699.763f), Rarity.Bronze),
            new(new Vector3(-729.549f, 106.981f,  561.150f), Rarity.Bronze),
            new(new Vector3(-648.005f,  74.998f,  403.952f), Rarity.Bronze),
        ];
    }
}
