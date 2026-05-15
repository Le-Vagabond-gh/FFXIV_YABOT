using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using YABOT.FeaturesSetup;
using System;

namespace YABOT.Features.Actions
{
    public sealed unsafe class AutoDismount : BaseFeature
    {
        public override string Name => "Auto-Dismount on Blocked Action";

        public override string Description =>
            "When you use an action that's blocked while mounted (status 579), auto-dismount instead of letting the action fail. " +
            "Skips FATE vehicles / cosmic mechs and steps aside while vnavmesh is actively pathing.";

        public override FeatureType FeatureType => FeatureType.Actions;

        private delegate bool UseActionDelegate(ActionManager* self, ActionType actionType, uint actionID, ulong targetID, uint a4, uint a5, uint a6, void* a7);
        private Hook<UseActionDelegate>? useActionHook;

        private ICallGateSubscriber<bool>? vnavIsRunning;
        private ICallGateSubscriber<bool>? vnavPathfindInProgress;

        public override void Enable()
        {
            // vnavmesh IPC - subscribing is safe even if vnavmesh isn't installed
            vnavIsRunning = Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
            vnavPathfindInProgress = Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");

            useActionHook ??= Svc.Hook.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction,
                UseActionDetour);
            useActionHook.Enable();

            base.Enable();
        }

        public override void Disable()
        {
            useActionHook?.Disable();
            useActionHook?.Dispose();
            useActionHook = null;
            vnavIsRunning = null;
            vnavPathfindInProgress = null;
            base.Disable();
        }

        private bool UseActionDetour(ActionManager* self, ActionType actionType, uint actionID, ulong targetID, uint a4, uint a5, uint a6, void* a7)
        {
            try
            {
                var actionStatus = self->GetActionStatus(actionType, actionID);
                if (actionStatus == 579)
                {
                    var isMounted = Svc.Condition[ConditionFlag.Mounted];
                    var isRidingPillion = Svc.Condition[ConditionFlag.RidingPillion];

                    if (isMounted || isRidingPillion)
                    {
                        if (IsVnavmeshPathing())
                        {
                            Svc.Log.Info("[YABOT][AutoDismount] Skipping: vnavmesh is actively pathing");
                            return useActionHook!.Original(self, actionType, actionID, targetID, a4, a5, a6, a7);
                        }

                        if (!IsPlayerMount())
                        {
                            Svc.Log.Info("[YABOT][AutoDismount] Skipping: not a real player mount");
                            return useActionHook!.Original(self, actionType, actionID, targetID, a4, a5, a6, a7);
                        }

                        Svc.Log.Info($"[YABOT][AutoDismount] Auto-dismounting: action {actionID} blocked while mounted (status {actionStatus})");

                        try
                        {
                            // 0xE0000000 = InvalidGameObjectId (self/no-target) - matches the manual dismount keybind path
                            self->UseAction(ActionType.GeneralAction, 9, 0xE0000000, 0, 0, 0, null);
                        }
                        catch (Exception ex)
                        {
                            Svc.Log.Error(ex, "[YABOT][AutoDismount] Error during dismount action");
                        }

                        // Don't run the original blocked action - it will fail anyway
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[YABOT][AutoDismount] Error in UseAction hook - executing original action anyway");
            }

            // CRITICAL: always call Original with untouched arguments on the default path
            return useActionHook!.Original(self, actionType, actionID, targetID, a4, a5, a6, a7);
        }

        private bool IsVnavmeshPathing()
        {
            try
            {
                if (vnavIsRunning != null && vnavIsRunning.InvokeFunc())
                    return true;
                if (vnavPathfindInProgress != null && vnavPathfindInProgress.InvokeFunc())
                    return true;
            }
            catch
            {
                // vnavmesh not installed or IPC unavailable - that's fine
            }
            return false;
        }

        private bool IsPlayerMount()
        {
            // Cosmic mech has its own condition flag
            if (Svc.Condition[ConditionFlag.PilotingMech])
                return false;

            var localPlayer = (Character*)Svc.Objects.LocalPlayer?.Address;
            if (localPlayer == null)
                return false;

            var mountId = localPlayer->Mount.MountId;
            if (mountId == 0)
                return false;

            return PlayerState.Instance()->IsMountUnlocked(mountId);
        }
    }
}
