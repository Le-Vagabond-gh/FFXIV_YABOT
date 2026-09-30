using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System.Collections.Generic;
using System.Linq;
using DD = YABOT.Helpers.ZoneHelper.DeepDungeonKind;

namespace YABOT.Features.DeepDungeons
{
    public unsafe class DeepDungeonTitles : BaseFeature
    {
        public override string Name => "Deep Dungeon Titles";

        public override string Description =>
            "Pick a title per deep dungeon; it gets applied when you walk into the area around that deep dungeon's entrance (Quarrymill, Crick, The Eight Sentinels, Wolekdorf) or enter the deep dungeon itself. Your previous title isn't restored when you leave.";

        public override FeatureType FeatureType => FeatureType.DeepDungeons;

        public class Configs : FeatureConfig
        {
            // Title sheet row id per deep dungeon; 0 or missing = leave the title alone.
            public Dictionary<DD, int> TitleIds = new();

            public bool AnnounceInChat = false;
        }

        public Configs Config { get; private set; } = null!;

        // Entrance = territory + PlaceName of the area around the entrance, matched against both the
        // current area and sub-area so it works whichever level the game files it under.
        private static readonly (DD Kind, string Label, uint Territory, uint Place, uint[] Achievements)[] Dungeons =
        {
            // South Shroud / Quarrymill. In Too Deep VI, Pal-less Palace II/III, Some Kinna Wonderful
            (DD.PalaceOfTheDead, "Palace of the Dead", 153, 129, new uint[] { 1655, 1660, 1952, 1658 }),
            // The Ruby Sea / Crick. She's So High III/V, Heaven Is a Lonely Place II, Dressed for Heaven
            (DD.HeavenOnHigh, "Heaven-on-High", 613, 2774, new uint[] { 2049, 2051, 2055, 2053 }),
            // Mor Dhona / The Eight Sentinels. Le Morte d'Allagan Monstrosities III/V, All by Eurekaself II, Vintage Vogue
            (DD.EurekaOrthos, "Eureka Orthos", 156, 942, new uint[] { 3176, 3178, 3183, 3185 }),
            // Il Mheg / Wolekdorf. Pilgrim's Progress III/VI, Solo Traveler II, Faerie Favors, Unholy Sacrament/Devotion
            (DD.PilgrimsTraverse, "Pilgrim's Traverse / Final Verse", 816, 3157, new uint[] { 3809, 3811, 3815, 3817, 3818, 3819 }),
        };

        // The Trouble with Buried II, For the Hoard III/V/VI/VII - the hoard counts across every deep dungeon.
        private static readonly uint[] HoardAchievements = { 1666, 1663, 2056, 3184, 3816 };

        private readonly Dictionary<DD, int[]> titleOptions = new();

        // Index into Dungeons while in an entrance territory, -1 otherwise.
        private int entranceIndex = -1;

        // Edge trigger: apply once per walk into the entrance area, not every frame spent there.
        private bool inEntranceArea;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
            Svc.Framework.Update += OnUpdate;
            OnTerritoryChanged(Svc.ClientState.TerritoryType);
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
            Svc.Framework.Update -= OnUpdate;
            base.Disable();
        }

        private void OnTerritoryChanged(uint territory)
        {
            entranceIndex = System.Array.FindIndex(Dungeons, d => d.Territory == territory);
            inEntranceArea = false;

            var kind = ZoneHelper.GetDeepDungeon(territory);
            if (kind == DD.None) return;

            // The local player isn't there yet right after the zone change; retry until it is.
            TaskManager.Enqueue(() => TryApply(kind) != TitleHelper.ApplyResult.NotReady);
        }

        private void OnUpdate(IFramework framework)
        {
            if (entranceIndex < 0) return;

            try
            {
                var info = FFXIVClientStructs.FFXIV.Client.Game.UI.TerritoryInfo.Instance();
                if (info == null) return;

                var place = Dungeons[entranceIndex].Place;
                var inside = info->AreaPlaceNameId == place || info->SubAreaPlaceNameId == place;
                if (inside && !inEntranceArea)
                    inside = TryApply(Dungeons[entranceIndex].Kind) != TitleHelper.ApplyResult.NotReady;
                inEntranceArea = inside;
            }
            catch (System.Exception e)
            {
                Svc.Log.Error(e, $"[{Name}] OnUpdate");
            }
        }

        private TitleHelper.ApplyResult TryApply(DD kind)
        {
            if (!Config.TitleIds.TryGetValue(kind, out var titleId) || titleId <= 0) return TitleHelper.ApplyResult.AlreadyActive;

            var result = TitleHelper.Apply(titleId);
            if (result == TitleHelper.ApplyResult.Applied)
            {
                Log($"{kind} -> {TitleHelper.TitleName(titleId)}");
                if (Config.AnnounceInChat) TitleHelper.Announce(titleId);
            }
            return result;
        }

        // Title row ids for a deep dungeon: its own achievements' titles, then the shared hoard ones.
        private int[] TitleOptions(DD kind, uint[] achievements)
        {
            if (titleOptions.TryGetValue(kind, out var cached)) return cached;
            var sheet = Svc.Data.GetExcelSheet<Achievement>();
            var ids = achievements.Concat(HoardAchievements)
                .Select(a => sheet.TryGetRow(a, out var row) ? (int)row.Title.RowId : 0)
                .Where(t => t > 0)
                .Distinct()
                .ToArray();
            return titleOptions[kind] = ids;
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            var scale = ImGui.GetIO().FontGlobalScale;
            var unlocksKnown = TitleHelper.UnlocksKnown();

            var announce = Config.AnnounceInChat;
            if (ImGui.Checkbox("Announce the title change in chat##ddtitles_announce", ref announce))
            {
                Config.AnnounceInChat = announce;
                hasChanged = true;
            }

            foreach (var (kind, label, _, _, achievements) in Dungeons)
            {
                var current = Config.TitleIds.GetValueOrDefault(kind);
                ImGui.SetNextItemWidth(260 * scale);
                if (ImGui.BeginCombo($"{label}##ddtitles_{kind}", current > 0 ? TitleHelper.TitleName(current) : "(don't change)"))
                {
                    if (ImGui.Selectable($"(don't change)##ddtitles_{kind}_none", current <= 0))
                    {
                        Config.TitleIds.Remove(kind);
                        hasChanged = true;
                    }

                    foreach (var titleId in TitleOptions(kind, achievements))
                    {
                        var locked = unlocksKnown && !TitleHelper.IsUnlocked(titleId);
                        if (locked) ImGui.BeginDisabled();
                        if (ImGui.Selectable($"{TitleHelper.TitleName(titleId)}##ddtitles_{kind}_{titleId}", current == titleId))
                        {
                            Config.TitleIds[kind] = titleId;
                            hasChanged = true;
                        }
                        if (locked) ImGui.EndDisabled();
                    }

                    ImGui.EndCombo();
                }
            }

            if (!unlocksKnown)
                ImGui.TextDisabled("Title list not loaded yet - open the game's title window once to grey out locked titles.");
        };
    }
}
