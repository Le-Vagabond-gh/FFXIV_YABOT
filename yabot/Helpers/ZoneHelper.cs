using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace YABOT.Helpers;

public static class ZoneHelper
{
    private const uint OccultCrescentIntendedUse = 61;
    private const uint DeepDungeonIntendedUse = 31;

    // Open-field maps of the Occult Crescent territories (South Horn 967, North Basin 1135,
    // Subterrane 1244). The Forked Tower duties share the field territories but use their own
    // maps, so "OC territory on a non-field map" means inside a Forked Tower instance.
    private static readonly HashSet<uint> OccultFieldMapIds = new() { 967, 1135, 1244 };

    public static bool IsInsideForkedTower() =>
        IsOccultCrescent() && !OccultFieldMapIds.Contains(Svc.ClientState.MapId);

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
