# Occult Chest Labels

Draws floating labels over treasure coffers in range while in the Occult Crescent, similar to NecroLens in deep dungeons. Chests are detected live from the object table (any `Treasure` object in the zone), so this works in any Horn without needing a chest map - the main way to find coffers in North Horn until its chest locations are mapped.

Chest type is identified via the Treasure sheet's SGB model link (bronze/silver/gold), with matching label colors. Options: per-type toggles, distance display, max label range, and chest dot size. Opened or despawned chests are skipped automatically.
