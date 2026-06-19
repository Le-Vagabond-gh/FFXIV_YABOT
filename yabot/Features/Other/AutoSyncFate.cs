using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using System;
using System.Linq;
using System.Numerics;

namespace YABOT.Features.Other
{
    public unsafe class AutoSyncFate : BaseFeature
    {
        private ushort fateID;
        private bool pendingSync;

        public override string Name => "Auto-Sync FATEs";

        public override string Description => "Syncs when entering a FATE if you're overlevelled.";

        public override FeatureType FeatureType => FeatureType.Other;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption($@"Exclude ""A Realm Reborn"" zones", "" , 1)]
            public bool ExcludeARR = false;

            [FeatureConfigOption($@"Exclude ""Heavensward"" zones", "", 2)]
            public bool ExcludeHW = false;

            [FeatureConfigOption($@"Exclude ""Stormblood"" zones", "", 3)]
            public bool ExcludeSB = false;

            [FeatureConfigOption($@"Exclude ""Shadowbringers"" zones", "", 4)]
            public bool ExcludeShB = false;

            [FeatureConfigOption($@"Exclude ""Endwalker"" zones", "", 5)]
            public bool ExcludeEW = false;

            [FeatureConfigOption("Don't trigger when in combat", "", 6)]
            public bool ExcludeCombat = false;

            [FeatureConfigOption("Don't trigger when mounted", "", 7)]
            public bool ExcludeMounted = true;

            [FeatureConfigOption("Auto-confirm FATE NPC start dialogs", "", 8)]
            public bool AutoConfirmFateDialog = false;

            [FeatureConfigOption("Skip NPC talk while in a FATE area", "Auto-advances any Talk dialog box.", 9)]
            public bool SkipFateTalk = false;
        }

        public Configs Config { get; private set; } = null!;

        public override bool UseAutoConfig => true;

        public ushort FateID
        {
            get => fateID; set
            {
                if (fateID != value)
                {
                    SyncFate(value);
                }
                fateID = value;
            }
        }

        private bool IsInDialogue()
        {
            return Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
                || Svc.Condition[ConditionFlag.OccupiedInEvent]
                || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent];
        }

        public byte FateMaxLevel;
        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.Framework.Update += CheckFates;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", OnTalkUpdate);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", OnTalkUpdate);
            base.Enable();
        }

        public void SyncFate(ushort value)
        {
            if (value != 0)
            {
                var zone = Svc.Data.GetExcelSheet<TerritoryType>().Where(x => x.RowId == Svc.ClientState.TerritoryType).First();
                if (zone.ExVersion.RowId == 0 && Config.ExcludeARR) return;
                if (zone.ExVersion.RowId == 1 && Config.ExcludeHW) return;
                if (zone.ExVersion.RowId == 2 && Config.ExcludeSB) return;
                if (zone.ExVersion.RowId == 3 && Config.ExcludeShB) return;
                if (zone.ExVersion.RowId == 4 && Config.ExcludeEW) return;
                if (Svc.Condition[ConditionFlag.InCombat] && Config.ExcludeCombat) return;
                if ((Svc.Condition[ConditionFlag.Mounted] || Svc.Condition[ConditionFlag.RidingPillion]) && Config.ExcludeMounted) return;
                if (Svc.Objects.LocalPlayer?.ClassJob.Value.ClassJobCategory is { RowId: 32 or 33 }) return;

                if (Svc.Objects.LocalPlayer?.Level > FateMaxLevel)
                {
                    if (IsInDialogue())
                        pendingSync = true;
                    else
                        Chat.SendMessage("/lsync");
                }
            }
        }
        private void CheckFates(IFramework framework)
        {
            try
            {
                if (FateManager.Instance()->CurrentFate != null)
                {
                    FateMaxLevel = FateManager.Instance()->CurrentFate->MaxLevel;
                    FateID = FateManager.Instance()->CurrentFate->FateId;

                    if (pendingSync && !IsInDialogue())
                    {
                        pendingSync = false;
                        if (Svc.Objects.LocalPlayer?.Level > FateMaxLevel)
                            Chat.SendMessage("/lsync");
                    }
                }
                else
                {
                    FateID = 0;
                    pendingSync = false;
                }
            }
            catch { }
        }

        private void OnSelectYesnoSetup(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (!Config.AutoConfirmFateDialog) return;

                var addon = (AtkUnitBase*)args.Addon.Address;
                var text = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(addon->AtkValues[0].String)).ToString();
                if (!text.Contains("recommended level for this FATE", StringComparison.OrdinalIgnoreCase)) return;

                TaskManager.EnqueueDelay(300);
                TaskManager.Enqueue(() => Callback.Fire(addon, true, 0));
            }
            catch { }
        }

        // CurrentFate (and thus IsInFateRadius/fateID) is null until you're formally joined,
        // which is after the start NPC talk. Walk the zone's fate list and check proximity instead.
        private bool IsInFateArea(Vector3 pos)
        {
            var mgr = FateManager.Instance();
            if (mgr == null) return false;
            foreach (var fate in mgr->Fates)
            {
                if (fate.Value == null) continue;
                var loc = fate.Value->Location;
                var dx = pos.X - loc.X;
                var dz = pos.Z - loc.Z;
                if (dx * dx + dz * dz <= fate.Value->Radius * fate.Value->Radius)
                    return true;
            }
            return false;
        }

        private void OnTalkUpdate(AddonEvent type, AddonArgs args)
        {
            try
            {
                if (!Config.SkipFateTalk) return;

                var player = Svc.Objects.LocalPlayer;
                if (player == null) return;

                if (!IsInFateArea(player.Position)) return;

                var addon = (AtkUnitBase*)args.Addon.Address;
                if (!addon->IsVisible) return;

                new AddonMaster.Talk(args.Addon.Address).Click();
            }
            catch (Exception e) { Svc.Log.Error(e, "FateTalk"); }
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= CheckFates;
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", OnTalkUpdate);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", OnTalkUpdate);
            base.Disable();
        }
    }
}
