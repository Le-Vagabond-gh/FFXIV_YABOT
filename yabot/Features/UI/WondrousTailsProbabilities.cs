using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;

namespace YABOT.Features.UI
{
    public unsafe class WondrousTailsProbabilities : BaseFeature
    {
        public override string Name => "Wondrous Tails Probabilities";

        public override string Description =>
            "Adds line-completion probabilities to the Wondrous Tails (Khloe's Journal) window so you can see your odds of 1/2/3 line shuffle outcomes before spending a Second Chance.";

        public override FeatureType FeatureType => FeatureType.UI;

        private PerfectTails? solver;
        private readonly bool[] lastStickerState = new bool[16];
        private bool lastStickerStateInitialized;

        public override void Enable()
        {
            // PerfectTails takes ~100ms on first load - precompute on a background thread,
            // then patch the addon if it's already open (otherwise we'd miss the chance until
            // the next open/refresh).
            Task.Run(() =>
            {
                try
                {
                    solver = new PerfectTails();
                    Svc.Framework.RunOnFrameworkThread(UpdateOpenAddon);
                }
                catch (Exception e) { Svc.Log.Error(e, "WondrousTailsProbabilities: solver init"); }
            });

            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "WeeklyBingo", OnWeeklyBingoChanged);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "WeeklyBingo", OnWeeklyBingoChanged);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "WeeklyBingo", OnWeeklyBingoFinalize);
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        private void UpdateOpenAddon()
        {
            var addon = (AddonWeeklyBingo*)Svc.GameGui.GetAddonByName("WeeklyBingo").Address;
            if (addon == null || !((AtkUnitBase*)addon)->IsVisible) return;
            ApplyProbabilityText(addon);
        }

        public override void Disable()
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            Svc.AddonLifecycle.UnregisterListener(OnWeeklyBingoChanged);
            Svc.AddonLifecycle.UnregisterListener(OnWeeklyBingoFinalize);
            RestoreOriginalText();
            solver = null;
            lastStickerStateInitialized = false;
            base.Disable();
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                if (solver == null) return;

                var changed = !lastStickerStateInitialized;
                for (var i = 0; i < 16; i++)
                {
                    var placed = PlayerState.Instance()->IsWeeklyBingoStickerPlaced(i);
                    if (placed != lastStickerState[i]) { changed = true; lastStickerState[i] = placed; }
                }
                lastStickerStateInitialized = true;
                if (!changed) return;

                var addon = (AddonWeeklyBingo*)Svc.GameGui.GetAddonByName("WeeklyBingo").Address;
                if (addon == null || !((AtkUnitBase*)addon)->IsVisible) return;
                ApplyProbabilityText(addon);
            }
            catch { /* defensive: never crash the game from Framework.Update */ }
        }

        private void OnWeeklyBingoChanged(AddonEvent type, AddonArgs args)
            => ApplyProbabilityText((AddonWeeklyBingo*)args.Addon.Address);

        private void ApplyProbabilityText(AddonWeeklyBingo* addon)
        {
            try
            {
                if (solver == null || addon == null) return;

                var textNode = addon->GetTextNodeById(34);
                if (textNode == null) return;

                for (var i = 0; i < 16; i++)
                    solver.GameState[i] = PlayerState.Instance()->IsWeeklyBingoStickerPlaced(i);

                textNode->SetText(solver.SolveAndGetProbabilitySeString().Encode());
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "WondrousTailsProbabilities: refresh");
            }
        }

        private void OnWeeklyBingoFinalize(AddonEvent type, AddonArgs args)
            => RestoreOriginalText();

        private void RestoreOriginalText()
        {
            // No-op: the game refills node 34 with its own help text on the next refresh.
            // We don't need to restore anything explicitly.
        }
    }

    // Wondrous Tails line-probability solver. Originally by Daemitris,
    // adapted for VanillaPlus by MidoriKami - this is a straight port.
    internal sealed unsafe class PerfectTails
    {
        private static readonly Random Random = new();
        private readonly Dictionary<int, long[]> possibleBoards = new();
        private readonly Dictionary<int, double[]> sampleProbabilities = new();

        public readonly bool[] GameState = new bool[16];

        public PerfectTails()
        {
            CalculateBoards(0, 0, 0, 0, 0);
            CalculateSamples();
        }

        private static double[] Error { get; } = { -1, -1, -1 };

        private double[] Solve(bool[] cells)
        {
            var counts = Values(cells);
            if (counts == null) return Error;
            var divisor = (double)counts[0];
            return counts.Skip(1).Select(c => Math.Round(c / divisor, 4)).ToArray();
        }

        private double[] GetSample(int stickersPlaced)
            => sampleProbabilities.TryGetValue(stickersPlaced, out var v) ? v : Error;

        private long[]? Values(bool[] cells)
            => possibleBoards.TryGetValue(CellsToMask(cells), out var v) ? v : null;

        private long[] CalculateBoards(int mask, int numStickers, int numRows, int numCols, int numDiags)
        {
            if (possibleBoards.TryGetValue(mask, out var result)) return result;

            if (numStickers == 9)
            {
                var lines = numRows + numCols + numDiags;
                return possibleBoards[mask] = new long[]
                {
                    1,
                    lines >= 1 ? 1 : 0,
                    lines >= 2 ? 1 : 0,
                    lines >= 3 ? 1 : 0,
                };
            }

            if (numStickers > 9) return possibleBoards[mask] = new long[] { 0, 0, 0, 0 };

            result = possibleBoards[mask] = new long[] { 0, 0, 0, 0 };

            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    if (MaskHasBit(mask, r, c)) continue;

                    var nMask = SetMaskBit(mask, r, c);
                    var nRows = MaskHasRow(nMask, r) ? 1 : 0;
                    var nCols = MaskHasCol(nMask, c) ? 1 : 0;
                    var nDiag1 = MaskHasDiag1(nMask) && r == c ? 1 : 0;
                    var nDiag2 = MaskHasDiag2(nMask) && r == 3 - c ? 1 : 0;
                    var nResult = CalculateBoards(nMask, numStickers + 1, numRows + nRows, numCols + nCols, numDiags + nDiag1 + nDiag2);

                    for (var i = 0; i < 4; i++) result[i] += nResult[i];
                }
            }

            return result;
        }

        private void CalculateSamples()
        {
            for (var stickersPlaced = 1; stickersPlaced <= 7; stickersPlaced++)
            {
                var samples = new List<double[]>();
                for (var i = 0; i < 500; i++)
                {
                    var sampleState = new bool[16];
                    var sampleIndexes = Enumerable.Range(0, 16).OrderBy(_ => Random.Next()).Take(stickersPlaced);
                    foreach (var sampleIndex in sampleIndexes) sampleState[sampleIndex] = true;
                    samples.Add(Solve(sampleState));
                }

                sampleProbabilities[stickersPlaced] = new[]
                {
                    Math.Round(samples.Average(s => s[0]), 4),
                    Math.Round(samples.Average(s => s[1]), 4),
                    Math.Round(samples.Average(s => s[2]), 4),
                };
            }
        }

        public SeString SolveAndGetProbabilitySeString()
        {
            var stickersPlaced = PlayerState.Instance()->WeeklyBingoNumPlacedStickers;
            var values = Solve(GameState);

            double[]? samples = null;
            if (stickersPlaced > 0 && stickersPlaced <= 7) samples = GetSample(stickersPlaced);

            var seString = new SeStringBuilder().AddText("Line Chances: ");

            if (values == Error)
            {
                seString.AddUiForeground("?? ", 704).AddUiForeground("?? ", 704).AddUiForeground("??", 704);
                return seString.Build();
            }

            var valuePayloads = values.Select(v => $"{v * 100:F2}%").ToArray();

            if (samples != null)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var value = values[i];
                    var sample = samples[i];
                    var valuePayload = valuePayloads[i];

                    const double bound = 0.05;
                    var sampleBoundLower = Math.Max(0, sample - bound);

                    if (Math.Abs(value - 1) < 0.1f) seString.AddUiGlow(valuePayload, 2);
                    else if (value < 1 && value >= sample) seString.AddUiForeground(valuePayload, 67);
                    else if (sample > value && value > sampleBoundLower) seString.AddUiForeground(valuePayload, 66);
                    else if (sampleBoundLower > value && value > 0) seString.AddUiForeground(valuePayload, 561);
                    else if (value == 0) seString.AddUiForeground(valuePayload, 704);
                    else seString.AddText(valuePayload);

                    seString.AddText("  ");
                }

                seString.AddText("\nShuffle Average: ");
                seString.AddText(string.Join(" ", samples.Select(v => $"{v * 100:F2}%")));
            }
            else
            {
                seString.AddText(string.Join(" ", valuePayloads));
            }

            return seString.Build();
        }

        private static int CellsToMask(bool[] cells)
        {
            var mask = 0;
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                    if (cells[(r * 4) + c]) mask = SetMaskBit(mask, r, c);
            return mask;
        }

        private static int GetMaskBit(int r, int c) => 1 << ((4 * r) + c);
        private static int SetMaskBit(int mask, int r, int c) => mask | GetMaskBit(r, c);
        private static bool MaskHasBit(int mask, int r, int c) => (mask & GetMaskBit(r, c)) == GetMaskBit(r, c);
        private static bool MaskHasRow(int mask, int r) => Enumerable.Range(0, 4).All(c => MaskHasBit(mask, r, c));
        private static bool MaskHasCol(int mask, int c) => Enumerable.Range(0, 4).All(r => MaskHasBit(mask, r, c));
        private static bool MaskHasDiag1(int mask) => Enumerable.Range(0, 4).All(i => MaskHasBit(mask, i, i));
        private static bool MaskHasDiag2(int mask) => Enumerable.Range(0, 4).All(i => MaskHasBit(mask, i, 3 - i));
    }
}
