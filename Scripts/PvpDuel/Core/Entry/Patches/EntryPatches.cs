using HarmonyLib;

using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using PvpDuel.Core.Duel;
using YouJin.Scripts;

namespace PvpDuel.Core.Entry;


/// <summary>吞掉决斗事件选项在「角色变量注入」那一步抛出的异常。</summary>
/// <remarks>
/// 本体 EventOption 构造时会调 AddLocVars：<c>eventModel.Owner?.Character.AddDetailsTo(Description)</c> 在我们的
/// 路径上会 NRE（Character 为 null）。关键点是 Description 在传参时已求值（异常发生时文案已就绪），
/// 后面那句只是往里面塞一个 {IsMultiplayer} 变量，我们的文案不用它，所以吞掉即可。
/// 只吞我方事件的异常，其它事件、其它异常一律原样抛回。
/// </remarks>
[HarmonyPatch(typeof(EventOption), "AddLocVars")]
internal static class DuelOptionLocVarsPatch
{
    [HarmonyFinalizer]
    private static Exception? Finalizer(EventModel __0, Exception? __exception)
    {
        if (__exception is null || __0 is not BattleBattleBattleEvent)
        {
            return __exception;
        }

        ModInfo.Warn(
            "已吞掉决斗事件选项的角色变量注入异常（文案已就绪，不影响显示）："
            + $"{__exception.GetType().Name}: {__exception.Message}");

        return null;
    }
}

