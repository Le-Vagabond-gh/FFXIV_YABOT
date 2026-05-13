using System;
using Dalamud.Hooking;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace YABOT.Features.Other
{
    public unsafe class BetterQuestMapLink : Feature
    {
        public override string Name => "Better Quest Map Link";

        public override string Description =>
            "Clicking a quest's map link no longer forces you to the quest's flag - it just opens the map normally so you can scroll around.";

        public override FeatureType FeatureType => FeatureType.Other;

        private Hook<AgentMap.Delegates.OpenMap>? openMapHook;

        public override void Enable()
        {
            openMapHook ??= Svc.Hook.HookFromAddress<AgentMap.Delegates.OpenMap>(
                AgentMap.MemberFunctionPointers.OpenMap,
                OnOpenMap);
            openMapHook.Enable();
            base.Enable();
        }

        public override void Disable()
        {
            openMapHook?.Disable();
            openMapHook?.Dispose();
            openMapHook = null;
            base.Disable();
        }

        private void OnOpenMap(AgentMap* agent, OpenMapInfo* data)
        {
            openMapHook!.Original(agent, data);

            try
            {
                if (data == null) return;
                if (!Svc.Data.GetExcelSheet<Map>().TryGetRow(data->MapId, out var mapData)) return;

                // Disable inside Cosmic Exploration zones - quest links there are routinely useful as-is.
                if (mapData.TerritoryType.ValueNullable?.TerritoryIntendedUse.RowId is 60) return;

                if (data->Type == MapType.QuestLog && agent->CurrentMapId != data->MapId)
                {
                    data->Type = MapType.Centered;
                    data->TerritoryId = 0;
                    openMapHook!.Original(agent, data);
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "BetterQuestMapLink");
            }
        }
    }
}
