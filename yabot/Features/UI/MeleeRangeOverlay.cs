using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using YABOT.FeaturesSetup;
using YABOT.UI;
using System.Numerics;

namespace YABOT.Features.UI;

public unsafe class MeleeRangeOverlay : BaseFeature
{
    public override string Name => "Melee Range Overlay";

    public override string Description =>
        "Shows a small coloured circle on screen indicating whether your current target is within melee weapon-skill range. " +
        "Uses the game's own in-range/LoS check (Standard Attack, action 7) so the range matches your equipped weapon. " +
        "Only displays on melee jobs (tanks and melee DPS - ClassJob role 1 or 2); hidden on ranged DPS and healers. " +
        "Green when in range, red when out of range or out of line of sight. Hold Shift and drag to reposition.";

    public override FeatureType FeatureType => FeatureType.UI;

    public override bool UseAutoConfig => false;

    private const uint StandardAttackActionId = 7;

    public enum NoTargetDisplay
    {
        Hidden,
        Grey,
    }

    public class Configs : FeatureConfig
    {
        public Vector2 WindowPos = new(-1, -1);
        public int CircleSize = 12;
        public NoTargetDisplay NoTargetMode = NoTargetDisplay.Hidden;
    }

    public Configs Config { get; private set; } = null!;

    private Overlays Overlay = null!;

    public override void Enable()
    {
        Config = LoadConfig<Configs>() ?? new Configs();
        Overlay = new(this);
        base.Enable();
    }

    public override void Disable()
    {
        SaveConfig(Config);
        if (Overlay != null)
        {
            P.Ws.RemoveWindow(Overlay);
            Overlay = null!;
        }
        base.Disable();
    }

    protected override DrawConfigDelegate? DrawConfigTree => DrawConfig;

    private void DrawConfig(ref bool hasChanged)
    {
        var size = Config.CircleSize;
        ImGui.SetNextItemWidth(300 * ImGui.GetIO().FontGlobalScale);
        if (ImGui.SliderInt("Circle size (px)", ref size, 8, 128))
        {
            Config.CircleSize = size;
            SaveConfig(Config);
            hasChanged = true;
        }

        ImGui.Separator();
        ImGui.Text("When no target:");
        if (ImGui.RadioButton("Hide the circle", Config.NoTargetMode == NoTargetDisplay.Hidden))
        {
            Config.NoTargetMode = NoTargetDisplay.Hidden;
            SaveConfig(Config);
            hasChanged = true;
        }
        if (ImGui.RadioButton("Show a grey circle", Config.NoTargetMode == NoTargetDisplay.Grey))
        {
            Config.NoTargetMode = NoTargetDisplay.Grey;
            SaveConfig(Config);
            hasChanged = true;
        }
    }

    private bool IsMeleeJob()
    {
        try
        {
            var player = Svc.Objects.LocalPlayer;
            if (player is null) return false;
            var role = player.ClassJob.Value.Role;
            return role == 1 || role == 2;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInDialogue()
    {
        return Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
            || Svc.Condition[ConditionFlag.OccupiedInEvent]
            || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent];
    }

    public override bool DrawConditions()
    {
        try
        {
            if (Svc.Objects.LocalPlayer is null) return false;
            if (!IsMeleeJob()) return false;
            if (IsInDialogue()) return false;
            if (Svc.Targets.Target is null && Config.NoTargetMode == NoTargetDisplay.Hidden) return false;
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
            var player = Svc.Objects.LocalPlayer;
            if (player is null) return;
            var target = Svc.Targets.Target;

            Vector4 color;
            if (target is null)
            {
                color = new Vector4(0.5f, 0.5f, 0.5f, 0.8f);
            }
            else
            {
                var source = (GameObject*)player.Address;
                var tgt = (GameObject*)target.Address;
                var status = ActionManager.GetActionInRangeOrLoS(StandardAttackActionId, source, tgt);
                var inRange = status == 0;
                color = inRange
                    ? new Vector4(0.2f, 0.9f, 0.2f, 0.9f)
                    : new Vector4(0.95f, 0.25f, 0.25f, 0.9f);
            }

            ImGuiHelpers.ForceNextWindowMainViewport();
            if (Config.WindowPos.X >= 0 && Config.WindowPos.Y >= 0)
                ImGui.SetNextWindowPos(Config.WindowPos, ImGuiCond.Once);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
            var shiftHeld = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            if (!shiftHeld)
                flags |= ImGuiWindowFlags.NoMove;

            ImGui.Begin("###MeleeRangeOverlay", flags);

            var diameter = Config.CircleSize;
            ImGui.Dummy(new Vector2(diameter, diameter));
            var min = ImGui.GetItemRectMin();
            var center = new Vector2(min.X + diameter * 0.5f, min.Y + diameter * 0.5f);
            var radius = diameter * 0.5f;

            var drawList = ImGui.GetWindowDrawList();
            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(color));

            Config.WindowPos = ImGui.GetWindowPos();

            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }
        catch { }
    }
}
