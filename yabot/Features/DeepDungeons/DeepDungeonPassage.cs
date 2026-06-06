namespace YABOT.Features.DeepDungeons
{
    // The Cairn/Beacon of Passage opens after a random number of kills that is rolled per floor
    // within a fixed per-floorset range (data: ddcompendium.com). The game never exposes the rolled
    // value or a faithful running count - PassageProgress is just decorative fill that snaps to >=11
    // once the threshold is met - so the best a tracker can do is show kills-so-far against the range.
    // Ranges fold the documented "rarely" outliers into the min/max bounds.
    internal static class DeepDungeonPassage
    {
        // Indexed by floor-set = (floor - 1) / 10.
        private static readonly (byte Min, byte Max)[] Potd =
        {
            (3, 7), (3, 7), (3, 7), (3, 7), (3, 9), (3, 7), (3, 7), (3, 7), (3, 7), (3, 9),
            (4, 10), (4, 10), (4, 10), (4, 10), (4, 10), (4, 10), (4, 10), (4, 10), (4, 10), (5, 13),
        };

        private static readonly (byte Min, byte Max)[] HoH =
        {
            (3, 7), (3, 7), (3, 7), (3, 9), (3, 9), (4, 10), (4, 10), (4, 10), (4, 10), (5, 13),
        };

        private static readonly (byte Min, byte Max)[] Eo =
        {
            (3, 7), (3, 7), (3, 7),
        };

        /// <summary>
        /// Returns the kills-to-open-passage range for the given deep dungeon and floor, or false on
        /// boss floors (no passage) or when no data covers the floor (e.g. Pilgrim's Traverse, which
        /// uses a different votive mechanic).
        /// </summary>
        /// <param name="deepDungeonId">DeepDungeon sheet row: 1 = PotD, 2 = HoH, 3 = EO, 4 = Pilgrim's Traverse.</param>
        /// <param name="floor">Absolute floor number.</param>
        public static bool TryGetKillRange(int deepDungeonId, int floor, out int min, out int max)
        {
            min = 0;
            max = 0;
            if (floor <= 0) return false;
            if (DeepDungeonRespawn.IsBossFloor(deepDungeonId, floor)) return false;

            var table = deepDungeonId switch
            {
                1 => Potd,
                2 => HoH,
                3 => Eo,
                _ => null,
            };
            if (table == null) return false;

            var set = (floor - 1) / 10;
            if (set >= table.Length) return false;

            min = table[set].Min;
            max = table[set].Max;
            return true;
        }
    }
}
