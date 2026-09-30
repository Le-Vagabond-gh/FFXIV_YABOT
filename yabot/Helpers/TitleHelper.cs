using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace YABOT.Helpers;

public static unsafe class TitleHelper
{
    // False until the server has sent the title list; unlock checks are meaningless before that.
    // Asks for the list on the way so a later call has it.
    public static bool UnlocksKnown()
    {
        var ui = UIState.Instance();
        if (ui == null) return false;
        if (ui->TitleList.DataReceived) return true;
        if (!ui->TitleList.DataRequested) ui->TitleList.RequestTitleList();
        return false;
    }

    public static bool IsUnlocked(int titleId)
    {
        if (titleId == 0) return true;
        var ui = UIState.Instance();
        return ui != null && ui->TitleList.IsTitleUnlocked((ushort)titleId);
    }

    public static string TitleName(int titleId)
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

    public static void Announce(int titleId) => Svc.Chat.Print($"[YABOT] Title set to {TitleName(titleId)}.");

    public enum ApplyResult { Applied, AlreadyActive, NotUnlocked, NotReady }

    // Sends the title change unless it's already worn or not owned. The unlock check is only
    // trustworthy once the server has sent the title list; before that, trust the id.
    public static ApplyResult Apply(int titleId)
    {
        var player = Svc.Objects.LocalPlayer;
        var ui = UIState.Instance();
        if (player == null || ui == null) return ApplyResult.NotReady;

        if (titleId > 0 && ui->TitleList.DataReceived && !ui->TitleList.IsTitleUnlocked((ushort)titleId))
            return ApplyResult.NotUnlocked;

        if (((Character*)player.Address)->CharacterData.TitleId == titleId) return ApplyResult.AlreadyActive;

        ui->TitleController.SendTitleIdUpdate((ushort)titleId);
        return ApplyResult.Applied;
    }
}
