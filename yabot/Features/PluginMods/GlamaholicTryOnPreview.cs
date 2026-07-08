using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;

namespace YABOT.Features.PluginMods
{
    // Live "try on" preview for Glamaholic's item picker.
    //
    // When Glamaholic's plate editor item list is open (the popup that appears when you click a
    // slot in edit mode) AND the game's Fitting Room ("Tryon") window is open, hovering an item in
    // that list auto-tries it on.
    //
    // Glamaholic never stores which row is hovered - that fact only exists for a few microseconds
    // inside its own ImGui draw. Since every Dalamud plugin shares one ImGui context, we read the
    // global hovered-id from that context and map it back to an item row id ourselves by reproducing
    // ImGui's own id hash (ImHashStr) over Glamaholic's "##{rowId}" selectable labels. No Glamaholic
    // edit, no reflection into Glamaholic - only shared ImGui state + a normal AgentTryon call.
    //
    // Coupling to Glamaholic internals is limited to two string constants: the child window str_id
    // ("item search") and the selectable label format ("##{rowId}"). If a future Glamaholic version
    // restructures that popup, this feature silently stops firing (and is trivial to re-point).
    public unsafe class GlamaholicTryOnPreview : PluginModFeature
    {
        public override string Name => "Glamaholic: Try On Item List On Hover";

        public override string Description =>
            "In Glamaholic's plate editor, hovering an item in the slot item-picker list automatically tries it on, " +
            "as long as the game's Fitting Room window is already open. Lets you flip through looks without clicking each one. " +
            "A short delay prevents fast scrolling from spamming try-ons.";

        public override string RequiredPluginName => "Glamaholic";

        // Glamaholic's item-picker child window is BeginChild("item search"); ImGui builds the child
        // window name as "<parent>/item search_<hex>", so this substring identifies it uniquely
        // (its dye pickers are "dye 1 picker" / "dye 2 picker").
        private const string GlamaholicItemChildName = "item search";
        private const string FittingRoomAddon = "Tryon";

        public class Configs : FeatureConfig
        {
            public int DebounceMs = 100;
        }

        public Configs Config { get; private set; } = null!;

        public override bool UseAutoConfig => false;
        protected override DrawConfigDelegate? DrawConfigTree => DrawConfig;

        private void DrawConfig(ref bool hasChanged)
        {
            var ms = Config.DebounceMs;
            ImGui.SetNextItemWidth(300 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.SliderInt("Hover delay before trying on (ms)", ref ms, 0, 500))
            {
                Config.DebounceMs = ms;
                SaveConfig(Config);
                hasChanged = true;
            }
            ImGui.TextDisabled("Higher = less spam while scrolling; lower = snappier preview.");
        }

        // --- state ---
        private uint _seedForMap;                       // seed the current hash map was built for
        private Dictionary<uint, uint>? _idToRow;       // imgui id -> item row id, for _seedForMap
        private uint[]? _equippableRows;                // cached equippable item row ids

        private uint _candidateRow;                     // row the cursor is currently over
        private uint _lastTriedRow;                     // row we last actually tried on
        private readonly Stopwatch _hoverTimer = new(); // how long the cursor has sat on _candidateRow

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.PluginInterface.UiBuilder.Draw += OnDraw;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.PluginInterface.UiBuilder.Draw -= OnDraw;
            SaveConfig(Config);
            ResetHover();
            base.Disable();
        }

        private void ResetHover()
        {
            _candidateRow = 0;
            _lastTriedRow = 0;
            _hoverTimer.Reset();
        }

        private void OnDraw()
        {
            try
            {
                // Only ever act while the Fitting Room is actually open.
                var addon = Svc.GameGui.GetAddonByName(FittingRoomAddon, 1);
                if (addon == null || !addon.IsVisible)
                {
                    ResetHover();
                    return;
                }

                var row = ResolveHoveredRow();
                if (row == 0)
                {
                    // Not hovering a Glamaholic item row this frame. Keep _lastTriedRow so we don't
                    // re-fire the same item if the cursor briefly leaves and returns to it.
                    _candidateRow = 0;
                    _hoverTimer.Reset();
                    return;
                }

                if (row != _candidateRow)
                {
                    _candidateRow = row;
                    _hoverTimer.Restart();
                    return;
                }

                if (row == _lastTriedRow) return;

                if (_hoverTimer.ElapsedMilliseconds >= Config.DebounceMs)
                {
                    TryOn(row);
                    _lastTriedRow = row;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[GlamaholicTryOnPreview] draw");
            }
        }

        // Returns the item row id the cursor is over in Glamaholic's item list, or 0 if none.
        private uint ResolveHoveredRow()
        {
            var ctx = ImGui.GetCurrentContext();
            if (ctx.Handle == null) return 0;

            uint hoveredId = ctx.HoveredIdPreviousFrame;
            if (hoveredId == 0) return 0;

            var win = ctx.HoveredWindow;
            if (win.Handle == null) return 0;

            var namePtr = win.Name;
            if (namePtr == null) return 0;
            var name = Marshal.PtrToStringUTF8((nint)namePtr);
            if (string.IsNullOrEmpty(name) || !name.Contains(GlamaholicItemChildName))
                return 0;

            uint seed = win.ID;
            var map = GetIdMap(seed);
            return map.TryGetValue(hoveredId, out var row) ? row : 0u;
        }

        // Builds (and caches per-seed) a map from the ImGui id of each equippable item's
        // "##{rowId}" selectable to its row id, matching how Glamaholic labels its list entries.
        private Dictionary<uint, uint> GetIdMap(uint seed)
        {
            if (_idToRow != null && _seedForMap == seed) return _idToRow;

            _equippableRows ??= Svc.Data.GetExcelSheet<Item>()!
                .Where(i => i.EquipSlotCategory.RowId != 0)
                .Select(i => i.RowId)
                .ToArray();

            var map = new Dictionary<uint, uint>(_equippableRows.Length);
            foreach (var rowId in _equippableRows)
                map[ImHashStr($"##{rowId}", seed)] = rowId;

            _idToRow = map;
            _seedForMap = seed;
            return map;
        }

        private static void TryOn(uint rowId)
        {
            var agent = AgentTryon.Instance();
            if (agent == null) return;

            // Plain single-item try-on (undyed preview). We deliberately do NOT touch the Fitting
            // Room's "Save/Delete Outfit" toggle, so hovered items layer onto the current glamour
            // exactly as the game's own right-click try-on would.
            AgentTryon.TryOn(0, rowId, 0, 0);
        }

        // --- ImGui id hashing (ImHashStr), reproduced so we can match the shared hovered id. ---
        // ImGui hashes item labels with a reflected CRC32 (poly 0xEDB88320) seeded by the enclosing
        // id-stack entry. Glamaholic's list selectables use "##{rowId}" with no intervening PushID,
        // so the seed is simply the child window's id.
        private static readonly uint[] Crc32 = BuildCrc32();

        private static uint[] BuildCrc32()
        {
            var t = new uint[256];
            const uint poly = 0xEDB88320u;
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                t[i] = crc;
            }
            return t;
        }

        private static uint ImHashStr(string s, uint seed)
        {
            seed = ~seed;
            uint crc = seed;
            for (int i = 0; i < s.Length; i++)
            {
                byte c = (byte)s[i];
                // "###" resets the hash to the seed; "##" (our case) does not.
                if (c == (byte)'#' && i + 2 < s.Length && s[i + 1] == '#' && s[i + 2] == '#')
                    crc = seed;
                crc = (crc >> 8) ^ Crc32[(crc & 0xFF) ^ c];
            }
            return ~crc;
        }
    }
}
