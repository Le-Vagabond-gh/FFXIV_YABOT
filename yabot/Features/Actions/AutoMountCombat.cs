using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System;
using static YABOT.Helpers.ZoneHelper;
using System.Linq;
using PlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using static ECommons.GenericHelpers;

namespace YABOT.Features.Actions
{
    public unsafe class AutoMountCombat : BaseFeature
    {
        public override string Name => "Auto-Mount After Combat";

        public override string Description => "Mounts upon ending combat.";

        public override FeatureType FeatureType => FeatureType.Actions;

        public class Configs : FeatureConfig
        {
            public float ThrottleF = 0.1f;
            public bool UseMountRoulette = true;
            public uint SelectedMount = 0;
            public bool DisableInFates = true;
            public bool ExcludeHousing = false;
            public bool ExcludeOccultCrescent = false;
            public bool UseReturnInOccultCrescent = false;
            public bool AutoConfirmReturnInOccultCrescent = false;
            public bool JumpAfterMount = false;
        }

        public Configs Config { get; private set; } = null!;

        public override bool UseAutoConfig => false;

        private delegate bool TeleportDelegate(Telepo* telepo, uint aetheryteId, byte subIndex);
        private Hook<TeleportDelegate>? teleportHook;
        private delegate bool TeleportWithTicketsDelegate(Telepo.SelectUseTicketInvoker* invoker, uint aetheryteId, byte subIndex);
        private Hook<TeleportWithTicketsDelegate>? teleportWithTicketsHook;
        private long mountActionTime;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            teleportHook ??= Svc.Hook.HookFromAddress<TeleportDelegate>((nint)Telepo.Addresses.Teleport.Value, TeleportDetour);
            teleportHook?.Enable();
            teleportWithTicketsHook ??= Svc.Hook.HookFromAddress<TeleportWithTicketsDelegate>((nint)Telepo.SelectUseTicketInvoker.Addresses.TeleportWithTickets.Value, TeleportWithTicketsDetour);
            teleportWithTicketsHook?.Enable();
            Svc.Condition.ConditionChange += RunFeature;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
            base.Enable();
        }

        private void OnSelectYesnoSetup(AddonEvent type, AddonArgs args)
        {
            if (!Config.AutoConfirmReturnInOccultCrescent) return;
            if (!IsOccultCrescent()) return;
            if (Svc.Condition[ConditionFlag.Unconscious]) return;

            try
            {
                if (TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) && addon->AtkUnitBase.IsVisible)
                {
                    if (addon->PromptText == null) return;
                    var text = addon->PromptText->NodeText.ToString();
                    if (!text.Contains("Return") && !text.Contains("return"))
                        return;

                    new AddonMaster.SelectYesno((nint)addon).Yes();
                }
            }
            catch { }
        }

        private void RunFeature(ConditionFlag flag, bool value)
        {
            if (flag == ConditionFlag.InCombat && !value)
            {
                TaskManager.Abort();
                TaskManager.Enqueue(() => NotInCombat);
                TaskManager.EnqueueDelay((int)(Config.ThrottleF * 1000));
                TaskManager.Enqueue(() =>
                {
                    if (Config.ExcludeOccultCrescent && IsOccultCrescent())
                    {
                        if (Config.UseReturnInOccultCrescent)
                        {
                            TaskManager.EnqueueWithTimeout(TryReturn, 15000);
                            TaskManager.EnqueueWithTimeout(ConfirmReturn, 5000);
                        }
                        return;
                    }

                    TaskManager.EnqueueWithTimeout(TryMount, 3000);
                    TaskManager.Enqueue(() =>
                    {
                        if (Config.JumpAfterMount && ZoneHasFlight())
                        {
                            TaskManager.EnqueueWithTimeout(() => Svc.Condition[ConditionFlag.Mounted], 5000);
                            TaskManager.EnqueueDelay(50);
                            TaskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2));
                            TaskManager.EnqueueDelay(50);
                            TaskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2));
                        }
                    });
                });
            }
        }

        private bool? TryReturn()
        {
            if (Svc.Condition[ConditionFlag.InCombat]) return false;
            if (Svc.Condition[ConditionFlag.Casting]) return false;
            if (Config.DisableInFates && FateManager.Instance()->CurrentFate != null) return false;
            var am = ActionManager.Instance();
            if (am->GetActionStatus(ActionType.GeneralAction, 8) != 0) return false;
            am->UseAction(ActionType.GeneralAction, 8);
            return true;
        }

        private static bool NotInCombat => !Svc.Condition[ConditionFlag.InCombat];

        private bool? ConfirmReturn()
        {
            try
            {
                if (TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) && addon->AtkUnitBase.IsVisible)
                {
                    new AddonMaster.SelectYesno((nint)addon).Yes();
                    return true;
                }
            }
            catch { }
            return false;
        }
        private bool? TryMount()
        {
            if (Svc.Objects.LocalPlayer is null) return false;
            if (Svc.Condition[ConditionFlag.InCombat]) return false;
            if (Svc.Condition[ConditionFlag.Casting]) return false;
            if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return false;
            if (Config.DisableInFates && FateManager.Instance()->CurrentFate != null) return false;
            if (Svc.Condition[ConditionFlag.Mounted]) return true;
            if (!Svc.Data.GetExcelSheet<TerritoryType>().First(x => x.RowId == Svc.ClientState.TerritoryType).Mount) return false;

            var territory = Svc.Data.GetExcelSheet<TerritoryType>().First(x => x.RowId == Svc.ClientState.TerritoryType);

            if (territory.Bg.ToString().Contains("/hou/") && Config.ExcludeHousing)
            {
                TaskManager.Abort();
                return false;
            }

            if (Config.ExcludeOccultCrescent && IsOccultCrescent())
            {
                TaskManager.Abort();
                return false;
            }

            var am = ActionManager.Instance();

            if (!Config.UseMountRoulette && Config.SelectedMount > 0)
            {
                if (am->GetActionStatus(ActionType.Mount, Config.SelectedMount) != 0) return false;
                mountActionTime = Environment.TickCount64;
                am->UseAction(ActionType.Mount, Config.SelectedMount);

                return true;
            }
            else
            {
                if (am->GetActionStatus(ActionType.GeneralAction, 9) != 0) return false;
                mountActionTime = Environment.TickCount64;
                am->UseAction(ActionType.GeneralAction, 9);

                return true;
            }

        }

        private bool ShouldBlockTeleport()
        {
            if (mountActionTime == 0)
                return false;

            var mounted = Svc.Condition[ConditionFlag.Mounted];
            var elapsed = Environment.TickCount64 - mountActionTime;

            if (mounted || elapsed > 6000)
            {
                mountActionTime = 0;
                return false;
            }

            if (TaskManager.IsBusy)
                TaskManager.Abort();

            return true;
        }

        private unsafe bool TeleportDetour(Telepo* telepo, uint aetheryteId, byte subIndex)
        {
            try
            {
                if (ShouldBlockTeleport())
                    return false;
            }
            catch { }
            return teleportHook!.Original(telepo, aetheryteId, subIndex);
        }

        private unsafe bool TeleportWithTicketsDetour(Telepo.SelectUseTicketInvoker* invoker, uint aetheryteId, byte subIndex)
        {
            try
            {
                if (ShouldBlockTeleport())
                    return false;
            }
            catch { }
            return teleportWithTicketsHook!.Original(invoker, aetheryteId, subIndex);
        }

        public override void Disable()
        {
            SaveConfig(Config);
            teleportHook?.Disable();
            teleportWithTicketsHook?.Disable();
            Svc.Condition.ConditionChange -= RunFeature;
            Svc.AddonLifecycle.UnregisterListener(OnSelectYesnoSetup);
            base.Disable();
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool haschanged) =>
        {
            ImGui.PushItemWidth(300);
            if (ImGui.SliderFloat("Set Delay (seconds)", ref Config.ThrottleF, 0.1f, 10f, "%.1f")) haschanged = true;
            if (ImGui.Checkbox("Use Mount Roulette", ref Config.UseMountRoulette)) haschanged = true;
            if (!Config.UseMountRoulette)
            {
                var ps = PlayerState.Instance();
                var preview = Svc.Data.GetExcelSheet<Mount>().First(x => x.RowId == Config.SelectedMount).Singular.ExtractText().ToTitleCase();
                if (ImGui.BeginCombo("Select Mount", preview))
                {
                    if (ImGui.Selectable("", Config.SelectedMount == 0))
                    {
                        Config.SelectedMount = 0;
                        haschanged = true;
                    }

                    foreach (var mount in Svc.Data.GetExcelSheet<Mount>().OrderBy(x => x.Singular.ExtractText()))
                    {
                        if (ps->IsMountUnlocked(mount.RowId))
                        {
                            var selected = ImGui.Selectable(mount.Singular.ExtractText().ToTitleCase(), Config.SelectedMount == mount.RowId);

                            if (selected)
                            {
                                Config.SelectedMount = mount.RowId;
                                haschanged = true;
                            }
                        }
                    }

                    ImGui.EndCombo();
                }
            }

            if (ImGui.Checkbox("Disable in fates", ref Config.DisableInFates)) haschanged = true;
            if (ImGui.Checkbox("Exclude Housing Zones", ref Config.ExcludeHousing)) haschanged = true;
            if (ImGui.Checkbox("Exclude The Occult Crescent", ref Config.ExcludeOccultCrescent)) haschanged = true;
            if (Config.ExcludeOccultCrescent)
            {
                ImGui.Indent();
                if (ImGui.Checkbox("Use Return instead in The Occult Crescent", ref Config.UseReturnInOccultCrescent)) haschanged = true;
                if (ImGui.Checkbox("Auto-confirm Return dialog in The Occult Crescent", ref Config.AutoConfirmReturnInOccultCrescent)) haschanged = true;
                ImGui.Unindent();
            }
            if (ImGui.Checkbox("Jump after mounting", ref Config.JumpAfterMount)) haschanged = true;

        };
    }
}
