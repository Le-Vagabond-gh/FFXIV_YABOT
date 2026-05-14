using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using YABOT.FeaturesSetup;
using YABOT.UI;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YABOT.Features.UI;

public unsafe class CommendationOverlay : Feature
{
    public override string Name => "Commendation Tracker";

    public override string Description => "Displays your commendation count after you leave a duty, so you can see if other players commended you. Commendations aren't delivered while the commend popup is up (so players can't pressure each other for them) - they only arrive once everyone has left the duty.";

    public override FeatureType FeatureType => FeatureType.UI;

    public override bool UseAutoConfig => true;

    // /ycommend is intentionally not surfaced in CommandReferences - debug-only command,
    // hidden from the Commands table and the Commands category filter. Still registered in Enable().

    public class Configs : FeatureConfig
    {
        public Vector2 WindowPos = new(-1, -1);

        [FeatureConfigOption("Font scale", IntMin = 50, IntMax = 300, EditorSize = 300)]
        public int FontScale = 100;

        [FeatureConfigOption("Hide background")]
        public bool HideBackground = false;

        [FeatureConfigOption("Always show (for repositioning)")]
        public bool AlwaysShow = false;
    }

    public Configs Config { get; private set; } = null!;

    private Overlays Overlay = null!;
    private short baseCommendations = -1;
    private int gained = 0;
    private bool tracking = false;
    private bool dutyCompleted = false;
    private DateTime? hideAt = null;
    private DateTime lastGainTime = DateTime.MinValue;

    public override void Enable()
    {
        Config = LoadConfig<Configs>() ?? new Configs();
        Overlay = new(this);
        Svc.DutyState.DutyCompleted += OnDutyCompleted;
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Condition.ConditionChange += OnConditionChange;
        Svc.Commands.AddHandler("/ycommend", new CommandInfo(OnDebugCommand) { HelpMessage = "Debug commendation overlay", ShowInHelp = false });
        base.Enable();
    }

    public override void Disable()
    {
        SaveConfig(Config);
        Svc.DutyState.DutyCompleted -= OnDutyCompleted;
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.Condition.ConditionChange -= OnConditionChange;
        Svc.Commands.RemoveHandler("/ycommend");
        Reset();
        P.Ws.RemoveWindow(Overlay);
        Overlay = null!;
        base.Disable();
    }

    private void OnDebugCommand(string command, string args)
    {
        if (args == "reset")
        {
            Reset();
            return;
        }
        if (!tracking)
        {
            baseCommendations = PlayerState.Instance()->PlayerCommendations;
            tracking = true;
        }
        gained++;
        lastGainTime = DateTime.Now;
        hideAt = DateTime.Now.AddMinutes(2);
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        try
        {
            baseCommendations = PlayerState.Instance()->PlayerCommendations;
            gained = 0;
            dutyCompleted = true;
            tracking = false;
        }
        catch { }
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        try
        {
            if (!dutyCompleted && !tracking) return;
            if (chatMessage.Message.TextValue.Contains("player commendation", StringComparison.OrdinalIgnoreCase))
            {
                gained++;
                lastGainTime = DateTime.Now;
                hideAt = DateTime.Now.AddMinutes(2);
                if (!tracking)
                    tracking = true;
            }
        }
        catch { }
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.BoundByDuty && !value && tracking)
            hideAt = DateTime.Now.AddMinutes(2);
    }

    private void Reset()
    {
        tracking = false;
        dutyCompleted = false;
        baseCommendations = -1;
        gained = 0;
        hideAt = null;
        lastGainTime = DateTime.MinValue;
    }

    public override bool DrawConditions()
    {
        if (tracking && hideAt.HasValue && DateTime.Now > hideAt.Value)
            Reset();
        return tracking || Config.AlwaysShow;
    }

    public override void Draw()
    {
        try
        {
            var current = baseCommendations >= 0 ? baseCommendations + gained : PlayerState.Instance()->PlayerCommendations;

            var oldScale = ImGui.GetFont().Scale;
            ImGui.GetFont().Scale *= Config.FontScale / 100f;
            ImGui.PushFont(ImGui.GetFont());

            var glowDuration = 2.0f;
            var elapsed = (float)(DateTime.Now - lastGainTime).TotalSeconds;
            var glowAlpha = elapsed < glowDuration ? (1f - elapsed / glowDuration) : 0f;
            if (glowAlpha > 0)
                glowAlpha *= 0.5f + 0.5f * (float)Math.Sin(elapsed * 6f);

            var bgColor = Config.HideBackground ? new Vector4(0, 0, 0, 0) : new Vector4(0, 0, 0, 0.5f);
            if (glowAlpha > 0)
            {
                var glowBg = new Vector4(0.8f, 0.6f, 0.1f, 0.4f * glowAlpha);
                bgColor = new Vector4(
                    bgColor.X + (glowBg.X - bgColor.X) * glowAlpha,
                    bgColor.Y + (glowBg.Y - bgColor.Y) * glowAlpha,
                    bgColor.Z + (glowBg.Z - bgColor.Z) * glowAlpha,
                    Math.Max(bgColor.W, glowBg.W));
            }
            ImGui.PushStyleColor(ImGuiCol.WindowBg, bgColor);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 4));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
            var shiftHeld = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            if (!shiftHeld)
                flags |= ImGuiWindowFlags.NoMove;

            ImGui.Begin("###CommendationOverlay", flags);

            var iconSize = ImGui.GetFontSize() * 1.4f;
            var tex = Svc.Texture.GetFromGame("ui/uld/Character.tex").GetWrapOrDefault();
            if (tex != null)
            {
                var uv0 = new Vector2(160f / 256f, 104f / 220f);
                var uv1 = new Vector2(192f / 256f, 136f / 220f);
                var iconTint = glowAlpha > 0
                    ? new Vector4(1f, 0.9f + 0.1f * glowAlpha, 0.7f + 0.3f * glowAlpha, 1f)
                    : new Vector4(1f, 1f, 1f, 1f);
                ImGui.Image(tex.Handle, new Vector2(iconSize, iconSize), uv0, uv1, iconTint);
                ImGui.SameLine();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetFontSize()) * 0.5f);
            }

            var textColor = glowAlpha > 0
                ? new Vector4(1f, 0.85f + 0.15f * glowAlpha, 0.3f + 0.2f * glowAlpha, 1f)
                : new Vector4(0.2f, 1f, 0.2f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            ImGui.TextUnformatted($"{current} (+{gained})");
            ImGui.PopStyleColor();

            if (glowAlpha > 0)
            {
                var drawList = ImGui.GetForegroundDrawList();
                var winPos = ImGui.GetWindowPos();
                var winSize = ImGui.GetWindowSize();
                var center = new Vector2(winPos.X + iconSize * 0.5f + 6f, winPos.Y + winSize.Y * 0.5f);

                var particleCount = 12;
                var particleDuration = 1.5f;
                var t = Math.Min(elapsed / particleDuration, 1f);

                for (var p = 0; p < particleCount; p++)
                {
                    var seed = gained * 73 + p * 137;
                    var angle = (float)(seed % 360) * (float)Math.PI / 180f;
                    var speed = 30f + (seed % 50);
                    var sparkleSize = 2f + (seed % 3);

                    var dist = speed * t * (1f - t * 0.3f);
                    var pos = new Vector2(
                        center.X + (float)Math.Cos(angle) * dist,
                        center.Y + (float)Math.Sin(angle) * dist);

                    var alpha = (1f - t) * (0.7f + 0.3f * (float)Math.Sin(elapsed * 10f + p));
                    if (alpha <= 0) continue;

                    var gold = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.3f, alpha));
                    var white = ImGui.GetColorU32(new Vector4(1f, 1f, 0.9f, alpha * 0.8f));

                    var s = sparkleSize * (1f - t * 0.5f);
                    var rot = elapsed * (3f + (seed % 5)) + p;
                    var cos = (float)Math.Cos(rot);
                    var sin = (float)Math.Sin(rot);
                    var arm1 = s * 1.5f;
                    drawList.AddLine(
                        new Vector2(pos.X - cos * arm1, pos.Y - sin * arm1),
                        new Vector2(pos.X + cos * arm1, pos.Y + sin * arm1), gold, 1.5f);
                    drawList.AddLine(
                        new Vector2(pos.X + sin * arm1, pos.Y - cos * arm1),
                        new Vector2(pos.X - sin * arm1, pos.Y + cos * arm1), gold, 1.5f);
                    drawList.AddCircleFilled(pos, s * 0.6f, white);
                }
            }

            if (Config.WindowPos.X >= 0 && Config.WindowPos.Y >= 0)
                ImGui.SetWindowPos(Config.WindowPos, ImGuiCond.Once);

            Config.WindowPos = ImGui.GetWindowPos();

            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
            ImGui.GetFont().Scale = oldScale;
            ImGui.PopFont();
        }
        catch { }
    }
}
