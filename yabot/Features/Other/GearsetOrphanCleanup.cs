using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using YABOT.FeaturesSetup;

namespace YABOT.Features.Other
{
    // Watches every gearset's item list and reacts to changes, whoever made them - the game's own
    // "Update Gearset" action, Stylist (which writes gearset memory directly rather than calling the
    // native update function), IPC, macros, etc. When a gearset drops an item and that item is no
    // longer registered to ANY gearset (the state the inventory's "in a gear set" icon reflects), the
    // orphan is pulled out of the armoury chest into the first free inventory slot, scanning
    // Inventory1 -> Inventory4 slot 0 upward (top-left of bag 1) instead of the game's last-bag default.
    public unsafe class GearsetOrphanCleanup : BaseFeature
    {
        public override string Name => "Gearset Orphan Cleanup";

        public override string Description =>
            "When a gearset changes (via the game, Stylist, or IPC) any gear that was removed or replaced " +
            "and is no longer used by any gearset gets moved out of the armoury chest into your first free " +
            "inventory slot (top-left of bag 1, working down - not the game's default last bag/last slot).";

        public override FeatureType FeatureType => FeatureType.Other;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Announce moved items in chat")]
            public bool AnnounceInChat = true;
        }

        public Configs Config { get; private set; } = null!;

        // HQ items are stored in gearsets as itemId + 1,000,000; normalise to the base id for comparison.
        private const uint HqOffset = 1_000_000;

        private const int GearsetCount = 100;
        private const int ThrottleMs = 400;

        // How long a detected orphan stays queued for moving. A direct-memory editor like Stylist writes
        // the gearset entry immediately but shuffles the physical gear across several later frames, so the
        // orphaned piece may not land in the armoury for a moment - keep trying within this window.
        private const long PendingWindowMs = 15_000;

        // Quiet period with no detections/moves after which the accumulated batch is announced as one
        // message, so a multi-slot update reads as a single line instead of one line per item.
        private const long FlushQuietMs = 1_500;

        private static readonly InventoryType[] ArmouryContainers =
        {
            InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
            InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
            InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
            InventoryType.ArmorySoulCrystal,
        };

        // Regular inventory bags in the fill order we want (bag 1 first).
        private static readonly InventoryType[] InventoryBags =
        {
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
        };

        // Last-seen base item ids per gearset id. The cache is our "before" for the next diff.
        private readonly Dictionary<int, HashSet<uint>> gearsetCache = new();

        // Orphaned base item ids awaiting a move, mapped to the tick at which we give up on them.
        private readonly Dictionary<uint, long> pendingOrphans = new();

        // Batched chat output, flushed once the run settles.
        private readonly List<string> movedNames = new();
        private readonly List<string> failedNames = new();
        private long lastActivityMs;

        private bool initialized;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            ResetState();
            Svc.Framework.Update += OnUpdate;
            Svc.ClientState.Login += ResetState;
            Svc.ClientState.Logout += OnLogout;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= OnUpdate;
            Svc.ClientState.Login -= ResetState;
            Svc.ClientState.Logout -= OnLogout;
            ResetState();
            base.Disable();
        }

        private void OnLogout(int type, int code) => ResetState();

        private void ResetState()
        {
            gearsetCache.Clear();
            pendingOrphans.Clear();
            movedNames.Clear();
            failedNames.Clear();
            initialized = false;
        }

        private void OnUpdate(IFramework framework)
        {
            if (!Svc.ClientState.IsLoggedIn) return;
            if (!EzThrottler.Throttle("YABOT_GearsetOrphanCleanup", ThrottleMs)) return;

            try
            {
                var module = RaptureGearsetModule.Instance();
                if (module == null) return;

                // First pass after login just seeds the cache so pre-existing gear isn't treated as removed.
                if (!initialized)
                {
                    for (var i = 0; i < GearsetCount; i++)
                        if (TryReadGearset(module, i, out var items))
                            gearsetCache[i] = items;
                    initialized = true;
                    return;
                }

                DetectRemovals(module);
                ProcessPending(module);
                FlushBatch();
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "GearsetOrphanCleanup.OnUpdate");
            }
        }

        // Diffs each gearset against the cache; newly-removed items that aren't in any gearset get queued.
        private void DetectRemovals(RaptureGearsetModule* module)
        {
            for (var i = 0; i < GearsetCount; i++)
            {
                if (!TryReadGearset(module, i, out var current))
                {
                    gearsetCache.Remove(i);
                    continue;
                }

                if (!gearsetCache.TryGetValue(i, out var previous))
                {
                    // Freshly created gearset - seed it, nothing was removed.
                    gearsetCache[i] = current;
                    continue;
                }

                foreach (var oldId in previous)
                {
                    if (current.Contains(oldId)) continue;
                    if (IsInAnyGearset(module, oldId)) continue;
                    pendingOrphans[oldId] = Environment.TickCount64 + PendingWindowMs;
                    lastActivityMs = Environment.TickCount64;
                }

                gearsetCache[i] = current;
            }
        }

        // Tries to move each queued orphan from the armoury into inventory, dropping ones that are done,
        // have re-entered a gearset, or have timed out.
        private void ProcessPending(RaptureGearsetModule* module)
        {
            if (pendingOrphans.Count == 0) return;

            var manager = InventoryManager.Instance();
            if (manager == null) return;

            var now = Environment.TickCount64;
            var done = new List<uint>();

            foreach (var (itemId, expiry) in pendingOrphans)
            {
                // An item re-added to a gearset within the window is no longer an orphan - drop silently.
                if (IsInAnyGearset(module, itemId)) { done.Add(itemId); continue; }

                if (MoveItemFromArmoury(manager, itemId, out var full))
                {
                    movedNames.Add(GetItemName(itemId));
                    lastActivityMs = now;
                    done.Add(itemId);
                    continue;
                }

                if (full) break; // out of inventory space - stop trying the rest this tick

                // Not in the armoury yet (still being shuffled) - keep waiting until the window elapses.
                if (now > expiry)
                {
                    failedNames.Add(GetItemName(itemId));
                    lastActivityMs = now;
                    done.Add(itemId);
                }
            }

            foreach (var id in done)
                pendingOrphans.Remove(id);
        }

        // Emits the accumulated results as one line each (moved / failed) once nothing is pending and the
        // run has been quiet for a moment.
        private void FlushBatch()
        {
            if (pendingOrphans.Count > 0) return;
            if (movedNames.Count == 0 && failedNames.Count == 0) return;
            if (Environment.TickCount64 - lastActivityMs < FlushQuietMs) return;

            if (Config.AnnounceInChat)
            {
                if (movedNames.Count > 0)
                    Svc.Chat.Print($"[YABOT] Moved orphaned gear to inventory: {string.Join(", ", movedNames)}");
                if (failedNames.Count > 0)
                    Svc.Chat.PrintError($"[YABOT] Could not move (inventory full or not found): {string.Join(", ", failedNames)}");
            }

            movedNames.Clear();
            failedNames.Clear();
        }

        // Moves every armoury copy of the given base item id into the first free inventory slot.
        // Returns true if at least one copy was moved. Sets full=true if inventory ran out of space.
        private static bool MoveItemFromArmoury(InventoryManager* manager, uint baseItemId, out bool full)
        {
            full = false;
            var movedAny = false;

            foreach (var container in ArmouryContainers)
            {
                var inv = manager->GetInventoryContainer(container);
                if (inv == null || !inv->IsLoaded) continue;

                for (var slot = 0; slot < inv->Size; slot++)
                {
                    var item = inv->GetInventorySlot(slot);
                    if (item == null || item->ItemId == 0) continue;
                    if (item->ItemId % HqOffset != baseItemId) continue;

                    if (!TryFindFirstFreeInventorySlot(manager, out var dstContainer, out var dstSlot))
                    {
                        full = true;
                        return movedAny;
                    }

                    // The trailing 'true' makes the game honour the explicit destination slot; without it
                    // the item auto-places into the default (last bag) slot.
                    manager->MoveItemSlot(container, (ushort)slot, dstContainer, dstSlot, true);

                    // Confirm via the source slot rather than trusting the return code.
                    var after = inv->GetInventorySlot(slot);
                    if (after == null || after->ItemId == 0) movedAny = true;
                }
            }

            return movedAny;
        }

        // Base item ids held in a single gearset's 14 gear slots. Returns false for missing gearsets.
        private static bool TryReadGearset(RaptureGearsetModule* module, int gearsetId, out HashSet<uint> items)
        {
            items = new HashSet<uint>();
            var gearset = module->GetGearset(gearsetId);
            if (gearset == null) return false;
            if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) return false;
            if (gearset->Id != gearsetId) return false;

            foreach (ref var item in gearset->Items)
            {
                var id = item.ItemId % HqOffset;
                if (id != 0) items.Add(id);
            }
            return true;
        }

        // True if the base item id is still registered to any existing gearset - mirrors the game's
        // "registered to a gear set" inventory icon. FFXIVClientStructs maps no native single-call
        // equivalent in this version, so we walk the gearset entries the same way the game does.
        private static bool IsInAnyGearset(RaptureGearsetModule* module, uint baseItemId)
        {
            for (var i = 0; i < GearsetCount; i++)
            {
                var gearset = module->GetGearset(i);
                if (gearset == null) continue;
                if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                if (gearset->Id != i) continue;

                foreach (ref var item in gearset->Items)
                    if (item.ItemId % HqOffset == baseItemId)
                        return true;
            }
            return false;
        }

        private static bool TryFindFirstFreeInventorySlot(InventoryManager* manager, out InventoryType container, out ushort slot)
        {
            foreach (var bag in InventoryBags)
            {
                var inv = manager->GetInventoryContainer(bag);
                if (inv == null || !inv->IsLoaded) continue;

                for (var i = 0; i < inv->Size; i++)
                {
                    var item = inv->GetInventorySlot(i);
                    if (item != null && item->ItemId == 0)
                    {
                        container = bag;
                        slot = (ushort)i;
                        return true;
                    }
                }
            }

            container = InventoryType.Inventory1;
            slot = 0;
            return false;
        }

        private static string GetItemName(uint itemId)
        {
            try
            {
                var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId);
                var name = row.HasValue ? row.Value.Name.ExtractText() : null;
                return string.IsNullOrEmpty(name) ? $"item #{itemId}" : name;
            }
            catch
            {
                return $"item #{itemId}";
            }
        }
    }
}
