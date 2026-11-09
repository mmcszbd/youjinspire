using HarmonyLib;

using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.SplitMode;

/// <summary>本机打完最后一幕（三幕）时置标志、抓快照，并把这一局留在等待房里等对面。</summary>
/// <remarks>
/// <para>
/// 挂点选在 <c>RunManager.EnterNextAct</c>：本体的流程是「BOSS 结算完 → <c>EnterNextAct</c>」，
/// 而它内部第一件事就是判 <c>CurrentActIndex &gt;= Acts.Count - 1</c>（已经是最后一幕）
/// —— 那正是「三幕打完」的那一刻，再往后就进「建筑师」事件房走结局了。
/// </para>
/// <para>
/// 分离模式是「各自单机爬到三幕 → 都打完再合流」，所以这里必须<b>拦下</b>原版流程：
/// 不然先打完的一方直接进建筑师事件、本局收掉，合流窗口只剩几十毫秒。
/// 拦下来之后换成 <see cref="WaitingForPeersEvent"/>（一场 99999 血木桩的遭遇战），
/// 人留在局里等着，对面一打完就由合流那条线接管。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterNextAct))]
internal static class SplitLastActPatch
{
    [HarmonyPrefix]
    private static bool Prefix(RunManager __instance)
    {
        if (!ModuleSwitches.SplitMode)
        {
            return true;
        }

        try
        {
            var state = __instance.DebugOnlyGetState();

            if (state is null || state.CurrentActIndex < state.Acts.Count - 1)
            {
                return true;
            }

            SplitRuntime.NoteLastActFinished();

            // 此刻 RunState 还在，立刻把手里的玩家数据抓一份存着：
            // 再往后会进「建筑师」事件、玩家点结束本局，那时候 ToSave 已经拿不到东西了。
            SplitMeeting.CaptureLocalSnapshot();

            // 只进一次等待房：之后再触发（比如合流失败、玩家自己再爬一遍）就放行原版流程。
            if (WaitingForPeersEvent.Entered)
            {
                return true;
            }

            WaitingForPeersEvent.MarkEntered();

            ModInfo.Info("[SplitMode] 三层打完 → 先进等待房打木桩，等对面也打完");

            TaskHelper.RunSafely(__instance.EnterRoom(new EventRoom(ModelDb.Event<WaitingForPeersEvent>())));

            // 拦掉原版 EnterNextAct：否则一口气进建筑师事件、本局当场收掉，
            // 先打完的那一方连个等的地方都没有。
            return false;
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[SplitMode] 判定「三幕打完」出错（忽略，放行原版）：{ex.Message}");
            return true;
        }
    }
}

/// <summary>新一局开始时清掉「已打完」标志。</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
internal static class SplitProgressResetPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        SplitRuntime.ResetProgress();
        WaitingForPeersEvent.Reset();
    }
}
