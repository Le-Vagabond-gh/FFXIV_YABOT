using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using YABOT.FeaturesSetup;
using System;
using System.Linq;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace YABOT.Features.CosmicExploration
{
    // When a Cosmic Exploration mission completes, re-opens the Stellar Missions list and re-accepts
    // the same mission if it's still available. Detection/restart mechanics mirror Ice's Cosmic
    // Exploration, scoped down for manual play: the user reports the mission themselves; this only
    // saves the manual reopen-and-select between runs, and stays quiet on a manual abandon.
    public unsafe class CosmicMissionAutoRestart : BaseFeature
    {
        public override string Name => "Cosmic Mission Auto-Restart";

        public override string Description =>
            "When you complete a Cosmic Exploration mission, automatically re-opens the Stellar Missions " +
            "list and re-accepts the same mission if it's still available. You still report/turn in the " +
            "mission yourself - this only saves the manual reopen-and-select between runs. Does nothing if " +
            "you abandon a mission instead of completing it.";

        public override FeatureType FeatureType => FeatureType.CosmicExploration;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Action delay (ms)", IntMin = 100, IntMax = 3000, EditorSize = 300)]
            public int ActionDelayMs = 500;
        }

        public Configs Config { get; private set; } = null!;

        private uint _activeMission;
        private WKSManager.MissionRank _bestRank;
        private uint _pendingRestart;
        private int _restartAttempts;

        // Give the Stellar Missions list time to populate before concluding the mission is unavailable.
        private const int MaxRestartAttempts = 5;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.Framework.Update += Tick;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= Tick;
            TaskManager.Abort();
            base.Disable();
        }

        private void Tick(IFramework framework)
        {
            try
            {
                var mgr = WKSManager.Instance();
                if (mgr == null)
                {
                    // Not on the moon / WKS not initialised: reset everything.
                    _activeMission = 0;
                    _bestRank = default;
                    _pendingRestart = 0;
                    return;
                }

                uint current = mgr->State.CurrentMissionUnitRowId;

                if (current != 0)
                {
                    // A mission is active: a new one being accepted cancels any pending restart.
                    _pendingRestart = 0;

                    // Track the best rank reached. A genuine completion reaches at least Bronze; an
                    // early abandon stays at None - this is the signal ICE uses to gauge completion.
                    var rank = mgr->State.CurrentRank;
                    if (rank >= WKSManager.MissionRank.Bronze && rank <= WKSManager.MissionRank.Gold && rank > _bestRank)
                        _bestRank = rank;

                    if (current != _activeMission)
                        Svc.Log.Debug($"[AutoRestart] active mission {_activeMission} -> {current}.");

                    if (EzThrottler.Throttle("YABOT_AutoRestart_State", 3000))
                        Svc.Log.Debug($"[AutoRestart] active={current} rank={rank} bestRank={_bestRank} score={mgr->State.CurrentScore}");

                    _activeMission = current;
                    return;
                }

                // current == 0: no active mission.
                if (_activeMission != 0)
                {
                    // Mission just ended - only queue a restart if it reached at least Bronze (completed),
                    // not on an early abandon (rank stayed None).
                    bool completed = _bestRank >= WKSManager.MissionRank.Bronze && _bestRank <= WKSManager.MissionRank.Gold;
                    Svc.Log.Debug($"[AutoRestart] mission {_activeMission} ended. bestRank={_bestRank} -> {(completed ? "queue restart" : "ignore (abandon)")}.");
                    if (completed)
                    {
                        _pendingRestart = _activeMission;
                        _restartAttempts = 0;
                    }

                    _activeMission = 0;
                    _bestRank = default;
                }

                if (_pendingRestart == 0)
                    return;

                if (!EzThrottler.Throttle("YABOT_CosmicMissionRestart", Config.ActionDelayMs))
                    return;

                bool missionOpen = GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var mission) && mission.IsAddonReady;
                bool hudOpen = GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var hud) && hud.IsAddonReady;

                if (missionOpen)
                {
                    // Stellar Missions list is open: re-accept the same mission if it's currently offered.
                    var available = mission.StellerMissions;
                    Svc.Log.Debug($"[AutoRestart] Stellar Missions list shows: {string.Join(", ", available.Select(m => m.MissionId))}");
                    var match = available.FirstOrDefault(m => m.MissionId == _pendingRestart);
                    if (match != null)
                    {
                        Svc.Log.Info($"[AutoRestart] Re-accepting mission {_pendingRestart}.");
                        match.Initiate();
                        _pendingRestart = 0;
                    }
                    else if (++_restartAttempts >= MaxRestartAttempts)
                    {
                        // Mission isn't in the currently offered set (the board rotates which missions
                        // it offers), so it can't be restarted right now. Stop rather than reopening forever.
                        Svc.Log.Info($"[AutoRestart] Mission {_pendingRestart} is not currently offered; not restarting.");
                        _pendingRestart = 0;
                    }
                }
                else if (hudOpen)
                {
                    // Open the Stellar Missions list so availability can be read.
                    Svc.Log.Debug($"[AutoRestart] Opening Stellar Missions list via WKSHud.");
                    hud.Mission();
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "CosmicMissionAutoRestart.Tick");
            }
        }
    }
}
