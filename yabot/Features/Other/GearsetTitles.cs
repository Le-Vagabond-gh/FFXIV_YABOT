using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Collections.Generic;
using YABOT.FeaturesSetup;

namespace YABOT.Features.Other
{
    // Hooks RaptureGearsetModule.EquipGearsetInternal, the chokepoint every gearset switch funnels
    // through - the gearset window, hotbar slots, /gearset macros and other plugins all end up there,
    // including via the safety-checked EquipGearset wrapper. That makes the title change immediate and
    // exact rather than inferred from a polled CurrentGearsetIndex, and it still fires when you re-equip
    // the gearset you are already on. A title set by hand afterwards stays until the next switch.
    public unsafe class GearsetTitles : BaseFeature
    {
        public override string Name => "Gearset Titles";

        public override string Description =>
            "Assign a title to each gearset and it gets applied whenever you switch to that gearset, no matter what " +
            "did the switching - the gearset window, a hotbar slot, a macro or another plugin. Gearsets without a " +
            "title of their own fall back to a configurable default title (or leave your current title untouched).";

        public override FeatureType FeatureType => FeatureType.Other;

        public override bool UseAutoConfig => false;

        public class Configs : FeatureConfig
        {
            // Gearset id -> title id, where 0 means "no title". A gearset with no entry uses DefaultTitleId.
            public Dictionary<int, int> GearsetTitleIds = new();

            // -1 leaves the current title alone, 0 clears it, anything higher is a Title sheet row id.
            public int DefaultTitleId = InheritValue;

            public bool AnnounceInChat = false;
        }

        public Configs Config { get; private set; } = null!;

        // Sentinel for "no choice of my own": per gearset it means "use the default", on the default
        // itself it means "leave whatever title I'm wearing".
        private const int InheritValue = -1;

        private const int GearsetCount = 100;

        private Hook<RaptureGearsetModule.Delegates.EquipGearsetInternal>? equipGearsetHook;

        private List<(int Id, string Name)>? unlockedTitles;
        private string titleFilter = string.Empty;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();

            equipGearsetHook ??= Svc.Hook.HookFromAddress<RaptureGearsetModule.Delegates.EquipGearsetInternal>(
                RaptureGearsetModule.MemberFunctionPointers.EquipGearsetInternal, EquipGearsetInternalDetour);
            equipGearsetHook.Enable();

            Svc.ClientState.Login += ResetTitleCache;
            Svc.ClientState.Logout += OnLogout;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            equipGearsetHook?.Disable();
            Svc.ClientState.Login -= ResetTitleCache;
            Svc.ClientState.Logout -= OnLogout;
            ResetTitleCache();
            base.Disable();
        }

        public override void Dispose()
        {
            equipGearsetHook?.Dispose();
            equipGearsetHook = null;
            base.Dispose();
        }

        private void OnLogout(int type, int code) => ResetTitleCache();

        // The unlocked list is per character, so it can't survive a character switch.
        private void ResetTitleCache() => unlockedTitles = null;

        private int EquipGearsetInternalDetour(RaptureGearsetModule* module, int gearsetId, byte glamourPlateId)
        {
            var result = equipGearsetHook!.Original(module, gearsetId, glamourPlateId);

            try
            {
                // Negative results are the game refusing the switch (in combat, gear locked, bad id);
                // don't change the title for an equip that never happened.
                if (result >= 0) ApplyTitle(gearsetId);
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, "GearsetTitles.EquipGearsetInternalDetour");
            }

            return result;
        }

        private void ApplyTitle(int gearsetId)
        {
            var player = Svc.Objects.LocalPlayer;
            if (player == null) return;

            var ui = UIState.Instance();
            if (ui == null) return;

            var titleId = Config.GearsetTitleIds.TryGetValue(gearsetId, out var assigned) ? assigned : Config.DefaultTitleId;
            if (titleId < 0) return;

            // The unlock check is only trustworthy once the server has sent the title list; before that,
            // trust the stored id rather than refusing to apply anything.
            if (titleId > 0 && ui->TitleList.DataReceived && !ui->TitleList.IsTitleUnlocked((ushort)titleId))
            {
                Log($"title #{titleId} is not unlocked, skipping gearset {gearsetId}");
                return;
            }

            if (((Character*)player.Address)->CharacterData.TitleId == titleId) return;

            Log($"gearset {gearsetId} -> title {titleId} ({TitleName(titleId)})");
            ui->TitleController.SendTitleIdUpdate((ushort)titleId);

            if (Config.AnnounceInChat)
                Svc.Chat.Print($"[YABOT] Title set to {TitleName(titleId)}.");
        }

        private static string TitleName(int titleId)
        {
            if (titleId == 0) return "(no title)";
            try
            {
                var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Title>().GetRowOrDefault((uint)titleId);
                if (!row.HasValue) return $"title #{titleId}";

                var playerState = PlayerState.Instance();
                var feminine = playerState != null && playerState->Sex == 1;
                var name = (feminine ? row.Value.Feminine : row.Value.Masculine).ExtractText();
                return string.IsNullOrEmpty(name) ? $"title #{titleId}" : name;
            }
            catch
            {
                return $"title #{titleId}";
            }
        }

        // Titles the character actually owns, cached once the server has sent the list.
        private List<(int Id, string Name)> GetUnlockedTitles()
        {
            if (unlockedTitles != null) return unlockedTitles;

            var list = new List<(int Id, string Name)>();
            var ui = UIState.Instance();
            if (ui == null) return list;

            if (!ui->TitleList.DataReceived)
            {
                if (!ui->TitleList.DataRequested) ui->TitleList.RequestTitleList();
                return list;
            }

            foreach (var row in Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Title>())
            {
                if (row.RowId == 0) continue;
                if (!ui->TitleList.IsTitleUnlocked((ushort)row.RowId)) continue;
                list.Add(((int)row.RowId, TitleName((int)row.RowId)));
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            unlockedTitles = list;
            return list;
        }

        // Shared by the default picker and every per-gearset row; `inheritLabel` names what -1 means here.
        private bool DrawTitleCombo(string id, ref int titleId, string inheritLabel)
        {
            var changed = false;
            var preview = titleId < 0 ? inheritLabel : TitleName(titleId);

            if (ImGui.BeginCombo(id, preview))
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint($"{id}_filter", "Search...", ref titleFilter, 256);
                var hasFilter = !string.IsNullOrEmpty(titleFilter);

                if (ImGui.Selectable($"{inheritLabel}##{id}_inherit", titleId < 0)) { titleId = InheritValue; changed = true; }
                if (ImGui.Selectable($"(no title)##{id}_none", titleId == 0)) { titleId = 0; changed = true; }

                var titles = GetUnlockedTitles();
                if (titles.Count == 0)
                    ImGui.TextDisabled("Title list not loaded yet - open the game's title window once.");

                foreach (var (rowId, name) in titles)
                {
                    if (hasFilter && !name.Contains(titleFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ImGui.Selectable($"{name}##{id}_{rowId}", titleId == rowId)) { titleId = rowId; changed = true; }
                }

                ImGui.EndCombo();
            }

            return changed;
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            var scale = ImGui.GetIO().FontGlobalScale;

            ImGui.TextUnformatted("Default title");
            ImGui.SetNextItemWidth(300 * scale);
            var defaultTitle = Config.DefaultTitleId;
            if (DrawTitleCombo("##gearsettitles_default", ref defaultTitle, "(leave my title alone)"))
            {
                Config.DefaultTitleId = defaultTitle;
                hasChanged = true;
            }
            ImGui.TextDisabled("Applied by every gearset that has no title of its own.");

            var announce = Config.AnnounceInChat;
            if (ImGui.Checkbox("Announce the title change in chat", ref announce))
            {
                Config.AnnounceInChat = announce;
                hasChanged = true;
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Per-gearset titles");

            var module = RaptureGearsetModule.Instance();
            if (module == null)
            {
                ImGui.TextDisabled("Log in to assign titles to gearsets.");
                return;
            }

            if (ImGui.BeginChild("##gearsettitles_list", new(0, 300 * scale), true))
            {
                for (var i = 0; i < GearsetCount; i++)
                {
                    var gearset = module->GetGearset(i);
                    if (gearset == null) continue;
                    if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                    if (gearset->Id != i) continue;

                    var titleId = Config.GearsetTitleIds.TryGetValue(i, out var stored) ? stored : InheritValue;
                    ImGui.SetNextItemWidth(260 * scale);
                    if (DrawTitleCombo($"##gearsettitles_{i}", ref titleId, "(use default)"))
                    {
                        if (titleId < 0) Config.GearsetTitleIds.Remove(i);
                        else Config.GearsetTitleIds[i] = titleId;
                        hasChanged = true;
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"#{i + 1} {gearset->NameString}");
                }
            }
            ImGui.EndChild();
        };
    }
}
