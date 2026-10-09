using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using YABOT.FeaturesSetup;
using YABOT.UI;

namespace YABOT.Features.Events;

// Stage 3 is a port of the AoE prediction from awgil/ffxiv_vfallguy (Map.cs, Map3.cs, Geom.cs).
// Upstream passes header->ActionType instead of header->ActionId to its action-effect handler, so only
// the cast-driven rect sequence ever got timings; fixed here. vfallguy's voxel pathfinder is not ported:
// upstream has it commented out and builds the path from the hand-written lane script kept below.
// Stage 2 (crystal courier) is not in vfallguy; its data comes from our own recordings.
public unsafe class FallGuysAoeOverlay : BaseFeature
{
    public override string Name => "Fall Guys AoE Markers";

    public override string Description =>
        "On stages 2 and 3 of the Fall Guys collaboration event, outlines obstacles about to go off with a countdown, filled red while dangerous. " +
        "Outlines are green while safe and yellow just before they get dangerous. " +
        "Stage 3 also draws a route through the lanes once the first few mechanics have been seen.";

    public override FeatureType FeatureType => FeatureType.Events;

    private const uint TerritoryId = 1165;
    private const float InvSpeed = 1f / 6; // run speed 6 y/s
    private const float SafetyMargin = 0.5f; // AoEs count as this much bigger when predicting hits
    // The server checks hits against where it last saw you, ~0.2s behind your screen (recordings: hit 1.2y outside
    // a square at run speed), so treat AoEs as dangerous this long before their effect arrives.
    private const float LatencyLead = 0.3f;
    private const uint AoeSafeColor = 0xff00ff00; // ABGR green
    private const uint AoeWarnColor = 0xff00ffff; // ABGR yellow
    private const float WarnTime = 0.5f;          // seconds before getting dangerous that a marker turns yellow
    private const uint AoeHitColor = 0xff0000ff;  // ABGR red
    private const uint PathColor = 0xff00ff00;    // ABGR green
    private const uint AoeActiveFill = 0x600000ff; // ABGR translucent red

    private Overlays Overlay = null!;
    private Hook<ActionEffectHandler.Delegates.Receive>? actionEffectHook;
    private Stage? stage;
    private HashSet<ulong> casting = [];

    public override void Enable()
    {
        Overlay = new(this);
        actionEffectHook ??= Svc.Hook.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
            (nint)ActionEffectHandler.MemberFunctionPointers.Receive, ActionEffectDetour);
        actionEffectHook.Enable();
        base.Enable();
    }

    public override void Disable()
    {
        if (Overlay != null)
        {
            P.Ws.RemoveWindow(Overlay);
            Overlay = null!;
        }
        actionEffectHook?.Disable();
        actionEffectHook?.Dispose();
        actionEffectHook = null;
        stage = null;
        casting.Clear();
        base.Disable();
    }

    public override bool DrawConditions() =>
        Svc.ClientState.TerritoryType == TerritoryId && !Svc.Condition[ConditionFlag.BetweenAreas] && Player.Available;

    private void ActionEffectDetour(uint casterEntityId, Character* casterPtr, Vector3* targetPos, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        actionEffectHook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        try
        {
            if (stage != null && casterPtr != null && header->ActionType == (byte)ActionType.Action)
                stage.OnActionEffect(header->ActionId, casterPtr->GameObject.Position);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{Name}] action effect handling failed");
        }
    }

    public override void Draw()
    {
        try
        {
            var pos = Player.Position;
            var stageType = pos switch
            {
                { X: >= -40 and <= 40, Z: >= 100 and <= 350 } => typeof(Stage3),
                { X: >= -250 and <= -150, Z: >= 195 and <= 285 } => typeof(Stage2),
                _ => null,
            };
            if (stage?.GetType() != stageType)
            {
                stage = stageType != null ? (Stage)Activator.CreateInstance(stageType)! : null;
                casting.Clear();
            }
            if (stage == null)
                return;
            PollCasts();

            var dl = ImGui.GetBackgroundDrawList();
            var now = DateTime.Now;

            // Route segments turn red when walking them without stopping, starting now, would put you in an AoE as it fires.
            var from = pos;
            var startIn = 0f;
            foreach (var wp in stage.BuildPath(pos))
            {
                var seg = wp - from;
                var len = LengthXZ(seg);
                var hit = len > 0.01f && stage.Aoes.Any(a => HitWhileWalking(a, now, from, seg / len, startIn, len));
                DrawLine(dl, from, wp, hit ? AoeHitColor : PathColor);
                startIn += len * InvSpeed;
                from = wp;
            }

            // Upcoming: outline with a countdown, green, yellow in the last WarnTime before it gets dangerous.
            // Dangerous now: filled red.
            foreach (var aoe in stage.Aoes)
            {
                var untilActivation = (float)(aoe.NextActivation - now).TotalSeconds;
                if (aoe.NextActivation == default || untilActivation >= 2.5 || untilActivation < -aoe.Hold)
                    continue;
                var untilDangerous = untilActivation - LatencyLead;
                if (untilDangerous <= 0)
                {
                    DrawAoe(dl, aoe, AoeHitColor, AoeActiveFill, null);
                    continue;
                }
                DrawAoe(dl, aoe, untilDangerous <= WarnTime ? AoeWarnColor : AoeSafeColor, 0, untilDangerous.ToString("0.0", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{Name}] Draw failed");
        }
    }

    // Upstream hooks the StartCast function by signature; polling the object table for new casts avoids the sig.
    private void PollCasts()
    {
        var current = new HashSet<ulong>();
        foreach (var bc in Svc.Objects.OfType<IBattleChara>())
        {
            if (!bc.IsCasting)
                continue;
            current.Add(bc.GameObjectId);
            if (!casting.Contains(bc.GameObjectId))
                stage!.OnCast(bc.CastActionId, bc.Position);
        }
        casting = current;
    }

    // True for any point in front of the camera, on screen or not (ImGui clips the rest); points behind the
    // camera project to garbage, so pieces touching them are skipped.
    private static bool ToScreen(Vector3 world, out Vector2 screen) => Svc.GameGui.WorldToScreen(world, out screen, out _);

    private static void DrawLine(ImDrawListPtr dl, Vector3 a, Vector3 b, uint color)
    {
        if (ToScreen(a, out var sa) && ToScreen(b, out var sb))
            dl.AddLine(sa, sb, color, 2);
    }

    private static void DrawAoe(ImDrawListPtr dl, Aoe aoe, uint color, uint fill, string? label)
    {
        if (fill != 0)
        {
            foreach (var (a, b, c, d) in aoe.FillPieces())
            {
                if (ToScreen(a, out var sa) && ToScreen(b, out var sb)
                    && ToScreen(c, out var sc) && ToScreen(d, out var sd))
                    dl.AddQuadFilled(sa, sb, sc, sd, fill);
            }
        }
        var pts = aoe.Outline();
        for (var i = 0; i < pts.Length; i++)
            DrawLine(dl, pts[i], pts[(i + 1) % pts.Length], color);
        if (label != null && ToScreen(aoe.Origin, out var center))
            dl.AddText(center - ImGui.CalcTextSize(label) / 2, color, label);
    }

    // Walking from start along dir for at most maxDist, setting off startIn seconds from now: is the aoe
    // dangerous while we're inside it (with a 0.1s window on both sides)?
    private static bool HitWhileWalking(Aoe aoe, DateTime now, Vector3 start, Vector3 dir, float startIn, float maxDist)
    {
        var (enter, exit) = aoe.Intersect(start, dir);
        if (float.IsNaN(enter))
            return false;
        enter = Math.Max(enter, 0);
        exit = Math.Min(exit, maxDist);
        return enter <= exit && aoe.DangerousDuring(now, startIn + enter * InvSpeed - 0.1f, startIn + exit * InvSpeed + 0.1f);
    }

    private static float DotXZ(Vector3 a, Vector3 b) => a.X * b.X + a.Z * b.Z;
    private static float LengthXZ(Vector3 v) => MathF.Sqrt(DotXZ(v, v));

    // Circle: R = radius. Rect: centered on Origin, R = half-length along Rotation, HalfWidth across it
    // (halfWidth < 0 means a square). Rotation follows the game: 0 faces +Z, direction = (sin, cos).
    private sealed class Aoe(bool circle, float r, Vector3 origin, float seqDelay = 0, float halfWidth = -1, float rotation = 0)
    {
        public readonly bool Circle = circle;
        public readonly float R = r;
        public readonly float HalfWidth = halfWidth < 0 ? r : halfWidth;
        public readonly Vector3 Origin = origin;
        public readonly Vector3 Forward = new(MathF.Sin(rotation), 0, MathF.Cos(rotation));
        public readonly Vector3 Right = new(MathF.Cos(rotation), 0, -MathF.Sin(rotation));
        public readonly float SeqDelay = seqDelay; // delay until the next aoe in the sequence
        public float Repeat;                       // seconds between activations of this aoe
        public float Hold = 0.3f;                  // stays dangerous this long after activation (e.g. a charge still travelling)
        public DateTime NextActivation;

        // Is it dangerous at any point between from and to (seconds from now)? Each activation is dangerous
        // for [activation - LatencyLead, activation + Hold], repeating every Repeat seconds.
        public bool DangerousDuring(DateTime now, float from, float to)
        {
            if (NextActivation == default || to < 0)
                return false;
            var a = (float)(NextActivation - now).TotalSeconds - LatencyLead;
            var len = LatencyLead + Hold;
            if (a + len < from && Repeat > 0)
                a += MathF.Ceiling((from - a - len) / Repeat) * Repeat;
            return a <= to && a + len >= from;
        }

        // distances along dir where the ray enters/exits the shape (NaN if it misses)
        // Intersect uses the shape grown by SafetyMargin; Outline draws the real shape.
        public (float enter, float exit) Intersect(Vector3 start, Vector3 dir)
        {
            var oa = start - Origin;
            if (Circle)
            {
                var r = R + SafetyMargin;
                var b = DotXZ(dir, oa);
                var d = MathF.Sqrt(b * b - DotXZ(oa, oa) + r * r);
                return (-b - d, -b + d);
            }
            var (ex, xx) = Slab(DotXZ(oa, Right), DotXZ(dir, Right), HalfWidth + SafetyMargin);
            var (ez, xz) = Slab(DotXZ(oa, Forward), DotXZ(dir, Forward), R + SafetyMargin);
            return float.IsNaN(ex) || float.IsNaN(ez) ? (float.NaN, float.NaN) : (Math.Max(ex, ez), Math.Min(xx, xz));
        }

        private static (float, float) Slab(float o, float d, float h) => d switch
        {
            > 0.05f => ((-h - o) / d, (h - o) / d),
            < -0.05f => ((h - o) / d, (-h - o) / d),
            _ => Math.Abs(o) <= h ? (float.MinValue, float.MaxValue) : (float.NaN, float.NaN),
        };


        // Shapes are cut into ~1y pieces so only the parts behind the camera get dropped (a long slider rect
        // drawn from 4 corners lost whole edges and fill triangles when one corner was behind the camera).
        public Vector3[] Outline()
        {
            if (!Circle)
            {
                Vector3 f = Forward * R, r = Right * HalfWidth;
                Vector3[] corners = [Origin - f - r, Origin + f - r, Origin + f + r, Origin - f + r];
                return corners.SelectMany((c, i) =>
                {
                    var next = corners[(i + 1) % 4];
                    var n = (int)MathF.Ceiling(LengthXZ(next - c));
                    return Enumerable.Range(0, n).Select(k => Vector3.Lerp(c, next, (float)k / n));
                }).ToArray();
            }
            var pts = new Vector3[32];
            for (var i = 0; i < pts.Length; i++)
            {
                var a = i * 2 * MathF.PI / pts.Length;
                pts[i] = Origin + R * new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
            }
            return pts;
        }

        // Fill as quads: circle = fan slices (center doubled), rect = strips across its length.
        public IEnumerable<(Vector3, Vector3, Vector3, Vector3)> FillPieces()
        {
            if (Circle)
            {
                var pts = Outline();
                for (var i = 0; i < pts.Length; i++)
                    yield return (Origin, pts[i], pts[(i + 1) % pts.Length], Origin);
                yield break;
            }
            var n = (int)MathF.Ceiling(2 * R);
            var r = Right * HalfWidth;
            for (var i = 0; i < n; i++)
            {
                var a = Origin + Forward * (-R + 2 * R * i / n);
                var b = Origin + Forward * (-R + 2 * R * (i + 1) / n);
                yield return (a - r, b - r, b + r, a + r);
            }
        }
    }

    private sealed class Sequence(int start, int count)
    {
        public readonly int Start = start;
        public readonly int Count = count;
        public int FirstIndex = -1; // index of the first aoe seen firing, drives the lane choice
    }

    private abstract class Stage
    {
        public readonly List<Aoe> Aoes = [];

        public abstract void OnActionEffect(uint actionId, Vector3 casterPos);
        public virtual void OnCast(uint actionId, Vector3 casterPos) { }

        // waypoints to draw after the player's position; stage 2 has no route (timing is what matters there)
        public virtual IEnumerable<Vector3> BuildPath(Vector3 playerPos) => [];
    }

    private sealed class Stage3 : Stage
    {
        private const uint RectsCastId = 34812;

        private readonly Sequence mech1Rotating, mech2Exaflares, mech3Rotating, mech3Exaflares;
        private readonly Sequence mech4RectsL, mech4RectsR, mech4RectsC, mech4Exaflare;
        private readonly Sequence mech5PairL1, mech5PairL2, mech5PairR1, mech5PairR2;
        private readonly Sequence mech6Exaflare, mech7PairL, mech7PairR;

        private static readonly (float z, float y)[] HeightProfile =
        [
            (135.6f, 36.2f), (139.0f, 35.5f), (139.1f, 34.5f), (143.0f, 34.3f), (143.1f, 33.6f), (147.0f, 33.4f),
            (147.1f, 32.8f), (150.9f, 32.5f), (151.0f, 31.9f), (180.7f, 28.8f), (218.9f, 15.1f), (229.7f, 14.5f),
            (236.8f, 13.5f), (262.9f, 6.0f), (273.2f, 6.0f), (286.9f, 3.2f),
        ];

        // Gaps measured from our own recording (upstream's were 1.2 / 1.0 for the rotating circles, 1.4 for exaflares,
        // 0.5 / 1.1 / 4.2 for the rects and 2.5 for the pairs, with the long single-exaflare gap after the wrong one).
        public Stage3()
        {
            mech1Rotating = Rotating(1.27f, 6, 267.5f, -10, 10);
            mech2Exaflares = DoubleExaflare(9.39f, 251, 11.75f, 243);
            mech3Rotating = Rotating(1.09f, 14.76f, 225.3f, -10, 0, 10);
            mech3Exaflares = DoubleExaflare(14.54f, 229.3f, 14.97f, 221.3f);
            mech4RectsL = Rects(-12, -6);
            mech4RectsR = Rects(12, 6);
            mech4RectsC = RectsCenter();
            mech4Exaflare = SingleExaflare(22.52f, 198.7f, (-12, 1.354f), (-4, 1.868f), (4, 1.405f), (12, 1.383f));
            mech5PairL1 = Pairs(5, new(-10, 29.95f, 170), new(-2, 29.95f, 170));
            mech5PairL2 = Pairs(5, new(-10, 31.39f, 156), new(-4.34f, 30.81f, 161.66f));
            mech5PairR1 = Pairs(5, new(10, 29.95f, 170), new(4.34f, 29.37f, 175.66f));
            mech5PairR2 = Pairs(5, new(10, 31.39f, 156), new(2, 31.39f, 156));
            mech6Exaflare = SingleExaflare(33.47f, 145.7f, (-4, 1.405f), (4, 1.383f), (12, 1.354f), (-12, 1.868f));
            mech7PairL = Pairs(3, new(-6.78f, 35.91f, 136.91f), new(-3.22f, 36.30f, 135.09f));
            mech7PairR = Pairs(3, new(6.78f, 35.91f, 136.91f), new(3.22f, 36.30f, 135.09f));
        }

        public override void OnActionEffect(uint actionId, Vector3 casterPos)
        {
            switch (actionId)
            {
                case 34801:
                    Update(casterPos, 0, mech2Exaflares, mech3Exaflares, mech4Exaflare, mech6Exaflare);
                    break;
                case 34802:
                    Update(casterPos, 0, mech1Rotating, mech3Rotating);
                    break;
                case 34804:
                case RectsCastId:
                    Update(casterPos, 0, mech4RectsL, mech4RectsR, mech4RectsC);
                    break;
                case 34796:
                    Update(casterPos, 0, mech5PairL1, mech5PairL2, mech5PairR1, mech5PairR2);
                    break;
                case 29795:
                    Update(casterPos, 0, mech7PairL, mech7PairR);
                    break;
            }
        }

        public override void OnCast(uint actionId, Vector3 casterPos)
        {
            if (actionId == RectsCastId)
                Update(casterPos, 1, mech4RectsL, mech4RectsR);
        }

        // The course runs down (decreasing Z); skip the waypoints already passed.
        public override IEnumerable<Vector3> BuildPath(Vector3 playerPos) => Waypoints().Where(wp => wp.Z < playerPos.Z);

        private List<Vector3> Waypoints()
        {
            var res = new List<Vector3>();
            if (mech2Exaflares.FirstIndex < 0 || mech3Exaflares.FirstIndex < 0 || mech4RectsL.FirstIndex < 0 || mech4Exaflare.FirstIndex < 0)
                return res;

            var mech3Left = mech3Exaflares.FirstIndex is 1 or 2 or 3 or 6 or 7;
            var lane1 = mech2Exaflares.FirstIndex switch
            {
                0 or 4 => mech3Left ? 1 : 4,
                1 or 5 => mech3Left ? 1 : 2,
                2 or 6 => mech3Left ? 2 : 3,
                3 or 7 => mech3Left ? 3 : 4,
                _ => 0,
            };
            var initialLeft = lane1 <= 2;
            // by the time we reach the bottom of the rects ramp the full cycle has ended - if it started outside, we'll be at '2'
            var mech4InnerRectsWhenReached = mech4RectsL.FirstIndex < 5;
            var lane2 = (mech4Exaflare.FirstIndex + (mech4InnerRectsWhenReached ? 0 : 1)) % 4 + 1;

            void MoveTo(float x, float z) => res.Add(new(x, HeightAt(z), z));

            MoveTo(initialLeft ? -5.5f : 5.5f, 270.5f);
            MoveTo(initialLeft ? -5.5f : 5.5f, 263.5f);
            var lane1X = lane1 switch { 1 => -9.5f, 2 => -5.5f, 3 => 5.5f, 4 => 9.5f, _ => 0 };
            MoveTo(lane1X, 255.5f);
            var lane1EndX = lane1 switch
            {
                2 => mech3Left ? -5.5f : -2f,
                3 => mech3Left ? 2f : 5.5f,
                _ => lane1X,
            };
            MoveTo(lane1EndX, 238.5f);
            MoveTo(mech3Left ? -5 : 5, 228);
            MoveTo(mech3Left ? -5 : 5, 223);

            if (mech4InnerRectsWhenReached)
            {
                MoveTo(lane2 <= 2 ? -6 : 6, 203.5f);
                MoveTo(lane2 switch { 1 => -12.5f, 4 => 12.5f, _ => 0 }, 194.5f);
                MoveTo(lane2 switch { 1 => -12f, 2 => -3.5f, 3 => 3.5f, 4 => 12f, _ => 0 }, 188);
            }
            else
            {
                var lowX = lane2 switch
                {
                    1 => mech3Left ? -12 : 0,
                    4 => mech3Left ? 0 : 12,
                    _ => 0,
                };
                MoveTo(lowX, 212);
                MoveTo(lowX, 208);
                MoveTo(lane2 switch { 1 => -12, 2 => -6, 3 => 6, 4 => 12, _ => 0 }, 203);
            }

            var lane2X = lane2 switch { 1 => -12, 2 => -5, 3 => 5, 4 => 12, _ => 0 };
            MoveTo(lane2X, 145.5f);
            MoveTo(lane2X <= 2 ? -6.5f : 6.5f, 135);
            MoveTo(lane2X <= 2 ? -8.5f : 8.5f, 124);
            return res;
        }

        private static float HeightAt(float z)
        {
            var idx = Array.FindIndex(HeightProfile, p => p.z > z);
            if (idx == 0)
                return HeightProfile[0].y;
            if (idx < 0)
                return HeightProfile[^1].y;
            var (z1, y1) = HeightProfile[idx - 1];
            var (z2, y2) = HeightProfile[idx];
            return y1 + (z - z1) / (z2 - z1) * (y2 - y1);
        }

        // A sequence's aoes fire one after another (SeqDelay apart) and loop; seeing one fire pins the timing of all.
        private void Update(Vector3 pos, float activateIn, params Sequence[] candidates)
        {
            foreach (var c in candidates)
            {
                var index = Enumerable.Range(0, c.Count).FirstOrDefault(i => (Aoes[c.Start + i].Origin - pos).LengthSquared() < 1, -1);
                if (index < 0)
                    continue;
                if (Aoes[c.Start].NextActivation == default)
                    c.FirstIndex = index;

                // A cast start means 'index' fires in activateIn; an effect means 'index' just fired (its own next
                // activation is then a full cycle away). SeqDelay is the gap to the following aoe - upstream applied
                // it as the gap from the previous one on effects, shifting every prediction by one step.
                var t = DateTime.Now.AddSeconds(activateIn);
                if (activateIn > 0)
                    Aoes[c.Start + index].NextActivation = t;
                var n = activateIn > 0 ? c.Count - 1 : c.Count;
                for (var i = 1; i <= n; i++)
                {
                    t = t.AddSeconds(Aoes[c.Start + (index + i - 1) % c.Count].SeqDelay);
                    Aoes[c.Start + (index + i) % c.Count].NextActivation = t;
                }
                return;
            }
            Svc.Log.Debug($"[Fall Guys] no aoe sequence matches {pos}");
        }

        private Sequence Create(bool circle, float r, IEnumerable<(Vector3 pos, float delay)> instances)
        {
            var start = Aoes.Count;
            Aoes.AddRange(instances.Select(i => new Aoe(circle, r, i.pos, i.delay)));
            var repeat = Aoes.Skip(start).Sum(a => a.SeqDelay);
            foreach (var aoe in Aoes.Skip(start))
                aoe.Repeat = repeat;
            return new(start, Aoes.Count - start);
        }

        private Sequence Rotating(float repeat, float y, float z, params float[] xs) =>
            Create(true, 5, xs.Select(x => (new Vector3(x, y, z), repeat)));

        // (x, gap until the next one) in firing order
        private Sequence SingleExaflare(float y, float z, params (float x, float gap)[] xs) =>
            Create(true, 6, xs.Select(e => (new Vector3(e.x, y, z), e.gap)));

        private Sequence DoubleExaflare(float y1, float z1, float y2, float z2)
        {
            float[] xs = [-12, -4, 4, 12];
            return Create(true, 6, xs.Select(x => (new Vector3(x, y1, z1), 1.38f)).Concat(xs.Select(x => (new Vector3(x, y2, z2), 1.38f))));
        }

        private static readonly (float y, float z)[] RectRows = [(25.59f, 190.4f), (23.37f, 196.4f), (21.15f, 202.4f), (18.94f, 208.4f), (16.73f, 214.4f)];

        // Rects fire in waves down the ramp (0.46s per row), alternating between the outer (+-12, 0) and inner (+-6) lanes.
        private Sequence Rects(float x1, float x2)
        {
            IEnumerable<(Vector3, float)> Lane(float x) => RectRows.Select((e, i) => (new Vector3(x, e.y, e.z), i == RectRows.Length - 1 ? 1.13f : 0.46f));
            return Create(false, 3, Lane(x1).Concat(Lane(x2)));
        }

        private Sequence RectsCenter() =>
            Create(false, 3, RectRows.Select((e, i) => (new Vector3(0, e.y, e.z), i == RectRows.Length - 1 ? 4.1f : 0.46f)));

        private Sequence Pairs(float r, Vector3 p1, Vector3 p2) => Create(true, r, [(p1, 2.54f), (p2, 2.54f)]);
    }

    // Stage 2 (crystal courier): a hub with three lanes (north, east, west), each ending at a goal. Every obstacle
    // repeats on a fixed cycle, so each one's next activation is predicted from its own previous ones.
    // Shapes from the Action sheet: CastType 12 = rect centered on the caster, EffectRange long and
    // XAxisModifier wide (matches stage 3's 6x6 squares); CastType 8 = charge from caster to target.
    // Positions and periods measured from recordings; 34800 (the north goal's pulse) is not an obstacle.
    private sealed class Stage2 : Stage
    {
        private const uint SweeperId = 34716; // 3x2 rects, three in a row firing as a sweep out and back
        private const uint SquareId = 34799;  // 4x4 squares
        private const uint SliderId = 34774;  // block charging between two endpoints, 3 wide
        private const float SweeperPeriod = 4.205f;

        private sealed class Timed(uint actionId, Vector3 firePos, Aoe aoe, float period, int perCycle)
        {
            public readonly uint ActionId = actionId;
            public readonly Vector3 FirePos = firePos; // caster position reported when it fires
            public readonly Aoe Aoe = aoe;
            public readonly float Period = period;
            public readonly int PerCycle = perCycle;   // activations per period (sweeper pieces fire on the way out and back)
            public readonly List<DateTime> Fired = [];
        }

        private readonly List<Timed> timed = [];

        public Stage2()
        {
            // north
            Sweeper(1.5708f, new(-203, 6, 226.9f), new(-200, 6, 226.9f), new(-197, 6, 226.9f));
            Squares(3.70f, 0, new(-204, 6, 216.5f), new(-196, 6, 216.5f));
            // east
            Sweeper(-0.5236f, new(-187.13f, 6, 255.99f), new(-188.63f, 6, 258.59f), new(-190.13f, 6, 261.19f));
            Sweeper(-0.5236f, new(-168.04f, 6, 266.99f), new(-169.54f, 6, 269.59f), new(-171.03f, 6, 272.19f));
            Slider(8.018f, new(-169.68f, 6, 267.78f), new(-187.0f, 6, 257.77f));
            Slider(8.018f, new(-169.24f, 6, 264.56f), new(-184.53f, 6, 255.73f));
            Slider(5.974f, new(-176.55f, 6, 259.69f), new(-181.66f, 6, 268.48f));
            Slider(8.018f, new(-188.5f, 6, 260.37f), new(-171.19f, 6, 270.34f));
            Slider(8.018f, new(-188.96f, 6, 263.57f), new(-173.75f, 6, 272.36f));
            // west
            Squares(4.101f, 2.0944f, new(-214.68f, 6, 255.91f), new(-212.73f, 6, 259.4f), new(-210.69f, 6, 262.86f));
            Squares(4.101f, 2.0944f, new(-228.55f, 6, 263.9f), new(-226.6f, 6, 267.39f), new(-224.56f, 6, 270.85f));
            Slider(8.018f, new(-221.03f, 6, 269.97f), new(-226.12f, 6, 261.13f));
            Slider(8.018f, new(-218.43f, 6, 268.47f), new(-223.5f, 6, 259.66f));
            Slider(5.974f, new(-207.18f, 6, 261.94f), new(-212.24f, 6, 253.16f));
            Aoes.AddRange(timed.Select(t => t.Aoe));
        }

        public override void OnActionEffect(uint actionId, Vector3 casterPos)
        {
            var t = timed.FirstOrDefault(t => t.ActionId == actionId && LengthXZ(t.FirePos - casterPos) < 1);
            if (t == null)
                return;
            t.Fired.Add(DateTime.Now);
            if (t.Fired.Count > t.PerCycle)
                t.Fired.RemoveAt(0);
            if (t.Fired.Count == t.PerCycle)
                t.Aoe.NextActivation = t.Fired[0].AddSeconds(t.Period);
        }

        private void Add(uint actionId, Vector3 firePos, Aoe aoe, float period, int perCycle)
        {
            aoe.Repeat = period / perCycle;
            timed.Add(new(actionId, firePos, aoe, period, perCycle));
        }

        private void Sweeper(float rot, params Vector3[] pieces)
        {
            foreach (var p in pieces)
                Add(SweeperId, p, new Aoe(false, 1.5f, p, halfWidth: 1, rotation: rot), SweeperPeriod, 2);
        }

        // Squares stay dangerous after firing: a recording has a stun walking in 0.6s after one fired with
        // nobody in it, while crossings 2.4s+ after a firing were clean.
        private void Squares(float period, float rot, params Vector3[] squares)
        {
            foreach (var p in squares)
                Add(SquareId, p, new Aoe(false, 2, p, rotation: rot) { Hold = 1.0f }, period, 1);
        }

        // The block charges a -> b, then b -> a half a period later; it is reported at the start of each charge.
        // The rect covers the travelled segment plus the block's own size at both ends, and stays shown while it travels.
        private void Slider(float period, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var rot = MathF.Atan2(ab.X, ab.Z);
            foreach (var from in new[] { a, b })
                Add(SliderId, from, new Aoe(false, LengthXZ(ab) / 2 + 1.5f, (a + b) / 2, halfWidth: 1.5f, rotation: rot) { Hold = 0.6f }, period, 1);
        }
    }
}
