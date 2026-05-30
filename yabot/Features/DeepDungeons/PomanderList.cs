using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using YABOT.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;

namespace YABOT.Features.DeepDungeons
{
    public unsafe class PomanderList : BaseFeature
    {
        public override string Name => "Pomander List";

        public override string Description =>
            "While inside a deep dungeon (Palace of the Dead, Heaven-on-High, Eureka Orthos), shows a clickable overlay listing the pomanders and magicite/demiclones you currently hold. Each row shows the icon, name, a short effect summary, and the quantity. Click a row to use that pomander/stone. Optionally shows the Beacon of Passage progress percentage and an estimated mob respawn timer for the current floor. Hold Shift to drag the window to reposition it.";

        public override FeatureType FeatureType => FeatureType.DeepDungeons;
        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            // Remembered top-left, used to place the window when left-aligned.
            public Vector2 WindowPos = new(-1, -1);

            // Remembered top-right, used to place the window when right-aligned.
            public Vector2 WindowPosRight = new(-1, -1);

            [FeatureConfigOption("Window scale", FloatMin = 0.5f, FloatMax = 3f, EditorSize = 200, Format = "%.2fx")]
            public float WindowScale = 1f;

            [FeatureConfigOption("Show background")]
            public bool ShowBackground = true;

            [FeatureConfigOption("Right-align (icon on the right)")]
            public bool RightAlign = false;

            [FeatureConfigOption("Hide name (show on hover)")]
            public bool HideName = false;

            [FeatureConfigOption("Show passage progress")]
            public bool ShowPassageProgress = true;

            [FeatureConfigOption("Show respawn timer")]
            public bool ShowRespawnTimer = true;

            [FeatureConfigOption("Lock panel (disable Shift to move)")]
            public bool LockPanel = false;

            [FeatureConfigOption("When a coffer holds a pomander you're already at max on:", "RadioEnum")]
            public CappedAction CappedPomanderAction = CappedAction.Off;
        }

        // What to do when a coffer can't be picked up because we're capped on that pomander.
        public enum CappedAction
        {
            [Description("Do nothing (just flash the row)")]
            Off,

            [Description("Use one automatically to free a slot")]
            AutoUse,

            [Description("Ask with a mid-screen Yes/No prompt")]
            Prompt,
        }

        public Configs Config { get; private set; } = null!;
        private Overlays Overlay = null!;
        private Vector2 _lastWindowSize = Vector2.Zero;
        private int _lastDrawFrame = -10;
        private bool _prevRightAlign;

        // Respawn timer is dead-reckoned (the game exposes no countdown): we anchor to the time
        // the player entered the current floor, detected by watching dd->Floor change.
        private byte _lastFloor;
        private DateTime _floorEnterTime;

        // Flash a row's text blue<->white briefly when its count goes up, so a freshly acquired
        // pomander/magicite is easy to spot. Counts are tracked per slot (so a 0->1 pickup is
        // caught); the flash is keyed by the same name DrawRow draws under. On entry/re-entry we
        // "prime" the baseline without flashing for a short grace window - the saved stock doesn't
        // populate on the very first frame (counts arrive 0 then jump to their real values a frame
        // or two later), and a one-frame prime would read that jump as a pickup on every row.
        private readonly byte[] _prevPomanderCounts = new byte[16];
        private readonly byte[] _prevMagiciteSlots = new byte[3];
        private DateTime _primeUntil = DateTime.MinValue;
        private const double PrimeGraceSeconds = 3.0;

        // Pending "use a capped pomander?" prompt (Prompt mode). Holds the slot to use, the name to
        // show, and an auto-dismiss time so a stale prompt doesn't linger.
        private uint? _promptSlot;
        private string _promptName = string.Empty;
        private DateTime _promptExpiry;
        private const double PromptTimeoutSeconds = 15.0;

        // Pickup = green arrows when a count went up; Capped = blue arrows when the game told us we
        // can't carry any more of that item (returned to the coffer), hinting to use one first.
        private enum FlashKind { Pickup, Capped }
        private readonly Dictionary<string, (DateTime Start, FlashKind Kind)> _flashStart = new(StringComparer.Ordinal);
        private static readonly Vector4 FlashBlue = new(0.35f, 0.6f, 1f, 1f);
        private static readonly Vector4 FlashWhite = new(1f, 1f, 1f, 1f);
        private static readonly Vector4 ActiveGreen = new(0.55f, 1f, 0.55f, 1f);
        private static readonly Vector4 RespawnAmber = new(1f, 0.78f, 0.25f, 1f);
        private const double FlashDuration = 2.5;
        private const uint FlashArrowIconId = 60358; // green up-arrow (pickup); rotated 90 CW to point right
        private const uint CappedArrowIconId = 60361; // blue triangle (already capped); rotated the same way
        private const float FlashArrowSizeMul = 1.95f; // per-arrow size vs the row icon (~30% larger)
        private const int FlashArrowCount = 3; // ">>>" stacked chevrons
        private const float FlashArrowStepMul = 0.25f; // horizontal step between arrows (fraction of size)
        private const float FlashArrowBounceMul = 0.4f; // bounce travel vs the row icon size
        private const double FlashArrowBouncePeriod = 0.45; // seconds per right-bounce

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            _prevRightAlign = Config.RightAlign;
            Overlay = new(this);
            Svc.Chat.ChatMessage += OnChatMessage;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Chat.ChatMessage -= OnChatMessage;
            if (Overlay != null)
            {
                P.Ws.RemoveWindow(Overlay);
                Overlay = null!;
            }
            base.Disable();
        }

        // The game prints "You return the <item> to the coffer. You cannot carry any more of that
        // item." when a coffer holds a pomander you're already capped on. Flash that row blue so the
        // player knows which one to use to make room - and, if enabled, use one automatically.
        private void OnChatMessage(IHandleableChatMessage chatMessage)
        {
            try
            {
                var ef = EventFramework.Instance();
                if (ef == null) return;
                var dd = ef->GetInstanceContentDeepDungeon();
                if (dd == null) return;
                if (!Svc.Data.GetExcelSheet<DeepDungeon>().TryGetRow((uint)dd->DeepDungeonId, out var ddRow))
                    return;

                var text = chatMessage.Message.TextValue;
                if (!text.Contains("cannot carry any more", StringComparison.OrdinalIgnoreCase)) return;

                // Match against the dungeon's own pomander slots so we land on the exact slot the
                // line names (and pick the right pomander/protomander variant for this dungeon).
                var pomanderSheet = Svc.Data.GetExcelSheet<DeepDungeonItem>();
                for (var slot = 0; slot < 16; slot++)
                {
                    var slotRef = ddRow.PomanderSlot[slot];
                    if (slotRef.RowId == 0) continue;
                    if (!pomanderSheet.TryGetRow(slotRef.RowId, out var pomander)) continue;

                    var singular = pomander.Singular.ToString();
                    if (string.IsNullOrEmpty(singular)) continue;
                    if (!text.Contains(singular, StringComparison.OrdinalIgnoreCase)) continue;

                    var shortName = StripPomanderPrefix(pomander.Name.ToString());

                    // Flash the row blue (keyed off the stripped name DrawRow draws under).
                    _flashStart[shortName] = (DateTime.Now, FlashKind.Capped);

                    // Nothing to spend if we don't actually hold one.
                    if (dd->Items[slot].Count == 0) break;

                    switch (Config.CappedPomanderAction)
                    {
                        case CappedAction.AutoUse:
                            dd->UsePomander((uint)slot);
                            break;
                        case CappedAction.Prompt:
                            _promptSlot = (uint)slot;
                            _promptName = shortName;
                            _promptExpiry = DateTime.Now.AddSeconds(PromptTimeoutSeconds);
                            break;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] chat handler failed");
            }
        }

        public override bool DrawConditions()
        {
            try
            {
                var ef = EventFramework.Instance();
                if (ef == null) return false;
                return ef->GetInstanceContentDeepDungeon() != null;
            }
            catch { return false; }
        }

        public override void Draw()
        {
            try
            {
                var dd = EventFramework.Instance()->GetInstanceContentDeepDungeon();
                if (dd == null) return;
                if (!Svc.Data.GetExcelSheet<DeepDungeon>().TryGetRow((uint)dd->DeepDungeonId, out var ddRow))
                    return;

                // When locked, ignore Shift entirely so the window can't be dragged or re-anchored.
                var shiftHeld = !Config.LockPanel
                    && (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift));

                // The overlay only draws inside a deep dungeon, so leaving and re-entering
                // leaves a gap in the ImGui frame count. Detect it to force the saved position
                // back on the re-appearing frame instead of trusting whatever ImGui last had.
                var frame = ImGui.GetFrameCount();
                var reappearing = frame - _lastDrawFrame > 1;

                // On the first draw and on re-entry, prime the baseline (no flashing) for a short
                // grace window so the saved stock settling in over the next frames doesn't light up.
                if (reappearing)
                {
                    _primeUntil = DateTime.Now.AddSeconds(PrimeGraceSeconds);
                    _flashStart.Clear();
                }
                var prime = DateTime.Now < _primeUntil;

                // Toggling alignment re-anchors to the window's current spot so it doesn't jump
                // to a stale remembered corner; the corner is recaptured after this frame draws.
                if (Config.RightAlign != _prevRightAlign)
                {
                    if (Config.RightAlign)
                        Config.WindowPosRight = new Vector2(-1, -1);
                    _prevRightAlign = Config.RightAlign;
                }

                ImGuiHelpers.ForceNextWindowMainViewport();
                if (Config.RightAlign && !shiftHeld && Config.WindowPosRight.X >= 0 && Config.WindowPosRight.Y >= 0 && _lastWindowSize.X > 0)
                {
                    // Pin the remembered top-RIGHT corner: place the top-left at rightX - width
                    // so AlwaysAutoResize grows the window leftward and the right edge stays put.
                    // Width is predicted from last frame; it converges within one frame when the
                    // content (and so the width) changes, including the first frame on re-entry.
                    ImGui.SetNextWindowPos(new Vector2(Config.WindowPosRight.X - _lastWindowSize.X, Config.WindowPosRight.Y), ImGuiCond.Always);
                }
                else if (Config.WindowPos.X >= 0 && Config.WindowPos.Y >= 0)
                {
                    // Left-align, or right-align before its corner is captured: place by the
                    // remembered top-left. Force it on re-appearance so we land where we left off.
                    ImGui.SetNextWindowPos(Config.WindowPos, reappearing ? ImGuiCond.Always : ImGuiCond.Once);
                }

                // When the user has disabled the backdrop, force it transparent unless they're
                // dragging the window - the dimmed glass while shift is held still gives them a
                // visual handle to grab.
                var bgAlpha = Config.ShowBackground
                    ? (shiftHeld ? 0.7f : 0.55f)
                    : (shiftHeld ? 0.35f : 0f);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, bgAlpha));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

                var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
                if (!shiftHeld)
                    flags |= ImGuiWindowFlags.NoMove;

                ImGui.Begin("###YABOTPomanderList", flags);

                ImGui.SetWindowFontScale(Math.Clamp(Config.WindowScale, 0.5f, 3f));

                UpdateFloorTracking(dd);
                DrawStatusLine(dd);
                DrawPomanderRows(dd, ddRow, prime);
                DrawMagiciteRows(dd, ddRow, prime);

                ImGui.SetWindowFontScale(1f);

                var winPos = ImGui.GetWindowPos();
                _lastWindowSize = ImGui.GetWindowSize();

                // Top-left always tracks the live position (drives left-align and a clean
                // hand-off when toggling modes). The remembered top-right only moves when it's
                // unset (first frame / after a toggle) or the user shift-drags, so right-align
                // otherwise keeps the right edge fixed as the content width changes.
                Config.WindowPos = winPos;
                if (Config.RightAlign && (Config.WindowPosRight.X < 0 || shiftHeld))
                    Config.WindowPosRight = new Vector2(winPos.X + _lastWindowSize.X, winPos.Y);

                _lastDrawFrame = frame;
                ImGui.End();

                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor();

                DrawCappedPrompt(dd);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] Draw failed");
            }
        }

        // Mid-screen Yes/No prompt (Prompt mode) asking whether to spend one of a capped pomander.
        private void DrawCappedPrompt(InstanceContentDeepDungeon* dd)
        {
            if (_promptSlot is not { } slot) return;
            if (DateTime.Now > _promptExpiry)
            {
                _promptSlot = null;
                return;
            }

            var center = ImGui.GetMainViewport().GetCenter();
            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));

            if (ImGui.Begin("Use pomander?###YABOTCappedPrompt",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoFocusOnAppearing))
            {
                ImGui.TextUnformatted($"At max {_promptName}. Use one to free a slot?");
                ImGui.Spacing();

                if (ImGui.Button("Yes", new Vector2(120, 0)))
                {
                    try { dd->UsePomander(slot); }
                    catch (Exception ex) { Svc.Log.Error(ex, $"[{Name}] prompt use failed"); }
                    _promptSlot = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(120, 0)))
                    _promptSlot = null;
            }
            ImGui.End();

            ImGui.PopStyleVar();
        }

        private void UpdateFloorTracking(InstanceContentDeepDungeon* dd)
        {
            // Re-anchor the respawn clock whenever the floor changes. The DD instance survives
            // floor-to-floor transitions, so dd->Floor updating is a reliable "new floor" signal.
            // (Enabling the overlay mid-floor anchors to that moment, so the first cycle can read
            // early - same inherent caveat as NecroLens.)
            if (dd->Floor != _lastFloor)
            {
                _lastFloor = dd->Floor;
                _floorEnterTime = DateTime.Now;
            }
        }

        // The status line sits above the item rows: Beacon of Passage progress on the left and the
        // dead-reckoned respawn timer on the right, both optional. Either can be hidden, and both are
        // suppressed on boss floors (no passage, no respawns there).
        private void DrawStatusLine(InstanceContentDeepDungeon* dd)
        {
            var boss = DeepDungeonRespawn.IsBossFloor(dd->DeepDungeonId, dd->Floor);
            var showPassage = Config.ShowPassageProgress && !boss;

            var interval = 0;
            var showRespawn = Config.ShowRespawnTimer
                && DeepDungeonRespawn.TryGetInterval(dd->DeepDungeonId, dd->Floor, out interval);

            if (!showPassage && !showRespawn) return;

            // Left-to-right segments: passage progress first, then the timer to its right.
            var segments = new List<(string Text, Vector4? Color)>(2);

            if (showPassage)
            {
                // PassageProgress fills 0-10 as the floor is cleared and reads >=11 once the beacon
                // is open; surface it as a 0-100% tracker, green once it's open.
                var raw = dd->PassageProgress;
                var open = raw >= 11;
                var pct = open ? 100 : Math.Min((int)raw, 10) * 10;
                segments.Add(($"Passage  {pct}%", open ? ActiveGreen : (Vector4?)null));
            }

            if (showRespawn)
            {
                // Cycle the countdown by advancing through whole intervals since floor entry.
                var elapsed = (DateTime.Now - _floorEnterTime).TotalSeconds;
                var remaining = interval - elapsed % interval;
                // Tint amber in the final few seconds so an imminent wave is easy to catch at a glance.
                segments.Add(($"Respawn  {TimeSpan.FromSeconds(remaining):mm\\:ss}",
                    remaining <= 5 ? RespawnAmber : (Vector4?)null));
            }

            DrawInlineSegments(segments);
            ImGui.Spacing();
        }

        // Draws a row of colored text segments separated by a one-em gap, right-aligned as a group
        // when the panel is right-aligned (matching how the item rows hug the right edge).
        private void DrawInlineSegments(List<(string Text, Vector4? Color)> segments)
        {
            var gap = ImGui.GetFontSize();

            if (Config.RightAlign)
            {
                var total = 0f;
                for (var i = 0; i < segments.Count; i++)
                {
                    total += ImGui.CalcTextSize(segments[i].Text).X;
                    if (i > 0) total += gap;
                }
                var avail = ImGui.GetContentRegionAvail().X;
                if (avail > total)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - total);
            }

            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0) ImGui.SameLine(0, gap);
                var (text, color) = segments[i];
                if (color.HasValue) ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
                ImGui.TextUnformatted(text);
                if (color.HasValue) ImGui.PopStyleColor();
            }
        }

        private void DrawPomanderRows(InstanceContentDeepDungeon* dd, DeepDungeon ddRow, bool prime)
        {
            var pomanderSheet = Svc.Data.GetExcelSheet<DeepDungeonItem>();
            var any = false;

            for (var slot = 0; slot < 16; slot++)
            {
                var info = dd->Items[slot];

                var prevCount = _prevPomanderCounts[slot];
                _prevPomanderCounts[slot] = info.Count;

                var slotRef = ddRow.PomanderSlot[slot];
                if (slotRef.RowId == 0) continue;
                if (!pomanderSheet.TryGetRow(slotRef.RowId, out var pomander)) continue;

                var name = pomander.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                var shortName = StripPomanderPrefix(name);

                // Some pomanders grant a normal player buff rather than a floor-wide effect, so the
                // struct's IsActive flag never lights up for them; detect those by buff name instead.
                var isActive = PomanderStatusId.TryGetValue(shortName, out var statusId)
                    ? PlayerHasStatus(statusId)
                    : info.IsActive;

                // Keep a pomander listed while its effect is active even if the last one was
                // consumed, so the active marker doesn't vanish the moment the count hits zero.
                if (info.Count == 0 && !isActive) continue;

                if (!prime && info.Count > prevCount)
                    _flashStart[shortName] = (DateTime.Now, FlashKind.Pickup);

                var slotCapture = (uint)slot;
                DrawRow(
                    pomander.Icon,
                    shortName,
                    shortName,
                    LookupDescription(name, PomanderDescriptions),
                    info.Count,
                    showCount: info.Count > 0,
                    isActive,
                    () => dd->UsePomander(slotCapture));
                any = true;
            }

            if (!any)
                ImGui.TextDisabled("No pomanders");
        }

        private void DrawMagiciteRows(InstanceContentDeepDungeon* dd, DeepDungeon ddRow, bool prime)
        {
            // DeepDungeonType switches MagiciteSlot's link target: 1 = DeepDungeonMagicStone
            // (PotD/HoH magicite), 2 = DeepDungeonDemiclone (Eureka Orthos demiclones).
            // Pilgrim's Traverse (added in 7.4) introduces Juniper Incenses with an unknown
            // type value not covered by the current schema, so for anything outside 1/2 we
            // probe both known sheets and use whichever has the row. The UseStone call works
            // regardless because the engine indexes by slot, not by sheet identity.
            var stoneSheet = Svc.Data.GetExcelSheet<DeepDungeonMagicStone>();
            var demicloneSheet = Svc.Data.GetExcelSheet<DeepDungeonDemiclone>();
            var type = ddRow.DeepDungeonType;

            // The 3-byte _magicite array is NOT (count-per-MagiciteSlot-index). Each byte is the
            // 1-based MagiciteSlot index of the magicite occupying that inventory slot, or 0 if
            // empty. So _magicite[0] = 3 means inventory slot 0 currently holds the type at
            // MagiciteSlot[2] (e.g. Vortex in HoH), with an implicit count of 1 - you can hold
            // at most three different magicite, one per inventory slot. UseStone takes the
            // inventory slot (0-2), not the MagiciteSlot index.
            //
            // Collect first so we can skip the spacing + section when empty - a "No magicite"
            // line is just noise once you've already cleared the floor.
            var rows = new List<(uint Icon, string Name, byte Count, uint Slot)>();
            for (var inventorySlot = 0; inventorySlot < 3; inventorySlot++)
            {
                var typeByte = dd->Magicite[inventorySlot];

                var prevType = _prevMagiciteSlots[inventorySlot];
                _prevMagiciteSlots[inventorySlot] = typeByte;

                if (typeByte == 0) continue;

                var sheetIndex = typeByte - 1;
                if (sheetIndex >= 4) continue;

                var slotRef = ddRow.MagiciteSlot[sheetIndex];
                if (slotRef.RowId == 0) continue;

                var (icon, name) = ResolveStoneSlot(type, slotRef.RowId, stoneSheet, demicloneSheet);
                if (string.IsNullOrEmpty(name)) continue;

                // Flash only on an empty->occupied transition (prevType == 0): a real pickup always
                // lands in a free slot (you can hold at most 3 and can't pick up while full). Using a
                // stone compacts the array - the survivors shift down toward slot 0 - so an occupied
                // slot's value changes (nonzero->nonzero, or nonzero->0 at the tail) without anything
                // being picked up; keying off prevType == 0 ignores that shift. Duplicate magicite
                // share a name across slots, so the flash key stays per-slot to light only the new row.
                if (!prime && prevType == 0)
                    _flashStart[MagiciteRowKey(name, (uint)inventorySlot)] = (DateTime.Now, FlashKind.Pickup);

                rows.Add((icon, name, (byte)1, (uint)inventorySlot));
            }

            if (rows.Count == 0) return;

            ImGui.Spacing();
            foreach (var row in rows)
            {
                var slotCapture = row.Slot;
                DrawRow(
                    row.Icon,
                    row.Name,
                    MagiciteRowKey(row.Name, row.Slot),
                    LookupDescription(row.Name, MagiciteDescriptions),
                    row.Count,
                    showCount: false,
                    isActive: false,
                    () => dd->UseStone(slotCapture));
            }
        }

        // Per-slot flash/row identity for magicite, since duplicates share a display name.
        private static string MagiciteRowKey(string name, uint slot) => $"{name}#{slot}";

        private static (uint Icon, string Name) ResolveStoneSlot(
            byte deepDungeonType,
            uint rowId,
            Lumina.Excel.ExcelSheet<DeepDungeonMagicStone> stoneSheet,
            Lumina.Excel.ExcelSheet<DeepDungeonDemiclone> demicloneSheet)
        {
            if (deepDungeonType == 1 && stoneSheet.TryGetRow(rowId, out var stone) && stone.Icon != 0)
                return (stone.Icon, stone.Name.ToString());
            if (deepDungeonType == 2 && demicloneSheet.TryGetRow(rowId, out var demi) && demi.Icon != 0)
                return (demi.Icon, demi.TitleCase.ToString());

            if (stoneSheet.TryGetRow(rowId, out stone) && stone.Icon != 0)
                return (stone.Icon, stone.Name.ToString());
            if (demicloneSheet.TryGetRow(rowId, out demi) && demi.Icon != 0)
                return (demi.Icon, demi.TitleCase.ToString());

            return (0, string.Empty);
        }

        private void DrawRow(uint iconId, string name, string rowKey, string desc, byte count, bool showCount, bool isActive, System.Action onClick)
        {
            // GetFontSize reflects the SetWindowFontScale already pushed in Draw, so icons
            // track the configured scale via font size alone.
            var iconSize = ImGui.GetFontSize() * 1.6f;
            var qty = showCount ? $"   x{count}" : string.Empty;
            var label = Config.HideName
                ? (string.IsNullOrEmpty(desc) ? qty.TrimStart() : $"{desc}{qty}")
                : (string.IsNullOrEmpty(desc) ? $"{name}{qty}" : $"{name} - {desc}{qty}");
            // rowKey uniquely identifies the row (display name isn't unique - duplicate magicite
            // share a name across slots), so the selectable ID, ImGui ID and flash lookup all key
            // off it to keep rows that share text from colliding.
            var hiddenId = $"###{rowKey}_row";

            ImGui.PushID(rowKey);

            // A flash takes the text color and adds the left arrows; otherwise fall back to the
            // resting color (green when active, default otherwise). The kind picks the arrow icon:
            // green for a fresh pickup, blue when we're capped on it.
            var flashing = TryGetFlashColor(rowKey, out var flashColor, out var bounce, out var kind);
            var color = flashing ? flashColor : (isActive ? ActiveGreen : (Vector4?)null);
            var arrowIcon = kind == FlashKind.Capped ? CappedArrowIconId : FlashArrowIconId;

            if (Config.RightAlign)
                DrawRowRightAligned(iconId, iconSize, label, hiddenId, color, flashing, bounce, arrowIcon, onClick);
            else
                DrawRowLeftAligned(iconId, iconSize, label, hiddenId, color, flashing, bounce, arrowIcon, onClick);

            // Tooltip surfaces the canonical name when it's hidden, and the active-on-floor
            // marker either way. Combined into one tooltip so they don't fight for the hover.
            if (ImGui.IsItemHovered())
            {
                var tooltip = Config.HideName
                    ? (isActive ? $"{name}\n(currently active on this floor)" : name)
                    : (isActive ? "Currently active on this floor" : null);
                if (tooltip != null)
                    ImGui.SetTooltip(tooltip);
            }

            ImGui.PopID();
        }

        private void DrawRowLeftAligned(uint iconId, float iconSize, string label, string id, Vector4? color, bool flashing, float bounce, uint arrowIcon, System.Action onClick)
        {
            var startY = ImGui.GetCursorPosY();
            if (flashing)
                DrawFlashArrow(iconSize, bounce, arrowIcon);

            DrawIcon(iconId, iconSize);
            ImGui.SameLine();

            var textOffsetY = (iconSize - ImGui.GetTextLineHeight()) * 0.5f;
            if (textOffsetY > 0)
                ImGui.SetCursorPosY(startY + textOffsetY);

            DrawSelectable(label + id, 0f, iconSize, color, onClick);
        }

        private void DrawRowRightAligned(uint iconId, float iconSize, string label, string id, Vector4? color, bool flashing, float bounce, uint arrowIcon, System.Action onClick)
        {
            // Push the row to the right edge of the auto-resized window. ContentRegionAvail is
            // the widest row's width (set by previous frames in AlwaysAutoResize windows), so
            // shorter rows still settle next to the right edge across frames.
            var gap = ImGui.GetStyle().ItemSpacing.X;
            var labelWidth = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
            var arrowWidth = flashing ? FlashArrowWidth(iconSize) + gap : 0f;
            var totalWidth = arrowWidth + labelWidth + gap + iconSize;
            var avail = ImGui.GetContentRegionAvail().X;
            if (avail > totalWidth)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - totalWidth);

            var startY = ImGui.GetCursorPosY();
            if (flashing)
                DrawFlashArrow(iconSize, bounce, arrowIcon);

            var textOffsetY = (iconSize - ImGui.GetTextLineHeight()) * 0.5f;
            if (textOffsetY > 0)
                ImGui.SetCursorPosY(startY + textOffsetY);

            DrawSelectable(label + id, labelWidth, iconSize, color, onClick);

            ImGui.SameLine();
            ImGui.SetCursorPosY(startY);
            DrawIcon(iconId, iconSize);
        }

        private static void DrawIcon(uint iconId, float iconSize)
        {
            if (iconId != 0 && ThreadLoadImageHandler.TryGetIconTextureWrap(iconId, false, out var tex) && tex != null)
                ImGui.Image(tex.Handle, new Vector2(iconSize, iconSize));
            else
                ImGui.Dummy(new Vector2(iconSize, iconSize));
        }

        private void DrawSelectable(string label, float width, float height, Vector4? color, System.Action onClick)
        {
            if (color.HasValue)
                ImGui.PushStyleColor(ImGuiCol.Text, color.Value);

            if (ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(width, height)))
            {
                try { onClick(); }
                catch (Exception ex) { Svc.Log.Error(ex, $"[{Name}] use failed"); }
            }

            if (color.HasValue)
                ImGui.PopStyleColor();
        }

        // Resolves a row's text color: a transient blue<->white pulse right after pickup, otherwise
        // green while active on the floor, otherwise default (null = no override).
        private Vector4? GetRowColor(string name, bool isActive)
        {
            if (TryGetFlashColor(name, out var flash, out _, out _))
                return flash;
            return isActive ? ActiveGreen : (Vector4?)null;
        }

        // True while the row is mid-flash; outputs the current pulsing text color, a 0..1 bounce
        // value (the arrows nudge right and back each period), and the flash kind (which selects the
        // arrow icon) so the marker arrows and the text share one timeline.
        private bool TryGetFlashColor(string name, out Vector4 textColor, out float bounce, out FlashKind kind)
        {
            textColor = default;
            bounce = 0f;
            kind = FlashKind.Pickup;
            if (!_flashStart.TryGetValue(name, out var flash)) return false;
            kind = flash.Kind;

            var elapsed = (DateTime.Now - flash.Start).TotalSeconds;
            if (elapsed < 0 || elapsed > FlashDuration)
            {
                _flashStart.Remove(name);
                return false;
            }

            // Oscillate blue<->white several times, easing toward white as it ends so the text
            // decays smoothly instead of cutting off mid-pulse.
            var pulse = (float)(0.5 + 0.5 * Math.Sin(elapsed * Math.PI * 6));
            var fade = (float)(elapsed / FlashDuration);
            textColor = Vector4.Lerp(Vector4.Lerp(FlashBlue, FlashWhite, pulse), FlashWhite, fade);
            // Ease 0 -> 1 -> 0 over each bounce period for a repeated rightward nudge.
            var phase = (elapsed % FlashArrowBouncePeriod) / FlashArrowBouncePeriod;
            bounce = (float)Math.Sin(phase * Math.PI);
            return true;
        }

        private static float FlashArrowWidth(float iconSize)
            => iconSize * FlashArrowSizeMul * (1f + FlashArrowStepMul * (FlashArrowCount - 1));

        // Draws the ">>>" pickup marker at the left edge of a row: FlashArrowCount copies of the
        // green up-arrow game icon, each rotated 90 clockwise (via cycled UVs) so they point right
        // and stepped across to read as a chevron, nudging right and back by the bounce. Painted to
        // the draw list so the oversized glyphs don't inflate the row's line height, with a Dummy
        // reserving the width so the icon/text that follow on the same line sit to their right.
        private void DrawFlashArrow(float iconSize, float bounce, uint arrowIcon)
        {
            var size = iconSize * FlashArrowSizeMul;
            var step = size * FlashArrowStepMul;
            var bounceX = bounce * iconSize * FlashArrowBounceMul;
            if (arrowIcon != 0 && ThreadLoadImageHandler.TryGetIconTextureWrap(arrowIcon, false, out var tex) && tex != null)
            {
                var pos = ImGui.GetCursorScreenPos();
                var top = pos.Y + (iconSize - size) * 0.5f; // centre the larger arrows on the row icon
                var col = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
                var dl = ImGui.GetWindowDrawList();
                for (var i = 0; i < FlashArrowCount; i++)
                {
                    var x = pos.X + bounceX + step * i;
                    var tl = new Vector2(x, top);
                    var tr = new Vector2(x + size, top);
                    var br = new Vector2(x + size, top + size);
                    var bl = new Vector2(x, top + size);
                    // Cycle the UVs one corner clockwise so the up-arrow source renders pointing right.
                    dl.AddImageQuad(tex.Handle, tl, tr, br, bl,
                        new Vector2(0, 1), new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), col);
                }
            }

            ImGui.Dummy(new Vector2(FlashArrowWidth(iconSize), iconSize));
            ImGui.SameLine();
        }

        // Effect summaries are two-word blurbs keyed off the canonical pomander/magicite name.
        // The names live in the DeepDungeonItem / DeepDungeonMagicStone / DeepDungeonDemiclone
        // sheets; we look up the trailing distinguishing word (after "of " for pomanders, the
        // leading word for magicite/demiclones) to keep matching stable across language clients
        // as long as the English sheet is loaded.

        // Effects per consolegameswiki Palace of the Dead / Heaven-on-High / Eureka Orthos /
        // Pilgrim's Traverse pages. Keys are the distinguishing word from each item's name
        // ("Pomander of X", "Protomander of X" -> X). DeepDungeonItem rows are shared across
        // dungeons (only the per-dungeon slot mapping differs), so one dictionary covers them
        // all - dungeon-exclusive entries simply never resolve in other dungeons.
        private static readonly Dictionary<string, string> PomanderDescriptions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Shared across all deep dungeons
            { "Safety", "remove traps" },
            { "Sight", "reveal map" },
            { "Strength", "+30% 8min" },
            { "Steel", "-40% 8min" },
            { "Affluence", "more coffers f+1" },
            { "Flight", "fewer enemies f+1" },
            { "Alteration", "mimic spawn f+1" },
            { "Purity", "remove pox" },
            { "Fortune", "drop boost" },
            { "Witching", "polymorph 30s" },
            { "Serenity", "remove enchantments" },
            { "Intuition", "reveal treasure" },
            { "Raising", "auto raise" },
            // Palace of the Dead exclusives
            { "Rage", "manticore form" },
            { "Lust", "succubus form" },
            { "Resolution", "kuribu form" },
            // Heaven-on-High exclusives
            { "Frailty", "weaken 3min" },
            { "Concealment", "party stealth" },
            { "Petrification", "stone 30s" },
            // Eureka Orthos protomander exclusives
            { "Lethargy", "slow 10min" },
            { "Storms", "drain HP" },
            { "Dread", "dreadnaught form" },
            // Pilgrim's Traverse exclusives
            { "Haste", "swift actions" },
            { "Purification", "cleanse barrier" },
            { "Devotion", "guarantee votive f+1" },
        };

        // Combined dictionary for stones, demiclones, and juniper incenses - they all
        // share the same _magicite slots + UseStone path, so one lookup table is fine.
        // Keys are the leading word of each item's name.
        private static readonly Dictionary<string, string> MagiciteDescriptions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Heaven-on-High magicite (summon a primal simulacrum)
            { "Inferno", "summon ifrit" },
            { "Crag", "summon titan" },
            { "Vortex", "summon garuda" },
            { "Elder", "summon odin" },
            // Eureka Orthos demiclones
            { "Unei", "healer summon" },
            { "Doga", "damage summon" },
            { "Onion", "balanced summon" },
            // Pilgrim's Traverse juniper incenses
            { "Mazeroot", "faerie utility" },
            { "Barkbalm", "double HP" },
            { "Poisonfruit", "invuln nuke" },
        };

        // A few pomanders apply an ordinary player status instead of the floor-wide effect the
        // DeepDungeon struct's IsActive flag tracks, so we detect those by status ID. Keys are the
        // stripped pomander name; values are the granted status (Strength -> "Damage Up" 687,
        // Steel -> "Vulnerability Down" 1100). IDs are language-independent.
        private static readonly Dictionary<string, uint> PomanderStatusId = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Strength", 687 },
            { "Steel", 1100 },
        };

        private static bool PlayerHasStatus(uint statusId)
        {
            var statuses = Svc.Objects.LocalPlayer?.StatusList;
            if (statuses == null) return false;

            foreach (var s in statuses)
                if (s.StatusId == statusId) return true;
            return false;
        }

        private static string StripPomanderPrefix(string name)
        {
            var ofIdx = name.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
            return ofIdx >= 0 ? name.Substring(ofIdx + 4).Trim() : name;
        }

        private static string LookupDescription(string name, Dictionary<string, string> map)
        {
            // Pomanders read "Pomander of X" -> key on "X". Magicite/demiclones read "X Magicite"
            // or "X Demiclone" -> key on the leading word. We probe both shapes against the table
            // and fall back to empty when we don't have a curated blurb.
            var ofIdx = name.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
            if (ofIdx >= 0)
            {
                var key = name.Substring(ofIdx + 4).Trim();
                if (map.TryGetValue(key, out var desc)) return desc;
            }

            var spaceIdx = name.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var key = name.Substring(0, spaceIdx).Trim();
                if (map.TryGetValue(key, out var desc)) return desc;
            }

            return string.Empty;
        }
    }
}
