using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System;
using System.Reflection;

namespace YABOT.Features.OccultCrescent
{
    public unsafe class AutoShowFateHelper : BaseFeature
    {
        public override string Name => "Auto-Show FATE Helper";

        public override string Description =>
            "When entering Occult Crescent, sends /fatehelper to display the FATE Helper window if it isn't already shown. Requires the FATE Helper plugin (by Teechep Bird) to be installed.";

        public override FeatureType FeatureType => FeatureType.OccultCrescent;

        public override bool UseAutoConfig => false;

        private const string FateHelperInternalName = "FATEhelper";

        public override void Enable()
        {
            Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
            base.Disable();
        }

        private void OnTerritoryChanged(uint territoryId)
        {
            if (!ZoneHelper.IsOccultCrescent(territoryId)) return;

            TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51]);
            TaskManager.Enqueue(() => Svc.Objects.LocalPlayer != null);
            TaskManager.Enqueue(() => PlayerState.Instance() != null && PlayerState.Instance()->IsLoaded);
            TaskManager.Enqueue(TryShowFateHelper);
        }

        private bool TryShowFateHelper()
        {
            try
            {
                if (!DalamudReflector.TryGetDalamudPlugin(FateHelperInternalName, out var plugin, suppressErrors: true))
                    return true;

                if (IsMainWindowOpen(plugin)) return true;

                Chat.SendMessage("/fatehelper");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[AutoShowFateHelper] Failed to query/show FATE Helper window");
            }
            return true;
        }

        private static bool IsMainWindowOpen(object plugin)
        {
            var prop = plugin.GetType().GetProperty("MainWindow",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop?.GetValue(plugin) is not Window window) return false;
            return window.IsOpen;
        }
    }
}
