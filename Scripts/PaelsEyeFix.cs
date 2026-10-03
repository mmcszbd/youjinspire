// ============================================================================
// 友尽尖塔（youjin）：佩尔之眼（Relics.PaelsEye）修复
// ----------------------------------------------------------------------------
// 症状：佩尔之眼「每场战斗第一次空过回合 → 消耗手牌 + 一个额外回合」
//       变成了**每回合都触发** —— 日志里连续十几行
//       "Player ... (IRONCLAD) is taking an extra turn"，无限续回合。
//
// 根因（反编译 + 日志确认）：
//   · 原版逻辑就两句（sts2.dll）：
//         ShouldTakeExtraTurn(player)
//             => !UsedThisCombat && !AnyCardsPlayedThisTurn()
//                && WasOwnerPartOfLastPlayerTurn && player == Owner;
//         AfterTakingExtraTurn(player)
//             { if (player != Owner) return; Flash(); Status = 0; UsedThisCombat = true; }
//     —— 唯一的"本场已用"写入点就是 AfterTakingExtraTurn。
//   · Watcher 这个 mod（Watcher.dll → Watcher.Code.Patches.PaelsEyeSourceTrackingPatches）
//     给 AfterTakingExtraTurn 挂了一个"闸门"前缀：
//         if (player != relic.Owner) return true;        // 放行
//         if (SelfTriggered.Remove(relic)) return true;  // 放过一次就放行原版
//         __result = Task.CompletedTask; return false;   // 否则跳过原版
//     它配了一个 RecordVote(relic, player, voted) 用来往 SelfTriggered 里登记，
//     但整个 Watcher.dll 里**搜不到任何地方调用 RecordVote**（IL 引用为 0）——
//     SelfTriggered 永远是空集合 → 闸门永远走最后那条分支 → 原版被跳过 →
//     UsedThisCombat 永远是 false → 每回合都满足"第一次空过" → 无限额外回合。
//
// 修法：我们自己给 AfterTakingExtraTurn 挂一个**后置**补丁，把原版那句话补上。
//   关键点是"后置补丁在前缀返回 false（跳过原版）时照样会执行"，
//   所以不管 Watcher 的闸门放不放行，"本场已用"都会被置真，
//   行为回到原版：一场战斗只送一次额外回合。
//   闸门放行时原版自己会置真，我们这里会提前 return，不会重复闪光/重复置位。
// ============================================================================

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models.Relics;

namespace YouJin.Scripts;

/// <summary>补丁安装入口。</summary>
internal static class PaelsEyeFixPatches
{
    /// <summary>在 mod 初始化时调用一次。</summary>
    public static void Apply()
    {
        try
        {
            var harmony = new Harmony("youjin.paelseye");
            harmony.CreateClassProcessor(typeof(PaelsEyeConsumeFixPatch)).Patch();
            Entry.Logger.Info("佩尔之眼：修复补丁安装完成");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"佩尔之眼：修复补丁安装失败 {ex}");
        }
    }
}

/// <summary>把「本场已用过」补回去（见文件头）。</summary>
[HarmonyPatch(typeof(PaelsEye), nameof(PaelsEye.AfterTakingExtraTurn))]
internal static class PaelsEyeConsumeFixPatch
{
    /// <summary>`_usedThisCombat` 是 private 字段（属性 getter 也是 private）。</summary>
    private static readonly AccessTools.FieldRef<PaelsEye, bool> UsedThisCombatOf =
        AccessTools.FieldRefAccess<PaelsEye, bool>("_usedThisCombat");

    /// <summary>参数用 __0（位置参数），免得猜原方法参数叫什么。</summary>
    private static void Postfix(PaelsEye __instance, Player __0)
    {
        try
        {
            if (__instance is null || __0 is null)
                return;

            // 原版只认主人（别的玩家拿额外回合不该消耗这位的佩尔之眼）
            if (!ReferenceEquals(__0, __instance.Owner))
                return;

            // 原版已经置真（Watcher 的闸门放行的情况）—— 什么都不用做
            if (UsedThisCombatOf(__instance))
                return;

            // 照抄原版 AfterTakingExtraTurn 的收尾：闪一下、状态归零、标记"本场已用"
            __instance.Flash();
            __instance.Status = (RelicStatus)0;
            UsedThisCombatOf(__instance) = true;

            Entry.Logger.Info("佩尔之眼：补记「本场已用过」（否则会每回合都白送一个额外回合）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"佩尔之眼：补记「本场已用过」失败 {ex}");
        }
    }
}
