using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System.Collections.Generic;
using System.Linq;
using DD = YABOT.Helpers.ZoneHelper.DeepDungeonKind;

namespace YABOT.Features.GearsetsTitles
{
    public unsafe class DeepDungeonTitles : BaseFeature
    {
        // class name kept: the config file and the enabled list are keyed on it
        public override string Name => "Zone Titles";

        public override string Description =>
            "Pick a title per deep dungeon; it gets applied when you walk into the area around that deep dungeon's entrance (Quarrymill, Crick, The Eight Sentinels, Wolekdorf) or enter the deep dungeon itself. Your previous title isn't restored when you leave. Optionally wears a Makai title in the PvP zones you pick, and that one does give you your previous title back on the way out.";

        public override FeatureType FeatureType => FeatureType.GearsetsTitles;

        public class Configs : FeatureConfig
        {
            // Title sheet row id per deep dungeon; 0 or missing = leave the title alone.
            public Dictionary<DD, int> TitleIds = new();

            public bool AnnounceInChat = false;

            // old on/off switch, only read once to migrate it into MakaiZones
            public bool MakaiTitleInPvP = false;

            public PvPZone MakaiZones = PvPZone.None;
        }

        [System.Flags]
        public enum PvPZone
        {
            None = 0,
            WolvesDen = 1,
            Frontline = 2,
            RivalWings = 4,
            CrystallineConflict = 8,
        }

        private static readonly (PvPZone Zone, string Label)[] MakaiZoneLabels =
        {
            (PvPZone.WolvesDen, "Wolves' Den Pier"),
            (PvPZone.Frontline, "Frontline"),
            (PvPZone.RivalWings, "Rival Wings"),
            (PvPZone.CrystallineConflict, "Crystalline Conflict"),
        };

        private const PvPZone AllPvPZones = PvPZone.WolvesDen | PvPZone.Frontline | PvPZone.RivalWings | PvPZone.CrystallineConflict;

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

        // GARO collaboration titles, ClassJob row id -> Title row id. the other jobs have none
        private static readonly Dictionary<uint, int> MakaiJobTitles = new()
        {
            { 20, 327 }, { 23, 330 }, { 25, 331 }, { 24, 332 }, { 27, 334 }, { 28, 335 }, { 31, 337 },
            { 33, 338 }, { 34, 639 }, { 35, 640 }, { 37, 641 }, { 38, 642 }, { 39, 643 }, { 40, 644 },
        };

        private const int MakaiMasterTitle = 325;

        private readonly Dictionary<DD, int[]> titleOptions = new();

        // Index into Dungeons while in an entrance territory, -1 otherwise.
        private int entranceIndex = -1;

        // Edge trigger: apply once per walk into the entrance area, not every frame spent there.
        private bool inEntranceArea;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            if (Config.MakaiTitleInPvP)
            {
                Config.MakaiZones = AllPvPZones;
                Config.MakaiTitleInPvP = false;
                SaveConfig(Config);
            }
            Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
            Svc.Framework.Update += OnUpdate;
            Svc.ClientState.ClassJobChanged += OnClassJobChanged;
            Svc.ClientState.Logout += OnLogout;
            OnTerritoryChanged(Svc.ClientState.TerritoryType);
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
            Svc.Framework.Update -= OnUpdate;
            Svc.ClientState.ClassJobChanged -= OnClassJobChanged;
            Svc.ClientState.Logout -= OnLogout;
            base.Disable();
        }

        private void OnTerritoryChanged(uint territory)
        {
            entranceIndex = System.Array.FindIndex(Dungeons, d => d.Territory == territory);
            inEntranceArea = false;

            makaiPending = IsMakaiZone(territory);
            restorePending = !makaiPending && titleBeforeMakai != null;
            if (makaiPending) return;

            var kind = ZoneHelper.GetDeepDungeon(territory);
            if (kind == DD.None) return;

            // The local player isn't there yet right after the zone change; retry until it is.
            TaskManager.Enqueue(() => TryApply(kind) != TitleHelper.ApplyResult.NotReady);
        }

        private void OnUpdate(IFramework framework)
        {
            if ((makaiPending || restorePending) && System.DateTime.Now >= nextMakaiTry)
            {
                nextMakaiTry = System.DateTime.Now.AddSeconds(MakaiRetrySeconds);
                try
                {
                    var state = makaiPending ? TryApplyMakai() : TryRestoreTitle();
                    if (state != lastMakaiState) Log($"PvP: {state}");
                    lastMakaiState = state;
                }
                catch (System.Exception e)
                {
                    Svc.Log.Error(e, $"[{Name}] Makai title");
                }
            }

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
            return ApplyAndLog(titleId, kind.ToString());
        }

        private TitleHelper.ApplyResult ApplyAndLog(int titleId, string source)
        {
            var result = TitleHelper.Apply(titleId);
            if (result == TitleHelper.ApplyResult.Applied)
            {
                Log($"{source} -> {TitleHelper.TitleName(titleId)}");
                if (Config.AnnounceInChat) TitleHelper.Announce(titleId);
            }
            return result;
        }

        // keyed on TerritoryIntendedUse. the Den is filed as open world (1), so it needs the PvP flag
        // to stand out; Crystalline Conflict has two ids, casual/ranked and custom matches
        private static PvPZone GetPvPZone(uint territory)
        {
            if (!Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row) || !row.IsPvpZone) return PvPZone.None;
            return row.TerritoryIntendedUse.RowId switch
            {
                1 => PvPZone.WolvesDen,
                18 => PvPZone.Frontline,
                39 => PvPZone.RivalWings,
                28 or 37 => PvPZone.CrystallineConflict,
                _ => PvPZone.None,
            };
        }

        private bool IsMakaiZone(uint territory) => (Config.MakaiZones & GetPvPZone(territory)) != 0;

        // the job can still be changed inside a PvP zone (Wolves' Den Pier, before a match starts)
        private void OnClassJobChanged(uint classJobId)
        {
            if (IsMakaiZone(Svc.ClientState.TerritoryType)) makaiPending = true;
        }

        // retried from OnUpdate until the character is seen wearing the title: the request is
        // silently dropped while zoning in or busy, so a sent update proves nothing
        private bool makaiPending;
        private System.DateTime nextMakaiTry;
        private string lastMakaiState = string.Empty;
        private const double MakaiRetrySeconds = 2;

        // the job's Makai title, else Makai Master, else any owned Makai title. needs the title
        // list from the server, so it waits for it instead of trusting ids.
        // returns what happened, logged when it changes
        private string TryApplyMakai()
        {
            var player = Svc.Objects.LocalPlayer;
            if (player == null) return "no player yet";
            if (!TitleHelper.UnlocksKnown()) return "title list not received yet";

            var candidates = new List<int>();
            if (MakaiJobTitles.TryGetValue(player.ClassJob.RowId, out var jobTitle)) candidates.Add(jobTitle);
            candidates.Add(MakaiMasterTitle);
            candidates.AddRange(MakaiJobTitles.Values);

            var titleId = candidates.FirstOrDefault(TitleHelper.IsUnlocked);
            if (titleId <= 0)
            {
                makaiPending = false;
                return "no Makai title owned";
            }

            // only the first swap of a PvP outing counts: a job change or a second PvP zone in a
            // row would otherwise save the Makai title as the one to go back to
            titleBeforeMakai ??= TitleHelper.CurrentTitle();

            var state = ApplyUntilWorn(titleId, out var done);
            if (done) makaiPending = false;
            return state;
        }

        // the title worn before the first Makai swap, put back when leaving for a zone that isn't
        // ticked. null = nothing to restore. kept in memory only, per character
        private int? titleBeforeMakai;
        private bool restorePending;

        private void OnLogout(int type, int code)
        {
            titleBeforeMakai = null;
            makaiPending = restorePending = false;
        }

        private string TryRestoreTitle()
        {
            var state = ApplyUntilWorn(titleBeforeMakai!.Value, out var done);
            if (done)
            {
                restorePending = false;
                titleBeforeMakai = null;
            }
            return state;
        }

        // one step of the retry loop, `done` once the title is seen on the character (or can't be worn)
        private string ApplyUntilWorn(int titleId, out bool done)
        {
            var name = TitleHelper.TitleName(titleId);
            var result = TitleHelper.Apply(titleId);
            done = result is TitleHelper.ApplyResult.AlreadyActive or TitleHelper.ApplyResult.NotUnlocked;
            switch (result)
            {
                case TitleHelper.ApplyResult.AlreadyActive:
                    if (Config.AnnounceInChat && lastMakaiState.StartsWith("sent")) TitleHelper.Announce(titleId);
                    return $"wearing {name}";
                case TitleHelper.ApplyResult.Applied:
                    return $"sent {name}, waiting for it to show";
                case TitleHelper.ApplyResult.NotUnlocked:
                    return $"{name} is not owned";
                default:
                    return "busy";
            }
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

            var zones = Config.MakaiZones;
            var preview = zones == PvPZone.None ? "(off)"
                : zones == AllPvPZones ? "All PvP zones"
                : string.Join(", ", MakaiZoneLabels.Where(z => zones.HasFlag(z.Zone)).Select(z => z.Label));
            ImGui.SetNextItemWidth(260 * scale);
            if (ImGui.BeginCombo("Makai title in PvP zones##ddtitles_makai", preview))
            {
                foreach (var (zone, label) in MakaiZoneLabels)
                {
                    var on = zones.HasFlag(zone);
                    if (ImGui.Checkbox($"{label}##ddtitles_makai_{zone}", ref on))
                    {
                        Config.MakaiZones ^= zone;
                        hasChanged = true;
                    }
                }
                ImGui.EndCombo();
            }
            Dalamud.Interface.Components.ImGuiComponents.HelpMarker("Applied when you enter one of the ticked PvP zones or change job there: the Makai title of your job, else Makai Master, else any Makai title you own. Your previous title comes back when you leave.");

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
