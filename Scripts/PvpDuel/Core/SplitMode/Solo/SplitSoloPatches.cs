using HarmonyLib;

using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.SplitMode;

/// <summary>
/// 把「开始一局联机局」换成「各自开始单机局」。
/// </summary>
/// <remarks>
/// 调用方（角色选择界面）在await之后会自己 <c>CleanUpLobby(disconnectSession: false)</c>，
/// 所以连接不会被断——那正是我们要的：大厅没了、Session 还在。
/// </remarks>
[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewMultiplayerRun))]
internal static class SplitSoloLobbyPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        StartRunLobby lobby,
        IReadOnlyList<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers,
        string seed,
        int ascensionLevel,
        ref Task<RunState> __result)
    {
        if (!SplitSoloRun.CanIntercept(lobby))
        {
            return true;
        }

        __result = SplitSoloRun.StartInsteadOfMultiplayer(lobby, acts, modifiers, seed, ascensionLevel);

        return false;
    }
}
