using Dalamud.Bindings.ImGui;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YABOT.Features.Other
{
    // RaptureGearsetModule.CurrentGearsetIndex is client-side state, not character state: it survives
    // logout on the machine that set it and is whatever that machine last equipped everywhere else.
    // Two ways it ends up pointing at the wrong job - changing job outside a gearset (armoury chest,
    // job stone, another plugin) leaves it on the previously equipped set, and logging in from a
    // different computer inherits that machine's stale index. Anything that reads the active gearset -
    // glamour links, other plugins, the gearset UI highlight - then works off the wrong set. This
    // re-points the active gearset at the job you actually logged in as; the job itself is never
    // changed, so no gear swap beyond re-applying that job's own set.
    public unsafe class LoginGearsetSync : BaseFeature
    {
        public override string Name => "Login Gearset Sync";

        public override string Description =>
            "After logging in, compares your current job against the job of the active gearset. If they don't match, " +
            "equips the lowest-numbered gearset for the job you're actually on. Your job is never changed. Runs once " +
            "per login, a few seconds after you regain control, so inn wake-up animations don't get interrupted. " +
            "Particularly useful if you play the same character from several computers - the active gearset is " +
            "remembered per machine, so it's usually stale after switching.";

        public override FeatureType FeatureType => FeatureType.Other;

        public override bool UseAutoConfig => false;

        public class Configs : FeatureConfig
        {
            public int DelaySeconds = 5;
            public bool AnnounceInChat = true;

            // Gearset ids that are never picked as the switch target. Lets you keep a job's canonical set
            // out of a low slot without it being chosen (e.g. an Occult/Bozja set at gearset 0).
            public List<int> ExcludedGearsetIds = new();
        }

        public Configs Config { get; private set; } = null!;

        private const int GearsetCount = 100;

        // Generous ceiling on the "wait for control" task: a slow zone load plus an inn wake-up
        // animation can easily outrun the task manager's 30s default.
        private const int ControlWaitTimeoutMs = 180_000;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.ClientState.Login += OnLogin;
            base.Enable();

            // Enabling the feature mid-session shouldn't wait for the next login to be useful.
            if (Svc.ClientState.IsLoggedIn)
                QueueCheck();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.ClientState.Login -= OnLogin;
            base.Disable();
        }

        private void OnLogin() => QueueCheck();

        private void QueueCheck()
        {
            TaskManager.Abort();
            TaskManager.Enqueue(ControlRestored, "Wait for character control", new() { TimeLimitMS = ControlWaitTimeoutMs, AbortOnTimeout = true });
            TaskManager.EnqueueDelay(Math.Max(0, Config.DelaySeconds) * 1000);
            // The delay is there to let inn wake-up / cutscene animations finish; re-check afterwards in
            // case something else grabbed control in the meantime.
            TaskManager.Enqueue(ControlRestored, "Re-check character control", new() { TimeLimitMS = ControlWaitTimeoutMs, AbortOnTimeout = true });
            TaskManager.Enqueue(SyncGearset, "Sync gearset to current job");
        }

        // True once the player is loaded, targetable, out of any cutscene/animation lock and the screen
        // has faded in - i.e. the point where the character actually answers to input.
        private static bool ControlRestored()
        {
            if (!Svc.ClientState.IsLoggedIn) return false;
            if (Svc.Objects.LocalPlayer == null) return false;

            var playerState = PlayerState.Instance();
            if (playerState == null || !playerState->IsLoaded) return false;
            if (RaptureGearsetModule.Instance() == null) return false;

            return Player.Interactable
                && !Player.IsAnimationLocked
                && !GenericHelpers.IsOccupied()
                && GenericHelpers.IsScreenReady();
        }

        private bool SyncGearset()
        {
            var player = Svc.Objects.LocalPlayer;
            if (player == null) return true;

            var module = RaptureGearsetModule.Instance();
            if (module == null) return true;

            var currentJob = (byte)player.ClassJob.RowId;
            var activeId = module->CurrentGearsetIndex;
            var active = activeId >= 0 ? module->GetGearset(activeId) : null;

            // A missing/empty active gearset counts as a mismatch - there's nothing sane for other
            // consumers to read off it either way.
            if (active != null && active->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) && active->ClassJob == currentJob)
                return true;

            var target = FindGearsetForJob(module, currentJob);
            if (target == null)
            {
                Log($"job {JobName(currentJob)} has no usable gearset, leaving gearset {activeId} active");
                return true;
            }

            var targetId = target->Id;
            var targetName = target->NameString;

            var result = module->EquipGearset(targetId);
            if (result != targetId)
            {
                Log($"EquipGearset({targetId}) returned {result}, retrying");
                return false;
            }

            if (Config.AnnounceInChat)
                Svc.Chat.Print($"[YABOT] Active gearset didn't match your job ({JobName(currentJob)}) - switched to #{targetId + 1} {targetName}.");

            return true;
        }

        // Lowest-numbered existing gearset for the job that isn't on the exclusion list.
        private RaptureGearsetModule.GearsetEntry* FindGearsetForJob(RaptureGearsetModule* module, byte classJob)
        {
            for (var i = 0; i < GearsetCount; i++)
            {
                var gearset = module->GetGearset(i);
                if (gearset == null) continue;
                if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                if (gearset->Id != i) continue;
                if (gearset->ClassJob != classJob) continue;
                if (Config.ExcludedGearsetIds.Contains(i)) continue;
                return gearset;
            }
            return null;
        }

        private static string JobName(byte classJob)
        {
            try
            {
                var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(classJob);
                var name = row.HasValue ? row.Value.Abbreviation.ExtractText() : null;
                return string.IsNullOrEmpty(name) ? $"job #{classJob}" : name;
            }
            catch
            {
                return $"job #{classJob}";
            }
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            var delay = Config.DelaySeconds;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Delay after regaining control (seconds)", ref delay, 0, 30))
            {
                Config.DelaySeconds = delay;
                hasChanged = true;
            }
            ImGui.TextDisabled("Inn wake-up animations can leave you 'in control' before the game settles.");

            var announce = Config.AnnounceInChat;
            if (ImGui.Checkbox("Announce the switch in chat", ref announce))
            {
                Config.AnnounceInChat = announce;
                hasChanged = true;
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Excluded gearsets");
            ImGui.TextDisabled("Ticked gearsets are never picked as the switch target.");

            DrawExclusionList(ref hasChanged);
        };

        private void DrawExclusionList(ref bool hasChanged)
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
            {
                // Character select / not loaded: the gearset list doesn't exist yet, so just show what's
                // already excluded rather than an empty box that looks like the settings were lost.
                if (Config.ExcludedGearsetIds.Count == 0)
                    ImGui.TextDisabled("Log in to pick gearsets to exclude.");
                else
                    ImGui.TextDisabled($"Excluded gearset numbers: {string.Join(", ", Config.ExcludedGearsetIds.OrderBy(i => i).Select(i => $"#{i + 1}"))}");
                return;
            }

            var changed = false;
            if (ImGui.BeginChild("##gearsetsync_exclusions", new(0, 200 * ImGui.GetIO().FontGlobalScale), true))
            {
                for (var i = 0; i < GearsetCount; i++)
                {
                    var gearset = module->GetGearset(i);
                    if (gearset == null) continue;
                    if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                    if (gearset->Id != i) continue;

                    var excluded = Config.ExcludedGearsetIds.Contains(i);
                    if (ImGui.Checkbox($"#{i + 1} {gearset->NameString} ({JobName(gearset->ClassJob)})##gearsetsync_ex_{i}", ref excluded))
                    {
                        if (excluded) Config.ExcludedGearsetIds.Add(i);
                        else Config.ExcludedGearsetIds.Remove(i);
                        changed = true;
                    }
                }
            }
            ImGui.EndChild();

            if (changed) hasChanged = true;
        }
    }
}
