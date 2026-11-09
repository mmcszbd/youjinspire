// ============================================================================
// 回合制 PVP：后手保护（「律动残余」）
// ----------------------------------------------------------------------------
// 要的效果：**先手玩家的第 1 个回合里，后手玩家一点血都不会掉** —— 手速模式里
// 先手白打一轮，回合制里先手本来就占先，这里给后手一个缓冲。
//
// 为什么拦 setter 而不是拦伤害（用户 2026-11-01 选定「只保血、不保格挡」）：
//   · 拦 `CreatureCmd.Damage` 是「伤害根本不发生」→ 连格挡都不掉，属于完全免疫；
//   · 拦 `Creature.set_CurrentHp` 才是「伤害照常结算、格挡照常被打掉，只是血条不动」——
//     这正是本体遗物「律动残余」（BeatingRemnant）的限伤语义，也是这次要的那一档。
//   `SharedVitals` 的注释里已经确认过：三条改血的路（受伤 LoseHpInternal / 治疗
//   SetCurrentHpInternal / 直接赋值）**全部汇聚到 set_CurrentHp**，所以挂这一个点就够。
//
// 判据全部来自 `DuelSession`（同一场决斗的两端算出同一个答案）：
//   · `DuelSession.InDuel`             —— 只在这场决斗里管
//   · `DuelSession.RhythmGuardActive`  —— 只在「先手第 1 回合」这个窗口里管
//   · `RhythmGuardedPlayer`            —— 只保护后手那一位本人
//
// 写回手法：`object[] __args` 拿到装箱后的新血量，判断是「往下掉」就把它换回当前血量。
//   这么做不依赖 `CurrentHp` 到底是 int 还是 decimal（写回去的是同一类型），
//   也不碰 `_hpDepth` 那种重入锁，最不容易把战斗搞坏。
//   拦截本身包在 try/catch 里：判不出来就放行，绝不因为一个保护把战斗弄崩。
// ============================================================================

using System;

using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Creatures;

namespace PvpDuel.Core.Duel;

/// <summary>
/// 后手保护（「律动残余」）：先手第 1 回合内，后手掉血一律被拦回原值。
/// </summary>
[HarmonyPatch]
internal static class DuelRhythmGuardPatch
{
    /// <summary>
    /// 挂点：`Creature.set_CurrentHp`（血量收口的唯一入口，见文件头）。
    /// 用 `AccessTools.Method` 而不是 `PropertySetter`：`SharedVitals` 已经验证过这个名字能取到。
    /// </summary>
    private static System.Reflection.MethodBase TargetMethod()
        => AccessTools.Method(typeof(Creature), "set_CurrentHp");

    [HarmonyPrefix]
    private static void Prefix(Creature __instance, object[] __args)
    {
        try
        {
            if (!ModuleSwitches.Core || !DuelSession.InDuel || !DuelSession.RhythmGuardActive)
            {
                return;
            }

            if (DuelSession.RhythmGuardedPlayer is not { } guarded)
            {
                return;
            }

            // 只认被保护的那一位本人（宠物 / 敌人不享受）。
            if (__instance.Player is not { } owner || !ReferenceEquals(owner, guarded))
            {
                return;
            }

            if (__args.Length == 0)
            {
                return;
            }

            var incoming = Convert.ToDecimal(__args[0]);
            var current = Convert.ToDecimal(__instance.CurrentHp);

            // 只有「往下掉」才拦；治疗、上限同步之类的照旧放行。
            if (incoming >= current)
            {
                return;
            }

            ModInfo.LogOnce(
                $"后手保护（律动残余）：拦下一次掉血 {current} → {incoming}"
                + "（先手第 1 回合内后手不掉血；伤害与格挡照常结算）");

            __args[0] = __instance.CurrentHp;
        }
        catch (Exception ex)
        {
            // 兜底：判不出来就按原版掉血，绝不把战斗弄坏。
            ModInfo.Warn($"后手保护判定失败（按原版掉血）：{ex.Message}");
        }
    }
}
