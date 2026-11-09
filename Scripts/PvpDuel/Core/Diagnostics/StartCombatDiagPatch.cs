using System.Threading.Tasks;

using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using PvpDuel.Core.Duel;

namespace PvpDuel.Core.Diagnostics;

/// <summary>诊断：把 <c>StartCombatInternal</c> 里被吞掉的异常完整打出来。</summary>
/// <remarks>
/// 本体启动战斗那条链路只打顶层栈（log 里只剩 at CombatManager.StartCombatInternal(...) 一行），
/// 看不出是哪一句炸的。这里包一层打印完整异常再原样抛回，不改变本体行为。定位完可以整体删掉。
/// </remarks>
[HarmonyPatch(typeof(CombatManager), "StartCombatInternal")]
internal static class DuelStartCombatDiagPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result)
    {
        if (!ModuleSwitches.Diagnostics || __result is null)
        {
            return;
        }

        __result = Wrap(__result);
    }

    private static async Task Wrap(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            // 战斗被取消（离房、退出、读档）是正常路径：本体回合循环收尾时一律以取消结束。
            if (ex is TaskCanceledException or OperationCanceledException)
            {
                throw;
            }

            ModInfo.Error($"战斗启动失败（完整异常）：{ex}");
            throw;
        }
    }
}
