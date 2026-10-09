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
using System.Linq;
using System.Numerics;
using YABOT.FeaturesSetup;
using YABOT.UI;

namespace YABOT.Features.Events;

// Port of the stage 3 AoE prediction from awgil/ffxiv_vfallguy (Map.cs, Map3.cs, Geom.cs).
// Upstream passes header->ActionType instead of header->ActionId to its action-effect handler, so only
// the cast-driven rect sequence ever got timings; fixed here. vfallguy's voxel pathfinder is not ported:
// upstream has it commented out and builds the path from the hand-written lane script kept below.
public unsafe class FallGuysAoeOverlay : BaseFeature
{
    public override string Name => "Fall Guys Stage 3 AoE Markers";

    public override string Description =>
        "On the last stage of the Fall Guys collaboration event, outlines AoEs about to go off and draws the safe route through the lanes. " +
        "Markers turn red when your current movement would put you inside an AoE as it fires, yellow otherwise. " +
        "The route appears once the first few mechanics have been seen.";

    public override FeatureType FeatureType => FeatureType.Events;

    private const uint TerritoryId = 1165;
    private const uint RectsCastId = 34812;
    private const uint AoeSafeColor = 0xff00ffff; // ABGR yellow
    private const uint AoeHitColor = 0xff0000ff;  // ABGR red
    private const uint PathColor = 0xff00ff00;    // ABGR green

    private Overlays Overlay = null!;
    private Hook<ActionEffectHandler.Delegates.Receive>? actionEffectHook;
    private Stage3? stage;
    private HashSet<ulong> casting = [];
    private Vector3 prevPos;

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
            var delta = pos - prevPos;
            prevPos = pos;
            if (pos.X is < -40 or > 40 || pos.Z is < 100 or > 350)
            {
                stage = null;
                casting.Clear();
                return;
            }
            stage ??= new();
            PollCasts();

            var dl = ImGui.GetBackgroundDrawList();
            var now = DateTime.Now;

            // Path: waypoints run down the course (decreasing Z); skip the ones already passed.
            var from = pos;
            foreach (var wp in stage.BuildPath().Where(wp => wp.Z < pos.Z))
            {
                DrawLine(dl, from, wp, PathColor);
                from = wp;
            }

            Vector3? dir = LengthXZ(delta) > 0.001f ? delta / LengthXZ(delta) : null;
            foreach (var aoe in stage.Aoes)
            {
                if (aoe.NextActivation == default || (aoe.NextActivation - now).TotalSeconds >= 2.5)
                    continue;
                var (enter, exit) = dir is { } d ? aoe.Intersect(pos, d) : aoe.Contains(pos) ? (0f, float.PositiveInfinity) : (float.NaN, float.NaN);
                var hitIn = float.IsNaN(enter) ? 0 : aoe.ActivatesBetween(now, enter * Stage3.InvSpeed - 0.1f, exit * Stage3.InvSpeed + 0.1f);
                DrawAoe(dl, aoe, hitIn > 0 ? AoeHitColor : AoeSafeColor);
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
            if (!bc.IsCasting || bc.CastActionId != RectsCastId)
                continue;
            current.Add(bc.GameObjectId);
            if (!casting.Contains(bc.GameObjectId))
                stage!.OnRectsCast(bc.Position);
        }
        casting = current;
    }

    private static void DrawLine(ImDrawListPtr dl, Vector3 a, Vector3 b, uint color)
    {
        if (Svc.GameGui.WorldToScreen(a, out var sa) && Svc.GameGui.WorldToScreen(b, out var sb))
            dl.AddLine(sa, sb, color, 2);
    }

    private static void DrawAoe(ImDrawListPtr dl, Aoe aoe, uint color)
    {
        Vector3[] pts;
        if (aoe.Circle)
        {
            pts = new Vector3[32];
            for (var i = 0; i < pts.Length; i++)
            {
                var a = i * 2 * MathF.PI / pts.Length;
                pts[i] = aoe.Origin + aoe.R * new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
            }
        }
        else
        {
            var r = aoe.R;
            pts = [aoe.Origin + new Vector3(-r, 0, -r), aoe.Origin + new Vector3(-r, 0, r), aoe.Origin + new Vector3(r, 0, r), aoe.Origin + new Vector3(r, 0, -r)];
        }
        for (var i = 0; i < pts.Length; i++)
            DrawLine(dl, pts[i], pts[(i + 1) % pts.Length], color);
    }

    private static float DotXZ(Vector3 a, Vector3 b) => a.X * b.X + a.Z * b.Z;
    private static float LengthXZ(Vector3 v) => MathF.Sqrt(DotXZ(v, v));

    // Circle: R = radius. Square: R = half-side.
    private sealed class Aoe(bool circle, float r, Vector3 origin, float seqDelay)
    {
        public readonly bool Circle = circle;
        public readonly float R = r;
        public readonly Vector3 Origin = origin;
        public readonly float SeqDelay = seqDelay; // delay until the next aoe in the sequence
        public float Repeat;                       // seconds between activations of this aoe
        public DateTime NextActivation;

        private float TimeUntilNextActivation(DateTime now)
        {
            if (NextActivation == default)
                return float.MaxValue;
            var t = (float)(NextActivation - now).TotalSeconds;
            return t >= 0 ? t : t % Repeat + Repeat;
        }

        // negative if it doesn't fire in [min, max] seconds from now, otherwise seconds between min and activation
        public float ActivatesBetween(DateTime now, float min, float max)
        {
            if (max < 0)
                return -1;
            min = Math.Max(0, min);
            var t = TimeUntilNextActivation(now.AddSeconds(min));
            return t < max - min ? t : -1;
        }

        // distances along dir where the ray enters/exits the shape (NaN if it misses)
        public (float enter, float exit) Intersect(Vector3 start, Vector3 dir)
        {
            var oa = start - Origin;
            if (Circle)
            {
                var b = DotXZ(dir, oa);
                var d = MathF.Sqrt(b * b - DotXZ(oa, oa) + R * R);
                return (-b - d, -b + d);
            }
            var (ex, xx) = Slab(oa.X, dir.X);
            var (ez, xz) = Slab(oa.Z, dir.Z);
            return float.IsNaN(ex) || float.IsNaN(ez) ? (float.NaN, float.NaN) : (Math.Max(ex, ez), Math.Min(xx, xz));
        }

        private (float, float) Slab(float o, float d) => d switch
        {
            > 0.05f => ((-R - o) / d, (R - o) / d),
            < -0.05f => ((R - o) / d, (-R - o) / d),
            _ => Math.Abs(o) <= R ? (float.MinValue, float.MaxValue) : (float.NaN, float.NaN),
        };

        public bool Contains(Vector3 p)
        {
            var d = p - Origin;
            return Circle ? DotXZ(d, d) <= R * R : Math.Max(Math.Abs(d.X), Math.Abs(d.Z)) <= R;
        }
    }

    private sealed class Sequence(int start, int count)
    {
        public readonly int Start = start;
        public readonly int Count = count;
        public int FirstIndex = -1; // index of the first aoe seen firing, drives the lane choice
    }

    private sealed class Stage3
    {
        public const float InvSpeed = 1f / 6; // run speed 6 y/s

        public readonly List<Aoe> Aoes = [];

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

        public Stage3()
        {
            mech1Rotating = Rotating(1.2f, 6, 267.5f, -10, 10);
            mech2Exaflares = DoubleExaflare(9.39f, 251, 11.75f, 243);
            mech3Rotating = Rotating(1.0f, 14.76f, 225.3f, -10, 0, 10);
            mech3Exaflares = DoubleExaflare(14.54f, 229.3f, 14.97f, 221.3f);
            mech4RectsL = Rects(-12, -6);
            mech4RectsR = Rects(12, 6);
            mech4RectsC = RectsCenter();
            mech4Exaflare = SingleExaflare(22.52f, 198.7f, -12, -4, 4, 12);
            mech5PairL1 = Pairs(5, new(-10, 29.95f, 170), new(-2, 29.95f, 170));
            mech5PairL2 = Pairs(5, new(-10, 31.39f, 156), new(-4.34f, 30.81f, 161.66f));
            mech5PairR1 = Pairs(5, new(10, 29.95f, 170), new(4.34f, 29.37f, 175.66f));
            mech5PairR2 = Pairs(5, new(10, 31.39f, 156), new(2, 31.39f, 156));
            mech6Exaflare = SingleExaflare(33.47f, 145.7f, -4, 4, 12, -12);
            mech7PairL = Pairs(3, new(-6.78f, 35.91f, 136.91f), new(-3.22f, 36.30f, 135.09f));
            mech7PairR = Pairs(3, new(6.78f, 35.91f, 136.91f), new(3.22f, 36.30f, 135.09f));
        }

        public void OnActionEffect(uint actionId, Vector3 casterPos)
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

        public void OnRectsCast(Vector3 casterPos) => Update(casterPos, 1, mech4RectsL, mech4RectsR);

        public List<Vector3> BuildPath()
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

                // a cast start means 'index' fires in activateIn; an effect means 'index' just fired
                var next = activateIn > 0 ? index : (index + 1) % c.Count;
                var t = DateTime.Now.AddSeconds(activateIn);
                for (var i = 0; i < c.Count; i++)
                {
                    var aoe = Aoes[c.Start + (next + i) % c.Count];
                    if (activateIn > 0)
                    {
                        aoe.NextActivation = t;
                        t = t.AddSeconds(aoe.SeqDelay);
                    }
                    else
                    {
                        t = t.AddSeconds(aoe.SeqDelay);
                        aoe.NextActivation = t;
                    }
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

        private Sequence SingleExaflare(float y, float z, params float[] xs) =>
            Create(true, 6, xs.Select(x => (new Vector3(x, y, z), x == xs[^1] ? 1.9f : 1.4f)));

        private Sequence DoubleExaflare(float y1, float z1, float y2, float z2)
        {
            float[] xs = [-12, -4, 4, 12];
            return Create(true, 6, xs.Select(x => (new Vector3(x, y1, z1), 1.4f)).Concat(xs.Select(x => (new Vector3(x, y2, z2), 1.4f))));
        }

        private static readonly (float y, float z)[] RectRows = [(25.59f, 190.4f), (23.37f, 196.4f), (21.15f, 202.4f), (18.94f, 208.4f), (16.73f, 214.4f)];

        private Sequence Rects(float x1, float x2)
        {
            IEnumerable<(Vector3, float)> Lane(float x) => RectRows.Select((e, i) => (new Vector3(x, e.y, e.z), i == RectRows.Length - 1 ? 1.1f : 0.5f));
            return Create(false, 3, Lane(x1).Concat(Lane(x2)));
        }

        private Sequence RectsCenter() =>
            Create(false, 3, RectRows.Select((e, i) => (new Vector3(0, e.y, e.z), i == RectRows.Length - 1 ? 4.2f : 0.5f)));

        private Sequence Pairs(float r, Vector3 p1, Vector3 p2) => Create(true, r, [(p1, 2.5f), (p2, 2.5f)]);
    }
}
