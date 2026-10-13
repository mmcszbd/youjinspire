// ============================================================================
//「建筑师的挑拨」：每回合出牌上限（设置项，默认无上限）
// ----------------------------------------------------------------------------
// 统计：本场战斗里每个玩家"本回合已出牌数"，回合开始清零。
// 限制：卡牌的 CanPlay 判定被我们否决 —— UI 会变灰、真的也打不出。
//   用的是原版现成的 UnplayableReason.BlockedByHook（正好是"被钩子挡下"的语义）。
// 只在「建筑师的挑拨」战斗里生效；其它时候一个字都不动。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace YouJin.Scripts;

/// <summary>建筑师的挑拨：每回合出牌上限的计数器与判定。</summary>
internal static class ArchitectDuelRules
{
    private static readonly Dictionary<ulong, int> PlayedThisTurn = new();

    /// <summary>回合开始：把这个玩家的计数清零。</summary>
    public static void ResetForPlayer(Player? player)
    {
        if (player is null)
            return;

        PlayedThisTurn.Remove(player.NetId);
    }

    /// <summary>战斗结束/清场：全清。</summary>
    public static void ResetAll() => PlayedThisTurn.Clear();

    /// <summary>打出一张牌：计数 +1。</summary>
    public static void OnCardPlayed(Player? player)
    {
        if (player is null)
            return;

        PlayedThisTurn.TryGetValue(player.NetId, out var played);
        PlayedThisTurn[player.NetId] = played + 1;
    }

    /// <summary>这个玩家本回合还能不能再出牌（不在那场战斗 / 无上限 → 永远 true）。</summary>
    public static bool CanPlayMore(Player? player)
    {
        if (player is null || !ArchitectBetrayalEvent.BetrayalCombatActive)
            return true;

        var limit = YouJinSettings.EffectiveCardPlayLimit;
        if (limit <= 0)
            return true;

        PlayedThisTurn.TryGetValue(player.NetId, out var played);
        return played < limit;
    }
}

/// <summary>到上限就把牌挡下来（无参数的那个 CanPlay）。</summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.CanPlay), new Type[0])]
internal static class ArchitectCardLimitPatch
{
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        try
        {
            if (__result && !ArchitectDuelRules.CanPlayMore(__instance.Owner))
                __result = false;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：出牌上限判定失败（按原版放行）{ex}");
        }
    }
}

/// <summary>到上限就把牌挡下来（带 out 原因的那个 CanPlay —— UI 主要走这条）。</summary>
[HarmonyPatch]
internal static class ArchitectCardLimitReasonPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(
            typeof(CardModel),
            nameof(CardModel.CanPlay),
            new[]
            {
                typeof(UnplayableReason).MakeByRefType(),
                typeof(AbstractModel).MakeByRefType(),
            });

    private static void Postfix(CardModel __instance, ref bool __result, ref UnplayableReason __1)
    {
        try
        {
            if (!__result || ArchitectDuelRules.CanPlayMore(__instance.Owner))
                return;

            __result = false;
            __1 = UnplayableReason.BlockedByHook;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：出牌上限判定失败（按原版放行）{ex}");
        }
    }
}
