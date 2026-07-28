using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace YABOT.Helpers;

public enum OccultChestKind
{
    Bronze,
    Silver,
    Gold,
    Unknown,
}

public static class OccultChestHelper
{
    // ExportedSG rows for the shared world coffer models (Treasure sheet SGB column).
    private const uint BronzeSgbId = 1596;
    private const uint SilverSgbId = 1597;
    private const uint GoldSgbId = 1598;

    public static readonly Vector3 BronzeRgb = new(0.722f, 0.451f, 0.200f);
    public static readonly Vector3 SilverRgb = new(0.831f, 0.835f, 0.847f);
    public static readonly Vector3 GoldRgb = new(0.855f, 0.647f, 0.125f);
    public static readonly Vector3 UnknownRgb = new(1f, 1f, 1f);

    // A treasure object's BaseId is its Treasure sheet row; the SGB link identifies the coffer model.
    // Occult chest rows per EurekaTrackerAutoPopper: South Horn 1789-1856, North Horn 2006-2073 -
    // classifying by SGB instead of BaseId range keeps this working for future zones.
    public static OccultChestKind Classify(uint treasureBaseId)
    {
        try
        {
            if (!Svc.Data.GetExcelSheet<Treasure>().TryGetRow(treasureBaseId, out var row))
                return OccultChestKind.Unknown;

            return row.SGB.RowId switch
            {
                BronzeSgbId => OccultChestKind.Bronze,
                SilverSgbId => OccultChestKind.Silver,
                GoldSgbId => OccultChestKind.Gold,
                _ => OccultChestKind.Unknown,
            };
        }
        catch
        {
            return OccultChestKind.Unknown;
        }
    }

    public static Vector3 GetColor(OccultChestKind kind) => kind switch
    {
        OccultChestKind.Bronze => BronzeRgb,
        OccultChestKind.Silver => SilverRgb,
        OccultChestKind.Gold => GoldRgb,
        _ => UnknownRgb,
    };

    public static string GetLabel(OccultChestKind kind) => kind switch
    {
        OccultChestKind.Bronze => "Bronze Coffer",
        OccultChestKind.Silver => "Silver Coffer",
        OccultChestKind.Gold => "Gold Coffer",
        _ => "Coffer",
    };
}
