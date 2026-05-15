using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YABOT.Features.OccultCrescent;

public unsafe class AutoInviteOccult : BaseFeature
{
    public override string Name => "Auto-Invite in Occult Crescent";

    public override string Description => "Automatically invites players who send a message containing \"lfg\" or \"lfp\" in any chat channel while in Occult Crescent, if you are party leader and the party is not full.";

    public override FeatureType FeatureType => FeatureType.OccultCrescent;

    public override bool UseAutoConfig => false;

    private readonly HashSet<string> recentInvites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Random Rng = new();

    public override void Enable()
    {
        recentInvites.Clear();
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        base.Enable();
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        recentInvites.Clear();
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        if (!ZoneHelper.IsOccultCrescent()) return;

        var text = chatMessage.Message.TextValue;
        if (!ContainsLfg(text)) return;

        var playerPayload = chatMessage.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (playerPayload == null) return;

        var playerName = playerPayload.PlayerName;
        var worldId = playerPayload.World.RowId;

        if (Svc.Objects.LocalPlayer != null
            && playerName == Svc.Objects.LocalPlayer.Name.TextValue
            && worldId == Svc.Objects.LocalPlayer.HomeWorld.RowId)
            return;

        if (!CanInvite()) return;

        var inviteKey = $"{playerName}@{worldId}";
        if (recentInvites.Contains(inviteKey)) return;

        ulong contentId = 0;
        try
        {
            var logModule = RaptureLogModule.Instance();
            if (logModule != null)
                contentId = logModule->AddonMessageSub3488.ContentId;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[AutoInviteOccult] AddonMessageSub read failed");
        }

        if (contentId == 0) return;

        var capturedName = playerName;
        var capturedKey = inviteKey;
        var capturedContentId = contentId;
        var delayMs = 1000 + Rng.Next(1000);
        TaskManager.EnqueueDelay(delayMs);
        TaskManager.Enqueue(() =>
        {
            try
            {
                if (!recentInvites.Add(capturedKey)) return true;

                var proxy = InfoProxyPartyInvite.Instance();
                proxy->InviteToPartyInInstanceByContentId(capturedContentId);
                Svc.Chat.Print($"[YABOT] Invited {capturedName}");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[AutoInviteOccult] Invite failed");
            }
            return true;
        });
    }

    private static bool ContainsLfg(string text)
    {
        return text.Contains("lfg", StringComparison.OrdinalIgnoreCase)
            || text.Contains("lfp", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanInvite()
    {
        try
        {
            if (Svc.Objects.LocalPlayer == null) return false;

            var group = GroupManager.Instance()->GetGroup();
            if (group == null) return false;

            if (group->MemberCount == 0) return false;
            if (group->MemberCount >= 8) return false;

            return group->IsEntityIdPartyLeader(Svc.Objects.LocalPlayer.EntityId);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[AutoInviteOccult] CanInvite check failed");
            return false;
        }
    }

    public override void Disable()
    {
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        recentInvites.Clear();
        base.Disable();
    }
}
