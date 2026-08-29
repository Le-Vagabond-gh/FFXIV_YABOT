using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;

namespace YABOT.Helpers;

public static class ConditionHelper
{
    /// <summary>True during cutscenes, where the game hides the HUD.</summary>
    public static bool IsInCutscene() =>
        Svc.Condition[ConditionFlag.WatchingCutscene]
        || Svc.Condition[ConditionFlag.WatchingCutscene78]
        || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    /// <summary>True while talking to an NPC / in a quest event, cutscene scenes included.</summary>
    public static bool IsInDialogue() =>
        Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
        || Svc.Condition[ConditionFlag.OccupiedInEvent]
        || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent];
}
