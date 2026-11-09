// ============================================================================
// 友尽尖塔（youjin）：「建筑师的挑拨」判定门面（结构收口，不含任何玩法数值）
// ----------------------------------------------------------------------------
// 这个文件只做一件事：把 PvP 里那些**散落各处、被反复手抄**的判据收到一个地方。
//
// 改造前，同一套判据在三个文件里各写了一遍：
//   · ArchitectBetrayalCombatModel.AfterDeath / ShouldAllowHitting
//       写的是 `BetrayalCombatActive || IsBetrayalEncounter(creature.CombatState)`
//   · BetrayalCombatView.IsMyTeam / IsEnemyOfMe
//       写的是 `creature.Player ?? creature.PetOwner` 与"有主单位算对手、无主怪按原版"
//   · ArchitectDuelRules.CanPlayMore
//       写的是 `!ArchitectBetrayalEvent.BetrayalCombatActive`
//
// 抄三遍的问题不是丑，是**改一处漏两处**：上次修「木桩能不能被打」就差点只改到其中一处。
// 现在统一走这里：
//   · IsActive(state)              —— "这场是不是我们的 PvP"
//   · TeamOf / IsMyTeam / IsEnemyOfMe —— "谁是队友、谁是对手"（队伍口径）
//   · Report(reason)               —— 一条结构化诊断日志（排查时不用再猜场上有什么）
//
// 注意：这里的判据只是**转发**，语义与改造前逐字一致，没有新增/收紧任何条件。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace YouJin.Scripts;

/// <summary>「建筑师的挑拨」的判定门面。</summary>
internal static class PvpCombat
{
    // ------------------------------------------------------------------
    // ① 这是不是我们这场 PvP
    // ------------------------------------------------------------------

    /// <summary>
    /// 这场战斗是不是「建筑师的挑拨」。
    /// </summary>
    /// <remarks>
    /// 两个判据"或"起来，和改造前完全一致：
    ///   · <c>BetrayalCombatActive</c> —— 静态开关。玩家选战斗那一刻就置 true，
    ///     因为「队友搬到对面」「木桩挪最右」这些补丁跑在布场最开始，比 BeforeCombatStart 还早。
    ///   · <c>IsBetrayalEncounter(state)</c> —— 按遭遇类型判，是自愈用的兜底
    ///     （开关残留 / 时序边界时靠它认出来）。
    /// </remarks>
    public static bool IsActive(ICombatState? state)
    {
        if (state is null)
            return false;

        return ArchitectBetrayalEvent.BetrayalCombatActive
               || ArchitectBetrayalEvent.IsBetrayalEncounter(state);
    }

    /// <summary>
    /// 只看静态开关（不含遭遇类型兜底）。
    /// </summary>
    /// <remarks>
    /// 有几处补丁**故意**只认这一条：它们必须严格跟着"事件流程走到哪"走，
    /// 用遭遇类型兜底会把判定放宽（例：出牌计数、失血上限）。
    /// 收在这里只是为了让"我知道这里只认开关"这件事在代码里看得见，不是漏了兜底。
    /// </remarks>
    public static bool FlagActive => ArchitectBetrayalEvent.BetrayalCombatActive;

    /// <summary>这个生物所在的战斗是不是我们这场 PvP。</summary>
    public static bool IsActiveFor(Creature? creature) => IsActive(creature?.CombatState);

    // ------------------------------------------------------------------
    // ② 队伍口径：谁是队友、谁是对手
    // ------------------------------------------------------------------

    /// <summary>
    /// 这个生物"属于谁"：玩家本体 → 他自己；宠物 → 它的主人；无主怪（木桩/原版敌人）→ null。
    /// </summary>
    /// <remarks>
    /// `Creature.PetOwner` 是原版给召唤物留的字段。PvP 里它决定"这只算谁的队伍"，
    /// 所以 TeamOf 是整个队伍口径的唯一入口 —— 不要再各自写 `Player ?? PetOwner`。
    /// </remarks>
    public static Player? TeamOf(Creature? creature) => creature?.Player ?? creature?.PetOwner;

    /// <summary>从 <paramref name="me" /> 的视角看，这只算不算自己人。</summary>
    public static bool IsMyTeam(Creature? creature, Player? me)
        => me is not null && ReferenceEquals(TeamOf(creature), me);

    /// <summary>
    /// 从 <paramref name="me" /> 的视角看，这只算不算敌人：
    /// 别的玩家（含他的宠物）算；无主怪按原版那侧算（<c>Side == Enemy</c>）。
    /// </summary>
    /// <remarks>
    /// 无主怪那一支必须保留原版判定：木桩是 `CombatSide.Enemy`，
    /// 但它在 PvP 里既不是任何人的队友、也不该被当成"对手"去打（另有 ShouldAllowHitting 挡着）。
    /// </remarks>
    public static bool IsEnemyOfMe(Creature? creature, Player? me)
    {
        if (creature is null)
            return false;

        var team = TeamOf(creature);

        if (team is not null)
            return !ReferenceEquals(team, me);

        return creature.Side == CombatSide.Enemy;
    }

    // ------------------------------------------------------------------
    // ③ 诊断
    // ------------------------------------------------------------------

    /// <summary>
    /// 打一条结构化的 PvP 现场快照。
    /// </summary>
    /// <remarks>
    /// 排查 PvP 问题（黑屏 / 打不到人 / 战斗收不掉）时，最费时间的是"先确认场上到底有什么"。
    /// 这一条把开关、换过视图的单位数、玩家血量、无主怪血量一次打全，日志里直接能对。
    /// 只读，不改任何状态。
    /// </remarks>
    public static void Report(string reason, ICombatState? state = null)
    {
        try
        {
            var combat = state ?? ArchitectBetrayalCombatModel.CurrentStateForLog;

            var players = new List<string>();
            var dummies = new List<string>();

            if (combat is not null)
            {
                foreach (var creature in combat.Creatures)
                {
                    if (creature is null)
                        continue;

                    var team = TeamOf(creature);

                    if (team is not null)
                    {
                        players.Add(
                            $"netId={team.NetId} hp={creature.CurrentHp}/{creature.MaxHp}"
                            + (ReferenceEquals(creature.Player, team) ? "" : "(宠物)"));
                    }
                    else
                    {
                        dummies.Add(
                            $"{creature.Monster?.GetType().Name ?? "?"} hp={creature.CurrentHp}/{creature.MaxHp}"
                            + $" hittable={creature.IsHittable}");
                    }
                }
            }

            Entry.Logger.Info(
                $"建筑师的挑拨［结构快照·{reason}］：开关={ArchitectBetrayalEvent.BetrayalCombatActive}"
                + $" 遭遇判据={ArchitectBetrayalEvent.IsBetrayalEncounter(combat)}"
                + $" 视图={BetrayalCombatView.WrappedCount}"
                + $" 回合={combat?.RoundNumber.ToString() ?? "-"}"
                + $" 玩家=[{(players.Count == 0 ? "无" : string.Join("、", players))}]"
                + $" 无主怪=[{(dummies.Count == 0 ? "无" : string.Join("、", dummies))}]");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：结构快照失败（不影响战斗）{ex}");
        }
    }
}
