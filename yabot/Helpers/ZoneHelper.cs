using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace YABOT.Helpers;

public static class ZoneHelper
{
    private const uint OccultCrescentIntendedUse = 61;
    private const uint DeepDungeonIntendedUse = 31;

    public static bool IsOccultCrescent() => IsOccultCrescent(Svc.ClientState.TerritoryType);

    public static bool IsOccultCrescent(uint territoryId) => HasIntendedUse(territoryId, OccultCrescentIntendedUse);

    public static bool IsDeepDungeon() => IsDeepDungeon(Svc.ClientState.TerritoryType);

    public static bool IsDeepDungeon(uint territoryId) => HasIntendedUse(territoryId, DeepDungeonIntendedUse);

    private static bool HasIntendedUse(uint territoryId, uint intendedUse)
    {
        try
        {
            return Svc.Data.GetExcelSheet<TerritoryType>().GetRow(territoryId).TerritoryIntendedUse.RowId == intendedUse;
        }
        catch
        {
            return false;
        }
    }
}
