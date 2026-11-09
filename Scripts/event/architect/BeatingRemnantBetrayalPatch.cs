// ============================================================================
// 「建筑师的挑拨」：遗物「律动残余」（BeatingRemnant）的失血上限
// ----------------------------------------------------------------------------
// 原版效果（反编译确认，sts2.dll）：
//   MegaCrit.Sts2.Core.Models.Relics.BeatingRemnant
//     · BeforeSideTurnStart        轮到持有者时把 DamageReceivedThisTurn 清零（按回合重置）
//     · ModifyHpLostAfterOsty      持有者将要失去生命时：
//           Math.Min(损失量, DynamicVars["MaxHpLoss"].BaseValue - DamageReceivedThisTurn)
//       也就是「**每回合**最多失去 MaxHpLoss 点生命」——超出部分直接砍掉。
//
// 我们要的：**只在「建筑师的挑拨」那场战斗里**把这个上限改成 100；
//   其他时候（普通战斗、别的事件、别的地方）一个字都不动。
//
// 判定条件刻意和原版一致（只对遗物持有者本人），数值直接用 100 重算，
// 不依赖这件遗物原本的 MaxHpLoss 是多少（第一次生效时会把原值打进日志）。
// ============================================================================

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Relics;

namespace YouJin.Scripts;

/// <summary>建筑师的挑拨：把「律动残余」的每回合失血上限改成 100。</summary>
[HarmonyPatch(typeof(BeatingRemnant), nameof(BeatingRemnant.ModifyHpLostAfterOsty))]
internal static class BeatingRemnantBetrayalPatch
{
    /// <summary>这场战斗里的每回合失血上限。</summary>
    private const decimal BetrayalMaxHpLoss = 100m;

    private static bool _loggedOnce;

    // 参数用位置写法 __0/__1（不依赖原方法参数名）：
    //   __0 = creature（要失去生命的那只）
    //   __1 = amount  （原本要失去多少）
    private static void Postfix(BeatingRemnant __instance, Creature __0, decimal __1, ref decimal __result)
    {
        try
        {
            if (__instance is null)
                return;

            // 只有「建筑师的挑拨」那场战斗才改；其他时候保持原版。
            if (!PvpCombat.FlagActive)
                return;

            // 和原版同样的限定：只对遗物持有者本人生效。
            var owner = __instance.Owner;
            if (owner?.Creature is null || !ReferenceEquals(__0, owner.Creature))
                return;

            // 本回合已经受过多少伤（原版私有属性，用 Traverse 读）。
            var received = Traverse.Create(__instance)
                .Property("DamageReceivedThisTurn")
                .GetValue<decimal>();

            var capped = Math.Min(__1, BetrayalMaxHpLoss - received);
            if (capped < 0m)
                capped = 0m;

            if (!_loggedOnce)
            {
                _loggedOnce = true;

                decimal original;
                try
                {
                    original = __instance.DynamicVars["MaxHpLoss"].BaseValue;
                }
                catch
                {
                    original = -1m;
                }

                Entry.Logger.Info(
                    $"建筑师的挑拨：律动残余 每回合失血上限 {original} → {BetrayalMaxHpLoss}"
                    + "（只在这场战斗里生效）");
            }

            __result = capped;
        }
        catch (Exception ex)
        {
            // 兜底：判不出来就按原版放行，绝不把战斗弄坏。
            Entry.Logger.Info($"建筑师的挑拨：律动残余限伤失败（按原版放行）{ex}");
        }
    }
}
