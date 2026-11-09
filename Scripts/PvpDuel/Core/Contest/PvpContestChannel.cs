using System;
using System.Collections.Generic;

using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

using STS2RitsuLib.Networking.Sidecar;

namespace PvpDuel.Core.Contest;

/// <summary>一张选票：这一轮的这一位选了上台 / 观战 / 结束。</summary>
/// <param name="Round">第几轮（从 1 开始），用来丢掉上一轮的迟到包。</param>
/// <param name="Choice">0 = 上台，1 = 观战，2 = 结束。</param>
internal readonly record struct ContestVotePayload(int Round, byte Choice);

/// <summary>主机抽完签的结果。</summary>
/// <param name="Round">第几轮。</param>
/// <param name="NetIdA">第一位参战者。</param>
/// <param name="NetIdB">第二位参战者。</param>
/// <param name="Cancel"><c>true</c> = 全员选了「结束」→ 退出循环、真胜利结算。</param>
/// <param name="Restart">
/// <c>true</c> = 上台的人不到 2 个、但有人还想打（没人全票选结束）→ 本轮作废，
/// 原样再投一轮（轮次号会往前推，票和抽签重新来过）。
/// </param>
internal readonly record struct ContestDrawPayload(int Round, ulong NetIdA, ulong NetIdB, bool Cancel, bool Restart);

/// <summary>
/// 擂台赛的投票与抽签通道：走 RitsuLib 的 Sidecar，和 <c>MirrorSyncChannel</c> 同一套写法。
/// </summary>
/// <remarks>
/// <para>
/// 为什么抽签放在主机：<c>RunState.Rng</c> 在四台机器上的调用次数不一定同步（有人多开一次 UI、
/// 多翻一次日志都可能差），各算各的会让"谁上台"分叉，直接裂成两个平行世界。主机抽完广播，其余只照做。
/// </para>
/// <para>
/// 接收回调可能在网络线程上跑，所以这里只做"加锁存数 + 打日志"，绝不碰 Godot / RunManager；
/// 真正的判定由主线程的 <see cref="PvpContest" /> 泵去取。
/// </para>
/// </remarks>
internal static class PvpContestChannel
{
    /// <summary>选「上台」。</summary>
    public const byte ChoiceFight = 0;

    /// <summary>选「观战」。</summary>
    public const byte ChoiceWatch = 1;

    /// <summary>选「结束」。</summary>
    public const byte ChoiceEnd = 2;

    private const string VoteKey = "contest_vote";

    private const string DrawKey = "contest_draw";

    private static readonly RitsuLibSidecarJsonSerializer<ContestVotePayload> VoteCodec = new();

    private static readonly RitsuLibSidecarJsonSerializer<ContestDrawPayload> DrawCodec = new();

    private static readonly RitsuLibSidecarMessageDescriptor<ContestVotePayload> VoteDescriptor =
        new(ModInfo.ModId, VoteKey, VoteCodec.Serialize, VoteCodec.Deserialize);

    private static readonly RitsuLibSidecarMessageDescriptor<ContestDrawPayload> DrawDescriptor =
        new(ModInfo.ModId, DrawKey, DrawCodec.Serialize, DrawCodec.Deserialize);

    private static readonly object Gate = new();

    private static readonly Dictionary<ulong, byte> Votes = [];

    private static IDisposable? _voteSubscription;

    private static IDisposable? _drawSubscription;

    private static int _drawRound;

    private static ulong _drawA;

    private static ulong _drawB;

    private static bool _drawCancel;

    private static bool _drawRestart;

    private static bool _drawReady;

    public static bool Initialized { get; private set; }

    public static int SentVoteCount { get; private set; }

    public static int ReceivedVoteCount { get; private set; }

    public static int ReceivedDrawCount { get; private set; }

    public static int SendFailureCount { get; private set; }

    /// <summary>注册 + 订阅（幂等）。</summary>
    public static void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;

        try
        {
            RitsuLibSidecarTypedMessageRegistry.Register(VoteDescriptor);
            RitsuLibSidecarTypedMessageRegistry.Register(DrawDescriptor);

            _voteSubscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(VoteDescriptor, OnVoteReceived);
            _drawSubscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(DrawDescriptor, OnDrawReceived);

            ModInfo.Info("[擂台赛] 投票通道就绪");
        }
        catch (Exception ex)
        {
            Initialized = false;
            ModInfo.Error($"[擂台赛] 投票通道初始化失败（擂台赛不可用）：{ex}");
        }
    }

    /// <summary>把本机这一轮的票投出去（主机自己也走这里，只是不发网络）。</summary>
    public static void SendVote(INetGameService? transport, int round, byte choice)
    {
        if (transport is null || !InitializeIfNeeded())
        {
            return;
        }

        try
        {
            if (transport.Type == NetGameType.Host)
            {
                lock (Gate)
                {
                    Votes[transport.NetId] = choice;
                }

                ModInfo.Info($"[擂台赛] 主机自己的票：{NameOf(choice)}（第 {round} 轮）");
                return;
            }

            var payload = new ContestVotePayload(round, choice);
            var ok = RitsuLibSidecarTypedMessageRegistry.SendToHost(transport, VoteDescriptor, payload);

            if (ok)
            {
                SentVoteCount++;
                ModInfo.Info($"[擂台赛] → 已投：{NameOf(choice)}（第 {round} 轮）");
            }
            else
            {
                SendFailureCount++;
                ModInfo.Warn($"[擂台赛] 投票发送失败：{NameOf(choice)}");
            }
        }
        catch (Exception ex)
        {
            SendFailureCount++;
            ModInfo.Warn($"[擂台赛] 投票异常：{ex.Message}");
        }
    }

    /// <summary>主机：票收齐了就取走（不清空则返回 <c>false</c>）。</summary>
    public static bool TryCollectVotes(int round, int expected, out Dictionary<ulong, byte> votes)
    {
        votes = [];

        lock (Gate)
        {
            if (Votes.Count < expected)
            {
                return false;
            }

            foreach (var pair in Votes)
            {
                votes[pair.Key] = pair.Value;
            }

            Votes.Clear();
        }

        ModInfo.Info($"[擂台赛] 第 {round} 轮票收齐（{votes.Count} 张）");
        return true;
    }

    /// <summary>主机：把抽签结果广播出去。</summary>
    public static void BroadcastDraw(
        INetGameService? transport,
        int round,
        ulong netIdA,
        ulong netIdB,
        bool cancel,
        bool restart = false)
    {
        if (transport is null || !InitializeIfNeeded())
        {
            return;
        }

        // 主机自己的那一份直接落地，不必等网络回环。
        lock (Gate)
        {
            _drawRound = round;
            _drawA = netIdA;
            _drawB = netIdB;
            _drawCancel = cancel;
            _drawRestart = restart;
            _drawReady = true;
        }

        try
        {
            var payload = new ContestDrawPayload(round, netIdA, netIdB, cancel, restart);
            RitsuLibSidecarTypedMessageRegistry.Broadcast(transport, DrawDescriptor, payload);

            ModInfo.Info(
                cancel
                    ? $"[擂台赛] 第 {round} 轮：全员选结束 → 退出循环、结算"
                    : restart
                        ? $"[擂台赛] 第 {round} 轮：上台不足 2 人 → 重开一轮投票"
                        : $"[擂台赛] 第 {round} 轮抽签：netId={netIdA} vs netId={netIdB}");
        }
        catch (Exception ex)
        {
            SendFailureCount++;
            ModInfo.Warn($"[擂台赛] 抽签广播失败：{ex.Message}");
        }
    }

    /// <summary>主线程取走抽签结果（取完清掉）。</summary>
    public static bool TryTakeDraw(
        out int round,
        out ulong netIdA,
        out ulong netIdB,
        out bool cancel,
        out bool restart)
    {
        lock (Gate)
        {
            round = _drawRound;
            netIdA = _drawA;
            netIdB = _drawB;
            cancel = _drawCancel;
            restart = _drawRestart;
            var ready = _drawReady;
            _drawReady = false;
            return ready;
        }
    }

    /// <summary>清掉票箱里残留的票，返回清掉几张（新一局首次挂上事件时调）。</summary>
    /// <remarks>
    /// <para>
    /// 收场之后还会收到迟到票：玩家在事件被收掉那一瞬间又点了选项，包到的时候
    /// <see cref="Reset" /> 已经跑过，票就留在 <see cref="Votes" /> 里。
    /// </para>
    /// <para>
    /// 下一局 <c>_round</c> 又从 1 开始，而 <see cref="TryCollectVotes" /> 只数张数、
    /// <b>不看轮次</b> —— 残留的两张（多半还是「结束」）会被当成这一轮收齐，直接收场结算。
    /// 2026-11-05 的日志正是这样：第一局 1784 收场之后，1805/1818 又记了两张「第 0 轮」的票；
    /// 第二局 3460 刚「开始第 1 轮投票」，3468 立刻「票收齐（2 张）」→ 3469 全员选结束。
    /// </para>
    /// </remarks>
    public static int ClearVotes()
    {
        lock (Gate)
        {
            var count = Votes.Count;
            Votes.Clear();
            return count;
        }
    }

    /// <summary>清掉整场擂台赛的状态（收场 / 换局时调）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Votes.Clear();
            _drawRound = 0;
            _drawA = 0;
            _drawB = 0;
            _drawCancel = false;
            _drawRestart = false;
            _drawReady = false;

            SentVoteCount = 0;
            ReceivedVoteCount = 0;
            ReceivedDrawCount = 0;
            SendFailureCount = 0;
        }
    }

    /// <summary>选项名（日志用）。</summary>
    public static string NameOf(byte choice)
    {
        return choice switch
        {
            ChoiceFight => "上台",
            ChoiceWatch => "观战",
            ChoiceEnd => "结束",
            _ => $"未知({choice})",
        };
    }

    private static bool InitializeIfNeeded()
    {
        if (!Initialized)
        {
            Initialize();
        }

        return Initialized;
    }

    private static void OnVoteReceived(RitsuLibSidecarTypedDispatchContext<ContestVotePayload> context)
    {
        ReceivedVoteCount++;

        lock (Gate)
        {
            Votes[context.SenderNetId] = context.Message.Choice;
        }

        ModInfo.Info($"[擂台赛] ← netId={context.SenderNetId} 投了 {NameOf(context.Message.Choice)}");
    }

    private static void OnDrawReceived(RitsuLibSidecarTypedDispatchContext<ContestDrawPayload> context)
    {
        ReceivedDrawCount++;
        var message = context.Message;

        lock (Gate)
        {
            _drawRound = message.Round;
            _drawA = message.NetIdA;
            _drawB = message.NetIdB;
            _drawCancel = message.Cancel;
            _drawRestart = message.Restart;
            _drawReady = true;
        }

        ModInfo.Info(
            message.Cancel
                ? $"[擂台赛] ← 第 {message.Round} 轮：全员选结束 → 结算"
                : message.Restart
                    ? $"[擂台赛] ← 第 {message.Round} 轮：重开一轮投票"
                    : $"[擂台赛] ← 第 {message.Round} 轮抽签：netId={message.NetIdA} vs netId={message.NetIdB}");
    }
}
