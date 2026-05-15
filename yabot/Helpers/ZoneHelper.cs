using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace YABOT.Helpers;

public static class ZoneHelper
{
    private const uint OccultCrescentIntendedUse = 61;

    public static bool IsOccultCrescent() => IsOccultCrescent(Svc.ClientState.TerritoryType);

    public static bool IsOccultCrescent(uint territoryId)
    {
        try
        {
            return Svc.Data.GetExcelSheet<TerritoryType>().GetRow(territoryId).TerritoryIntendedUse.RowId == OccultCrescentIntendedUse;
        }
        catch
        {
            return false;
        }
    }
}
