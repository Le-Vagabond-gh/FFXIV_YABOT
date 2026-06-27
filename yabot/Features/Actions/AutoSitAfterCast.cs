using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using YABOT.FeaturesSetup;
using System;

namespace YABOT.Features.Actions
{
    public sealed unsafe class AutoSitAfterCast : BaseFeature
    {
        // FSH "Cast" - "Casts your line and begins fishing." (verified via xivapi Action sheet)
        private const uint CastActionId = 289;

        public override string Name => "Auto-Sit After Casting";

        public override string Description =>
            "When you start fishing, waits a configurable delay and then runs /sit so you fish from the stool. " +
            "Sits only once per fishing session, so recasts won't toggle you back up.";

        public override FeatureType FeatureType => FeatureType.Actions;

        public class Configs : FeatureConfig
        {
            public float DelaySeconds = 3.0f;
            public int TriggerChancePercent = 100;
        }

        public Configs Config { get; private set; } = null!;

        private readonly Random rng = new();

        // ConditionFlag.Fishing drops on every catch/recast, so it can't mark a session. The character
        // Mode stays "Gathering" the whole time the rod is out and only returns to "Normal" when you
        // actually stop fishing - so we sit once and re-arm only on the transition back to Normal.
        private bool hasSatThisSession;
        private CharacterModes lastMode = CharacterModes.Normal;

        private delegate bool UseActionDelegate(ActionManager* self, ActionType actionType, uint actionID, ulong targetID, uint a4, uint a5, uint a6, void* a7);
        private Hook<UseActionDelegate>? useActionHook;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();

            useActionHook ??= Svc.Hook.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction,
                UseActionDetour);
            useActionHook.Enable();

            Svc.Framework.Update += OnFrameworkUpdate;

            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= OnFrameworkUpdate;
            useActionHook?.Disable();
            useActionHook?.Dispose();
            useActionHook = null;
            base.Disable();
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                var chara = (Character*)(Svc.Objects.LocalPlayer?.Address ?? 0);
                if (chara == null) return;
                var mode = chara->Mode;

                if (mode != lastMode)
                {
                    // Returned to standing (rod put away) - end of the fishing session, re-arm.
                    if (mode == CharacterModes.Normal)
                    {
                        hasSatThisSession = false;
                        TaskManager.Abort();
                    }
                    lastMode = mode;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[YABOT][AutoSitAfterCast] Error in framework update");
            }
        }

        private bool UseActionDetour(ActionManager* self, ActionType actionType, uint actionID, ulong targetID, uint a4, uint a5, uint a6, void* a7)
        {
            var result = useActionHook!.Original(self, actionType, actionID, targetID, a4, a5, a6, a7);

            try
            {
                // Only when the cast actually fired - skip rejected/queued attempts and once-per-session repeats.
                if (result && !hasSatThisSession && actionType == ActionType.Action && actionID == CastActionId)
                {
                    // Roll for the configured chance - just for fun. A losing cast tries again next cast.
                    if (Config.TriggerChancePercent < 100 && rng.Next(100) >= Config.TriggerChancePercent)
                        return result;

                    TaskManager.Abort();
                    TaskManager.EnqueueDelay((int)(Config.DelaySeconds * 1000));
                    TaskManager.Enqueue(() =>
                    {
                        // Cast may have been cancelled in the meantime; don't sit if we're no longer fishing.
                        if (!Svc.Condition[ConditionFlag.Fishing]) return;
                        Chat.SendMessage("/sit");
                        hasSatThisSession = true;
                    });
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[YABOT][AutoSitAfterCast] Error scheduling /sit");
            }

            return result;
        }

        public override bool UseAutoConfig => false;

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            ImGui.PushItemWidth(300);
            if (ImGui.SliderFloat("Delay before /sit (seconds)", ref Config.DelaySeconds, 0.5f, 10f, "%.1f"))
                hasChanged = true;
            if (ImGui.SliderInt("Chance to sit (%% of casts)", ref Config.TriggerChancePercent, 0, 100))
                hasChanged = true;
        };
    }
}
