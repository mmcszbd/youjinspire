using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using PvpDuel.Core;
using PvpDuel.Core.Duel;

namespace YouJin.Scripts;

/// <summary>
/// 测试工具：跳房间 + 秒赢。
/// </summary>
/// <remarks>
/// <para>
/// 等价于游戏自带控制台的两条命令，但不用记命令、不用开控制台：
/// </para>
/// <list type="bullet">
/// <item><c>room &lt;ID&gt;</c> → <c>RunManager.Instance.EnterRoomDebug(RoomType)</c>（本体
/// <c>RoomConsoleCmd.Process</c> 就是这一句）。</item>
/// <item><c>win</c> → 把 <c>DebugOnlyGetState().Enemies</c> 逐个 <c>CreatureCmd.Kill</c>，
/// 再 <c>CombatManager.Instance.CheckWinCondition()</c>（本体 <c>WinConsoleCmd.KillEnemies</c> 的同款流程）。</item>
/// </list>
/// <para>
/// <b>只给测试用</b>：秒赢一律跳过 PvP 战斗 —— 决斗/擂台赛的胜负是靠 <c>DuelSession</c> 的
/// 判负与保活补丁算出来的，外面再塞一次「敌人全死」会把那一套判据打穿。
/// </para>
/// </remarks>
internal static class TestTools
{
    /// <summary>跳到指定类型的房间（等价控制台 <c>room &lt;ID&gt;</c>）。</summary>
    public static void Jump(RoomType type)
    {
        try
        {
            var run = RunManager.Instance;
            if (run is null || !run.IsInProgress)
            {
                ModInfo.Warn($"[测试工具] 当前不在跑局里，忽略跳转 {type}");
                return;
            }

            ModInfo.Info($"[测试工具] 跳房间：{type}");
            TaskHelper.RunSafely(run.EnterRoomDebug(type));
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[测试工具] 跳转 {type} 失败：{ex}");
        }
    }

    /// <summary>当前战斗直接判胜（等价控制台 <c>win</c>）。PvP 战斗直接跳过。</summary>
    public static void WinCombat()
    {
        try
        {
            var combat = CombatManager.Instance;
            if (combat is null || !combat.IsInProgress)
            {
                ModInfo.Warn("[测试工具] 当前不在战斗里，忽略秒赢");
                return;
            }

            if (DuelSession.InDuel)
            {
                ModInfo.Warn("[测试工具] 当前是 PvP 决斗，秒赢会让胜负判据失真，已跳过");
                return;
            }

            var enemies = combat.DebugOnlyGetState()?.Enemies?.ToList() ?? [];
            if (enemies.Count == 0)
            {
                ModInfo.Warn("[测试工具] 这一场没有敌人（手速模式的木桩局？），忽略秒赢");
                return;
            }

            ModInfo.Info($"[测试工具] 秒赢：击杀 {enemies.Count} 个敌人");
            TaskHelper.RunSafely(KillEnemiesThenCheck(enemies));
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[测试工具] 秒赢失败：{ex}");
        }
    }

    /// <summary>照抄 <c>WinConsoleCmd.KillEnemies</c>：先清能力再 Kill，最后让战斗自己收尾。</summary>
    private static async Task KillEnemiesThenCheck(List<Creature> enemies)
    {
        foreach (var creature in enemies)
        {
            creature.RemoveAllPowersInternalExcept();
            await CreatureCmd.Kill(creature);
        }

        await CombatManager.Instance.CheckWinCondition();
    }
}
