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
using System.Linq;
using System.Numerics;

namespace YABOT.Features.OccultCrescent
{
    public unsafe class CofferPathOverlay : BaseFeature
    {
        public override string Name => "Occult Crescent Coffer Path Overlay";

        public override string Description =>
            "When the area map is open in South Horn or North Horn, overlays the community minimap-chest farming paths " +
            "(South Horn: red NW loop, blue E/S loop; North Horn: yellow, blue, purple, green, orange - with aetheryte start points). " +
            "Bronze chests are drawn as copper dots, silver chests as lighter dots; links to optional chests are semi-transparent. " +
            "The purple path's subterrane chests are drawn on both the surface map and the North Subterrane map. " +
            "Path order matches the console wiki's Coffer Path images.";

        public override FeatureType FeatureType => FeatureType.OccultCrescent;
        public override bool UseAutoConfig => true;

        private const uint SouthHornTerritoryId = 1252;
        private const uint NorthHornTerritoryId = 1346;
        private const uint SouthHornMapId = 967;
        private const uint NorthHornSurfaceMapId = 1135;
        private const uint NorthSubterraneMapId = 1244;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Show red path (NW loop, South Horn)")]
            public bool ShowRed = true;

            [FeatureConfigOption("Show blue path (E/S loop, South Horn)")]
            public bool ShowBlue = true;

            [FeatureConfigOption("Show North Horn paths")]
            public bool ShowNorthHorn = true;

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

        // Line colors sampled from the wiki images (South Horn: R 249 G 50 B 40 / R 36 G 98 B 130).
        private static readonly Vector3 RedRgb = new(249f / 255f, 50f / 255f, 40f / 255f);
        private static readonly Vector3 BlueRgb = new(36f / 255f, 98f / 255f, 130f / 255f);
        private static readonly Vector3 NhYellowRgb = new(181f / 255f, 230f / 255f, 29f / 255f);
        private static readonly Vector3 NhBlueRgb = new(63f / 255f, 72f / 255f, 204f / 255f);
        private static readonly Vector3 NhPurpleRgb = new(163f / 255f, 73f / 255f, 164f / 255f);
        private static readonly Vector3 NhGreenRgb = new(34f / 255f, 177f / 255f, 76f / 255f);
        private static readonly Vector3 NhOrangeRgb = new(255f / 255f, 127f / 255f, 39f / 255f);
        private static readonly Vector3 AetheryteRgb = new(0f / 255f, 229f / 255f, 255f / 255f);
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
            Svc.ClientState.TerritoryType is SouthHornTerritoryId or NorthHornTerritoryId
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
                    // Keyed on the map being displayed (radio buttons on AreaMap switch layers),
                    // not the map the player is standing on.
                    if (ctx.MapId == SouthHornMapId)
                    {
                        if (Config.ShowRed) DrawPath(drawList, RedPath, RedRgb, palette, ctx);
                        if (Config.ShowBlue) DrawPath(drawList, BluePath, BlueRgb, palette, ctx);
                    }
                    else if (Config.ShowNorthHorn)
                    {
                        if (ctx.MapId == NorthHornSurfaceMapId)
                        {
                            DrawPath(drawList, NorthHornYellowPath, NhYellowRgb, palette, ctx);
                            DrawPath(drawList, NorthHornBluePath, NhBlueRgb, palette, ctx);
                            DrawPath(drawList, NorthHornPurplePath, NhPurpleRgb, palette, ctx);
                            DrawPath(drawList, NorthHornGreenPath, NhGreenRgb, palette, ctx);
                            DrawPath(drawList, NorthHornOrangePath, NhOrangeRgb, palette, ctx);
                        }
                        else if (ctx.MapId == NorthSubterraneMapId)
                        {
                            DrawPath(drawList, NorthHornPurpleSubPath, NhPurpleRgb, palette, ctx);
                        }
                    }
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

        private void DrawPath(ImDrawListPtr drawList, PathNode[] path, Vector3 rgb, in Palette palette, in TransformCtx ctx)
        {
            if (path.Length < 2) return;

            Span<Vector2> pts = stackalloc Vector2[path.Length];
            for (var i = 0; i < path.Length; i++)
                pts[i] = WorldToScreen(path[i].Pos, ctx);

            var lineFull = ImGui.GetColorU32(new Vector4(rgb, palette.Alpha));
            var lineFaint = ImGui.GetColorU32(new Vector4(rgb, palette.Alpha * 0.35f));

            for (var i = 1; i < path.Length; i++)
                drawList.AddLine(pts[i - 1], pts[i], SegmentOptional(path, i - 1) ? lineFaint : lineFull, Config.LineThickness);

            if (!Config.ShowDots) return;

            var radius = (float)Config.DotRadius;
            for (var i = 0; i < path.Length; i++)
            {
                switch (path[i].Kind)
                {
                    case NodeKind.Bend:
                        continue;
                    case NodeKind.Aetheryte:
                        AddDiamond(drawList, pts[i], radius + 4f, palette.Outline);
                        AddDiamond(drawList, pts[i], radius + 2.5f, palette.Aetheryte);
                        continue;
                    default:
                        var silver = path[i].Rarity == Rarity.Silver;
                        var fill = silver ? palette.Silver : palette.Bronze;
                        var size = silver ? radius + 1f : radius;
                        drawList.AddCircleFilled(pts[i], size + 1f, palette.Outline);
                        drawList.AddCircleFilled(pts[i], size, fill);
                        continue;
                }
            }
        }

        private static void AddDiamond(ImDrawListPtr drawList, Vector2 c, float r, uint color) =>
            drawList.AddQuadFilled(
                c + new Vector2(0, -r), c + new Vector2(r, 0),
                c + new Vector2(0, r), c + new Vector2(-r, 0), color);

        // A segment is semi-transparent when the chest interval it belongs to (the stretch between
        // the surrounding chests, bends included) touches an optional chest.
        private static bool SegmentOptional(PathNode[] path, int start)
        {
            for (var j = start; j >= 0; j--)
            {
                if (path[j].Kind != NodeKind.Chest) continue;
                if (path[j].Optional) return true;
                break;
            }
            for (var j = start + 1; j < path.Length; j++)
            {
                if (path[j].Kind != NodeKind.Chest) continue;
                if (path[j].Optional) return true;
                break;
            }
            return false;
        }

        private static Palette BuildPalette(float alpha)
        {
            var a = Math.Clamp(alpha, 0.2f, 1f);
            return new Palette(
                Bronze:    ImGui.GetColorU32(new Vector4(OccultChestHelper.BronzeRgb, a)),
                Silver:    ImGui.GetColorU32(new Vector4(OccultChestHelper.SilverRgb, a)),
                Aetheryte: ImGui.GetColorU32(new Vector4(AetheryteRgb, a)),
                Outline:   ImGui.GetColorU32(new Vector4(BlackRgb, a)),
                Alpha:     a);
        }

        private readonly record struct Palette(uint Bronze, uint Silver, uint Aetheryte, uint Outline, float Alpha);

        private readonly record struct TransformCtx(
            Vector2 AnchorScreen, Vector2 AnchorTex,
            Vector2 ClipCenter, Vector2 ClipHalfSize,
            float Scale,
            float SheetSizeFactor, float SheetOffsetX, float SheetOffsetY,
            uint MapId);

        // World -> screen relative to a calibrated anchor point (a texture position whose screen
        // position is known). Tex coords are map-texture-center-relative: (world + sheetOffset) * sizeFactor.
        private static Vector2 WorldToScreen(Vector3 world, in TransformCtx ctx)
        {
            var texX = (world.X + ctx.SheetOffsetX) * ctx.SheetSizeFactor;
            var texY = (world.Z + ctx.SheetOffsetY) * ctx.SheetSizeFactor;
            return new Vector2(
                ctx.AnchorScreen.X + (texX - ctx.AnchorTex.X) * ctx.Scale,
                ctx.AnchorScreen.Y + (texY - ctx.AnchorTex.Y) * ctx.Scale);
        }

        // AtkComponentMap.MapOffset pans the view; per ClientStructs "(0, 0) is the center of the
        // map texture", but its sign convention is undocumented. While the displayed map is the one
        // the player is on, the PlayerCone marker gives an exact anchor, and we use it to calibrate
        // the sign and any residual bias of the MapOffset-based anchor. That calibration then lets
        // us draw on a displayed map the player is NOT on (e.g. the North Subterrane layer radio
        // button while standing on the surface), where no player marker exists.
        private static float MapOffsetSign = 1f;
        private static Vector2 MapAnchorResidual = Vector2.Zero;

        private bool TryGetTransform(out TransformCtx ctx)
        {
            ctx = default;
            var addonPtr = Svc.GameGui.GetAddonByName("AreaMap");
            if (addonPtr == IntPtr.Zero) return false;
            var addon = (AddonAreaMap*)addonPtr.Address;
            if (addon == null || !addon->IsVisible || !addon->IsFullyLoaded()) return false;

            var comp = addon->ComponentMap;
            if (comp == null || comp->OwnerNode == null) return false;

            var agent = AgentMap.Instance();
            if (agent == null) return false;

            // ComponentMap's owner node bounds the visible map widget rectangle - used to clip the
            // overlay so it can't bleed onto the addon chrome around the map.
            var owner = (AtkResNode*)comp->OwnerNode;
            var ownerScale = AtkResNodeHelper.GetNodeScale(owner);
            var ownerPos = AtkResNodeHelper.GetNodePosition(owner);
            var ownerSize = new Vector2(owner->Width, owner->Height) * ownerScale;
            var clipCenter = ownerPos + ownerSize * 0.5f;

            // Transform values of the DISPLAYED map (the layer radio buttons change SelectedMapId,
            // CurrentMapId stays on the map the player is standing on).
            var sizeFactor = agent->SelectedMapSizeFactorFloat;
            var sheetOffsetX = (float)agent->SelectedOffsetX;
            var sheetOffsetY = (float)agent->SelectedOffsetY;
            var mapOffset = new Vector2(comp->MapOffsetX, comp->MapOffsetY);

            Vector2 anchorScreen, anchorTex;
            float scale;
            if (agent->SelectedMapId == agent->CurrentMapId && comp->PlayerCone != null && Player.Object != null)
            {
                // PlayerCone is the player-marker arrow. Its rotation pivot (OriginX/OriginY) is where
                // the game logically places the player; the geometric center of the node doesn't match
                // because the arrow texture isn't symmetric vertically. It lags one frame during fast
                // drags but is otherwise positionally exact.
                var cone = (AtkResNode*)comp->PlayerCone;
                var coneScale = AtkResNodeHelper.GetNodeScale(cone);
                var conePos = AtkResNodeHelper.GetNodePosition(cone);
                var playerScreen = conePos + new Vector2(cone->OriginX, cone->OriginY) * coneScale;

                var playerWorld = Player.Object.Position;
                var playerTex = new Vector2(
                    (playerWorld.X + sheetOffsetX) * sizeFactor,
                    (playerWorld.Z + sheetOffsetY) * sizeFactor);

                scale = comp->MapScale * coneScale.X;
                anchorScreen = playerScreen;
                anchorTex = playerTex;

                // Calibrate the MapOffset-based anchor against the exact cone anchor.
                var plus = clipCenter + (playerTex - mapOffset) * scale;
                var minus = clipCenter + (playerTex + mapOffset) * scale;
                MapOffsetSign = Vector2.DistanceSquared(plus, playerScreen) <= Vector2.DistanceSquared(minus, playerScreen) ? 1f : -1f;
                MapAnchorResidual = playerScreen - (clipCenter + (playerTex - mapOffset * MapOffsetSign) * scale);
            }
            else
            {
                // Viewing a map layer the player is not on: no player marker, anchor on the pan state.
                scale = comp->MapScale * ownerScale.X;
                anchorScreen = clipCenter + MapAnchorResidual;
                anchorTex = mapOffset * MapOffsetSign;
            }

            ctx = new TransformCtx(
                AnchorScreen: anchorScreen,
                AnchorTex:    anchorTex,
                ClipCenter:   clipCenter,
                ClipHalfSize: ownerSize * 0.5f,
                Scale:        scale,
                SheetSizeFactor: sizeFactor,
                SheetOffsetX:    sheetOffsetX,
                SheetOffsetY:    sheetOffsetY,
                MapId:           agent->SelectedMapId);
            return true;
        }

        private enum Rarity { Bronze, Silver }
        private enum NodeKind { Chest, Bend, Aetheryte }
        private readonly record struct PathNode(Vector3 Pos, NodeKind Kind, Rarity Rarity = Rarity.Bronze, bool Optional = false, bool Subterrane = false);

        private static PathNode Chest(float x, float y, float z, Rarity rarity = Rarity.Bronze, bool optional = false, bool sub = false) =>
            new(new Vector3(x, y, z), NodeKind.Chest, rarity, optional, sub);

        // Bends and aetherytes only exist for map drawing, which ignores Y.
        private static PathNode Bend(float x, float z) => new(new Vector3(x, 0f, z), NodeKind.Bend);

        private static PathNode Aetheryte(float x, float z) => new(new Vector3(x, 0f, z), NodeKind.Aetheryte);

        // Source: EurekaTrackerAutoPopper OccultChests.cs TreasurePosition[1252].
        // Visit order matches the wiki's Coffer Path image, captured by clicking each marker in order.
        private static readonly PathNode[] RedPath =
        [
            Chest( 617.090f,  66.300f, -703.883f),
            Chest( 490.410f,  62.455f, -590.570f),
            Chest( 386.923f,  96.788f, -451.377f),
            Chest( 381.735f,  22.171f, -743.648f),
            Chest( 142.107f,  16.403f, -574.060f),
            Chest(-118.975f,   4.990f, -708.461f),
            Chest(-451.682f,   2.975f, -775.570f),
            Chest(-585.290f,   4.990f, -864.836f),
            Chest(-729.427f,   4.990f, -724.819f),
            Chest(-825.162f,   2.975f, -832.273f, Rarity.Silver),
            Chest(-884.123f,   3.799f, -682.033f),
            Chest(-661.707f,   2.975f, -579.492f),
            Chest(-491.020f,   2.975f, -529.595f),
            Chest(-140.459f,  22.354f, -414.267f),
            Chest(-343.160f,  52.323f, -382.132f),
            Chest(-487.114f,  98.527f, -205.463f),
            Chest(-444.114f,  90.684f,   26.230f),
            Chest(-394.888f, 106.737f,  175.433f),
            Chest(-713.802f,  62.058f,  192.615f),
            Chest(-756.832f,  76.554f,   97.368f),
            Chest(-682.795f, 135.607f, -195.270f, Rarity.Silver),
            Chest(-729.915f, 116.533f,  -79.057f),
            Chest(-856.962f,  68.833f,  -93.156f),
            Chest(-798.245f, 105.577f, -310.567f, Rarity.Silver),
            Chest(-767.452f, 115.618f, -235.004f),
            Chest(-680.537f, 104.845f, -354.788f),
        ];

        private static readonly PathNode[] BluePath =
        [
            Chest( 666.529f,  79.118f, -480.369f),
            Chest( 870.664f,  95.689f, -388.357f),
            Chest( 779.019f,  96.086f, -256.245f),
            Chest( 770.748f, 107.988f, -143.572f, Rarity.Silver),
            Chest( 726.284f, 108.141f,  -67.918f),
            Chest( 475.730f,  95.994f,  -87.083f),
            Chest( 609.613f, 107.988f,  117.266f),
            Chest( 788.876f, 120.378f,  109.392f),
            Chest( 826.688f, 121.996f,  434.989f),
            Chest( 869.291f, 109.972f,  581.201f),
            Chest( 835.080f,  69.993f,  699.092f),
            Chest( 697.322f,  69.993f,  597.925f, Rarity.Silver),
            Chest( 596.460f,  70.298f,  622.766f),
            Chest( 433.707f,  70.298f,  683.528f),
            Chest( 294.880f,  56.077f,  640.223f),
            Chest( 140.978f,  55.985f,  770.992f),
            Chest(  35.721f,  65.110f,  648.951f),
            Chest( 256.153f,  73.167f,  492.363f),
            Chest( 471.183f,  70.298f,  530.022f),
            Chest( 642.969f,  69.993f,  407.797f),
            Chest( 517.754f,  67.887f,  236.133f, Rarity.Silver),
            Chest( 277.790f, 103.776f,  241.901f),
            Chest( 245.594f, 109.117f,  -18.174f),
            Chest( 354.116f,  95.659f, -288.930f),
            Chest(  55.283f, 111.314f, -289.082f),
            Chest(-158.648f,  98.619f, -132.738f),
            Chest( -25.681f, 102.220f,  150.164f),
            Chest(-256.886f, 120.989f,  125.078f),
            Chest(-401.663f,  85.038f,  332.540f),
            Chest(-283.986f, 115.984f,  377.035f, Rarity.Silver),
            Chest(   8.987f, 103.197f,  426.963f),
            Chest(-197.192f,  74.906f,  618.341f),
            Chest(-225.025f,  74.998f,  804.990f),
            Chest(-372.671f,  74.998f,  527.428f),
            Chest(-550.134f, 106.981f,  627.741f),
            Chest(-600.275f, 138.994f,  802.640f),
            Chest(-645.686f, 202.991f,  710.170f, Rarity.Silver),
            Chest(-716.152f, 170.977f,  794.430f),
            Chest(-676.417f, 170.977f,  640.375f),
            Chest(-784.756f, 138.994f,  699.763f),
            Chest(-729.549f, 106.981f,  561.150f),
            Chest(-648.005f,  74.998f,  403.952f),
        ];

        // Source: EurekaTrackerAutoPopper OccultChests.cs TreasurePosition[NorthHorn].
        // Visit order matches the wiki's NH Coffer Path image; aetheryte starts, terrain bends and
        // optional chests captured with the click editor.
        private static readonly PathNode[] NorthHornYellowPath =
        [
            Aetheryte(-394.401f, -417.959f),
            Chest(-439.551f,  43.044f, -558.449f),
            Chest(-525.781f,  46.857f, -783.468f),
            Chest(-416.774f,  45.937f, -945.431f),
            Chest(-736.024f,  21.035f, -881.486f),
            Chest(-815.808f, -21.835f, -699.370f, Rarity.Silver),
            Chest(-928.626f, -11.228f, -744.956f),
            Chest(-857.599f, -12.235f, -609.817f),
            Chest(-697.271f,  34.898f, -565.022f),
            Chest(-707.376f,  41.586f, -396.989f),
            Bend(-907.253f, -403.331f),
            Chest(-878.967f,  13.135f, -314.202f),
        ];

        private static readonly PathNode[] NorthHornBluePath =
        [
            Aetheryte(366.539f, -568.424f),
            Chest( 254.744f,  36.932f, -605.000f),
            Chest( 389.536f,  60.682f, -733.018f),
            Chest( 639.049f,  60.625f, -698.726f),
            Chest( 658.723f,  60.520f, -552.306f),
            Chest( 658.809f,  66.126f, -364.676f),
            Chest( 950.201f,  74.000f, -358.976f, optional: true),
            Chest( 815.443f,  60.554f, -657.313f),
            Chest( 865.457f,  70.215f, -874.087f),
            Chest( 634.792f,  60.515f, -831.787f, Rarity.Silver),
            Chest( 633.132f,  60.642f, -910.227f),
            Chest( 147.869f,  61.000f, -868.752f),
            Chest(  -2.306f,  66.691f, -814.905f, Rarity.Silver),
            Chest(-232.419f,  53.237f, -719.972f),
            Chest(-265.761f,  30.171f, -439.519f),
            Chest(-254.141f,   1.821f, -266.312f),
            Chest(-436.442f,   0.203f,  166.219f, optional: true),
        ];

        private static readonly PathNode[] NorthHornPurplePath =
        [
            Aetheryte(896.070f, 888.163f),
            Bend(810.594f, 804.571f),
            Chest( 676.996f,  190.978f,  957.447f),
            Chest( 673.740f,  161.165f,  729.666f),
            Chest( 812.000f,  192.000f,  669.000f),
            Chest( 758.147f,  130.000f,  506.813f),
            Chest( 719.348f,   69.655f,  268.304f),
            Chest( 449.408f,    0.147f,  105.234f),
            Chest( 649.544f,   46.245f, -157.774f),
            Chest( 383.314f,   33.000f, -175.648f, Rarity.Silver),
            Chest( 478.451f,   12.422f, -202.971f),
            Chest( 279.093f,  143.000f, -356.148f),
            Chest( -26.000f,    0.232f, -437.688f),
            Chest(  85.598f,    3.303f, -281.140f),
            Chest(  43.782f,    2.454f, -108.192f),
            Chest(-168.204f,    3.380f, -153.458f),
            Chest(-162.042f,    3.590f,   98.450f),
            Chest(-287.741f,  -92.000f,  125.666f, sub: true),
            Chest(-144.726f, -129.796f,  304.938f, sub: true),
            Chest(  41.233f, -140.771f,  168.502f, sub: true),
            Chest( 161.000f, -151.760f,   16.000f, sub: true),
            Chest( 223.653f, -161.864f,  -30.644f, Rarity.Silver, sub: true),
            Chest( 313.919f, -139.530f,  180.071f, sub: true),
        ];

        // The underground tail of the purple path, drawn alone when the North Subterrane map is displayed.
        private static readonly PathNode[] NorthHornPurpleSubPath =
            NorthHornPurplePath.Where(n => n.Subterrane).ToArray();

        private static readonly PathNode[] NorthHornGreenPath =
        [
            Aetheryte(-504.893f, 608.131f),
            Chest(-612.214f,  66.990f,  578.548f),
            Chest(-504.091f,  85.753f,  758.321f),
            Bend(-429.842f, 902.792f),
            Chest(-592.000f, 160.101f,  767.668f),
            Chest(-645.440f, 160.099f,  967.943f, Rarity.Silver),
            Chest(-699.837f, 160.000f,  926.379f),
            Chest(-857.793f, 159.850f,  772.237f),
            Chest(-800.396f, 157.800f,  633.387f),
            Chest(-775.894f,  70.719f,  377.153f),
            Chest(-923.142f, 113.265f,  197.947f),
            Bend(-746.726f, 179.722f),
            Chest(-631.779f,  78.255f,  240.000f),
            Bend(-644.573f, 81.502f),
            Chest(-590.207f,  87.979f,   -7.000f),
            Chest(-633.696f,  82.718f, -146.005f, Rarity.Silver),
            Chest(-581.489f,  40.914f, -257.411f),
        ];

        private static readonly PathNode[] NorthHornOrangePath =
        [
            Aetheryte(468.693f, 566.335f),
            Chest( 447.886f,  62.906f,  463.345f),
            Chest( 246.227f,  66.542f,  676.666f),
            Chest(  77.070f,  21.200f,  536.269f),
            Chest( -22.669f,  42.087f,  628.995f, Rarity.Silver),
            Chest(-278.056f,  47.784f,  567.973f),
            Chest(-256.947f, 100.667f,  812.197f),
            Chest( -12.099f,  66.651f,  773.862f),
            Chest( 222.912f,  90.400f,  913.629f),
        ];
    }
}
