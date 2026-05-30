namespace YABOT.Features.DeepDungeons
{
    // Deep dungeons expose no respawn countdown in their game struct, so we dead-reckon it the
    // same way NecroLens does: every 10-floor set has a fixed mob-respawn interval, and the clock
    // is anchored to when the player entered the current floor (then cycles by whole intervals).
    // Boss/transition floors (every 10th) and Eureka Orthos' floor 99 never respawn.
    internal static class DeepDungeonRespawn
    {
        // Indexed by floor-set = (floor - 1) / 10. Seconds per set, per consolegameswiki / NecroLens.
        private static readonly int[] Potd =
            { 40, 60, 60, 60, 120, 60, 60, 60, 60, 120, 90, 90, 90, 90, 90, 300, 300, 300, 300, 300 };

        // Heaven-on-High, Eureka Orthos and Pilgrim's Traverse all share the same cadence.
        private static readonly int[] HoHLike =
            { 60, 60, 60, 600, 600, 600, 600, 600, 600, 600 };

        /// <summary>
        /// Returns the mob respawn interval (seconds) for the given deep dungeon and floor, or
        /// false when the floor has no respawns (boss floors, Eureka Orthos floor 99, or out of range).
        /// </summary>
        /// <param name="deepDungeonId">DeepDungeon sheet row: 1 = PotD, 2 = HoH, 3 = EO, 4 = Pilgrim's Traverse.</param>
        /// <param name="floor">Absolute floor number (1-200).</param>
        public static bool TryGetInterval(int deepDungeonId, int floor, out int seconds)
        {
            seconds = 0;
            if (floor <= 0) return false;
            if (IsBossFloor(deepDungeonId, floor)) return false;

            var table = deepDungeonId == 1 ? Potd : HoHLike;
            var set = (floor - 1) / 10;
            if (set >= table.Length) return false;

            seconds = table[set];
            return true;
        }

        /// <summary>
        /// Boss/transition floors (every 10th) and Eureka Orthos' floor 99 have neither mob
        /// respawns nor a Beacon of Passage - you advance via the boss, not by clearing the floor.
        /// </summary>
        /// <param name="deepDungeonId">DeepDungeon sheet row: 1 = PotD, 2 = HoH, 3 = EO, 4 = Pilgrim's Traverse.</param>
        /// <param name="floor">Absolute floor number (1-200).</param>
        public static bool IsBossFloor(int deepDungeonId, int floor)
            => floor % 10 == 0 || (deepDungeonId == 3 && floor == 99);
    }
}
