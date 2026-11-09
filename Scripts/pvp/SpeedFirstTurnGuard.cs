// ============================================================================
// 手速模式：开局保护 —— 前 N 回合（设置可调 0~3）双方都不掉血
// ----------------------------------------------------------------------------
// 要的效果：手速模式（实时 PvP）里，**所有玩家都还没结束第 N 回合**时，
// 双方都不掉血（格挡照常被打掉）。N 由设置页的「开局保护」滑条决定
// （0~3，默认 1；0 = 不保护）。参考回合制 PvP 的「后手保护（律动残余）」
// （`DuelRhythmGuardModel`），但保护窗口是「所有玩家都还没结束第 N 回合」，
// 不是「先手第 1 回合」。
//
// 实现：
//   · `SpeedTurnTracker`：静态类，`Dictionary<Player, int> TurnsByPlayer`，
//     追踪每个玩家的回合数（`PlayerCmd.EndTurn` 的 postfix 里 +1）。
//   · `SpeedTurnEndPatch`：`HarmonyPatch` 挂 `PlayerCmd.EndTurn` 的 postfix，
//     手速模式激活时 `NoteTurnEnded(player)`。
//   · `SpeedFirstTurnGuardModel`：`HookedSingletonModel`，覆写
//     `ModifyHpLostAfterOsty`，手速模式激活 + 保护窗口 > 0 → 返回 0。
//
// 保护窗口：`AllPlayersBeforeTurn(ICombatState, int turns)` = 所有玩家的回合数都 < N
// （即所有玩家都还没结束第 N 回合；N = 设置里的「开局保护」值）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>
/// 手速模式回合追踪：记录每个玩家结束回合的次数。
/// </summary>
internal static class SpeedTurnTracker
{
    private static readonly Dictionary<Player, int> TurnsByPlayer = new();

    /// <summary>重置追踪（战斗开始时调用）。</summary>
    public static void Reset()
    {
        TurnsByPlayer.Clear();
    }

    /// <summary>记录某个玩家结束了一次回合。</summary>
    public static void NoteTurnEnded(Player player)
    {
        if (player is null)
        {
            return;
        }

        TurnsByPlayer[player] = TurnsByPlayer.GetValueOrDefault(player) + 1;
    }

    /// <summary>
    /// 判断是不是所有玩家都还没结束第 <paramref name="turns" /> 回合（回合数都 &lt; N）。
    /// </summary>
    /// <remarks><paramref name="turns" /> &lt;= 0 恒返回 <c>false</c>，等于关掉保护。</remarks>
    public static bool AllPlayersBeforeTurn(ICombatState state, int turns)
    {
        if (state is null || turns <= 0)
        {
            return false;
        }

        var players = state.Players;
        if (players is null || players.Count == 0)
        {
            return false;
        }

        // 2026-11-06：窗口从写死的 2 改成设置里选的值（0~3，默认 1）。
        return players.All(p => TurnsByPlayer.GetValueOrDefault(p) < turns);
    }
}

/// <summary>
/// 补丁安装入口。
/// </summary>
internal static class SpeedFirstTurnGuardPatches
{
    /// <summary>在 mod 初始化时调用一次。</summary>
    public static void Apply()
    {
        try
        {
            var harmony = new Harmony("youjin.speedfirstturnguard");
            harmony.CreateClassProcessor(typeof(SpeedTurnEndPatch)).Patch();
            harmony.CreateClassProcessor(typeof(SpeedTurnResetPatch)).Patch();
            Entry.Logger.Info("手速模式第一轮保护：补丁安装完成");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"手速模式第一轮保护：补丁安装失败 {ex}");
        }
    }
}

/// <summary>
/// Harmony 补丁：挂 `PlayerCmd.EndTurn` 的 postfix，手速模式激活时追踪回合数。
/// </summary>
[HarmonyPatch]
internal static class SpeedTurnEndPatch
{
    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.EndTurn))]
    [HarmonyPostfix]
    private static void AfterEndTurn(Player player)
    {
        try
        {
            if (player is null)
            {
                return;
            }

            // 只在手速模式激活时追踪。
            var state = player.Creature?.CombatState;
            if (!PvpBareCombat.Active(state))
            {
                return;
            }

            SpeedTurnTracker.NoteTurnEnded(player);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"手速模式第一轮保护：追踪回合数失败 {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony 补丁：挂 `CombatState.AddCreature` 的 prefix，手速模式激活时重置回合追踪。
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.AddCreature))]
internal static class SpeedTurnResetPatch
{
    [HarmonyPrefix]
    private static void Prefix(CombatState __instance)
    {
        try
        {
            if (PvpBareCombat.Active(__instance))
            {
                SpeedTurnTracker.Reset();
            }
        }
        catch
        {
            // 静默失败：重置不了就按旧数据走，不影响战斗。
        }
    }
}

/// <summary>
/// 手速模式第一轮保护：所有玩家都还没结束第二个回合时，双方都不掉血。
/// </summary>
[RegisterSingleton]
public sealed class SpeedFirstTurnGuardModel : HookedSingletonModel
{
    public SpeedFirstTurnGuardModel() : base(HookType.Combat)
    {
    }

    /// <summary>
    /// 改动「即将失去的生命」。返回 0 = 这次完全不失血（格挡已经在之前结算掉了）。
    /// </summary>
    public override decimal ModifyHpLostAfterOsty(
        Creature target,
        decimal amount,
        ValueProp props,
        Creature dealer,
        CardModel cardSource)
    {
        try
        {
            if (amount <= 0m)
            {
                return amount;
            }

            // 保护窗口 = 设置里选的前 N 回合；0 = 不保护。
            var guardTurns = YouJinSettings.SpeedFirstTurnGuardTurns;
            if (guardTurns <= 0)
            {
                return amount;
            }

            var state = target?.CombatState;
            if (!PvpBareCombat.Active(state))
            {
                return amount;
            }

            // 保护窗口：所有玩家都还没结束第 guardTurns 回合。
            if (!SpeedTurnTracker.AllPlayersBeforeTurn(state, guardTurns))
            {
                return amount;
            }

            // 自己伤自己（自伤卡、自身 debuff）放行。
            if (dealer is not null && ReferenceEquals(dealer, target))
            {
                return amount;
            }

            Entry.Logger.Info(
                $"手速模式开局保护（前 {guardTurns} 回合）：把一次失血压成 0（原本 {amount}，"
                + $"目标 netId={target?.Player?.NetId}，"
                + $"出手者 {(dealer?.Player is { } d ? $"netId={d.NetId}" : "无（状态/环境伤害）")}"
                + "；格挡仍在之前照常结算）");

            return 0m;
        }
        catch (Exception ex)
        {
            // 兜底：判不出来就按原版失血，绝不因为一个保护把战斗弄坏。
            Entry.Logger.Info($"手速模式第一轮保护（钩子）判定失败（按原版失血）：{ex.Message}");
            return amount;
        }
    }
}
