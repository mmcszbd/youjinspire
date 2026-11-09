// ============================================================================
// 友尽尖塔（youjin）：手速模式的「无木桩形态」
// ----------------------------------------------------------------------------
// 背景：以前手速模式必须摆一只「结实的卵」当木桩，因为没有木桩就活不下去：
//   ① `CombatManager.IsCombatEnding` 的判据是「敌方侧还有活着的单位」，
//      敌方为空 = 第一帧就判「敌人全灭」→ 战斗刚开就打完了；
//   ② `ShouldAllowHitting` / `HittableEnemies` 那套「不可打击」需要有一个具体的靶子。
//
// 回合制（PVP 决斗）早就证明了另一条路能走通，它就是三条一起做：
//   · 怪物一律不进战斗（`DuelStripMonstersPatch`）
//   · 把「敌人全灭」这条判定压住（`DuelKeepCombatAlivePatch`）
//   · 自己记账「轮到谁」（`DuelSession`）
// 本文件把前两条搬过来给手速模式用 —— 手速模式本来就有「同时在出牌」的那本账，
// 所以缺的刚好就是这两条，搬完木桩就不是必需品了。
//
// 搬完之后场上只有玩家：
//   · 目标能不能点 → 仍然是 `TeammateTargetingPatch` / `TeammateUiTargetingPatch` /
//     `TeammateAoeTargetingPatch` 那三处（它们只看 `Creature.Side` 和静态开关，与木桩无关）
//   · 站位 → 仍然是 `TeammateEnemySidePatch`（纯视觉镜像，不碰 `Creature.Side`）
//   · 收场 → 只剩 1 名玩家时把判定放开，让本体照常走「战斗结束 → 回到事件」那条路
//
// ⚠ 开关：`YouJinSettings.SpeedModeDummyEnabled` 为 true 时本文件整套让路，
//    切回 0.10.6 之前那套「有木桩」的旧形态（布场在 ArchitectBetrayalCombatModel 里）。
// ============================================================================

using System;
using System.Linq;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

using HarmonyLib;

namespace YouJin.Scripts;

/// <summary>「这场 PvP 不摆木桩」的判据 —— 补丁里只认这一处，别再各写一遍。</summary>
internal static class PvpBareCombat
{
    /// <summary>
    /// 这场战斗是不是「无木桩的手速 PvP」。
    /// </summary>
    /// <remarks>
    /// 四个条件缺一不可，其中两个是硬约束：
    ///   · <c>PvpSpeedModeEnabled</c> —— 只有手速模式走这条；回合制那边有自己的拦怪补丁，别抢它的活；
    ///   · <c>!SpeedModeDummyEnabled</c> —— 开关打开时整套让路，退回旧形态；
    ///   · <c>PvpCombat.IsActive</c> —— 确实是「建筑师的挑拨」这一场（开关 或 遭遇类型，任一成立）；
    ///   · <b>正好两人</b> —— 单人/三人局没有木桩会卡死战斗（没人能死到「只剩 1 人」），
    ///     所以这里直接判死，宁可退回有木桩的形态也不能让一场战斗永远结束不了。
    /// </remarks>
    public static bool Active(ICombatState? state)
    {
        if (state is null || state.Players.Count != 2)
            return false;

        if (!YouJinSettings.PvpSpeedModeEnabled || YouJinSettings.SpeedModeDummyEnabled)
            return false;

        return PvpCombat.IsActive(state);
    }

    /// <summary>场上还活着几名玩家（无木桩形态的胜负判据就是这个）。</summary>
    public static int AlivePlayerCount(ICombatState? state)
    {
        if (state is null)
            return 0;

        return state.PlayerCreatures.Count(creature => creature.IsAlive);
    }
}

/// <summary>无木桩形态：怪物一律不进战斗（照抄决斗那三条理由）。</summary>
/// <remarks>
/// `AddCreature` 是怪物进入战斗的唯一入口，在这里拦下比「先放进来再摘掉」干净
/// （后者会牵动意图、AI 一堆初始化）。
/// ⚠ 玩家自己的宠物 / 召唤物必须放行：它们随后走 `CombatManager.AddCreature`，
///   而那个方法要求「CombatState 里已经有这个 creature」，被拦掉会在 `BeforeCombatStart`
///   抛 `InvalidOperationException`，整个回合循环直接死掉（表现：战斗卡死 + "turn loop died … stuck"）。
/// </remarks>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.AddCreature))]
internal static class BareCombatStripMonstersPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CombatState __instance, Creature __0)
    {
        try
        {
            if (__0 is null || __0.IsPlayer || __0.PetOwner is not null)
                return true;

            if (!PvpBareCombat.Active(__instance))
                return true;

            Entry.Logger.Info($"手速模式（无木桩）：拦下怪物 {__0.Monster?.Id.Entry ?? "?"} 不进战斗");
            return false;
        }
        catch (Exception ex)
        {
            // 判据出问题就照原版放行 —— 宁可多一只怪，也不能把战斗初始化拦断。
            Entry.Logger.Info($"手速模式（无木桩）：拦怪判据出错，放行 {ex.Message}");
            return true;
        }
    }
}

/// <summary>无木桩形态：敌方侧本来就是空的，胜负判定要自己接管。</summary>
/// <remarks>
/// 两段逻辑，一进一出：
///   · 还有 2 名玩家活着 → 把「敌人全灭」压成 false（否则开局即结算）；
///   · 只剩 1 名（或 0 名）→ 放开让本体照常收场，`EnterCombatWithoutExitingEvent(..., shouldResumeAfterCombat: true)`
///     会把流程交回事件，往下走原版建筑师演出。
/// 压胜负判定是很重的动作，所以前面必须已经过 `PvpBareCombat.Active` 那套「正好两人」的闸门。
/// </remarks>
[HarmonyPatch(typeof(CombatManager), "IsCombatEnding")]
internal static class BareCombatKeepAlivePatch
{
    /// <summary>「压住结束判定」这条日志只打一次（它每帧都会被问）。</summary>
    private static bool _loggedKeepAlive;

    [HarmonyPostfix]
    private static void Postfix(ref bool __result)
    {
        try
        {
            var combat = ArchitectBetrayalCombatModel.CurrentStateForLog;

            if (!PvpBareCombat.Active(combat))
                return;

            var alive = PvpBareCombat.AlivePlayerCount(combat);

            if (alive > 1)
            {
                // 这条判定每帧都会被问，所以日志只打第一次。
                if (__result && !_loggedKeepAlive)
                {
                    _loggedKeepAlive = true;
                    Entry.Logger.Info("手速模式（无木桩）：敌方侧没有单位，但还有 2 人活着 → 不结束战斗");
                }

                __result = false;
                return;
            }

            // 只剩一人：让本体收场。原来那套是「把木桩打飞」来达成同一个效果。
            if (!__result)
            {
                Entry.Logger.Info($"手速模式（无木桩）：只剩 {alive} 名玩家 → 结束战斗");
                __result = true;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"手速模式（无木桩）：胜负判定接管失败（按原版继续）{ex.Message}");
        }
    }
}
