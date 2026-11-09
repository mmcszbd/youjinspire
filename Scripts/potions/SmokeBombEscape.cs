// ============================================================================
// 友尽尖塔（youjin）：烟雾弹的「脱战但不死」
// ----------------------------------------------------------------------------
// 用户要求（原话）：「使用后会直接自杀」「立刻脱战，但不死」「只能对自己使用」。
// 旧实现是 CreatureCmd.Kill(自己, force:true)，等于真死 —— 观感就是自杀。
//
// 为什么不直接用原版的 CreatureCmd.Escape：
//   · Escape 会走 CombatState.CreatureEscaped → RemoveCreature(creature, true)，
//     把生物从 _allies 里删掉、CombatState 置 null、清空所有能力、拆掉节点；
//   · 之后 CombatState.Players 里就没这个人了，而
//     CombatRoom.OfferRoomEndRewards 是 foreach (CombatState.Players) 发奖励的
//     → 脱战的人拿不到任何奖励；顺带还丢了所有能力。太脏。
//
// 本方案（补丁只挂一个属性 getter）：
//   跑路的人记进 HashTable<Creature> Escaped / Escaped 集合；
//   Harmony 后置补丁 Creature.get_IsAlive：
//     在集合里 且 CombatManager.Instance.IsInProgress 时返回 false。
//   Creature.get_IsDead = !IsAlive、get_IsHittable = !IsDead && Hook.ShouldAllowHitting(...)
//   都是从 IsAlive 派生的，所以自动获得原版「死人」的全部行为，而且是原版验证过的形状
//   （原版多人里死掉的玩家生物同样留在 _allies 里，从来没被移除过）：
//     · CombatManager.StartTurn 里 `if (player.Creature.IsDead)` → SetReadyToEndTurn(player,false,null)
//       → 每次回合开始自动置为「已结束回合」，不会卡住回合循环；
//     · IsHittable=false → 敌人不会再打他，卡牌也不能指定他；
//     · 血量和遗物/能量都保留（没走死亡钩子：不分钱、不触发遗物死亡效果）；
//     · 生物仍在 _allies → 仍在 CombatState.Players → 战斗结束时仍在
//       OfferRoomEndRewards 的遍历里。
//   战斗结束（IsInProgress=false）时 IsAlive 自动恢复原值（CurrentHp > 0），
//   于是 RewardsSet.Offer 开头那句 `if (Player.Creature.IsDead) return;` 不会拦他，
//   奖励照常发、战后回血类效果也照常生效 —— 这就是「脱战但不死」。
//   另外订阅 CombatManager.CombatEnded 清空集合，避免玩家生物跨战斗复用时被误判。
//
// 使用者自己这一回合要收尾（仿 CreatureCmd.Kill 里 HandlePlayerDeath 的善后，见 SmokeBombPotion.OnUse）：
//   收手牌、能量/星星清零；如果正轮到玩家出牌（CurrentSide == Player，枚举里 Player=1），
//   还要 PlayerCmd.EndTurn(player, false, null) —— 否则结束回合按钮被
//   NEndTurnButton.PlayerCanTakeAction（判 IsAlive）禁用，AllPlayersReadyToEndTurn 永远等不到他 → 卡死。
//   这正是原版 Kill 的处理方式（<Kill>d__14 偏移 01E2~0241），是已经验证过的调用场景。
// ============================================================================

using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;

namespace YouJin.Scripts;

/// <summary>补丁安装入口（Entry.Init 调一次）。</summary>
internal static class SmokeBombEscapePatches
{
    public static void Apply()
    {
        try
        {
            var harmony = new Harmony("youjin.smokebomb");
            harmony.CreateClassProcessor(typeof(CreatureIsAliveSmokeBombPatch)).Patch();
            Entry.Logger.Info("烟雾弹：脱战补丁安装完成（Creature.get_IsAlive 后置）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：脱战补丁安装失败 {ex}");
        }
    }
}

/// <summary>哪些生物被烟雾弹「脱战」了（只在当前这场战斗内有效）。</summary>
public static class SmokeBombEscape
{
    private static readonly HashSet<Creature> Escaped = new();

    /// <summary>订阅过 CombatEnded 的那个 CombatManager（单例，理论上只有一个）。</summary>
    private static CombatManager? _subscribedTo;

    /// <summary>热路径用：为 0 时补丁直接返回，零开销。</summary>
    public static int Count => Escaped.Count;

    public static bool IsEscaped(Creature? creature)
        => creature is not null && Escaped.Count > 0 && Escaped.Contains(creature);

    private static bool _forcedDeadLogged;

    /// <summary>第一次真的把某人骗成「倒下」时打一行日志（证明脱战状态确实生效了）。</summary>
    public static void NoteForcedDead(Creature creature)
    {
        if (_forcedDeadLogged)
            return;

        _forcedDeadLogged = true;
        Entry.Logger.Info(
            $"烟雾弹：已把 NetId={(creature.Player?.NetId.ToString() ?? "?")} 判定为「倒下」"
            + "（IsAlive=false）—— 之后不再被指定、不能出牌、敌人不会打他；血量保留。");
    }

    public static void MarkEscaped(Creature creature)
    {
        try
        {
            Prune();
            EnsureSubscribed();
            Escaped.Add(creature);
            Entry.Logger.Info($"烟雾弹：标记脱战（本场共 {Escaped.Count} 人）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：标记脱战失败 {ex}");
        }
    }

    /// <summary>清空标记（战斗结束 / 读档 / 异常兜底）。</summary>
    public static void Clear(string reason)
    {
        try
        {
            _forcedDeadLogged = false;

            if (Escaped.Count == 0)
                return;

            Entry.Logger.Info($"烟雾弹：清理脱战标记 {Escaped.Count} 个（{reason}）");
            Escaped.Clear();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：清理脱战标记失败 {ex}");
        }
    }

    /// <summary>还有别的活着的队友吗（单人 / 队友全倒 → false）。</summary>
    public static bool HasLivingTeammate(Player? player)
    {
        try
        {
            var run = player?.RunState;
            if (run is null || run.Players.Count <= 1)
                return false;

            foreach (var mate in run.Players)
            {
                if (mate is null || mate.NetId == player!.NetId)
                    continue;

                var creature = mate.Creature;
                // IsAlive 已被本补丁穿透：已经脱战的队友在这里算「不活」
                if (creature is not null && creature.IsAlive)
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：判断队友失败 {ex}");
            return false;
        }
    }

    /// <summary>药水弹窗「使用」按钮是否点亮（PassesCustomUsabilityCheck 唯一读取点）。</summary>
    public static bool CanUseNow(Player? player)
    {
        try
        {
            if (player?.Creature is null)
            {
                LogUseGate(false, "Owner/Creature 为空（弹窗拿到的可能不是可变实例）");
                return false;
            }

            if (!YouJinSettings.GameplayEnabled)
            {
                LogUseGate(false, "派对模式总开关（贪婪）关着");
                return false;
            }

            var combat = CombatManager.Instance;
            if (combat is null || !combat.IsInProgress)
            {
                LogUseGate(false, "不在战斗中");
                return false;
            }

            if (!player.Creature.IsAlive)
            {
                LogUseGate(false, "自己已经倒下/脱战");
                return false;
            }

            var ok = HasLivingTeammate(player);
            LogUseGate(ok, ok ? "有活着的队友" : "没有活着的队友（单人局或队友全倒）");
            return ok;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：判断能否使用失败 {ex}");
            return false;
        }
    }

    /// <summary>「使用」按钮的判定结果打一行日志（每次弹窗一次，量很小）。</summary>
    private static void LogUseGate(bool allowed, string reason)
        => Entry.Logger.Info($"烟雾弹：可用性判定 = {(allowed ? "可点" : "变灰")}（{reason}）");

    /// <summary>剔除已经不在战斗里的旧引用（玩家生物可能跨战斗复用，防止误判）。</summary>
    private static void Prune()
    {
        if (Escaped.Count == 0)
            return;

        Escaped.RemoveWhere(creature => creature is null || creature.CombatState is null);
    }

    private static void EnsureSubscribed()
    {
        var combat = CombatManager.Instance;
        if (combat is null || ReferenceEquals(combat, _subscribedTo))
            return;

        if (_subscribedTo is not null)
            _subscribedTo.CombatEnded -= OnCombatEnded;

        combat.CombatEnded += OnCombatEnded;
        _subscribedTo = combat;
    }

    private static void OnCombatEnded(CombatRoom _)
        => Clear("战斗结束");
}

/// <summary>
/// 核心补丁：脱战期间把 IsAlive 骗成 false（IsDead / IsHittable 都由它派生）。
/// 只在战斗进行中生效，战斗一结束就恢复，所以奖励照发。
/// </summary>
[HarmonyPatch(typeof(Creature), nameof(Creature.IsAlive), MethodType.Getter)]
internal static class CreatureIsAliveSmokeBombPatch
{
    private static void Postfix(Creature __instance, ref bool __result)
    {
        if (!__result)
            return; // 本来就死了（或已经脱战）：保持原版结果

        if (SmokeBombEscape.Count == 0)
            return; // 没人脱战：热路径直接返回

        if (!SmokeBombEscape.IsEscaped(__instance))
            return;

        var combat = CombatManager.Instance;
        if (combat is null || !combat.IsInProgress)
            return; // 战斗结束 → 恢复「活着」，奖励正常结算

        SmokeBombEscape.NoteForcedDead(__instance);
        __result = false;
    }
}
