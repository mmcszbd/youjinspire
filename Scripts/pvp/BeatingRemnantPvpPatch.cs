// ============================================================================
// 所有 PvP 战斗：遗物「律动残余」（BeatingRemnant）的每回合失血上限
// ----------------------------------------------------------------------------
// 原版效果（反编译确认，sts2.dll）：
//   MegaCrit.Sts2.Core.Models.Relics.BeatingRemnant
//     · BeforeSideTurnStart        轮到持有者时把 DamageReceivedThisTurn 清零（按回合重置）
//     · ModifyHpLostAfterOsty      持有者将要失去生命时：
//           Math.Min(损失量, DynamicVars["MaxHpLoss"].BaseValue - DamageReceivedThisTurn)
//       也就是「**每回合**最多失去 MaxHpLoss 点生命」（原版 20），超出部分直接砍掉。
//
// 我们要的：**任何一场 PvP 战斗里**都把这个上限改成 100；其它战斗一个字都不动。
//   这里的 PvP 战斗有两种，判据都收在下面 `InPvpCombat` 里：
//     · 「建筑师的挑拨」—— `PvpCombat.IsActive(state)`（静态开关 或 遭遇类型兜底）
//     · 「对决邀请」开的回合制决斗 —— `DuelSession.InDuel`
//
// 判定条件仍然和原版一致（只对遗物持有者本人），数值直接用 100 重算，
// 不依赖这件遗物原本的 MaxHpLoss 是多少（第一次生效时会把原值打进日志）。
//
// 历史：这个补丁原先叫 BeatingRemnantBetrayalPatch，只认
//   `PvpCombat.FlagActive`（建筑师的挑拨）；2026-11-13 按需求放宽到所有 PvP 战斗。
// ============================================================================

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Relics;
using PvpDuel.Core.Duel;

namespace YouJin.Scripts;

/// <summary>PvP 战斗：把「律动残余」的每回合失血上限改成 100。</summary>
[HarmonyPatch(typeof(BeatingRemnant), nameof(BeatingRemnant.ModifyHpLostAfterOsty))]
internal static class BeatingRemnantPvpPatch
{
    /// <summary>PvP 战斗里的每回合失血上限。</summary>
    private const decimal PvpMaxHpLoss = 100m;

    /// <summary>整局只打一次日志，免得每回合刷屏。</summary>
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

            // 只有 PvP 战斗才改；普通战斗、别的事件保持原版。
            if (!InPvpCombat(__0?.CombatState))
                return;

            // 和原版同样的限定：只对遗物持有者本人生效。
            var owner = __instance.Owner;
            if (owner?.Creature is null || !ReferenceEquals(__0, owner.Creature))
                return;

            // 本回合已经受过多少伤（原版私有属性，用 Traverse 读）。
            var received = Traverse.Create(__instance)
                .Property("DamageReceivedThisTurn")
                .GetValue<decimal>();

            var capped = Math.Min(__1, PvpMaxHpLoss - received);
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
                    $"PvP：律动残余 每回合失血上限 {original} → {PvpMaxHpLoss}"
                    + "（所有 PvP 战斗都生效）");
            }

            __result = capped;
        }
        catch (Exception ex)
        {
            // 兜底：判不出来就按原版放行，绝不把战斗弄坏。
            Entry.Logger.Info($"PvP：律动残余限伤失败（按原版放行）{ex}");
        }
    }

    /// <summary>
    /// 这场战斗算不算 PvP：建筑师的挑拨（开关或遭遇判据）或回合制决斗。
    /// </summary>
    private static bool InPvpCombat(ICombatState? state)
        => PvpCombat.IsActive(state) || DuelSession.InDuel;
}
