using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

using PvpDuel.Core.Entry;
using YouJin.Scripts;

namespace PvpDuel.Core.Duel;

/// <summary>
/// 一场决斗的会话：标记、轮流行动这本账、回合上限、以及结束结算。
/// </summary>
/// <remarks>
/// <para>
/// 生命周期（由补丁在对应时机驱动，本类不自己去挂钩子）：
/// 事件里 <see cref="Arm" /> → 战斗初始化 <see cref="Begin" /> → 每个玩家回合开始
/// <see cref="BeginActorTurn" /> → 每个回合 <see cref="TryFinishByRoundLimit" /> →
/// 分出胜负 <see cref="Finish" />。
/// </para>
/// <para>
/// 之所以把状态集中在这里：决胜条件分散时最难查的就是"某个标记没清"。现在一场战斗的全部运行时状态
/// （是否决斗、轮到谁、谁行动过、是否已结算）只在这一个对象里，读代码时不用去追四个静态类。
/// </para>
/// </remarks>
internal static class DuelSession
{
    private static readonly HashSet<Player> Acted = [];

    /// <summary>
    /// 擂台赛「这一轮实际参战的两位」。空 = 没设过，按默认口径走（<c>CombatState.Players</c> 全员）。
    /// </summary>
    /// <remarks>
    /// 四人局只有一个 <c>CombatState</c>，四个人都在里面。擂台赛让其中两位上场、其余观战，
    /// 所以「谁在打」必须和「谁在场」分开记 —— 这个集合就是前者。观战者仍然留在场上
    /// （不摘 <c>Creature</c>，摘了会把 <c>Players</c>/<c>Creatures</c> 的索引和存档搞乱），
    /// 只是不参与轮转、不能出牌、不会被选成目标。
    /// </remarks>
    private static readonly List<Player> ParticipantList = [];

    private static CombatState? _begun;
    private static int _actorIndex;

    /// <summary>下一场战斗是决斗（事件里点了「接受对决」）。</summary>
    public static bool Armed { get; private set; }

    /// <summary>这一场被判定为决斗（存档里的原始结论，不受收尾状态影响）。</summary>
    private static bool _inDuel;

    /// <summary>当前这场战斗是决斗。</summary>
    /// <remarks>
    /// <para>
    /// <b>收尾期间恒为 false</b>：本体放弃一局靠 <c>RunManager.GuaranteeKillAllPlayers()</c> 把人杀光来触发结算，
    /// 决斗补丁要是继续掌权，这场战斗就结束不了 ——
    /// <see cref="DuelKeepCombatAlivePatch" /> 会把 <c>IsCombatEnding</c> 强改回 false（人死了也不收尾），
    /// <c>EndRound</c> / 后手保护限伤又会把该掉的伤害拦掉。
    /// 日志实证（2026-11-04 13:10 godot.log）：`Abandoning an in-progress run` 之后紧跟着
    /// `后手保护（律动残余）：拦下一次掉血 64 → 0`、`Player 1 died`，然后战斗继续跑
    /// （`Combat state becomes EndTurnPhaseOne`），run 既不结束也退不出去；
    /// 玩家第二次点放弃时连接已经断了，`RunAbandonedMessage ... NetHostGameService is not connected!` 抛异常 → 彻底卡死。
    /// </para>
    /// <para>
    /// 一处收口比在每个补丁里各判一次稳：所有补丁（保活、判负、拦怪、限伤、轮转、HUD）都读这里。
    /// </para>
    /// </remarks>
    public static bool InDuel => _inDuel && !IsRunEnding;

    /// <summary>本局正在收尾（放弃、强制结算、退出、或者已经不在局里）。</summary>
    /// <remarks>
    /// 放弃一局时 <c>IsInProgress</c> 仍为 true 而 <c>IsAbandoned</c> 已经置上；
    /// 保存退出走 <c>IsCleaningUp</c>；结算完成之后 <c>IsInProgress</c> 才变 false。三个都要认。
    /// </remarks>
    public static bool IsRunEnding
    {
        get
        {
            var runner = RunManager.Instance;

            return runner is null
                   || runner.IsAbandoned
                   || runner.IsCleaningUp
                   || runner.IsGameOver
                   || !runner.IsInProgress;
        }
    }

    /// <summary>当前这场决斗（供回合上限等检查用）。</summary>
    public static CombatState? Combat { get; private set; }

    /// <summary>本回合该谁行动。</summary>
    public static Player? Actor { get; private set; }

    /// <summary>
    /// 「律动残余」保护对象：**先手玩家第 1 个回合**内，这一位一点血都不会掉（格挡照常被打掉）。
    /// </summary>
    /// <remarks>
    /// 先手 = <c>Players[0]</c>、后手 = <c>Players[1]</c> —— 和「对手固定是 Players[1]」同一套口径，
    /// 不按本机视角判断，两端算出同一个答案（见 §4.1.1）。
    /// 窗口就是「先手那条 BeginActorTurn」到「下一次 BeginActorTurn（后手）」之间，回合结束自然解除。
    /// </remarks>
    public static Player? RhythmGuardedPlayer { get; private set; }

    /// <summary>现在是否处在「后手保护」窗口里。</summary>
    public static bool RhythmGuardActive => RhythmGuardedPlayer is not null;

    /// <summary>
    /// 擂台赛的<b>这一轮</b>已经分出胜负。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只管「本轮打完了、该回事件页投票」这一件事。擂台赛要循环打多轮，
    /// 一轮结束不能把整局收掉。
    /// </para>
    /// <para>
    /// 置上之后 <see cref="DuelKeepCombatAlivePatch" /> 就不再压 <c>IsCombatEnding</c>，
    /// 本体于是走它自己的胜利结算（<c>EndCombatInternal</c>）：复活所有玩家、标记房间完成、
    /// 因为事件是 <c>shouldResumeAfterCombat: true</c> 开的，会自动回到事件页。
    /// </para>
    /// </remarks>
    public static bool RoundOver { get; private set; }

    /// <summary>本轮的输家（整局流程里也可能是「我方」）。</summary>
    public static Player? RoundLoser { get; private set; }

    /// <summary>这一轮实际参战的两位（擂台赛才会被显式设定）。</summary>
    public static IReadOnlyList<Player> Participants => ParticipantList;

    /// <summary>
    /// 这场战斗该按哪几位玩家来轮转：擂台赛设过参战者就用那个，否则退回全员。
    /// </summary>
    public static IReadOnlyList<Player> ParticipantsOf(ICombatState? state)
    {
        if (ParticipantList.Count == 2)
        {
            return ParticipantList;
        }

        return state is null ? [] : state.Players;
    }

    /// <summary>擂台赛开局前设定参战者（必须在开战之前调）。</summary>
    public static void SetParticipants(IReadOnlyList<Player>? players)
    {
        ParticipantList.Clear();

        if (players is not { Count: 2 })
        {
            ModInfo.Info("擂台赛：没有指定参战者 → 按默认口径（全员）轮转");
            return;
        }

        ParticipantList.AddRange(players);
        ModInfo.Info($"擂台赛参战者：netId={players[0].NetId} vs netId={players[1].NetId}");
    }

    /// <summary>这一位是不是参战者（没设过参战者时全员都算）。</summary>
    public static bool IsParticipant(Player? player)
    {
        if (player is null)
        {
            return false;
        }

        return ParticipantList.Count == 0 || ParticipantList.Contains(player);
    }

    /// <summary>这一位是不是观战者（设过参战者、且不在其中）。</summary>
    public static bool IsSpectator(Player? player)
    {
        return player is not null && ParticipantList.Count == 2 && !ParticipantList.Contains(player);
    }

    /// <summary>事件里登记「下一场是决斗」（只对下一场有效）。</summary>
    public static void Arm()
    {
        Armed = true;
        ModInfo.Info("已登记：下一场战斗为决斗");
    }

    /// <summary>战斗初始化：消费标记，并重置这一场的账（同一场重复调用无副作用）。</summary>
    public static void Begin(CombatState state)
    {
        Combat = state;

        if (ReferenceEquals(_begun, state))
        {
            return;
        }

        _begun = state;
        _actorIndex = 0;
        Acted.Clear();
        DuelBalance.Reset();
        Actor = null;
        RhythmGuardedPlayer = null;
        RoundOver = false;
        RoundLoser = null;

        // 读档 / 重连之后 Armed 是内存态、已经丢了，但"这一场是不是决斗"能从房间上读出来：
        // 事件开战会把事件 id 记进 CombatRoom.ParentEventId，而那个字段是跟着存档走的。
        _inDuel = Armed || StartedFromDuelEvent();
        Armed = false;

        ModInfo.Info(
            $"新的战斗：allies={state.Allies.Count} enemies={state.Enemies.Count} "
            + $"players={state.Players.Count} → {(InDuel ? "决斗" : "普通战斗（不做任何改造）")}");
    }

    /// <summary>这一场战斗是不是由「对决邀请」开出来的（存档 / 重连恢复时用）。</summary>
    private static bool StartedFromDuelEvent()
    {
        return RunManager.Instance.DebugOnlyGetState()?.CurrentRoom
               is CombatRoom { ParentEventId: { } parentEventId }
               && parentEventId == ModelDb.Event<BattleBattleBattleEvent>().Id;
    }

    /// <summary>我方回合开始：轮到下一位行动者。</summary>
    public static void BeginActorTurn(ICombatState state)
    {
        // 擂台赛只在参战者里轮；没设过参战者时 ParticipantsOf 退回全员（普通两人决斗）。
        var players = ParticipantsOf(state);

        if (players.Count == 0)
        {
            return;
        }

        var isFirstTurn = _actorIndex == 0;

        Actor = players[_actorIndex % players.Count];
        _actorIndex++;
        Acted.Add(Actor);

        // ★ 后手保护（「律动残余」）：**先手的第 1 个回合**里，后手一点血都不掉。
        //   先手 = players[0]、后手 = players[1]，不按本机视角判断，两端算出同一个答案。
        //   窗口只覆盖「先手这个回合」，下一次 BeginActorTurn（轮到后手）自动关掉。
        //   注意：拦的是「掉血」不是「伤害」—— 伤害照常打掉格挡，只是血条不动（见 DuelRhythmGuardPatches）。
        if (isFirstTurn && players.Count == 2)
        {
            RhythmGuardedPlayer = players[1];
            ModInfo.Info(
                $"后手保护（律动残余）：先手 netId={players[0].NetId} 的第 1 回合内，"
                + $"后手 netId={players[1].NetId} 不会掉血（格挡照常被打掉）");
        }
        else
        {
            RhythmGuardedPlayer = null;

            if (isFirstTurn)
            {
                ModInfo.Info($"后手保护：本场 {players.Count} 人，不是两人决斗 → 不启用");
            }
        }

        ModInfo.Info($"本回合行动者：netId={Actor.NetId}");
    }

    // 决斗只打到有人倒下：0.10.8 起没有回合上限，也不再做「打满 N 回合按血量判」。

    /// <summary>
    /// 本轮分出胜负：只收这一轮，<b>不结束整局</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里不做任何收场动作 —— 只置 <see cref="RoundOver" />，然后让本体自己走完
    /// <c>EndCombatInternal</c>（复活、标记房间、存档、回事件页）。谁赢谁输由调用方记分。
    /// </para>
    /// <para>
    /// 观战者<b>不</b>参与裁定：他没上场，本轮结果跟他无关，但他所在的那台机器同样要靠
    /// <see cref="RoundOver" /> 放开 <c>IsCombatEnding</c>，否则他那边战斗永远结束不了。
    /// 所以这个方法是「所有端都调」，只是没人被判定为输家。
    /// </para>
    /// </remarks>
    public static void EndRound(Player? loser)
    {
        if (RoundOver)
        {
            return;
        }

        RoundOver = true;
        RoundLoser = loser;

        ModInfo.Info(
            loser is null
                ? "擂台赛：本轮结束（没有人被判负）"
                : $"擂台赛：本轮结束，输家 netId={loser.NetId}");
    }

}
