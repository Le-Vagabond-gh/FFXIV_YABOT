using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using YABOT.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Numerics;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace YABOT.Features.DeepDungeons
{
    public unsafe class PomanderList : BaseFeature
    {
        public override string Name => "Pomander List";

        public override string Description =>
            "While inside a deep dungeon (Palace of the Dead, Heaven-on-High, Eureka Orthos), shows a clickable overlay listing the pomanders and magicite/demiclones you currently hold. Each row shows the icon, name, a short effect summary, and the quantity. Click a row to use that pomander/stone. Optionally shows the Beacon of Passage progress percentage, a count of monsters killed on the current floor against the floor's kill-to-open range (accounting for a Pomander of Flight used on the previous floor), and an estimated mob respawn timer. Hold Shift to drag the window to reposition it.";

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

            [FeatureConfigOption("Show kill counter (kills toward passage, with floor range)")]
            public bool ShowKillCounter = true;

            [FeatureConfigOption("Show respawn timer")]
            public bool ShowRespawnTimer = true;

            [FeatureConfigOption("Lock panel (disable Shift to move)")]
            public bool LockPanel = false;

            [FeatureConfigOption("Debug: show raw passage counter")]
            public bool DebugRawPassage = false;

            [FeatureConfigOption("When a coffer holds a pomander you're already at max on:", "RadioEnum")]
            public CappedAction CappedPomanderAction = CappedAction.Off;

            // Only meaningful (and only shown) in Prompt mode - see ShouldShow below. The name sorts
            // it right beneath the radio above.
            [FeatureConfigOption("When you confirm, re-open the coffer you just tried", ConditionalDisplay = true)]
            public bool ReopenCofferAfterUse = false;

            public bool ShouldShowReopenCofferAfterUse() => CappedPomanderAction == CappedAction.Prompt;
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

        // Count enemy NPCs killed on the current floor, shown against the per-floorset passage range
        // (the game exposes no real kill count - PassageProgress just snaps to >=11 when the hidden,
        // per-floor random threshold is met). Each enemy is counted once on the frame it's first seen
        // dead, keyed by GameObjectId; both reset on a floor change alongside the respawn anchor.
        private readonly HashSet<ulong> _countedKills = new();
        private int _floorKillCount;
        private const byte EnemySubKind = 5; // BattleNpcSubKind value for a standard enemy/combatant

        // Each pomander type caps at 3 in the carry inventory; the count turns blue once it's full
        // so the player can spot one to spend before the next coffer. Magicite shows no count.
        private const byte PomanderMaxCount = 3;

        // Latched once the beacon opens (raw >= 11) so the kill count freezes at the kills-to-open
        // value instead of ticking up on cleanup kills. Reset on floor change.
        private bool _passageOpen;

        // When a magicite/demiclone was last used (from chat). For a short window after, the kill
        // counter skips the floor-wide death burst the summon produces - it isn't a kills-to-open
        // signal and would otherwise spike the count.
        private DateTime _magiciteUsedAt = DateTime.MinValue;
        private const double MagiciteWipeWindowSeconds = 10.0;

        // Debug: per-floor log of (seconds-since-entry, kills, raw PassageProgress) samples, appended
        // whenever kills or raw change. Clicking the Raw segment dumps it to /xllog for easy pasting.
        // Reset on floor change with the rest of the per-floor state.
        private readonly List<(double T, int Kills, int Raw)> _passageHistory = new();

        // Pomander of Flight takes effect only on the floor *after* it's spent and has no on-floor
        // status to read, so we remember the floor it was used on (its stock drops) and treat the
        // immediately following floor as Flighted - where each kill counts double toward the passage.
        private byte _flightUsedOnFloor;

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

        // When a slot's count last hit zero. Using the last of a pomander empties the slot a beat
        // before its active-effect flag comes up, so the row would vanish then snap back - shifting
        // everything below it right under the cursor. Hold an emptied row in place briefly to bridge
        // that gap (it stays for good if the effect does light up, else falls off after the linger).
        private readonly DateTime[] _slotEmptiedAt = new DateTime[16];
        private const double EmptyLingerSeconds = 2.0;

        // Pending "use a capped pomander?" prompt (Prompt mode). Holds the slot to use, the name to
        // show, and an auto-dismiss time so a stale prompt doesn't linger.
        private uint? _promptSlot;
        private string _promptName = string.Empty;
        private DateTime _promptExpiry;
        private const double PromptTimeoutSeconds = 15.0;

        // ReopenCofferAfterUse: the coffer we bounced off of (captured when the prompt was armed, while
        // we're still standing on it). After Yes uses the pomander we re-open it insistently - target
        // then interact a few times a second, mirroring the potion retry - until it opens (vanishes
        // from the object table) or we give up. Coffer id is captured at arm time so a stale target or
        // a slight step doesn't lose it.
        private ulong _promptChestId;
        private ulong _reopenChestId;
        private DateTime _reopenDeadline;
        private DateTime _reopenLastTry = DateTime.MinValue;
        private const double ReopenTimeoutSeconds = 6.0;
        private const double ReopenTryThrottleSeconds = 0.2;

        // Pending pomander use from a Yes click. UsePomander returns void and can be silently rejected
        // (animation lock, etc.), so we retry until the slot count actually drops - the only proof it
        // landed - and only then arm the re-open. A use that never goes through opens nothing.
        private uint? _pendingUseSlot;
        private byte _pendingUseCount;
        private DateTime _pendingUseDeadline;
        private DateTime _pendingUseLastTry = DateTime.MinValue;
        private bool _pendingThenReopen;
        private const double UseTimeoutSeconds = 3.0;
        private const double UseTryThrottleSeconds = 0.5;

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
            _flightUsedOnFloor = 0;
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

        // Two pomander chat lines drive state here, both resolved against the dungeon's own slots:
        //  - "You return the <item> to the coffer. You cannot carry any more of that item." (capped):
        //    flash the row blue so the player knows which to spend, and optionally use/prompt.
        //  - "You use a <item>." (used): only Flight matters - it acts on the *next* floor, so we note
        //    the floor it was spent on; there's no on-floor status to read it back from later.
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
                var capped = text.Contains("cannot carry any more", StringComparison.OrdinalIgnoreCase);
                // "use a" keeps this off the capped/return lines, which never contain it.
                var used = !capped && text.Contains("use a", StringComparison.OrdinalIgnoreCase);
                if (!capped && !used) return;

                // Magicite (HoH) / demiclone (EO) use: e.g. "You use a splinter of Vortex magicite."
                // These wipe the floor, so flag the time and let the kill counter skip the death burst.
                // They live in MagiciteSlot, not PomanderSlot, so handle them before the pomander match.
                if (used && (text.Contains("magicite", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("demiclone", StringComparison.OrdinalIgnoreCase)))
                {
                    _magiciteUsedAt = DateTime.Now;
                    return;
                }

                if (!TryMatchPomanderSlot(ddRow, text, out var slot, out var shortName))
                    return;

                if (used)
                {
                    if (shortName.Equals("Flight", StringComparison.OrdinalIgnoreCase))
                        _flightUsedOnFloor = dd->Floor;
                    return;
                }

                // Capped: flash the row blue (keyed off the stripped name DrawRow draws under).
                _flashStart[shortName] = (DateTime.Now, FlashKind.Capped);

                // Nothing to spend if we don't actually hold one.
                if (dd->Items[slot].Count == 0) return;

                switch (Config.CappedPomanderAction)
                {
                    case CappedAction.AutoUse:
                        dd->UsePomander((uint)slot);
                        break;
                    case CappedAction.Prompt:
                        _promptSlot = (uint)slot;
                        _promptName = shortName;
                        _promptExpiry = DateTime.Now.AddSeconds(PromptTimeoutSeconds);
                        // We're standing on the coffer right now, so the nearest one is the one we
                        // just tried. Capture it so a Yes can re-open it even if we drift a little.
                        _promptChestId = Config.ReopenCofferAfterUse ? FindNearestChestId() : 0;
                        break;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{Name}] chat handler failed");
            }
        }

        // Finds the dungeon pomander slot a chat line refers to by matching the item's Singular form
        // (the name the game uses mid-sentence), so we land on the exact slot - and the right
        // pomander/protomander variant for this dungeon. Returns the slot and its stripped row name.
        private static bool TryMatchPomanderSlot(DeepDungeon ddRow, string text, out int slot, out string shortName)
        {
            slot = -1;
            shortName = string.Empty;
            var pomanderSheet = Svc.Data.GetExcelSheet<DeepDungeonItem>();
            for (var i = 0; i < 16; i++)
            {
                var slotRef = ddRow.PomanderSlot[i];
                if (slotRef.RowId == 0) continue;
                if (!pomanderSheet.TryGetRow(slotRef.RowId, out var pomander)) continue;

                var singular = pomander.Singular.ToString();
                if (string.IsNullOrEmpty(singular)) continue;
                if (!text.Contains(singular, StringComparison.OrdinalIgnoreCase)) continue;

                slot = i;
                shortName = StripPomanderPrefix(pomander.Name.ToString());
                return true;
            }
            return false;
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
                if (Config.ShowKillCounter || Config.DebugRawPassage) TrackKills(dd);
                if (Config.DebugRawPassage) RecordPassageHistory(dd);
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
                TickPendingUse(dd);
                TickReopenCoffer();
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
                    // Don't use (or re-open) inline: the use can be rejected. Hand it to the pending-use
                    // tick, which retries until the slot count drops and only then arms the re-open, so
                    // a use that never goes through never opens the coffer.
                    _pendingUseSlot = slot;
                    _pendingUseCount = dd->Items[(int)slot].Count;
                    _pendingUseDeadline = DateTime.Now.AddSeconds(UseTimeoutSeconds);
                    _pendingUseLastTry = DateTime.MinValue;
                    _pendingThenReopen = Config.ReopenCofferAfterUse && _promptChestId != 0;
                    _promptSlot = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(120, 0)))
                    _promptSlot = null;
            }
            ImGui.End();

            ImGui.PopStyleVar();
        }

        // The nearest deep-dungeon coffer to the player, by GameObjectId (0 if none in range). Called
        // when arming the prompt, so it resolves the coffer we just tried to open.
        private static ulong FindNearestChestId()
        {
            if (Svc.Objects.LocalPlayer is not { } player) return 0;
            var pos = player.Position;
            ulong best = 0;
            var bestDist = float.MaxValue;
            foreach (var o in Svc.Objects)
            {
                if (DeepDungeonChestPath.ClassifyChest(o.DataId) == null) continue;
                var d = Vector3.DistanceSquared(o.Position, pos);
                if (d < bestDist) { bestDist = d; best = o.GameObjectId; }
            }
            return best;
        }

        // Insistently use the pomander a Yes click queued, retrying until the slot count drops (the
        // only proof it landed, since UsePomander is void and silently no-ops while animation-locked).
        // Only then arm the re-open - so if the use never goes through, the coffer is left alone.
        private void TickPendingUse(InstanceContentDeepDungeon* dd)
        {
            if (_pendingUseSlot is not { } slot) return;

            var now = DateTime.Now;

            // Landed: count dropped. Optionally hand off to the re-open, then clear.
            if (dd->Items[(int)slot].Count < _pendingUseCount)
            {
                if (_pendingThenReopen)
                {
                    _reopenChestId = _promptChestId;
                    _reopenDeadline = now.AddSeconds(ReopenTimeoutSeconds);
                }
                _pendingUseSlot = null;
                return;
            }

            // Never went through within the window -> give up without touching the coffer.
            if (now > _pendingUseDeadline) { _pendingUseSlot = null; return; }

            // Retry a couple times a second; a successful use animation-locks us, which blocks a
            // second fire until the count drops above, so this won't burn two pomanders.
            if ((now - _pendingUseLastTry).TotalSeconds < UseTryThrottleSeconds) return;
            _pendingUseLastTry = now;
            try { dd->UsePomander(slot); }
            catch (Exception ex) { Svc.Log.Error(ex, $"[{Name}] pending use failed"); }
        }

        // Insistently re-open the armed coffer: target it, then interact, a few times a second until
        // it opens (drops out of the object table) or we time out. Mirrors the potion retry - the game
        // quietly ignores the interact while we're animation-locked from the pomander, so we just keep
        // re-attempting until it lands.
        private void TickReopenCoffer()
        {
            if (_reopenChestId == 0) return;

            var now = DateTime.Now;
            if (now > _reopenDeadline) { _reopenChestId = 0; return; }

            var chest = Svc.Objects.FirstOrDefault(o => o.GameObjectId == _reopenChestId);
            // An opened coffer despawns from the table - but not right away; for a beat it lingers
            // while becoming untargetable. Treat either as "opened, we're done" so the loop doesn't
            // keep firing on an already-open coffer and re-targeting it (which clears the player's target).
            if (chest == null || !chest.IsTargetable) { _reopenChestId = 0; return; }

            if ((now - _reopenLastTry).TotalSeconds < ReopenTryThrottleSeconds) return;
            _reopenLastTry = now;

            // Target first, then interact once it's the current target (same handshake the game uses).
            if (Svc.Targets.Target?.GameObjectId != _reopenChestId)
            {
                Svc.Targets.Target = chest;
                return;
            }

            var ts = TargetSystem.Instance();
            if (ts != null)
                ts->InteractWithObject((CSGameObject*)chest.Address, false);
        }

        private void UpdateFloorTracking(InstanceContentDeepDungeon* dd)
        {
            // Re-anchor the respawn clock whenever the floor changes. The DD instance survives
            // floor-to-floor transitions, so dd->Floor updating is a reliable "new floor" signal.
            // (Enabling the overlay mid-floor anchors to that moment, so the first cycle can read
            // early - same inherent caveat as NecroLens.)
            if (dd->Floor != _lastFloor)
            {
                // Dump the floor we're leaving to /xllog before clearing it (debug only, real floors
                // only - floor 0 is the pre-load transient). _lastFloor still holds the previous floor
                // and _flightUsedOnFloor isn't reset yet, so the dump reflects the collected floor.
                if (Config.DebugRawPassage && _lastFloor != 0 && _passageHistory.Count > 0)
                    DumpPassageHistory(dd->DeepDungeonId, _lastFloor);

                // A non-sequential change (fresh entry, or a Pomander of Return sending you back)
                // invalidates a queued Flight - it only ever carries to the next floor in sequence.
                if (dd->Floor != _lastFloor + 1)
                    _flightUsedOnFloor = 0;

                _lastFloor = dd->Floor;
                _floorEnterTime = DateTime.Now;
                _countedKills.Clear();
                _floorKillCount = 0;
                _passageHistory.Clear();
                _passageOpen = false;
                _magiciteUsedAt = DateTime.MinValue; // don't let a wipe window bleed into the next floor
            }
        }

        // Append a sample when kills or the raw counter changed since the last one (debug only).
        private void RecordPassageHistory(InstanceContentDeepDungeon* dd)
        {
            if (dd->Floor == 0) return; // pre-load transient: floor not established, time not anchored
            int raw = dd->PassageProgress;
            var last = _passageHistory.Count > 0 ? _passageHistory[^1] : (T: -1.0, Kills: -1, Raw: -1);
            if (_floorKillCount == last.Kills && raw == last.Raw) return;
            _passageHistory.Add(((DateTime.Now - _floorEnterTime).TotalSeconds, _floorKillCount, raw));
        }

        // Dump the current floor's passage history to /xllog in a paste-friendly block.
        private void DumpPassageHistory(int deepDungeonId, int floor)
        {
            var flight = _flightUsedOnFloor != 0 && floor == _flightUsedOnFloor + 1;
            var range = DeepDungeonPassage.TryGetKillRange(deepDungeonId, floor, out var lo, out var hi)
                ? $"{lo}-{hi}" : "n/a";
            var dungeon = deepDungeonId switch { 1 => "PotD", 2 => "HoH", 3 => "EO", 4 => "PT", _ => $"#{deepDungeonId}" };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{Name}] Passage history - {dungeon} floor {floor}, range {range}, flight {flight}");
            foreach (var (t, kills, raw) in _passageHistory)
                sb.AppendLine($"  t={t,6:0.0}s  kills={kills,2}  raw={raw,2}");
            Svc.Log.Info(sb.ToString().TrimEnd());
        }

        // Scan the object table for enemy NPCs that are dead and tally each one once. Corpses linger
        // for several seconds before despawning, so observing IsDead across frames reliably catches
        // every kill. Only runs while the kill counter is enabled to avoid the per-frame scan otherwise.
        private void TrackKills(InstanceContentDeepDungeon* dd)
        {
            // Freeze the count once the beacon has opened so it reflects kills-to-open, not cleanup
            // kills. The latch is checked before counting (so the kill that opens it is still tallied -
            // raw flips to >=11 only after that kill registers) and set after, off this frame's raw.
            if (_passageOpen) return;

            var newKills = 0;
            foreach (var obj in Svc.Objects)
            {
                if (obj is not IBattleNpc npc) continue;
                // BattleNpcSubKind == 5 is a standard enemy/combatant. Compared by underlying byte
                // because Dalamud and FFXIVClientStructs disagree on the member name across versions
                // (Enemy / BattleNpcEnemy / Combatant) while sharing the value.
                if ((byte)npc.BattleNpcKind != EnemySubKind) continue;
                if (!npc.IsDead) continue;
                if (_countedKills.Add(npc.GameObjectId))
                    newKills++;
            }

            // Skip the kill burst from a magicite/demiclone summon (detected in chat): it wipes the
            // floor at once and opens the passage, so those deaths aren't a kills-to-open signal. The
            // IDs are already marked seen above, so they're never recounted once the window lapses.
            if ((DateTime.Now - _magiciteUsedAt).TotalSeconds > MagiciteWipeWindowSeconds)
                _floorKillCount += newKills;

            _passageOpen = dd->PassageProgress >= 11;
        }

        // The status line sits above the item rows with up to three optional segments drawn
        // left-to-right: Beacon of Passage progress, the kills-toward-passage counter, and the
        // dead-reckoned respawn timer. Any can be hidden, and all are suppressed on boss floors
        // (no passage, no respawns there).
        private void DrawStatusLine(InstanceContentDeepDungeon* dd)
        {
            var boss = DeepDungeonRespawn.IsBossFloor(dd->DeepDungeonId, dd->Floor);
            var showPassage = Config.ShowPassageProgress && !boss;
            var showKills = Config.ShowKillCounter && !boss;
            // Debug: raw counter is shown everywhere (boss floors included) for data collection.
            var showRaw = Config.DebugRawPassage;

            var interval = 0;
            var showRespawn = Config.ShowRespawnTimer
                && DeepDungeonRespawn.TryGetInterval(dd->DeepDungeonId, dd->Floor, out interval);

            if (!showPassage && !showKills && !showRespawn && !showRaw) return;

            var open = dd->PassageProgress >= 11;

            // Left-to-right segments: passage progress, raw debug counter, kill counter, then the timer.
            var segments = new List<(string Text, Vector4? Color)>(4);

            if (showPassage)
            {
                // PassageProgress fills 0-10 as the floor is cleared and reads >=11 once the beacon
                // is open; surface it as a 0-100% tracker, green once it's open.
                var pct = open ? 100 : Math.Min((int)dd->PassageProgress, 10) * 10;
                segments.Add(($"Passage  {pct}%", open ? ActiveGreen : (Vector4?)null));
            }

            if (showRaw)
            {
                // The raw PassageProgress byte. It tracks "progress units" (a Flight kill counts as 2)
                // and snaps to >=11 once the per-floor random threshold is met. The floor's full
                // history is auto-dumped to /xllog on the next floor change for easy pasting.
                segments.Add(($"Raw  {dd->PassageProgress}", open ? ActiveGreen : (Vector4?)null));
            }

            if (showKills)
            {
                // The real kill count, shown against the per-floorset range the random open-threshold
                // is rolled from (the game never reveals the roll - PassageProgress just snaps to open).
                // Pomander of Flight, spent on the previous floor, makes each kill count double, so the
                // kills actually needed halve - reflected by halving the displayed range.
                var flight = _flightUsedOnFloor != 0 && dd->Floor == _flightUsedOnFloor + 1;
                var killText = $"Kills  {_floorKillCount}";
                if (!open && DeepDungeonPassage.TryGetKillRange(dd->DeepDungeonId, dd->Floor, out var lo, out var hi))
                {
                    if (flight) { lo = (lo + 1) / 2; hi = (hi + 1) / 2; } // ceil(n/2): each kill counts double
                    killText += $" / {lo}-{hi}";
                    if (flight) killText += " (Flight)";
                }
                segments.Add((killText, open ? ActiveGreen : (Vector4?)null));
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
                if (prevCount > 0 && info.Count == 0)
                    _slotEmptiedAt[slot] = DateTime.Now;

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
                // consumed, so the active marker doesn't vanish the moment the count hits zero -
                // and for a short linger after it empties, to bridge the gap before the effect
                // flag comes up so the row doesn't flicker out and shift the rows below.
                var lingering = (DateTime.Now - _slotEmptiedAt[slot]).TotalSeconds < EmptyLingerSeconds;
                if (info.Count == 0 && !isActive && !lingering) continue;

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
            // Pilgrim's Traverse (added in 7.4) introduces Juniper Incenses with a type value
            // not covered by the static schema, so we can't resolve their names through a
            // sheet link. Instead read the game's own pre-resolved name/icon out of the
            // DeepDungeonStatus agent, which covers every dungeon type; the sheets stay as a
            // fallback for 1/2 when the agent isn't up. The UseStone call works regardless
            // because the engine indexes by slot, not by sheet identity.
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
                // Anything that isn't a magic stone (1) or demiclone (2) is a Pilgrim's Traverse
                // incense; resolve those by RowId (crash-proof, unlike the status agent's name buffers).
                if (string.IsNullOrEmpty(name) && type != 1 && type != 2)
                    (icon, name) = ResolvePtIncense(sheetIndex, stoneSheet, demicloneSheet);
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
            // Magicite names come straight from the typed sheet the DeepDungeonType selects: 1 =
            // DeepDungeonMagicStone (PotD/HoH), 2 = DeepDungeonDemiclone (Eureka Orthos). Pilgrim's
            // Traverse incenses (type 3) share these sheets at RowIds the unmapped link can't
            // disambiguate, so the caller resolves them by catalog slot via ResolvePtIncense; we do
            // NOT probe by RowId here, which returned bogus names (Inferno/Odin). Never touch the
            // DeepDungeonStatus agent: its Utf8String name buffers are unpopulated while the status
            // window is closed, and ToString() on one throws an uncatchable AccessViolationException.
            if (deepDungeonType == 1 && stoneSheet.TryGetRow(rowId, out var stone) && stone.Icon != 0)
                return (stone.Icon, stone.Name.ToString());
            if (deepDungeonType == 2 && demicloneSheet.TryGetRow(rowId, out var demi) && demi.Icon != 0)
                return (demi.Icon, demi.TitleCase.ToString());

            return (0, string.Empty);
        }

        // Pilgrim's Traverse incenses are split across the two magicite sheets at colliding RowIds, so
        // the unmapped MagiciteSlot link can't disambiguate them (slots 0 and 2 both read RowId 5).
        // Their MagiciteSlot catalog position is fixed, though, so map each slot to its real sheet+row
        // and read the localized name + icon from there (verified via xivapi).
        private enum IncenseSheet { Stone, Demiclone }

        private static readonly Dictionary<int, (IncenseSheet Sheet, uint RowId)> PtIncenseSlots = new()
        {
            { 0, (IncenseSheet.Stone, 5) },     // Poisonfruit Incense
            { 1, (IncenseSheet.Demiclone, 4) }, // Mazeroot Incense
            { 2, (IncenseSheet.Demiclone, 5) }, // Barkbalm Incense
        };

        private static (uint Icon, string Name) ResolvePtIncense(
            int sheetIndex,
            Lumina.Excel.ExcelSheet<DeepDungeonMagicStone> stoneSheet,
            Lumina.Excel.ExcelSheet<DeepDungeonDemiclone> demicloneSheet)
        {
            if (!PtIncenseSlots.TryGetValue(sheetIndex, out var loc)) return (0, string.Empty);
            if (loc.Sheet == IncenseSheet.Stone && stoneSheet.TryGetRow(loc.RowId, out var stone) && stone.Icon != 0)
                return (stone.Icon, stone.Name.ToString());
            if (loc.Sheet == IncenseSheet.Demiclone && demicloneSheet.TryGetRow(loc.RowId, out var demi) && demi.Icon != 0)
                return (demi.Icon, demi.TitleCase.ToString());
            return (0, string.Empty);
        }

        private void DrawRow(uint iconId, string name, string rowKey, string desc, byte count, bool showCount, bool isActive, System.Action onClick)
        {
            // GetFontSize reflects the SetWindowFontScale already pushed in Draw, so icons
            // track the configured scale via font size alone.
            var iconSize = ImGui.GetFontSize() * 1.6f;

            // The trailing count is split off from the rest of the label so it can be tinted blue
            // at the carry cap while name/effect keep their normal color.
            var countText = showCount ? $"   x{count}" : string.Empty;
            var baseLabel = Config.HideName
                ? (string.IsNullOrEmpty(desc) ? string.Empty : desc)
                : (string.IsNullOrEmpty(desc) ? name : $"{name} - {desc}");
            // Nothing for the clickable selectable to anchor to (name hidden, no effect blurb): fold
            // the count back in so the row stays clickable - it just won't get the blue max tint.
            if (string.IsNullOrEmpty(baseLabel)) { baseLabel = countText.TrimStart(); countText = string.Empty; }

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
            // Count rides the row color normally, but turns blue once it hits the carry cap.
            var countColor = showCount && count >= PomanderMaxCount ? FlashBlue : color;

            if (Config.RightAlign)
                DrawRowRightAligned(iconId, iconSize, baseLabel, countText, hiddenId, color, countColor, flashing, bounce, arrowIcon, onClick);
            else
                DrawRowLeftAligned(iconId, iconSize, baseLabel, countText, hiddenId, color, countColor, flashing, bounce, arrowIcon, onClick);

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

        private void DrawRowLeftAligned(uint iconId, float iconSize, string baseLabel, string countText, string id, Vector4? color, Vector4? countColor, bool flashing, float bounce, uint arrowIcon, System.Action onClick)
        {
            var startY = ImGui.GetCursorPosY();
            if (flashing)
                DrawFlashArrow(iconSize, bounce, arrowIcon);

            DrawIcon(iconId, iconSize);
            ImGui.SameLine();

            var textOffsetY = (iconSize - ImGui.GetTextLineHeight()) * 0.5f;
            if (textOffsetY > 0)
                ImGui.SetCursorPosY(startY + textOffsetY);

            DrawSelectable(baseLabel + id, 0f, iconSize, color, onClick);
            DrawCountSegment(countText, countColor);
        }

        private void DrawRowRightAligned(uint iconId, float iconSize, string baseLabel, string countText, string id, Vector4? color, Vector4? countColor, bool flashing, float bounce, uint arrowIcon, System.Action onClick)
        {
            // Push the row to the right edge of the auto-resized window. ContentRegionAvail is
            // the widest row's width (set by previous frames in AlwaysAutoResize windows), so
            // shorter rows still settle next to the right edge across frames.
            var gap = ImGui.GetStyle().ItemSpacing.X;
            var labelWidth = ImGui.CalcTextSize(baseLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
            var countWidth = string.IsNullOrEmpty(countText) ? 0f : ImGui.CalcTextSize(countText).X;
            var arrowWidth = flashing ? FlashArrowWidth(iconSize) + gap : 0f;
            var totalWidth = arrowWidth + labelWidth + countWidth + gap + iconSize;
            var avail = ImGui.GetContentRegionAvail().X;
            if (avail > totalWidth)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - totalWidth);

            var startY = ImGui.GetCursorPosY();
            if (flashing)
                DrawFlashArrow(iconSize, bounce, arrowIcon);

            var textOffsetY = (iconSize - ImGui.GetTextLineHeight()) * 0.5f;
            if (textOffsetY > 0)
                ImGui.SetCursorPosY(startY + textOffsetY);

            DrawSelectable(baseLabel + id, labelWidth, iconSize, color, onClick);
            DrawCountSegment(countText, countColor);

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

        // Draws the trailing "xN" count right after a row's label, tinted independently (blue at the
        // carry cap) so the name/effect can keep their own color. The leading spaces in countText
        // give the gap, so this sits flush against the selectable.
        private static void DrawCountSegment(string countText, Vector4? color)
        {
            if (string.IsNullOrEmpty(countText)) return;
            ImGui.SameLine(0, 0);
            if (color.HasValue) ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
            ImGui.TextUnformatted(countText);
            if (color.HasValue) ImGui.PopStyleColor();
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
            { "Mazeroot", "reveal + cleanse + polymorph" },
            { "Barkbalm", "double HP + boss dps" },
            { "Poisonfruit", "invuln + floor wipe" },
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
