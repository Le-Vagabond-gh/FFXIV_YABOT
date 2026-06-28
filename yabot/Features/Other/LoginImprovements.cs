using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YABOT.Features.Other
{
    // ============================================================================
    // CONTEXT FOR PICKING THIS UP COLD
    // ============================================================================
    //
    // What this feature does:
    //   - Plays an emote on the char-select character. Three modes (radio button):
    //       LoopInCombatIdle    - configured emote, replay every few seconds.
    //       PlayOnceAndSheathe  - configured emote, end-of-loop sheathes if drawn.
    //       RandomSequence      - ignore configured emote, pick a random one each
    //                             loop end. Hides the Default Emote / per-job UI.
    //   - Per-job emote overrides + a global default (used in Loop and PlayOnce).
    //   - Real-time emote switching: edit the dropdown while on char-select and
    //     PlayEmote re-runs immediately (skipped in RandomSequence to avoid the
    //     random pick getting clobbered by the configured value every frame).
    //   - Pet/Carbuncle/Fairy display and minion display on char-select. Minion
    //     position is offset behind the character (Z=-0.5) so the character idle
    //     animation doesn't clip through it.
    //   - Optional white-text-with-blue-glow overlay showing the playing emote
    //     name near the bottom of the screen (matches FFXIV's char-name edge color
    //     R=00 G=99 B=FF, drawn as 8-direction outline rather than drop shadow).
    //   - Skip login/logout confirm, move queue dialog.
    //   - Debug-preview UI (in-game CharaView puppet) is commented out but the
    //     helpers are kept around: easy to wire back in to investigate animation
    //     state without logging out and selecting a character.
    //
    // Two playback contexts share the same PlayEmote code path:
    //   1. Char-select: UpdateCharaSelectDisplayDetour fires when a character is
    //      selected. It sets currentCharacter to the char-select Character* and
    //      calls PlayEmote.
    //   2. Debug preview (in-game only, currently disabled): StartDebugPreview
    //      opens the Character window (AgentStatus). UpdateDebugPreview each frame
    //      routes currentCharacter to AgentStatus.CharaView.GetCharacter().
    //
    // Emote classification (read from sheets):
    //   - Emote.DrawsWeapon (bool) -> previewWeaponDrawn. Drives weapon-drawn flow.
    //   - Emote.EmoteMode.RowId != 0 -> persistent (/sit, /doze, etc.).
    //
    // Weapon-drawn emote pipeline:
    //   The LIVE LOCAL PLAYER doing /battlestance then /idle:
    //     - Mid /battlestance: Mode=Normal, Flags3=0xD0 (incl. 0x40 WeaponDrawn),
    //       Slot[0]=3778 "emote/battle02" (the loop).
    //     - Mid /idle transition: Flags3=0x90 (0x40 cleared), Slot[0]=3 "normal/idle",
    //       Slot[1]=2 "battle/battle_end" (the sheathe overlay - upper body only).
    //
    //   In-game Character window (debug preview): the CharaView is a puppet, not
    //   driven by world simulation. PlayActionTimeline(2, 3) puts 2 in Slot[1]
    //   (UpperBody overlay) and the puppet renders it fine.
    //
    //   Char-select: the lobby's animation pipeline DOES NOT TICK Slot[1] overlays.
    //   We tried everything we could think of - keeping Flags3 0x40 set/cleared,
    //   re-asserting Slot[1] every frame, BaseOverride, SetSlotTimeline directly,
    //   Mode=AnimLock - the upper-body slot scheduler just doesn't run for the
    //   char-select character. The weapon visually transitions to sheathed (Flags3
    //   0x40 clear) but the arm-motion overlay never plays.
    //
    //   What works: play a real full-body "Sheathe Weapon" emote (row 237) instead
    //   of the bare battle/battle_end timeline. Its ActionTimeline[0] is base-slot
    //   data, which the lobby pipeline does process. ResolveSheatheEmoteId looks
    //   it up by name with a few candidate strings; result is cached. Falls back
    //   to PlayActionTimeline(2, 3) if the lookup fails (better than nothing).
    //
    //   When the visible sheathe runs:
    //     - On loop end (in OnFrameworkUpdate's weapon-drawn detection block),
    //       always - regardless of mode. PlayOnceAndSheathe stays sheathed after.
    //       LoopInCombatIdle / RandomSequence schedule a replay via
    //       pendingWeaponEmoteReplayAt at +1500ms (sheathe duration) +
    //       LoopReplayDelaySeconds idle wait.
    //   When it does NOT run (PlayExitAnimation):
    //     - When PlayEmote is called to switch to a different emote (real-time
    //       config edit, RandomSequence rotation, etc.). Playing the sheathe on
    //       top of the next emote looks wrong; we just clear Flags3 0x40 and let
    //       PlayEmote re-set it if the new emote also draws.
    //
    // Key state fields:
    //   - currentCharacter        Character* (char-select OR debug preview puppet).
    //   - currentContentId        Char-select's selected content id (gates pet/minion
    //                             config and the real-time emote-switch check).
    //   - currentClassJobId       Char-select's class job (used for per-job override).
    //   - activeEmoteId           The emote id currently playing.
    //   - activeLoopTimeline      Emote's ActionTimeline[0] (the loop).
    //   - activeIntroTimeline     Emote's ActionTimeline[1] (the intro).
    //   - previewWeaponDrawn      True while a DrawsWeapon=true emote is active.
    //   - previewEmoteStarted     True once we've observed Slot[0] = activeLoopTimeline.
    //                             Drives the "Slot[0] transitioned away" detection.
    //   - pendingSheatheCompleteAt  +1500ms after TriggerPreviewSheathe; final
    //                             cleanup of Flags3 0x40 + CharaView.DrawWeapon.
    //   - pendingWeaponEmoteReplayAt  Loop/Random replay timer (drawn emote path).
    //   - persistentEmoteStartedAt / persistentEmoteExitingSince
    //                             Timers for /sit /doze etc. loop/exit handling.
    //
    // Flat config (no contentId for emotes - those legitimately apply to all chars):
    //   - Config.DefaultEmote (uint)
    //   - Config.JobEmoteOverrides (Dictionary<byte, uint>)
    //   - Config.WeaponEmoteMode  LoopInCombatIdle | PlayOnceAndSheathe | RandomSequence.
    //   - Config.ShowCurrentEmoteName  toggles the screen overlay.
    //   Pet/minion settings ARE keyed by content id - those differ per character.
    //
    // Useful timeline IDs:
    //   2  = "battle/battle_end"  (sheathe transition - upper body, dropped on char-select)
    //   3  = "normal/idle"        (default base, weapons sheathed)
    //   34 = "battle/idle"        (combat idle, weapons drawn)
    //   3778 = "emote/battle02"   (real /battlestance loop)
    //   3779 = "emote/battle03"   (real /vpose loop)
    //   237 = "Sheathe Weapon" emote (full-body, plays on char-select)
    //
    // Diagnostic helpers retained for future debugging:
    //   - DumpEmoteData(emoteId), DumpPlayerTimelines(), DumpPreviewTimelines()
    //   - StartDebugPreview / StopDebugPreview (in-game CharaView puppet path)
    //   They have no UI exposure right now (Debug Preview section is commented
    //   out) but are still compiled in. Wire them back into DrawConfigTree if you
    //   need to investigate the lobby pipeline again.
    //
    // ============================================================================

    public unsafe class LoginImprovements : BaseFeature
    {
        public override string Name => "Login Improvements";

        public override string Description => "Emote loops, pets, minions, skip login confirm, and more on the character selection screen.";

        public override FeatureType FeatureType => FeatureType.Other;


        public class PetMirageSetting
        {
            public uint CarbuncleType;
            public uint FairyType;
        }

        public class SavedMinionData
        {
            public int ModelCharaId;
        }

        public enum WeaponEmoteBehavior
        {
            LoopInCombatIdle = 0,
            PlayOnceAndSheathe = 1,
            RandomSequence = 2,
        }

        public class Configs : FeatureConfig
        {
            public uint DefaultEmote = 0;
            public Dictionary<byte, uint> JobEmoteOverrides = new();
            public bool ShowPets = false;
            public Dictionary<ulong, PetMirageSetting> PetMirageSettings = new();
            public Vector3 PetPosition = new(-0.7f, 0f, 0f);
            public bool ShowMinion = false;
            public Dictionary<ulong, SavedMinionData> SavedMinions = new();
            public Vector3 MinionPosition = new(0.7f, 0f, -0.5f);
            public bool SkipLoginConfirm = false;
            public bool SkipLogoutConfirm = false;
            public bool SkipLogo = false;
            public bool MoveQueueDialog = false;
            //public bool OpenSettingsOnStartup = false;
            public WeaponEmoteBehavior WeaponEmoteMode = WeaponEmoteBehavior.LoopInCombatIdle;
            public bool ShowCurrentEmoteName = false;
        }

        private const double LoopReplayDelaySeconds = 3;

        // How long to hold a persistent emote (like /sit, /doze) before triggering its
        // exit animation and replaying it.
        private const double PersistentLoopIntervalSeconds = 10;

        // How long to wait for a persistent emote's end-emote animation to play out
        // before replaying the emote.
        private const double PersistentExitWaitSeconds = 2;

        public Configs Config { get; private set; }

        private Hook<AgentLobby.Delegates.UpdateCharaSelectDisplay> updateCharaSelectDisplayHook;
        private Hook<CharaSelectCharacterList.Delegates.CleanupCharacters> cleanupCharactersHook;

        private Character* currentCharacter;
        private ulong currentContentId;
        private byte currentClassJobId;
        private ushort activeLoopTimeline;
        private ushort activeIntroTimeline;
        private bool activeEmoteShouldReplay;
        private uint activeEmoteId;
        private DateTime emoteIdleStartTime;
        private bool emoteWaitingToLoop;

        private bool debugPreviewActive;
        private uint debugPreviewEmoteId;
        private bool debugPreviewPendingPlay;
        private bool previewWeaponDrawn;
        private bool previewEmoteStarted;
        private DateTime? persistentEmoteStartedAt;
        private DateTime? persistentEmoteExitingSince;
        private bool debugAutoLog;
        private DateTime lastAutoLogAt;
        private DateTime? pendingSheatheCompleteAt;
        private DateTime? pendingWeaponEmoteReplayAt;

        private BattleChara* pet;
        private ushort petIndex = 0xFFFF;
        private BattleChara* minion;
        private ushort minionIndex = 0xFFFF;
        private int lastSavedMinionModelId;
        private float minionTargetScale;

        private List<(uint RowId, string Name, uint Icon, string Category)> emoteList;
        private uint? cachedSheatheEmoteId;
        private bool sheatheEmoteResolved;
        private static readonly Random emoteRng = new();
        private List<(byte Id, string Name)> classJobList;
        private string emoteSearchFilter = "";
        private string jobSearchFilter = "";

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();

            // Migrate the old in-line minion position to the new further-back
            // spot so the character doesn't clip the minion. Only migrates if
            // the saved value matches the old default exactly; anyone who's
            // tweaked it keeps their customization.
            if (Math.Abs(Config.MinionPosition.X - 0.7f) < 0.001f &&
                Math.Abs(Config.MinionPosition.Y) < 0.001f &&
                Math.Abs(Config.MinionPosition.Z) < 0.001f)
            {
                Config.MinionPosition = new Vector3(0.7f, 0f, -0.5f);
                SaveConfig(Config);
            }

            updateCharaSelectDisplayHook ??= Svc.Hook.HookFromAddress<AgentLobby.Delegates.UpdateCharaSelectDisplay>(
                AgentLobby.MemberFunctionPointers.UpdateCharaSelectDisplay,
                UpdateCharaSelectDisplayDetour);

            cleanupCharactersHook ??= Svc.Hook.HookFromAddress<CharaSelectCharacterList.Delegates.CleanupCharacters>(
                CharaSelectCharacterList.MemberFunctionPointers.CleanupCharacters,
                CleanupCharactersDetour);

            updateCharaSelectDisplayHook.Enable();
            cleanupCharactersHook.Enable();

            Svc.Framework.Update += OnFrameworkUpdate;
            Svc.PluginInterface.UiBuilder.Draw += DrawEmoteNameOverlay;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "_CharaSelectListMenu", OnCharacterListReceiveEvent);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectOk", OnQueueDialogSetup);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "SelectOk", OnQueueDialogSetup);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Logo", OnLogoPostSetup);

            Svc.ClientState.Login += OnLogin;
            Svc.ClientState.Logout += OnLogout;
            UpdatePetMirageSettings();

            //if (Config.OpenSettingsOnStartup && !Svc.ClientState.IsLoggedIn)
            //    YABOT.P.MainWindow.IsOpen = true;

            base.Enable();
        }

        private bool UpdateCharaSelectDisplayDetour(AgentLobby* agent, sbyte index, bool a2)
        {
            var retVal = updateCharaSelectDisplayHook.Original(agent, index, a2);

            try
            {
                if (index < 0)
                {
                    CleanupCharaSelect();
                    return retVal;
                }

                if (index >= 100)
                    index -= 100;

                var entry = agent->LobbyData.GetCharacterEntryByIndex(0, agent->WorldIndex, index);
                if (entry == null)
                {
                    CleanupCharaSelect();
                    return retVal;
                }

                if (currentContentId == entry->ContentId)
                    return retVal;

                var character = CharaSelectCharacterList.GetCurrentCharacter();
                if (character == null)
                    return retVal;

                currentCharacter = character;
                currentContentId = entry->ContentId;
                currentClassJobId = entry->ClientSelectData.CurrentClass;

                SpawnPet();
                SpawnMinion();

                var emoteId = Config.WeaponEmoteMode == WeaponEmoteBehavior.RandomSequence
                    ? PickRandomEmote()
                    : ResolveEmoteForCurrentJob();
                if (emoteId != null)
                    PlayEmote(emoteId.Value);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.UpdateCharaSelectDisplay");
            }

            return retVal;
        }

        private void CleanupCharactersDetour()
        {
            CleanupCharaSelect();
            cleanupCharactersHook.Original();
        }

        private void CleanupCharaSelect()
        {
            DespawnPet();
            DespawnMinion();
            currentCharacter = null;
            currentContentId = 0;
            currentClassJobId = 0;
            activeLoopTimeline = 0;
            activeIntroTimeline = 0;
            activeEmoteShouldReplay = false;
            emoteWaitingToLoop = false;
            activeEmoteId = 0;
            previewEmoteStarted = false;
            persistentEmoteStartedAt = null;
            persistentEmoteExitingSince = null;
            pendingSheatheCompleteAt = null;
            pendingWeaponEmoteReplayAt = null;
        }

        // Draws the currently-playing emote name centered near the bottom of
        // the screen, below where the game shows the character's name. Plain
        // white text with a subtle dark blue drop shadow. Only renders while a
        // char-select character is selected and an emote is actually playing.
        private void DrawEmoteNameOverlay()
        {
            try
            {
                if (!Config.ShowCurrentEmoteName) return;
                if (debugPreviewActive) return;
                if (currentCharacter == null || currentContentId == 0) return;
                if (activeEmoteId == 0) return;

                var name = GetEmoteList()
                    .FirstOrDefault(e => e.RowId == activeEmoteId).Name;
                if (string.IsNullOrEmpty(name)) return;

                var size = ImGui.GetIO().DisplaySize;
                var textSize = ImGui.CalcTextSize(name);
                var x = (size.X - textSize.X) * 0.5f;
                var y = size.Y * 0.95f;

                var drawList = ImGui.GetForegroundDrawList();
                // Edge color FFXIV's char-select character name uses (R=00 G=99
                // B=FF A=FF in the AtkTextNode EdgeColor field). Drawn as an
                // 8-direction outline at 1px to mimic the game's glow rather
                // than a one-sided drop shadow.
                var glow = ImGui.GetColorU32(new Vector4(0f, 0x99 / 255f, 1f, 1f));
                var white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    drawList.AddText(new Vector2(x + dx, y + dy), glow, name);
                }
                drawList.AddText(new Vector2(x, y), white, name);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.DrawEmoteNameOverlay");
            }
        }

        // Plays an emote/timeline pair on the current character. Routes through
        // PlayActionTimeline (intro->loop) when both are set, falling back to
        // a single PlayTimeline call when only one is non-zero. Used everywhere
        // we need to start an emote, replay one, or play the sheathe transition.
        private void PlayTimelinePair(ushort intro, ushort loop)
        {
            if (currentCharacter == null) return;
            if (intro != 0 && loop != 0)
                currentCharacter->Timeline.PlayActionTimeline(intro, loop);
            else if (loop != 0)
                currentCharacter->Timeline.TimelineSequencer.PlayTimeline(loop);
            else if (intro != 0)
                currentCharacter->Timeline.TimelineSequencer.PlayTimeline(intro);
        }

        // Removes a ClientObjectManager-allocated character by index. Used by
        // DespawnPet / DespawnMinion - both follow the same lookup-then-delete
        // dance, the caller just clears its own index/pointer fields after.
        private void DeleteCharaByIndex(ushort index)
        {
            if (index == 0xFFFF) return;
            var com = ClientObjectManager.Instance();
            if (com != null && com->GetObjectByIndex(index) != null)
                com->DeleteObjectByIndex(index, 0);
        }

        private uint? ResolveEmoteForCurrentJob()
        {
            // Per-job override has priority, fall back to global default
            if (Config.JobEmoteOverrides.TryGetValue(currentClassJobId, out var jobEmoteId))
                return jobEmoteId;

            if (Config.DefaultEmote != 0)
                return Config.DefaultEmote;

            return null;
        }

        // Picks a random emote ID from the full emote list, avoiding the one
        // currently active so the user actually sees a change.
        private uint? PickRandomEmote()
        {
            var list = GetEmoteList();
            if (list.Count == 0)
                return null;
            if (list.Count == 1)
                return list[0].RowId;

            uint pick;
            var attempts = 0;
            do
            {
                pick = list[emoteRng.Next(list.Count)].RowId;
                attempts++;
            } while (pick == activeEmoteId && attempts < 8);
            return pick;
        }

        private void PlayEmote(uint emoteId)
        {
            try
            {
                if (currentCharacter == null)
                    return;

                // If switching away from a persistent emote, let it exit properly first
                PlayExitAnimation();

                activeEmoteShouldReplay = false;
                previewEmoteStarted = false;
                activeEmoteId = emoteId;

                if (emoteId == 0)
                {
                    currentCharacter->SetMode(CharacterModes.Normal, 0);
                    return;
                }

                var emoteSheet = Svc.Data.GetExcelSheet<Emote>();
                if (emoteSheet == null)
                    return;

                var emote = emoteSheet.GetRow(emoteId);
                var intro = (ushort)emote.ActionTimeline[1].RowId;
                var loop = (ushort)emote.ActionTimeline[0].RowId;

                previewWeaponDrawn = emote.DrawsWeapon;
                Svc.Log.Info($"[Emote] PlayEmote({emoteId}) loop={loop} intro={intro} drawsWeapon={previewWeaponDrawn} char=0x{(nint)currentCharacter:X}");
                if (previewWeaponDrawn)
                {
                    SetPreviewWeaponDrawnState(true);
                    Svc.Log.Info($"[Emote] post-SetPreviewWeaponDrawnState(true): Flags3=0x{currentCharacter->Timeline.Flags3:X2}");
                }

                var isPersistentMode = emote.EmoteMode.RowId != 0;
                var loopIsNatural = false;
                if (loop != 0)
                {
                    var atSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
                    if (atSheet != null)
                    {
                        var atRow = atSheet.GetRowOrDefault(loop);
                        if (atRow.HasValue)
                            loopIsNatural = atRow.Value.IsLoop;
                    }
                }

                // One-shot emotes return to idle after playing; replay them on a long timer
                // so the preview isn't frozen mid-idle. Persistent modes and natural loops
                // hold themselves, so we let the game drive those.
                activeEmoteShouldReplay = !isPersistentMode && !loopIsNatural;

                if (isPersistentMode)
                {
                    var mode = emote.EmoteMode.Value;
                    currentCharacter->SetMode((CharacterModes)mode.ConditionMode, (byte)emote.EmoteMode.RowId);
                    persistentEmoteStartedAt = DateTime.Now;
                }
                else
                {
                    currentCharacter->SetMode(CharacterModes.Normal, 0);
                    persistentEmoteStartedAt = null;
                }
                persistentEmoteExitingSince = null;

                activeLoopTimeline = loop;
                activeIntroTimeline = intro;

                PlayTimelinePair(intro, loop);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.PlayEmote");
            }
        }

        // Set weapon-drawn state on the active character. Required for both contexts:
        //  - Debug preview: sets CharaView.DrawWeapon so the post-emote default idle
        //    becomes combat idle, AND Flags3 0x40 so the sheathe overlay can play.
        //  - Char-select: sets Flags3 0x40 so by the time the emote ends and Slot[0]
        //    transitions, it goes to combat idle (consistent state). Without this,
        //    triggering the sheathe later would set Flags3 against a normal-idle
        //    Slot[0], and the state machine would correct that by jumping Slot[0]
        //    to combat idle and clobbering the sheathe overlay.
        private void SetPreviewWeaponDrawnState(bool drawn)
        {
            if (currentCharacter != null)
            {
                if (drawn)
                    currentCharacter->Timeline.Flags3 |= 0x40;
                else
                    currentCharacter->Timeline.Flags3 &= unchecked((byte)~0x40);
            }

            if (debugPreviewActive)
            {
                var agentModule = AgentModule.Instance();
                var baseAgent = agentModule != null ? agentModule->GetAgentByInternalId(AgentId.Status) : null;
                if (baseAgent != null)
                {
                    var agent = (AgentStatus*)baseAgent;
                    agent->CharaView.DrawWeapon = drawn;
                }
            }
        }

        // Weapon sheathe timeline - confirmed via PlayerTimelines snapshot during /idle
        // after /battlestance: Slot[0] = 3 "normal/idle", Slot[1] = 2 "battle/battle_end",
        // and Flags3 bit 0x40 is cleared. We replay that state on exit.
        private const ushort NormalIdleTimeline = 3;
        private const ushort BattleEndTimeline = 2;

        // Looks up a Slot[0] (full-body) sheathe emote on first call. Stand-alone
        // emotes whose ActionTimeline data is base-slot route through the same
        // path /battlestance does, which the lobby pipeline ticks fine - unlike
        // the bare battle/battle_end timeline that's upper-body overlay only.
        // Match common in-game names; first hit wins.
        private uint? ResolveSheatheEmoteId()
        {
            if (sheatheEmoteResolved)
                return cachedSheatheEmoteId;
            sheatheEmoteResolved = true;

            var sheet = Svc.Data.GetExcelSheet<Emote>();
            if (sheet == null)
                return null;

            string[] candidates = { "Sheathe Weapon", "Sheath Weapon", "Sheathe", "Sheath" };
            foreach (var emote in sheet)
            {
                if (emote.RowId == 0) continue;
                var name = emote.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var candidate in candidates)
                {
                    if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        cachedSheatheEmoteId = emote.RowId;
                        Svc.Log.Info($"[Sheathe] resolved emote: {emote.RowId} \"{name}\"");
                        return cachedSheatheEmoteId;
                    }
                }
            }
            Svc.Log.Info("[Sheathe] no sheathe emote found by name");
            return null;
        }

        private void TriggerPreviewSheathe()
        {
            if (!previewWeaponDrawn || currentCharacter == null)
                return;

            // Use PlayActionTimeline(intro=battle_end, loop=normal/idle) to play the
            // sheathe in the base slot then settle into normal idle. Slot[1] overlays
            // get dropped on char-select within a frame - the base slot is the only
            // animation slot the lobby pipeline reliably processes.
            //
            // CRITICAL: clear Flags3 0x40 BEFORE the call. The lobby's per-frame
            // animation update derives Slot[0] from Flags3 0x40 - while the bit is
            // set it force-corrects Slot[0] back to combat idle (timeline 34) within
            // a frame, clobbering Slot[0]=2 (sheathe) before it visibly plays. With
            // the bit cleared, the lobby expects Slot[0]=3 (normal idle, matching the
            // loop arg), so PlayActionTimeline's 2->3 sequence runs to completion.
            // Safe in the in-game preview path too: CharaView::Update() re-asserts
            // Flags3 0x40 from CharaView.DrawWeapon every frame, so the bit comes
            // right back on the puppet character.
            var seq = &currentCharacter->Timeline.TimelineSequencer;
            var slot0Before = seq->GetSlotTimeline(0);
            var slot1Before = seq->GetSlotTimeline(1);
            var flagsBefore = currentCharacter->Timeline.Flags3;
            var baseOvBefore = currentCharacter->Timeline.BaseOverride;

            currentCharacter->Timeline.Flags3 &= unchecked((byte)~0x40);

            // Prefer playing a real "Sheathe Weapon" emote: its ActionTimeline data
            // is full-body (Slot[0]), so the lobby pipeline actually ticks it.
            // Falls back to the bare battle/battle_end timeline pair, which is
            // upper-body overlay only and renders only in the in-game CharaView.
            var sheatheEmoteId = ResolveSheatheEmoteId();
            ushort intro = BattleEndTimeline;
            ushort loop = NormalIdleTimeline;
            if (sheatheEmoteId.HasValue)
            {
                var sheet = Svc.Data.GetExcelSheet<Emote>();
                var sheatheEmote = sheet?.GetRowOrDefault(sheatheEmoteId.Value);
                if (sheatheEmote.HasValue)
                {
                    intro = (ushort)sheatheEmote.Value.ActionTimeline[1].RowId;
                    loop = (ushort)sheatheEmote.Value.ActionTimeline[0].RowId;
                }
            }

            PlayTimelinePair(intro, loop);

            var slot0After = seq->GetSlotTimeline(0);
            var slot1After = seq->GetSlotTimeline(1);
            Svc.Log.Info($"[Sheathe] emote={(sheatheEmoteId?.ToString() ?? "fallback")} intro={intro} loop={loop} | Flags3 0x{flagsBefore:X2}->0x{currentCharacter->Timeline.Flags3:X2}, BaseOv {baseOvBefore}->{currentCharacter->Timeline.BaseOverride}, Slot[0] {slot0Before}->{slot0After}, Slot[1] {slot1Before}->{slot1After}");

            pendingSheatheCompleteAt = DateTime.Now.AddMilliseconds(1500);
            previewWeaponDrawn = false;
        }

        private void DumpEmoteData(uint emoteId)
        {
            if (emoteId == 0)
            {
                Svc.Log.Info("[EmoteDump] No emote selected (pick one from the dropdown first)");
                return;
            }

            var emoteSheet = Svc.Data.GetExcelSheet<Emote>();
            if (emoteSheet == null) return;

            var emote = emoteSheet.GetRowOrDefault(emoteId);
            if (!emote.HasValue)
            {
                Svc.Log.Info($"[EmoteDump] Emote {emoteId} not found");
                return;
            }

            var e = emote.Value;
            Svc.Log.Info($"[EmoteDump] Emote {emoteId}: {e.Name.ToString()}");
            Svc.Log.Info($"  DrawsWeapon={e.DrawsWeapon}, HasCancelEmote={e.HasCancelEmote}");
            Svc.Log.Info($"  EmoteMode.RowId={e.EmoteMode.RowId}");
            if (e.EmoteMode.RowId != 0)
            {
                var mode = e.EmoteMode.Value;
                Svc.Log.Info($"    StartEmote={mode.StartEmote.RowId}, EndEmote={mode.EndEmote.RowId}, ConditionMode={mode.ConditionMode}");
                Svc.Log.Info($"    Move={mode.Move}, Camera={mode.Camera}, EndOnRotate={mode.EndOnRotate}, EndOnEmote={mode.EndOnEmote}");
            }

            var atSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            for (var i = 0; i < 7; i++)
            {
                var rowId = e.ActionTimeline[i].RowId;
                if (rowId == 0)
                {
                    Svc.Log.Info($"  ActionTimeline[{i}] = 0");
                    continue;
                }
                var key = "";
                byte isLoop = 0, stance = 0, slot = 0, type = 0;
                if (atSheet != null)
                {
                    var at = atSheet.GetRowOrDefault(rowId);
                    if (at.HasValue)
                    {
                        key = at.Value.Key.ToString();
                        isLoop = at.Value.IsLoop ? (byte)1 : (byte)0;
                        stance = at.Value.Stance;
                        slot = at.Value.Slot;
                        type = at.Value.Type;
                    }
                }
                Svc.Log.Info($"  ActionTimeline[{i}] = {rowId} \"{key}\" IsLoop={isLoop} Stance={stance} Slot={slot} Type={type}");
            }

            if (e.EmoteMode.RowId != 0 && e.EmoteMode.Value.EndEmote.RowId != 0)
            {
                var endId = e.EmoteMode.Value.EndEmote.RowId;
                var endEmote = emoteSheet.GetRowOrDefault(endId);
                if (endEmote.HasValue)
                {
                    Svc.Log.Info($"  [EndEmote {endId} \"{endEmote.Value.Name.ToString()}\"]");
                    for (var i = 0; i < 7; i++)
                    {
                        var rowId = endEmote.Value.ActionTimeline[i].RowId;
                        if (rowId == 0) continue;
                        var key = "";
                        if (atSheet != null)
                        {
                            var at = atSheet.GetRowOrDefault(rowId);
                            if (at.HasValue) key = at.Value.Key.ToString();
                        }
                        Svc.Log.Info($"    EndEmote.ActionTimeline[{i}] = {rowId} \"{key}\"");
                    }
                }
            }
        }

        private void DumpPlayerTimelines()
        {
            var lp = Svc.Objects.LocalPlayer;
            if (lp == null)
            {
                Svc.Log.Info("[PlayerTimelines] not logged in");
                return;
            }

            var ch = (Character*)lp.Address;
            var seq = &ch->Timeline.TimelineSequencer;
            var flags3 = ch->Timeline.Flags3;
            var mode = ch->Mode;
            var modeParam = ch->ModeParam;

            var atSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            string KeyFor(ushort id)
            {
                if (id == 0 || atSheet == null) return "";
                var r = atSheet.GetRowOrDefault(id);
                return r.HasValue ? r.Value.Key.ToString() : "";
            }

            var playerSlots = new List<string>();
            for (uint i = 0; i < 6; i++)
            {
                var tid = seq->GetSlotTimeline(i);
                if (tid != 0)
                    playerSlots.Add($"[{i}]={tid}\"{KeyFor(tid)}\"");
            }
            var playerSlotsStr = playerSlots.Count > 0 ? " " + string.Join(" ", playerSlots) : "";
            Svc.Log.Info($"[PlayerTimelines] Mode={mode} Flags3=0x{flags3:X2} Drawn={ch->IsWeaponDrawn}{playerSlotsStr}");
        }

        private void DumpPreviewTimelines()
        {
            Character* ch = null;
            string charaViewInfo = "";

            var agentModule = AgentModule.Instance();
            var baseAgent = agentModule != null ? agentModule->GetAgentByInternalId(AgentId.Status) : null;
            if (baseAgent != null && baseAgent->IsAgentActive())
            {
                var agent = (AgentStatus*)baseAgent;
                ch = agent->CharaView.GetCharacter();
                charaViewInfo = $" CharaView.DrawWeapon={agent->CharaView.DrawWeapon}";
            }

            // Fall back to whatever currentCharacter is (e.g. the char-select character).
            if (ch == null)
                ch = currentCharacter;

            if (ch == null)
            {
                Svc.Log.Info("[PreviewTimelines] no character available");
                return;
            }

            var atSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            string KeyFor(ushort id)
            {
                if (id == 0 || atSheet == null) return "";
                var r = atSheet.GetRowOrDefault(id);
                return r.HasValue ? r.Value.Key.ToString() : "";
            }

            var seq = &ch->Timeline.TimelineSequencer;
            var slotParts = new List<string>();
            for (uint i = 0; i < 6; i++)
            {
                var tid = seq->GetSlotTimeline(i);
                if (tid != 0)
                    slotParts.Add($"[{i}]={tid}\"{KeyFor(tid)}\"");
            }
            var slotsStr = slotParts.Count > 0 ? " " + string.Join(" ", slotParts) : "";
            Svc.Log.Info($"[PreviewTimelines] Mode={ch->Mode} Flags3=0x{ch->Timeline.Flags3:X2} Drawn={ch->IsWeaponDrawn} BaseOv={ch->Timeline.BaseOverride}{charaViewInfo}{slotsStr}");
        }

        private void PlayExitAnimation()
        {
            if (currentCharacter == null || activeEmoteId == 0)
                return;

            // Weapon-drawn: clear weapon state without playing the sheathe
            // animation. The visible sheathe is reserved for the loop-end path
            // in OnFrameworkUpdate (where it represents the natural exit from a
            // weapon-drawn emote). When the user switches emotes via the config
            // UI, playing the sheathe on top of - or right before - the new
            // emote's own animation looks wrong, so we just drop the drawn
            // state and let PlayEmote re-set it if the next emote also draws.
            if (previewWeaponDrawn)
            {
                currentCharacter->Timeline.Flags3 &= unchecked((byte)~0x40);
                previewWeaponDrawn = false;
                pendingSheatheCompleteAt = null;
                pendingWeaponEmoteReplayAt = null;
                return;
            }

            var emoteToExit = activeEmoteId;

            var emoteSheet = Svc.Data.GetExcelSheet<Emote>();
            if (emoteSheet == null)
                return;

            var emote = emoteSheet.GetRowOrDefault(emoteToExit);
            if (!emote.HasValue || emote.Value.EmoteMode.RowId == 0)
            {
                currentCharacter->SetMode(CharacterModes.Normal, 0);
                return;
            }

            var endEmoteId = emote.Value.EmoteMode.Value.EndEmote.RowId;
            if (endEmoteId == 0)
            {
                currentCharacter->SetMode(CharacterModes.Normal, 0);
                return;
            }

            var endEmote = emoteSheet.GetRow(endEmoteId);
            var intro = (ushort)endEmote.ActionTimeline[1].RowId;
            var loop = (ushort)endEmote.ActionTimeline[0].RowId;

            currentCharacter->SetMode(CharacterModes.Normal, 0);

            PlayTimelinePair(intro, loop);
        }

        private void StartDebugPreview(uint emoteId)
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return;

            var agent = agentModule->GetAgentByInternalId(AgentId.Status);
            if (agent == null)
                return;

            if (!agent->IsAgentActive())
                agent->Show();

            debugPreviewActive = true;
            debugPreviewEmoteId = emoteId;
            debugPreviewPendingPlay = true;
        }

        private void StopDebugPreview()
        {
            try
            {
                PlayExitAnimation();
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.StopDebugPreview");
            }

            debugPreviewActive = false;
            debugPreviewPendingPlay = false;
            currentCharacter = null;
            activeLoopTimeline = 0;
            activeIntroTimeline = 0;
            activeEmoteShouldReplay = false;
            emoteWaitingToLoop = false;
            activeEmoteId = 0;
            previewWeaponDrawn = false;
            previewEmoteStarted = false;
            persistentEmoteStartedAt = null;
            persistentEmoteExitingSince = null;
            pendingSheatheCompleteAt = null;
            pendingWeaponEmoteReplayAt = null;
        }

        private void UpdateDebugPreview()
        {
            var agentModule = AgentModule.Instance();
            var baseAgent = agentModule != null
                ? agentModule->GetAgentByInternalId(AgentId.Status)
                : null;

            if (baseAgent == null || !baseAgent->IsAgentActive())
            {
                StopDebugPreview();
                return;
            }

            var agent = (AgentStatus*)baseAgent;
            var previewChar = agent->CharaView.GetCharacter();
            if (previewChar == null)
                return;

            currentCharacter = previewChar;

            if (debugPreviewPendingPlay)
            {
                debugPreviewPendingPlay = false;
                PlayEmote(debugPreviewEmoteId);
            }
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                if (Config.MoveQueueDialog)
                    MoveQueueDialogIfVisible();

                if (Config.SkipLogoutConfirm && Svc.ClientState.IsLoggedIn)
                    AutoConfirmLogoutIfVisible();

                if (pendingSheatheCompleteAt.HasValue && DateTime.Now >= pendingSheatheCompleteAt.Value)
                {
                    pendingSheatheCompleteAt = null;
                    Svc.Log.Info("[Sheathe] complete");

                    // Final cleanup of weapon-drawn state. Char-select already had
                    // Flags3 0x40 cleared by TriggerPreviewSheathe, but the in-game
                    // CharaView re-asserts Flags3 0x40 from CharaView.DrawWeapon
                    // every frame - so we have to flip both fields here, after the
                    // sheathe animation has played, for the puppet to settle.
                    if (currentCharacter != null)
                        currentCharacter->Timeline.Flags3 &= unchecked((byte)~0x40);

                    var agentModule = AgentModule.Instance();
                    var baseAgent = agentModule != null ? agentModule->GetAgentByInternalId(AgentId.Status) : null;
                    if (baseAgent != null)
                    {
                        var agent = (AgentStatus*)baseAgent;
                        agent->CharaView.DrawWeapon = false;
                        var previewChar = agent->CharaView.GetCharacter();
                        if (previewChar != null)
                            previewChar->Timeline.Flags3 &= unchecked((byte)~0x40);
                    }
                }

                // Weapon-drawn LoopInCombatIdle: after the sheathe finishes and an
                // idle pause, re-engage the weapon-drawn flow and replay the emote.
                // PlayOnceAndSheathe does not arm this timer, so it stays sheathed.
                if (pendingWeaponEmoteReplayAt.HasValue && currentCharacter != null &&
                    DateTime.Now >= pendingWeaponEmoteReplayAt.Value)
                {
                    pendingWeaponEmoteReplayAt = null;
                    if (Config.WeaponEmoteMode == WeaponEmoteBehavior.RandomSequence)
                    {
                        var next = PickRandomEmote();
                        if (next.HasValue)
                        {
                            Svc.Log.Info($"[Detect] random-sequence next emote: {next.Value}");
                            PlayEmote(next.Value);
                        }
                    }
                    else if (activeEmoteId != 0 && activeLoopTimeline != 0)
                    {
                        previewWeaponDrawn = true;
                        previewEmoteStarted = false;
                        emoteWaitingToLoop = false;
                        SetPreviewWeaponDrawnState(true);
                        PlayTimelinePair(activeIntroTimeline, activeLoopTimeline);
                        Svc.Log.Info("[Detect] weapon-drawn replay fired after sheathe");
                    }
                }

                if (debugPreviewActive)
                    UpdateDebugPreview();

                // Real-time char-select emote switching: pick up config edits
                // immediately instead of waiting for the user to re-select the
                // character. PlayEmote handles the exit-then-enter transition.
                // Disabled in RandomSequence mode - the random rotation drives
                // activeEmoteId there, and the configured emote is only the
                // initial one played when a character is first selected.
                if (!debugPreviewActive && currentCharacter != null && currentContentId != 0 &&
                    Config.WeaponEmoteMode != WeaponEmoteBehavior.RandomSequence)
                {
                    var resolved = ResolveEmoteForCurrentJob() ?? 0u;
                    if (resolved != activeEmoteId)
                        PlayEmote(resolved);
                }

                // Persistent emotes (/sit, /doze) don't return to idle on their own.
                // Force an exit after holding for a while, then either replay (Loop mode)
                // or stay idle (PlayOnce mode).
                if (persistentEmoteStartedAt.HasValue && currentCharacter != null &&
                    (DateTime.Now - persistentEmoteStartedAt.Value).TotalSeconds >= PersistentLoopIntervalSeconds)
                {
                    PlayExitAnimation();
                    persistentEmoteExitingSince = DateTime.Now;
                    persistentEmoteStartedAt = null;
                }

                if (persistentEmoteExitingSince.HasValue &&
                    (DateTime.Now - persistentEmoteExitingSince.Value).TotalSeconds >= PersistentExitWaitSeconds)
                {
                    var emoteToReplay = activeEmoteId;
                    persistentEmoteExitingSince = null;

                    if (Config.WeaponEmoteMode == WeaponEmoteBehavior.LoopInCombatIdle && emoteToReplay != 0)
                    {
                        // Replay. Clear activeEmoteId first so PlayEmote's own
                        // PlayExitAnimation call is a no-op (we already played it).
                        activeEmoteId = 0;
                        PlayEmote(emoteToReplay);
                    }
                    else
                    {
                        // PlayOnce: stay in normal idle.
                        activeEmoteId = 0;
                    }
                }

                // Auto-handle weapon-drawn emote end: once Slot[0] transitions away from
                // the emote timeline (e.g. battle02 -> battle/idle), either sheathe back
                // to normal idle or replay the emote after a short pause in combat idle,
                // depending on config. Applies to both debug preview and char-select.
                if (previewWeaponDrawn && activeLoopTimeline != 0 && currentCharacter != null)
                {
                    // Re-assert weapon-drawn state every frame. Char-select's animation
                    // pipeline doesn't persist Flags3 0x40 the way the in-game Character
                    // window does (CharaView.DrawWeapon keeps it set there).
                    currentCharacter->Timeline.Flags3 |= 0x40;

                    var slot0 = currentCharacter->Timeline.TimelineSequencer.GetSlotTimeline(0);
                    if (!previewEmoteStarted)
                    {
                        if (slot0 == activeLoopTimeline)
                        {
                            previewEmoteStarted = true;
                            emoteWaitingToLoop = false;
                            Svc.Log.Info($"[Detect] emote started slot0={slot0} Flags3=0x{currentCharacter->Timeline.Flags3:X2}");
                        }
                    }
                    else if (slot0 != activeLoopTimeline)
                    {
                        // Sheathe in both modes - the weapon should always settle
                        // back to sheathed between emote runs. PlayOnceAndSheathe
                        // stops here. LoopInCombatIdle schedules a replay after
                        // the sheathe + idle wait window.
                        Svc.Log.Info($"[Detect] sheathe slot0={slot0} Flags3=0x{currentCharacter->Timeline.Flags3:X2} mode={Config.WeaponEmoteMode}");
                        TriggerPreviewSheathe();
                        previewEmoteStarted = false;

                        if (Config.WeaponEmoteMode != WeaponEmoteBehavior.PlayOnceAndSheathe)
                        {
                            // Loop and Random both replay; PlayOnceAndSheathe stops here.
                            // pendingSheatheCompleteAt is +1500ms (sheathe duration);
                            // add the idle wait on top so the user sees a brief
                            // sheathed idle before the next loop redraws.
                            pendingWeaponEmoteReplayAt = pendingSheatheCompleteAt!.Value
                                .AddSeconds(LoopReplayDelaySeconds);
                        }
                    }
                }

                if (debugAutoLog && (DateTime.Now - lastAutoLogAt).TotalMilliseconds >= 500)
                {
                    lastAutoLogAt = DateTime.Now;
                    DumpPreviewTimelines();
                }

                // Track active minion while logged in
                if (Config.ShowMinion && Svc.Objects.LocalPlayer != null)
                {
                    var localChar = (Character*)Svc.Objects.LocalPlayer.Address;
                    var companionObj = localChar->CompanionObject;
                    var modelId = companionObj != null ? companionObj->Character.ModelContainer.ModelCharaId : 0;
                    if (modelId > 0 && modelId != lastSavedMinionModelId)
                    {
                        lastSavedMinionModelId = modelId;
                        var contentId = PlayerState.Instance()->ContentId;
                        if (contentId != 0)
                        {
                            Config.SavedMinions[contentId] = new SavedMinionData
                            {
                                ModelCharaId = modelId,
                            };
                            SaveConfig(Config);
                        }
                    }
                }

                // Keep minion scale enforced - the game may reset it
                if (minion != null && minionTargetScale > 0)
                {
                    minion->Character.GameObject.Scale = minionTargetScale;
                    minion->Character.GameObject.VfxScale = minionTargetScale;
                }

                if (currentCharacter == null)
                    return;

                var currentTimeline = currentCharacter->Timeline.TimelineSequencer.GetSlotTimeline(0);

                // Weapon-drawn emotes have their own detection/replay path above that
                // uses the same emoteWaitingToLoop state; don't let this block clobber
                // the timer.
                if (previewWeaponDrawn)
                    return;

                // Play-once mode: let the emote end, stay idle, no replay.
                if (Config.WeaponEmoteMode == WeaponEmoteBehavior.PlayOnceAndSheathe)
                    return;

                if (activeLoopTimeline == 0 && activeIntroTimeline == 0)
                    return;

                // Loop mode: replay when base slot returns to idle. For emotes that
                // actually loop their timeline (like /dance), currentTimeline stays
                // non-idle and this is a no-op - the game handles the loop itself.
                // For emotes that play once and return to idle (like /wave, or even
                // IsLoop-tagged ones like battle02 that still transition out), this
                // fires after the delay and replays.
                if (currentTimeline == 0 || currentTimeline == 3)
                {
                    if (!emoteWaitingToLoop)
                    {
                        emoteWaitingToLoop = true;
                        emoteIdleStartTime = DateTime.Now;
                        return;
                    }

                    if ((DateTime.Now - emoteIdleStartTime).TotalSeconds < LoopReplayDelaySeconds)
                        return;

                    emoteWaitingToLoop = false;
                    if (Config.WeaponEmoteMode == WeaponEmoteBehavior.RandomSequence)
                    {
                        var next = PickRandomEmote();
                        if (next.HasValue)
                        {
                            Svc.Log.Info($"[Detect] random-sequence next emote: {next.Value}");
                            PlayEmote(next.Value);
                        }
                    }
                    else
                    {
                        PlayTimelinePair(activeIntroTimeline, activeLoopTimeline);
                    }
                }
                else
                {
                    emoteWaitingToLoop = false;
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.OnFrameworkUpdate");
            }
        }

        #region Show Pets

        private void OnLogin()
        {
            UpdatePetMirageSettings();
        }

        private void OnLogout(int type, int code)
        {
            UpdatePetMirageSettings();
        }

        private void UpdatePetMirageSettings()
        {
            try
            {
                var playerState = PlayerState.Instance();
                if (playerState == null || !playerState->IsLoaded)
                    return;

                var contentId = playerState->ContentId;
                if (contentId == 0)
                    return;

                if (!Config.PetMirageSettings.TryGetValue(contentId, out var settings))
                    Config.PetMirageSettings[contentId] = settings = new PetMirageSetting();

                Svc.GameConfig.TryGet(UiConfigOption.PetMirageTypeCarbuncleSupport, out settings.CarbuncleType);
                Svc.GameConfig.TryGet(UiConfigOption.PetMirageTypeFairy, out settings.FairyType);
                SaveConfig(Config);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.UpdatePetMirageSettings");
            }
        }

        private void SpawnPet()
        {
            try
            {
                if (!Config.ShowPets)
                    return;

                // Arcanist=26, Summoner=27, Scholar=28
                if (currentClassJobId is not (26 or 27 or 28))
                {
                    DespawnPet();
                    return;
                }

                if (pet != null)
                    return;

                if (!Config.PetMirageSettings.TryGetValue(currentContentId, out var settings))
                    return;

                var bNpcId = currentClassJobId switch
                {
                    26 or 27 when settings.CarbuncleType is 0 => 13498u, // Carbuncle
                    26 or 27 when settings.CarbuncleType is 1 => 13501u, // Emerald Carbuncle (Blue)
                    26 or 27 when settings.CarbuncleType is 2 => 13500u, // Topaz Carbuncle (Yellow)
                    26 or 27 when settings.CarbuncleType is 3 => 13499u, // Ruby Carbuncle (Red)
                    26 or 27 when settings.CarbuncleType is 5 => 13502u, // Ifrit-Egi
                    26 or 27 when settings.CarbuncleType is 6 => 13503u, // Titan-Egi
                    26 or 27 when settings.CarbuncleType is 7 => 13504u, // Garuda-Egi
                    28 when settings.FairyType is 0 => 1008u,  // Eos
                    28 when settings.FairyType is 1 => 13501u, // Emerald Carbuncle (Blue)
                    28 when settings.FairyType is 2 => 13500u, // Topaz Carbuncle (Yellow)
                    28 when settings.FairyType is 3 => 13499u, // Ruby Carbuncle (Red)
                    28 when settings.FairyType is 4 => 13498u, // Carbuncle
                    28 when settings.FairyType is 8 => 1009u,  // Selene
                    _ => 0u
                };

                if (bNpcId == 0)
                    return;

                var clientObjectManager = ClientObjectManager.Instance();
                if (clientObjectManager == null)
                    return;

                petIndex = (ushort)clientObjectManager->CreateBattleCharacter();
                if (petIndex == 0xFFFF)
                    return;

                pet = (BattleChara*)clientObjectManager->GetObjectByIndex(petIndex);
                if (pet == null)
                {
                    petIndex = 0xFFFF;
                    return;
                }

                pet->Character.CharacterSetup.SetupBNpc(bNpcId);
                pet->Character.GameObject.SetPosition(Config.PetPosition.X, Config.PetPosition.Y, Config.PetPosition.Z);
                pet->Character.GameObject.EnableDraw();
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.SpawnPet");
            }
        }

        private void DespawnPet()
        {
            try
            {
                DeleteCharaByIndex(petIndex);
                petIndex = 0xFFFF;
                pet = null;
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.DespawnPet");
            }
        }

        private void SpawnMinion()
        {
            try
            {
                if (!Config.ShowMinion)
                    return;

                if (!Config.SavedMinions.TryGetValue(currentContentId, out var minionData) || minionData.ModelCharaId <= 0)
                {
                    DespawnMinion();
                    return;
                }

                if (minion != null)
                    return;

                // Find a BNpcBase that uses this ModelChara
                var bnpcSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.BNpcBase>();
                if (bnpcSheet == null)
                    return;

                uint bnpcBaseId = 0;
                foreach (var bnpc in bnpcSheet)
                {
                    if (bnpc.ModelChara.RowId == (uint)minionData.ModelCharaId)
                    {
                        bnpcBaseId = bnpc.RowId;
                        break;
                    }
                }

                if (bnpcBaseId == 0)
                    return;

                var clientObjectManager = ClientObjectManager.Instance();
                if (clientObjectManager == null)
                    return;

                minionIndex = (ushort)clientObjectManager->CreateBattleCharacter();
                if (minionIndex == 0xFFFF)
                    return;

                minion = (BattleChara*)clientObjectManager->GetObjectByIndex(minionIndex);
                if (minion == null)
                {
                    minionIndex = 0xFFFF;
                    return;
                }

                minion->Character.CharacterSetup.SetupBNpc(bnpcBaseId);

                var pos = Config.MinionPosition;
                minion->Character.GameObject.SetPosition(pos.X, pos.Y, pos.Z);
                minion->Character.GameObject.EnableDraw();

                // Look up companion sheet scale (stored as integer, e.g. 80 = 0.8)
                minionTargetScale = 0;
                var companionSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Companion>();
                if (companionSheet != null)
                {
                    foreach (var c in companionSheet)
                    {
                        if (c.Model.RowId == (uint)minionData.ModelCharaId)
                        {
                            minionTargetScale = c.Scale / 100f * 0.5f;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.SpawnMinion");
            }
        }

        private void DespawnMinion()
        {
            try
            {
                DeleteCharaByIndex(minionIndex);
                minionIndex = 0xFFFF;
                minion = null;
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.DespawnMinion");
            }
        }

        #endregion

        #region Skip Login Confirm

        private void OnCharacterListReceiveEvent(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (!Config.SkipLoginConfirm)
                    return;

                if (args is not AddonReceiveEventArgs receiveEventArgs)
                    return;
                if ((AtkEventType)receiveEventArgs.AtkEventType is not AtkEventType.MouseClick)
                    return;
                if (receiveEventArgs.EventParam is < 5 or > 12)
                    return;

                Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.OnCharacterListReceiveEvent");
            }
        }

        private void OnSelectYesnoSetup(AddonEvent type, AddonArgs args)
        {
            try
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                var agentModule = AgentModule.Instance();
                var atkModule = RaptureAtkModule.Instance();

                if (agentModule != null && atkModule != null &&
                    atkModule->AddonCallbackMapping.TryGetValue(addon->Id, out var entry, false) &&
                    entry.AgentInterface != null)
                {
                    var lobbyAgent = agentModule->GetAgentByInternalId(AgentId.Lobby);
                    if (entry.AgentInterface == lobbyAgent && entry.EventKind == 3)
                    {
                        var yesno = (AddonSelectYesno*)addon;
                        yesno->YesButton->SetEnabledState(false);

                        var atkValue = stackalloc AtkValue[1];
                        atkValue[0].SetInt(0);
                        addon->FireCallback(1, atkValue, true);
                    }
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.OnSelectYesnoSetup");
            }

            Svc.AddonLifecycle.UnregisterListener(OnSelectYesnoSetup);
        }

        #endregion

        #region Skip Logout Confirm

        private bool logoutConfirmed;

        private void AutoConfirmLogoutIfVisible()
        {
            try
            {
                var addonPtr = Svc.GameGui.GetAddonByName("SelectYesno");
                if (addonPtr == IntPtr.Zero)
                {
                    logoutConfirmed = false;
                    return;
                }

                var yesno = (AddonSelectYesno*)addonPtr.Address;
                if (yesno == null || !yesno->AtkUnitBase.IsVisible || logoutConfirmed)
                    return;

                if (yesno->PromptText == null)
                    return;
                var promptText = yesno->PromptText->NodeText.ToString();
                if (!promptText.Contains("Log out") && !promptText.Contains("title screen") && !promptText.Contains("exit the game"))
                    return;

                logoutConfirmed = true;
                yesno->YesButton->SetEnabledState(false);

                var atkValue = stackalloc AtkValue[1];
                atkValue[0].SetInt(0);
                yesno->AtkUnitBase.FireCallback(1, atkValue, true);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.AutoConfirmLogoutIfVisible");
            }
        }

        #endregion

        #region Skip Logo

        // Skips the Square Enix / FFXIV logo splash that plays before the title
        // screen. The "Logo" addon fires its own "advance to title" callback when
        // clicked/skipped - we fire it as soon as the addon is set up, then hide it.
        private void OnLogoPostSetup(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (!Config.SkipLogo)
                    return;

                var addon = (AtkUnitBase*)args.Addon.Address;
                if (addon == null)
                    return;

                var atkValue = stackalloc AtkValue[1];
                atkValue[0].SetInt(0);
                addon->FireCallback(1, atkValue, true);
                addon->Hide(false, false, 1);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.OnLogoPostSetup");
            }
        }

        #endregion

        #region Move Queue Dialog

        private void MoveQueueDialogIfVisible()
        {
            var addonPtr = Svc.GameGui.GetAddonByName("SelectOk");
            if (addonPtr == IntPtr.Zero) return;

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon == null || !addon->IsVisible) return;

            var root = addon->RootNode;
            if (root == null) return;

            var io = ImGui.GetIO();
            var targetX = (short)(io.DisplaySize.X - root->Width * addon->Scale - 20);
            if (addon->X != targetX)
                addon->SetPosition(targetX, addon->Y);

            // Hide the dark overlay so character rotation still works
            var filterPtr = Svc.GameGui.GetAddonByName("Filter");
            if (filterPtr != IntPtr.Zero)
            {
                var filter = (AtkUnitBase*)filterPtr.Address;
                if (filter != null && filter->IsVisible)
                    filter->Hide(false, false, 0);
            }
        }

        private void OnQueueDialogSetup(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (!Config.MoveQueueDialog)
                    return;

                var addon = (AtkUnitBase*)args.Addon.Address;
                if (addon == null)
                    return;

                var root = addon->RootNode;
                if (root == null) return;
                var io = ImGui.GetIO();
                var screenWidth = io.DisplaySize.X;
                addon->SetPosition((short)(screenWidth - root->Width * addon->Scale - 20), addon->Y);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CharaSelectEmote.OnQueueDialogSetup");
            }
        }

        #endregion

        private List<(uint RowId, string Name, uint Icon, string Category)> GetEmoteList()
        {
            if (emoteList != null)
                return emoteList;

            emoteList = new();
            var emoteSheet = Svc.Data.GetExcelSheet<Emote>();
            if (emoteSheet == null)
                return emoteList;

            foreach (var emote in emoteSheet)
            {
                var name = emote.Name.ToString();
                if (emote.RowId == 0 || emote.Icon == 0 || string.IsNullOrEmpty(name))
                    continue;

                var hasIntro = emote.ActionTimeline[1].RowId != 0;
                var hasLoop = emote.ActionTimeline[0].RowId != 0;

                if (!hasIntro && !hasLoop)
                    continue;

                var category = emote.EmoteCategory.ValueNullable?.Name.ToString() ?? "";
                if (category.Equals("Expressions", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Persistent emotes (/sit, /doze, etc.) get their own category since
                // they behave differently (hold state, get looped via the exit-replay
                // timer rather than the timeline-idle check).
                if (emote.EmoteMode.RowId != 0)
                    category = "Persistent";
                else if (string.IsNullOrEmpty(category))
                    category = "Other";

                emoteList.Add((emote.RowId, name, (uint)emote.Icon, category));
            }

            emoteList = emoteList.OrderBy(e => e.Category).ThenBy(e => e.Name).ToList();
            return emoteList;
        }

        private string GetSelectedName(uint selectedEmoteId)
        {
            if (selectedEmoteId == 0)
                return "None";
            return GetEmoteList().FirstOrDefault(e => e.RowId == selectedEmoteId).Name ?? $"#{selectedEmoteId}";
        }

        private bool DrawEmoteCombo(string label, uint selectedEmoteId, Action<uint?> onSelected)
        {
            var changed = false;
            if (ImGui.BeginCombo(label, GetSelectedName(selectedEmoteId)))
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint($"##EmoteFilter{label}", "Search...", ref emoteSearchFilter, 256);

                var hasFilter = !string.IsNullOrEmpty(emoteSearchFilter);

                if (ImGui.Selectable("None", selectedEmoteId == 0))
                {
                    onSelected(null);
                    changed = true;
                }

                var lastCategory = "";
                var list = GetEmoteList();

                foreach (var (rowId, name, icon, category) in list)
                {
                    if (hasFilter && !name.Contains(emoteSearchFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!hasFilter && category != lastCategory)
                    {
                        lastCategory = category;
                        ImGui.Separator();
                        ImGui.TextDisabled(category);
                    }

                    if (ImGui.Selectable(name, rowId == selectedEmoteId))
                    {
                        onSelected(rowId);
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }
            return changed;
        }

        private List<(byte Id, string Name)> GetClassJobList()
        {
            if (classJobList != null)
                return classJobList;

            classJobList = new();
            var sheet = Svc.Data.GetExcelSheet<ClassJob>();
            if (sheet == null)
                return classJobList;

            foreach (var cj in sheet)
            {
                var name = cj.Name.ToString();
                if (cj.RowId == 0 || string.IsNullOrEmpty(name))
                    continue;
                classJobList.Add(((byte)cj.RowId, char.ToUpper(name[0]) + name[1..]));
            }

            classJobList = classJobList.OrderBy(c => c.Name).ToList();
            return classJobList;
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {

            if (ImGui.Checkbox("Show Minion", ref Config.ShowMinion))
            {
                SaveConfig(Config);
                hasChanged = true;
            }

            if (ImGui.Checkbox("Show Pets (Carbuncle/Fairy)", ref Config.ShowPets))
            {
                SaveConfig(Config);
                hasChanged = true;
            }

            if (ImGui.Checkbox("Move Queue Dialog to Right (removes dark overlay)", ref Config.MoveQueueDialog))
            {
                SaveConfig(Config);
                hasChanged = true;
            }

            if (ImGui.Checkbox("Skip Login Confirm", ref Config.SkipLoginConfirm))
            {
                SaveConfig(Config);
                hasChanged = true;
            }

            if (ImGui.Checkbox("Skip Logout Confirm", ref Config.SkipLogoutConfirm))
            {
                SaveConfig(Config);
                hasChanged = true;
            }

            if (ImGui.Checkbox("Skip Logo (skip the startup logo splash)", ref Config.SkipLogo))
            {
                SaveConfig(Config);
                hasChanged = true;
            }

            //if (ImGui.Checkbox("Open Pandora's Box settings on startup (title screen)", ref Config.OpenSettingsOnStartup))
            //{
            //    SaveConfig(Config);
            //    hasChanged = true;
            //}

            if (ImGui.Checkbox("Show currently playing emote name", ref Config.ShowCurrentEmoteName))
            {
                SaveConfig(Config);
                hasChanged = true;
            }

            ImGui.Separator();
            ImGui.Text("Emote behavior:");
            if (ImGui.RadioButton("Loop (replay every few seconds)", Config.WeaponEmoteMode == WeaponEmoteBehavior.LoopInCombatIdle))
            {
                Config.WeaponEmoteMode = WeaponEmoteBehavior.LoopInCombatIdle;
                SaveConfig(Config);
                hasChanged = true;
            }
            if (ImGui.RadioButton("Play once, then return to idle (sheathe weapons if drawn)", Config.WeaponEmoteMode == WeaponEmoteBehavior.PlayOnceAndSheathe))
            {
                Config.WeaponEmoteMode = WeaponEmoteBehavior.PlayOnceAndSheathe;
                SaveConfig(Config);
                hasChanged = true;
            }
            if (ImGui.RadioButton("Play random emotes in sequence", Config.WeaponEmoteMode == WeaponEmoteBehavior.RandomSequence))
            {
                Config.WeaponEmoteMode = WeaponEmoteBehavior.RandomSequence;
                SaveConfig(Config);
                hasChanged = true;
            }

            // The configured default emote and per-job overrides are only used
            // outside RandomSequence mode. In random mode the emote is picked
            // from the full emote list at every loop end (and on first selection),
            // so showing the dropdowns would be misleading.
            if (Config.WeaponEmoteMode != WeaponEmoteBehavior.RandomSequence)
            {
                ImGui.Separator();

                // Default emote
                ImGui.Text("Default Emote:");
                if (DrawEmoteCombo("##DefaultEmote", Config.DefaultEmote, (emoteId) =>
                {
                    Config.DefaultEmote = emoteId ?? 0;
                    SaveConfig(Config);
                }))
                    hasChanged = true;

                // Per-job overrides
                ImGui.Spacing();
                ImGui.Text("Per-Job Overrides:");

                var overrides = Config.JobEmoteOverrides;
                var classJobs = GetClassJobList();
                var toRemove = new List<byte>();

                foreach (var (jobId, jobEmoteId) in overrides)
                {
                    var jobName = classJobs.FirstOrDefault(c => c.Id == jobId).Name ?? $"Job #{jobId}";
                    ImGui.PushID($"JobOverride_{jobId}");

                    if (ImGui.Button("X"))
                    {
                        toRemove.Add(jobId);
                        hasChanged = true;
                    }
                    ImGui.SameLine();
                    ImGui.Text($"{jobName}:");
                    ImGui.SameLine();

                    if (DrawEmoteCombo($"##JobEmote_{jobId}", jobEmoteId, (emoteId) =>
                    {
                        if (emoteId == null)
                            toRemove.Add(jobId);
                        else
                            overrides[jobId] = emoteId.Value;
                        SaveConfig(Config);
                    }))
                        hasChanged = true;

                    ImGui.PopID();
                }

                foreach (var jobId in toRemove)
                    overrides.Remove(jobId);

                if (toRemove.Count > 0)
                    SaveConfig(Config);

                // [+] button to add new job override
                if (ImGui.Button("+"))
                    ImGui.OpenPopup("AddJobOverride");

                if (ImGui.BeginPopup("AddJobOverride"))
                {
                    ImGui.SetNextItemWidth(200);
                    ImGui.InputTextWithHint("##JobSearch", "Search job...", ref jobSearchFilter, 256);

                    var hasJobFilter = !string.IsNullOrEmpty(jobSearchFilter);

                    foreach (var (id, name) in classJobs)
                    {
                        if (overrides.ContainsKey(id))
                            continue;
                        if (hasJobFilter && !name.Contains(jobSearchFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (ImGui.Selectable(name))
                        {
                            overrides[id] = 0;
                            SaveConfig(Config);
                            hasChanged = true;
                            jobSearchFilter = "";
                        }
                    }

                    ImGui.EndPopup();
                }
            }

            // Debug preview UI hidden - the helpers (StartDebugPreview, DumpEmoteData,
            // DumpPlayerTimelines, DumpPreviewTimelines, debugAutoLog) are kept in
            // place so they're easy to wire back in if we need to investigate the
            // animation pipeline again.
            //
            //ImGui.Separator();
            //ImGui.Text("Debug Preview (uses Character window model in-game):");
            //
            //DrawEmoteCombo("##DebugPreviewEmote", debugPreviewEmoteId, (id) =>
            //{
            //    debugPreviewEmoteId = id ?? 0;
            //});
            //
            //if (ImGui.Button("Preview"))
            //    StartDebugPreview(debugPreviewEmoteId);
            //ImGui.SameLine();
            //if (ImGui.Button("Stop Preview"))
            //    StopDebugPreview();
            //
            //ImGui.Spacing();
            //if (ImGui.Button("Dump Emote Data"))
            //    DumpEmoteData(debugPreviewEmoteId);
            //ImGui.SameLine();
            //if (ImGui.Button("Snapshot Player"))
            //    DumpPlayerTimelines();
            //ImGui.SameLine();
            //if (ImGui.Button("Snapshot Preview"))
            //    DumpPreviewTimelines();
            //ImGui.Checkbox("Auto-log preview every 500ms while active", ref debugAutoLog);
        };

        public override void Disable()
        {
            if (debugPreviewActive)
                StopDebugPreview();

            Svc.Framework.Update -= OnFrameworkUpdate;
            Svc.PluginInterface.UiBuilder.Draw -= DrawEmoteNameOverlay;
            Svc.ClientState.Login -= OnLogin;
            Svc.ClientState.Logout -= OnLogout;
            Svc.AddonLifecycle.UnregisterListener(OnCharacterListReceiveEvent);
            Svc.AddonLifecycle.UnregisterListener(OnSelectYesnoSetup);
            Svc.AddonLifecycle.UnregisterListener(OnQueueDialogSetup);
            Svc.AddonLifecycle.UnregisterListener(OnLogoPostSetup);
            SaveConfig(Config);

            updateCharaSelectDisplayHook?.Disable();
            cleanupCharactersHook?.Disable();

            CleanupCharaSelect();
            base.Disable();
        }

        public override void Dispose()
        {
            updateCharaSelectDisplayHook?.Dispose();
            cleanupCharactersHook?.Dispose();
            base.Dispose();
        }
    }
}
