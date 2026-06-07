using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using YABOT.FeaturesSetup;
using YABOT.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace YABOT.Features.DeepDungeons
{
    // Draws a navmesh path from you to nearby coffers in a deep dungeon, colored by tier. Pathfinding is
    // delegated to the vnavmesh plugin over IPC (vnavmesh.Nav.Pathfind), which builds the mesh reliably;
    // we only request paths and draw them - the character is never moved.
    public unsafe class DeepDungeonChestPath : BaseFeature
    {
        public override string Name => "Deep Dungeon Chest Paths";

        public override string Description =>
            "Draws a path from you to nearby bronze/silver/gold coffers while in a deep dungeon. " +
            "Requires the vnavmesh plugin (used only to compute the path - your character is not moved).";

        public override FeatureType FeatureType => FeatureType.DeepDungeons;

        public enum ChestTier { Bronze, Silver, Gold }

        public class Configs : FeatureConfig
        {
            public bool ShowBronze = true;
            public bool ShowSilver = true;
            public bool ShowGold = true;
            public bool ShowPassage = true;
            public float MaxRange = 100f;
            public int MaxPaths = 8;
            public int Thickness = 4;
            public int Opacity = 90;
            public bool ShowEta = true;
            public float WalkSpeed = 6f; // yalms/sec; FFXIV base run speed is ~6
        }

        public Configs Config { get; private set; } = null!;
        private Overlays Overlay = null!;

        // --- NecroLens DataId tables (Jukkales/NecroLens util/DataIds.cs) -------------------------------
        private const uint SilverChestDataId = 2007357;
        private const uint GoldChestDataId = 2007358;

        private static readonly HashSet<uint> BronzeChestDataIds = new()
        {
            782, 783, 784, 785, 786, 787, 788, 789, 790, 802, 803, 804, 805,                 // PotD
            1036, 1037, 1038, 1039, 1040, 1041, 1042, 1043, 1044, 1045, 1046, 1047, 1048, 1049, // HoH
            1541, 1542, 1543, 1544, 1545, 1546, 1547, 1548, 1549, 1550, 1551, 1552, 1553, 1554, // Eureka Orthos
            1882, 1884, 1885, 1886, 1888, 1889, 1890, 1891, 1892, 1893, 1906, 1907, 1908,       // Pilgrim's Traverse
        };

        // Beacon of Passage (stairs to the next floor), per deep dungeon.
        private static readonly HashSet<uint> PassageDataIds = new() { 2007188, 2009507, 2013287, 2014756 };

        private static readonly Vector3 PassageRgb = new(0f, 0.8f, 0.8f); // teal

        public static ChestTier? ClassifyChest(uint dataId)
        {
            if (dataId == GoldChestDataId) return ChestTier.Gold;
            if (dataId == SilverChestDataId) return ChestTier.Silver;
            if (BronzeChestDataIds.Contains(dataId)) return ChestTier.Bronze;
            return null;
        }

        private static readonly Vector3 BronzeRgb = new(0.722f, 0.451f, 0.200f);
        private static readonly Vector3 SilverRgb = new(0.831f, 0.835f, 0.847f);
        private static readonly Vector3 GoldRgb = new(0.953f, 0.788f, 0.149f);

        // --- vnavmesh IPC ------------------------------------------------------------------------------
        private ICallGateSubscriber<bool>? _navIsReady;
        private ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>? _navPathfind;

        private class ChestPath
        {
            public ChestTier Tier;
            public Vector3 Target;
            public Vector3 ComputedFrom;
            public Task<List<Vector3>>? Task;
            public List<Vector3>? Path;
            public float Length = -1f; // total walked path length (yalms); -1 = none yet
        }

        private readonly Dictionary<ulong, ChestPath> _paths = new();
        private ChestPath? _passage; // path to the beacon of passage (always shown)
        private DateTime _lastScan = DateTime.MinValue;

        // Recompute a path when the player has drifted this far from where it was last computed.
        private const float RecomputeMove = 3f;

        // Coffers this close (in walk time) are hidden and stop being recomputed - you're basically there.
        private const float MinEtaSeconds = 6f;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            _navIsReady = Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            _navPathfind = Svc.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");
            Overlay = new(this);
            Svc.Framework.Update += OnUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= OnUpdate;
            _paths.Clear();
            if (Overlay != null)
            {
                P.Ws.RemoveWindow(Overlay);
                Overlay = null!;
            }
            base.Disable();
        }

        private bool InDeepDungeon() => EventFramework.Instance()->GetInstanceContentDeepDungeon() != null;

        private bool NavReady()
        {
            try { return _navIsReady?.InvokeFunc() ?? false; }
            catch { return false; } // vnavmesh not installed / not loaded
        }

        private Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to)
        {
            try { return _navPathfind?.InvokeFunc(from, to, false); }
            catch { return null; }
        }

        private bool TierEnabled(ChestTier t) => t switch
        {
            ChestTier.Bronze => Config.ShowBronze,
            ChestTier.Silver => Config.ShowSilver,
            ChestTier.Gold => Config.ShowGold,
            _ => false,
        };

        private static Vector3 TierRgb(ChestTier t) => t switch
        {
            ChestTier.Bronze => BronzeRgb,
            ChestTier.Silver => SilverRgb,
            _ => GoldRgb,
        };

        private static bool InCombat => Svc.Condition[ConditionFlag.InCombat];

        private static float PathLength(List<Vector3> p)
        {
            var len = 0f;
            for (var i = 1; i < p.Count; i++) len += Vector3.Distance(p[i - 1], p[i]);
            return len;
        }

        // Promote a finished pathfind into the cached path + length (keep the old one until it lands).
        private static void Promote(ChestPath cp)
        {
            if (cp.Task is { IsCompletedSuccessfully: true } t && t.Result is { Count: > 0 } r)
            {
                cp.Path = r;
                cp.Length = PathLength(r);
            }
        }

        private void MaybePathfind(ChestPath cp, Vector3 from)
        {
            var stale = cp.Path == null || Vector3.Distance(from, cp.ComputedFrom) > RecomputeMove;
            var busy = cp.Task is { IsCompleted: false };
            if (stale && !busy)
            {
                cp.ComputedFrom = from;
                cp.Task = Pathfind(from, cp.Target);
            }
        }

        // Rescan nearby coffers + the passage a few times a second and (re)request paths for them.
        private void OnUpdate(IFramework framework)
        {
            try
            {
                if (Player.Object is not { } player || !InDeepDungeon())
                {
                    if (_paths.Count > 0) _paths.Clear();
                    _passage = null;
                    return;
                }
                if (!NavReady()) return;

                var now = DateTime.Now;
                if ((now - _lastScan).TotalSeconds < 0.5) return;
                _lastScan = now;

                // Nothing is pathed or drawn in combat.
                if (InCombat) { _paths.Clear(); _passage = null; return; }

                var pp = player.Position;

                // Beacon of passage - pathed independent of range / ETA cutoff (but not in combat).
                if (Config.ShowPassage)
                {
                    var beacon = Svc.Objects.FirstOrDefault(o => PassageDataIds.Contains(o.DataId));
                    if (beacon != null)
                    {
                        _passage ??= new ChestPath();
                        _passage.Target = beacon.Position;
                        Promote(_passage);
                        MaybePathfind(_passage, pp);
                    }
                    else _passage = null;
                }
                else _passage = null;

                var coffers = Svc.Objects
                    .Select(o => (o, tier: ClassifyChest(o.DataId)))
                    .Where(x => x.tier != null && TierEnabled(x.tier.Value))
                    .Select(x => (x.o.GameObjectId, x.tier!.Value, x.o.Position, dist: Vector3.Distance(x.o.Position, pp)))
                    .Where(x => x.dist <= Config.MaxRange)
                    .OrderBy(x => x.dist)
                    .Take(Math.Max(1, Config.MaxPaths))
                    .ToList();

                var present = new HashSet<ulong>();
                foreach (var (id, tier, pos, _) in coffers)
                {
                    present.Add(id);
                    if (!_paths.TryGetValue(id, out var cp))
                    {
                        cp = new ChestPath { Tier = tier };
                        _paths[id] = cp;
                    }
                    cp.Tier = tier;
                    cp.Target = pos;
                    Promote(cp);

                    // Too close (under the ETA cutoff) -> stop recomputing; Draw also hides it.
                    if (cp.Path != null && cp.Length >= 0 && cp.Length / Config.WalkSpeed < MinEtaSeconds)
                        continue;
                    MaybePathfind(cp, pp);
                }

                foreach (var k in _paths.Keys.Where(k => !present.Contains(k)).ToList())
                    _paths.Remove(k);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] update failed");
            }
        }

        public override bool DrawConditions() => Player.Object != null && InDeepDungeon();

        public override void Draw()
        {
            try
            {
                if (InCombat) return; // everything hidden in combat

                var alpha = Math.Clamp(Config.Opacity / 100f, 0.2f, 1f);
                var drawList = ImGui.GetForegroundDrawList();

                // Passage line - teal, animated flow toward the stairs.
                if (Config.ShowPassage && _passage?.Path is { Count: > 0 })
                {
                    Promote(_passage);
                    DrawPath(drawList, _passage.Path!, ImGui.GetColorU32(new Vector4(PassageRgb, alpha)), _passage.Length, Config.Thickness, dotted: false, animPxPerSec: 60f);
                }

                var chestThickness = Math.Max(1f, Config.Thickness * 0.5f);
                foreach (var cp in _paths.Values)
                {
                    Promote(cp);
                    var path = cp.Path;
                    if (path == null || path.Count < 1) continue;
                    if (cp.Length >= 0 && cp.Length / Config.WalkSpeed < MinEtaSeconds) continue; // too close

                    var color = ImGui.GetColorU32(new Vector4(TierRgb(cp.Tier), alpha));
                    DrawPath(drawList, path, color, cp.Length, chestThickness, dotted: true, animPxPerSec: 0f);
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] draw failed");
            }
        }

        private void DrawPath(ImDrawListPtr drawList, List<Vector3> path, uint color, float length, float thickness, bool dotted, float animPxPerSec)
        {
            DrawPatterned(drawList, path, color, thickness, dotted, animPxPerSec);

            var target = path[^1];
            if (Svc.GameGui.WorldToScreen(target, out var dest))
            {
                drawList.AddCircleFilled(dest, thickness * 1.8f, color);
                if (Config.ShowEta && Config.WalkSpeed > 0.1f && length >= 0)
                {
                    var label = FormatEta(length / Config.WalkSpeed);
                    var pos = dest + new Vector2(thickness * 2f + 2f, -ImGui.GetTextLineHeight() * 0.5f);
                    drawList.AddText(pos + new Vector2(1, 1), 0xFF000000, label); // shadow
                    drawList.AddText(pos, color, label);
                }
            }
        }

        // Draws the polyline as evenly spaced dots, or as dashes that scroll toward the target when
        // animPxPerSec > 0. A running arc-length keeps the pattern continuous across segments.
        private static void DrawPatterned(ImDrawListPtr drawList, List<Vector3> path, uint color, float thickness, bool dotted, float animPxPerSec)
        {
            var dash = dotted ? thickness : Math.Max(10f, thickness * 3f);
            var gap = dotted ? Math.Max(8f, thickness * 3f) : dash;
            var pattern = dash + gap;
            // Scrolls the dash origin over time; negative so dashes travel toward the destination.
            var anim = animPxPerSec != 0f ? -(Environment.TickCount64 % 1_000_000) / 1000f * animPxPerSec : 0f;
            var consumed = anim;

            for (var i = 1; i < path.Count; i++)
            {
                if (!Svc.GameGui.WorldToScreen(path[i - 1], out var a) ||
                    !Svc.GameGui.WorldToScreen(path[i], out var b))
                    continue;
                var vec = b - a;
                var segLen = vec.Length();
                if (segLen < 0.01f) continue;
                var dir = vec / segLen;

                for (var s = -Mod(consumed, pattern); s < segLen; s += pattern)
                {
                    var ds = Math.Max(s, 0f);
                    var de = Math.Min(s + dash, segLen);
                    if (de <= ds) continue;
                    if (dotted)
                        drawList.AddCircleFilled(a + dir * ((ds + de) * 0.5f), thickness, color);
                    else
                        drawList.AddLine(a + dir * ds, a + dir * de, color, thickness);
                }
                consumed += segLen;
            }
        }

        private static float Mod(float a, float b) => (a % b + b) % b;

        private static string FormatEta(float seconds)
        {
            if (seconds < 60f) return $"{seconds:0}s";
            var m = (int)(seconds / 60f);
            return $"{m}m {seconds - m * 60:00}s";
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (!NavReady())
            {
                ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.25f, 1f),
                    "vnavmesh not detected. Install/enable it - it computes the path.");
                ImGui.Separator();
            }

            if (ImGui.Checkbox("Bronze coffers", ref Config.ShowBronze)) hasChanged = true;
            if (ImGui.Checkbox("Silver coffers", ref Config.ShowSilver)) hasChanged = true;
            if (ImGui.Checkbox("Gold coffers", ref Config.ShowGold)) hasChanged = true;
            if (ImGui.Checkbox("Beacon of passage (teal, always shown)", ref Config.ShowPassage)) hasChanged = true;

            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Max range (yalms)", ref Config.MaxRange, 20f, 200f, "%.0f")) hasChanged = true;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Max paths", ref Config.MaxPaths, 1, 16)) hasChanged = true;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Line thickness", ref Config.Thickness, 1, 10)) hasChanged = true;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Opacity %", ref Config.Opacity, 20, 100)) hasChanged = true;

            if (ImGui.Checkbox("Show walk-time estimate", ref Config.ShowEta)) hasChanged = true;
            if (Config.ShowEta)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Walk speed (yalms/s)", ref Config.WalkSpeed, 3f, 12f, "%.1f")) hasChanged = true;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Base run speed is ~6. Raise it if you sprint a lot.");
                ImGui.Unindent();
            }
        };
    }
}
