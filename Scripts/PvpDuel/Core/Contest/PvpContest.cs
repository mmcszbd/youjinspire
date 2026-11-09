using System;
using System.Collections.Generic;

using Godot;

using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

using PvpDuel.Core.Entry;
using YouJin.Scripts;
using YouJin.Scripts.Pvp;

namespace PvpDuel.Core.Contest;

/// <summary>
/// 擂台赛的驱动：轮次、收票、抽签、把结果交给事件去开战。
/// </summary>
/// <remarks>
/// <para>流程：进事件 → 每人各投一票（上台 / 观战 / 结束）→ 主机收齐 → 主机抽两位 → 广播 →
/// 全员按结果进同一场战斗（参战者由 <c>DuelSession.Participants</c> 指定，其余人冻结在场）→
/// 打完 <c>Resume</c> 回到事件页 → 下一轮。</para>
/// <para>
/// 抽签只在主机做：<c>RunState.Rng</c> 在四台机器上的调用次数不保证同步，各算各的会把
/// "谁上台"算成分叉。结果走 <see cref="PvpContestChannel" /> 广播，其余机器只照做。
/// </para>
/// </remarks>
internal static class PvpContest
{
    /// <summary>本机这一局里的<b>全部</b>事件实例。</summary>
    /// <remarks>
    /// 共享事件是按玩家槽位各建一份 <c>EventModel</c> 的（3 人局主机端就有 3 份、日志里
    /// 「事件已挂上」会打 3 次），而进战斗要的是<b>每一份</b>都用各自的 <c>Owner</c> 去
    /// <c>EventCombatSynchronizer</c> 填 ready。早先这里只有一个 <c>_event</c> 字段、
    /// 被后一份反复覆写，结果只有最后一份 ready → 主机永远凑不齐 3/3 →
    /// <c>EnterCombat</c> 不触发 → 三端卡在事件页（「进不去」）。
    /// </remarks>
    private static readonly List<BattleBattleBattleEvent> Events = [];

    private static int _round;

    private static bool _hooked;

    private static bool _submitted;

    /// <summary>当前这批实例属于第几轮（新一轮会整批清掉旧实例）。</summary>
    private static int _attachedRound;

    /// <summary>这一轮已经由某份实例的 Resume 推进过了。</summary>
    private static bool _resumeHandled;

    /// <summary>正在打擂台（事件已挂上、还没收场）。</summary>
    public static bool Active => Events.Count > 0;

    /// <summary>本机持有的事件实例份数（正常 = 本局玩家数；日志用）。</summary>
    public static int InstanceCount => Events.Count;

    /// <summary>第几轮（从 1 开始）。</summary>
    public static int Round => _round;

    /// <summary>本机这一轮是否已经投过票。</summary>
    public static bool Submitted => _submitted;

    /// <summary>事件第一次生成选项时挂上来（之后每一轮由 <see cref="NextRound" /> 推进）。</summary>
    /// <remarks>
    /// 同一台机器上会被调「玩家数」次（每份实例一次）。第 2 份起只是多一个要 ready 的槽位，
    /// 绝不能重置轮次和投票标志。
    /// </remarks>
    public static void Attach(BattleBattleBattleEvent invitation)
    {
        if (Events.Contains(invitation))
        {
            return;
        }

        // 事件房间重建后 EventModel 会重挂成**新对象**，上一轮那批实例留在列表里只会污染：
        // Events[0] 指向已销毁的房间 → IsPrimary 永远假 → 没人推进轮次 → 新一轮的票
        // 全被「这一轮已经投过了」吞掉（2026-11-04 18:52 日志：第二轮本机共 4 份、
        // 只出现「又挂上一份」、没有「开始第 2 轮投票」）。所以按轮次归属清一次。
        if (_attachedRound != _round)
        {
            Events.Clear();
        }

        if (_round == 0)
        {
            _round = 1;
        }

        _attachedRound = _round;
        Events.Add(invitation);

        if (Events.Count > 1)
        {
            ModInfo.Info(
                $"[擂台赛] 又挂上一份实例（本机共 {Events.Count} 份，Owner={invitation.Owner?.NetId}）");
            return;
        }

        _submitted = false;
        _resumeHandled = false;

        PvpContestChannel.Initialize();

        // 上一局收场之后又进来的迟到票还压在票箱里（那一瞬间玩家又点了一下选项），
        // 而收票只看张数、不看轮次 —— 不清掉的话这一局一进事件就"票齐了"，直接结算。
        var stale = PvpContestChannel.ClearVotes();

        if (stale > 0)
        {
            ModInfo.Info($"[擂台赛] 清掉上一局残留的票（{stale} 张），这一轮重新投");
        }

        Hook();

        ModInfo.Info($"[擂台赛] 事件已挂上，开始第 {_round} 轮投票");
    }

    /// <summary>一轮打完、本机每份实例都会 Resume 一次；轮次只推一次。</summary>
    /// <remarks>
    /// <para>
    /// 去重靠 <see cref="_resumeHandled" />：一场战斗结束后本机所有实例都会 Resume，
    /// 不能让 3 份实例各推一轮。
    /// </para>
    /// <para>
    /// 这个标记必须在<b>每一场开战前</b>清掉（<see cref="DispatchDraw" /> 里做），
    /// 不能只在 <see cref="Attach" /> 里清 —— 事件页重建的时机晚于战斗结束的 Resume，
    /// 否则第二场打完轮次就永久卡住。
    /// </para>
    /// </remarks>
    public static void OnInstanceResumed()
    {
        if (!Active || _resumeHandled)
        {
            return;
        }

        _resumeHandled = true;
        NextRound();
    }

    /// <summary>本机「推进轮次 / 做开战准备」只走一份实例，免得 3 份各推一次。</summary>
    public static bool IsPrimary(BattleBattleBattleEvent invitation)
        => Events.Count > 0 && ReferenceEquals(Events[0], invitation);

    /// <summary>打完一轮回到事件页：进入下一轮。</summary>
    public static void NextRound()
    {
        _round++;
        _submitted = false;

        ModInfo.Info($"[擂台赛] 打完一轮，开始第 {_round} 轮投票");
    }

    /// <summary>摘掉实例（不传 = 全清）。</summary>
    public static void Detach(BattleBattleBattleEvent? invitation = null)
    {
        if (invitation is null)
        {
            Events.Clear();
        }
        else
        {
            Events.Remove(invitation);
        }

        if (Events.Count == 0)
        {
            _submitted = false;
        }
    }

    /// <summary>本机点了一个选项：投票，并切到「等待其他人」页。</summary>
    public static void SubmitChoice(byte choice)
    {
        if (_submitted)
        {
            ModInfo.Info("[擂台赛] 这一轮已经投过了，忽略重复点击");
            return;
        }

        _submitted = true;
        PvpContestChannel.SendVote(RunManager.Instance?.NetService, _round, choice);
    }

    /// <summary>整场擂台赛结束后的清账。</summary>
    public static void Reset()
    {
        Events.Clear();
        _round = 0;
        _submitted = false;
        PvpContestChannel.Reset();
    }

    /// <summary>主线程每帧：主机收票 / 全员取抽签结果。</summary>
    public static void Pump()
    {
        if (Events.Count == 0)
        {
            return;
        }

        var run = RunManager.Instance;
        var transport = run?.NetService;

        if (run is null || transport is null)
        {
            return;
        }

        if (transport.Type == NetGameType.Host)
        {
            var expected = run.DebugOnlyGetState()?.Players.Count ?? 0;

            if (expected > 0 && PvpContestChannel.TryCollectVotes(_round, expected, out var votes))
            {
                Decide(transport, votes);
            }
        }

        if (!PvpContestChannel.TryTakeDraw(
                out var round,
                out var netIdA,
                out var netIdB,
                out var cancel,
                out var restart))
        {
            return;
        }

        if (round != _round)
        {
            // 上一轮的迟到包：丢掉，否则会把已经开打的一轮重新拉一遍。
            ModInfo.Info($"[擂台赛] 丢掉过期的抽签结果（第 {round} 轮，当前第 {_round} 轮）");
            return;
        }

        DispatchDraw(netIdA, netIdB, cancel, restart);
    }

    /// <summary>把抽签结果发到本机的每一份事件实例上。</summary>
    /// <remarks>
    /// <para>
    /// 准备动作（<c>SetParticipants</c> / <c>Plan.Apply</c>）只由第一份做一次；
    /// 之后每一份都走一遍 <c>EnterCombatWithoutExitingEvent</c> —— 这一步是「用我的 Owner 占我的槽位」，
    /// 少一份就少一个 ready，主机那边的 <c>EventCombatSynchronizer</c> 永远不会触发 <c>EnterCombat</c>。
    /// </para>
    /// <para>
    /// 三种结果：<paramref name="restart" /> = 本轮作废重投；<paramref name="cancel" /> = 全员选结束、结算整局；
    /// 都不是 = 正常开打。
    /// </para>
    /// </remarks>
    private static void DispatchDraw(ulong netIdA, ulong netIdB, bool cancel, bool restart)
    {
        var events = Events.ToArray();

        if (events.Length == 0)
        {
            return;
        }

        if (restart)
        {
            // 上台的人不够凑一桌，但没人全票选「结束」→ 原样再来一轮。
            // 这里不能 Reset()：Reset 会清掉事件实例表，之后就没人在泵投票了。
            NextRound();

            foreach (var instance in events)
            {
                instance.ReopenOptions();
            }

            ModInfo.Info($"[擂台赛] 第 {_round} 轮投票重新开始（上台人数不足 2 人）");
            return;
        }

        ResetEventCombatSynchronizer(events[0]);

        // 每打一场之前把「这次回来已经推进过轮次」的标记清掉。
        // 只在 Attach 里清是不够的：同一轮事件页重建的时机晚于战斗结束的 Resume，
        // 于是第二场打完的 Resume 会看到一个还开着的标记 → 轮次不推进 → 新一轮的票
        // 全被「这一轮已经投过了」吞掉（2026-11-04 19:10 日志：第 2 轮打完回满血后
        // 两端都点「结束」，只有两条「这一轮已经投过了」，事件不退）。
        _resumeHandled = false;

        var plan = events[0].PrepareDraw(netIdA, netIdB, cancel);

        if (plan is null)
        {
            if (cancel)
            {
                // 全员选结束 → 收场：进原版建筑师房间拿「真胜利」。
                // 每份实例各自标记「事件已结束」（不标的话同步器会警告 THE_ARCHITECT
                // 「not yet finished」），但换房间只让第一份做 —— 多份都做会把建筑师事件
                // 推好几遍（2026-11-04 19:12 日志：收了 3 次、VisualOnly 战斗房建了 2 次）。
                ModInfo.Info("[擂台赛] 收场 → 各实例收掉事件页，由本机第一份实例换建筑师房间");
                foreach (var instance in events)
                {
                    instance.MarkContestFinished();
                }

                TaskHelper.RunSafely(events[0].EnterFinaleAsync());
            }
            else
            {
                // PrepareDraw 内部出错：每份实例都把事件页收掉，不然会有人停在「等待其他人选择」。
                // 这种情况不能顺手把本局判赢。
                foreach (var instance in events)
                {
                    instance.FinishContest();
                }
            }

            Reset();
            return;
        }

        ModInfo.Info($"[擂台赛] 逐份 ready：本机 {events.Length} 份实例");

        foreach (var instance in events)
        {
            instance.ReadyPendingCombat(plan);
        }
    }

    /// <summary>清掉上一轮残留的战斗同步器状态，并按这份实例重建事件战斗布局。</summary>
    /// <remarks>
    /// <para>
    /// 本体里 <c>EventCombatSynchronizer.ResetState()</c> 只有一个调用点：
    /// <c>EventSynchronizer.BeforeExitingRoom()</c>，注释原文是
    /// 「Called before the room is exited to ensure combat synchronizer state is reset.
    /// If we end up supporting multiple event combats in a single event, this will likely have to be refactored.」
    /// </para>
    /// <para>
    /// 而擂台赛是「打完一轮回到<b>同一个</b>事件房间继续投票」，房间根本没退出过，
    /// 所以这个重置一次都不会发生 → <c>_states[slot]</c> 里还留着上一轮的 ready →
    /// 第二轮 <c>ReadyToEnterCombat</c> 直接抛
    /// 「Player X became ready ... but they are already set to ready ...!」，
    /// 主机凑不齐人，战斗开不起来（2026-11-04 12:35 的日志）。
    /// </para>
    /// <para>
    /// <c>ResetState()</c> 会连 <c>MutableEncounterForLayout</c> 一起清空，而开战与战斗视觉都读它，
    /// 所以紧接着必须 <c>GenerateInternalCombatStateIfNecessary</c> 把遭受与布局重建出来。
    /// </para>
    /// </remarks>
    private static void ResetEventCombatSynchronizer(BattleBattleBattleEvent anchor)
    {
        try
        {
            var synchronizer = RunManager.Instance?.EventSynchronizer;

            if (synchronizer is null)
            {
                ModInfo.Info("[擂台赛] 拿不到 EventSynchronizer，跳过战斗同步器重置");
                return;
            }

            synchronizer.BeforeExitingRoom();
            synchronizer.GenerateInternalCombatStateIfNecessary(anchor);
            ModInfo.Info("[擂台赛] 已重置事件战斗同步器（清掉上一轮 ready 槽位、重建遭遇布局）");
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[擂台赛] 重置战斗同步器失败（会影响第二轮开战）：{ex}");
        }
    }

    private static void Decide(INetGameService transport, Dictionary<ulong, byte> votes)
    {
        var fights = new List<ulong>();
        var ends = 0;

        foreach (var pair in votes)
        {
            if (pair.Value == PvpContestChannel.ChoiceFight)
            {
                fights.Add(pair.Key);
            }
            else if (pair.Value == PvpContestChannel.ChoiceEnd)
            {
                ends++;
            }
        }

        ModInfo.Info(
            $"[擂台赛] 第 {_round} 轮统计：上台 {fights.Count} 人、选结束 {ends} 人、"
            + $"观战 {votes.Count - fights.Count - ends} 人");

        if (fights.Count < 2)
        {
            // 全员选「结束」才是收场；否则（有人想打、或大家都只是观战）重开一轮再投。
            if (ends >= votes.Count)
            {
                ModInfo.Info($"[擂台赛] 全员选结束（{ends}/{votes.Count}）→ 收场");
                PvpContestChannel.BroadcastDraw(transport, _round, 0, 0, cancel: true);
                return;
            }

            ModInfo.Info(
                $"[擂台赛] 上台的人不到 2 个（{fights.Count}），且不是全员选结束（{ends}/{votes.Count}）"
                + " → 重开一轮投票");
            PvpContestChannel.BroadcastDraw(transport, _round, 0, 0, cancel: false, restart: true);
            return;
        }

        // 手速形态：报名的人全部上场混战，没有「抽两位」这一步 —— 广播哨兵 0（两格都是 0）。
        // 判据在事件那一侧：PrepareDrawCore 见到 speed 形态就不认 netId，直接全员开打。
        if (PvpCombatKit.FromSettings() == PvpCombatKind.Speed)
        {
            ModInfo.Info($"[擂台赛] 手速形态：{fights.Count} 人一起上（不抽签）");
            PvpContestChannel.BroadcastDraw(transport, _round, 0, 0, cancel: false);
            return;
        }

        // 洗牌取前两位。不用 RunState.Rng：结果要广播给所有人，跨机一致性由广播保证，
        // 反而是「四台机器各算一遍」才会分叉。
        var pool = fights.ToArray();
        var picked = new ulong[2];

        for (var i = 0; i < picked.Length; i++)
        {
            var index = Random.Shared.Next(pool.Length - i);
            picked[i] = pool[index];
            pool[index] = pool[pool.Length - 1 - i];
        }

        PvpContestChannel.BroadcastDraw(transport, _round, picked[0], picked[1], cancel: false);
    }

    private static void Hook()
    {
        if (_hooked)
        {
            return;
        }

        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                ModInfo.Warn("[擂台赛] 拿不到 SceneTree，投票结果不会被处理");
                return;
            }

            tree.ProcessFrame += OnFrame;
            _hooked = true;
            ModInfo.Info("[擂台赛] 已挂上每帧回调");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[擂台赛] 每帧回调挂载失败：{ex.Message}");
        }
    }

    private static void OnFrame()
    {
        try
        {
            Pump();
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[擂台赛] 每帧处理出错（已忽略）：{ex.Message}");
        }
    }
}
