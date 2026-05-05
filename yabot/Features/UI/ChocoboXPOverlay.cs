using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using static ECommons.GenericHelpers;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using YABOT.FeaturesSetup;
using YABOT.UI;
using System.Collections.Generic;
using System.Numerics;

namespace YABOT.Features.UI
{
    public unsafe class ChocoboXPOverlay : Feature
    {
        private const uint DefaultChocoboIconId = 62143;

        private Dictionary<byte, uint> RankMaxXP = new();
        private Dictionary<byte, uint> StanceIcons = new();
        private Dictionary<byte, string> StanceNames = new();

        public override string Name => "Chocobo XP Overlay";

        public override string Description => "Displays your chocobo companion's current XP percentage on screen when summoned. Hold Shift and drag to reposition the overlay. Left-click to change stance, right-click to open the Buddy window.";

        public override FeatureType FeatureType => FeatureType.UI;

        public class Configs : FeatureConfig
        {
            public Vector2 WindowPos = new(-1, -1);

            [FeatureConfigOption("Font scale", IntMin = 50, IntMax = 300, EditorSize = 300)]
            public int FontScale = 100;

            [FeatureConfigOption("Hide background")]
            public bool HideBackground = false;
        }

        public Configs Config { get; private set; } = null!;

        public override bool UseAutoConfig => true;

        private Overlays Overlay = null!;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            LoadRankXPTable();
            LoadStanceIcons();
            Overlay = new(this);
            base.Enable();
        }

        private void LoadRankXPTable()
        {
            RankMaxXP.Clear();
            for (uint rank = 1; rank <= 20; rank++)
            {
                if (TryGetRawRow("BuddyRank", rank, out RawRow row))
                {
                    var xp = System.Convert.ToUInt32(row.ReadColumn(0));
                    if (xp > 0)
                        RankMaxXP[(byte)rank] = xp;
                }
            }
        }

        private void LoadStanceIcons()
        {
            StanceIcons.Clear();
            StanceNames.Clear();
            for (uint id = 1; id <= 20; id++)
            {
                if (TryGetRawRow("BuddyAction", id, out RawRow row))
                {
                    var iconId = System.Convert.ToUInt32(row.ReadColumn(2));
                    var name = row.ReadColumn(0)?.ToString() ?? "";
                    if (iconId > 0)
                        StanceIcons[(byte)id] = iconId;
                    if (!string.IsNullOrEmpty(name) && id >= 4)
                        StanceNames[(byte)id] = name;
                }
            }
        }

        private uint GetStanceIcon(byte activeCommand)
        {
            return StanceIcons.TryGetValue(activeCommand, out var iconId) ? iconId : DefaultChocoboIconId;
        }

        public override bool DrawConditions()
        {
            try
            {
                var companion = &UIState.Instance()->Buddy.CompanionInfo;
                if (companion->Companion == null || companion->Companion->EntityId == 0xE0000000)
                    return false;

                if (companion->Mounted)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        public override void Draw()
        {
            try
            {
                var companion = &UIState.Instance()->Buddy.CompanionInfo;
                RankMaxXP.TryGetValue(companion->Rank, out var maxXP);

                var isMaxed = maxXP == 0 || companion->CurrentXP >= maxXP;
                var percent = isMaxed ? 100f : (float)companion->CurrentXP / maxXP * 100f;

                var oldScale = ImGui.GetFont().Scale;
                ImGui.GetFont().Scale *= Config.FontScale / 100f;
                ImGui.PushFont(ImGui.GetFont());

                ImGuiHelpers.ForceNextWindowMainViewport();
                if (Config.WindowPos.X >= 0 && Config.WindowPos.Y >= 0)
                    ImGui.SetNextWindowPos(Config.WindowPos, ImGuiCond.Once);

                var bgColor = Config.HideBackground ? new Vector4(0, 0, 0, 0) : new Vector4(0, 0, 0, 0.5f);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, bgColor);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 4));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

                var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
                var shiftHeld = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
                if (!shiftHeld)
                    flags |= ImGuiWindowFlags.NoMove;

                ImGui.Begin("###ChocoboXPOverlay", flags);

                var iconSize = ImGui.GetFontSize() * 1.4f;
                var stanceIconId = GetStanceIcon(companion->ActiveCommand);
                if (ThreadLoadImageHandler.TryGetIconTextureWrap(stanceIconId, false, out var icon))
                {
                    ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
                    ImGui.SameLine();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetFontSize()) * 0.5f);
                }

                if (!isMaxed)
                    ImGui.TextUnformatted($"{percent:F1}%");

                if (ImGui.IsWindowHovered())
                {
                    if (!isMaxed)
                        ImGui.SetTooltip($"Rank {companion->Rank} - {companion->CurrentXP:N0} / {maxXP:N0} XP");
                    else
                        ImGui.SetTooltip($"Rank {companion->Rank} - MAX");

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                        AgentModule.Instance()->GetAgentByInternalId(AgentId.Buddy)->Show();

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !shiftHeld)
                        ImGui.OpenPopup("###ChocoboStancePopup");
                }

                if (ImGui.BeginPopup("###ChocoboStancePopup"))
                {
                    foreach (var (id, name) in StanceNames)
                    {
                        var isActive = companion->ActiveCommand == id;
                        if (ImGui.MenuItem(name, "", isActive))
                        {
                            ActionManager.Instance()->UseAction(ActionType.BuddyAction, id);
                        }
                    }
                    ImGui.EndPopup();
                }

                Config.WindowPos = ImGui.GetWindowPos();

                ImGui.End();
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor();
                ImGui.GetFont().Scale = oldScale;
                ImGui.PopFont();
            }
            catch { }
        }

        public override void Disable()
        {
            SaveConfig(Config);
            P.Ws.RemoveWindow(Overlay);
            Overlay = null!;
            base.Disable();
        }
    }
}
