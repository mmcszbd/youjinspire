using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.SplitMode;

/// <summary>合流：双方都打完三幕之后，各自把「我这一局的玩家快照」发给对方（合流的第一半）。</summary>
/// <remarks>
/// <para>
/// <b>触发点是「三幕打完」，不是「同节点相遇」</b>（2026-11-03 改）。原来的设计是两人走到同一个地图节点就合流，
/// 实测太难触发（两人各自爬，很难刚好同节点同状态），而且打断了正常的爬塔节奏。
/// 现在改成：各自把三幕爬完，两边都打完 → 合流进决斗局打 PvP。
/// </para>
/// <para>
/// 「我打完了」由 <see cref="SplitLastActPatch" /> 在 <c>RunManager.EnterNextAct</c> 里置标志，
/// 再经 <see cref="PresencePublisher" /> 以 <c>Activity = 3</c> 广播出去。
/// 两边都 <c>Activity = 3</c> 才算「都打完了」。
/// </para>
/// <para>
/// 快照取的是本机单机局的 <c>SerializablePlayer</c>（<c>RunManager.ToSave(null)</c> 里那位玩家）——
/// 角色、卡组、遗物、药水、血量、金币都在里面。单机局里它的 <c>NetId</c> 恒为 1，
/// 所以主机合成时要把它改写成双方真实的网络 id（<c>LoadRunLobby</c> 就是按 netId 认人的）。
/// </para>
/// </remarks>
internal static class SplitMeeting
{
    /// <summary>所有对端的快照，按 netId 存（三人以上时主机要拿全部人的才合成得出来）。</summary>
    private static readonly Dictionary<ulong, SplitSnapshotPayload> PeerSnapshots = [];

    private static bool _builtDuelSave;

    /// <summary>「都打完了」期间隔多久重发一次快照（两边触发差几帧也能凑齐）。</summary>
    private const double ResendSeconds = 1.0;

    private static double _sinceSnapshot;

    /// <summary>我这条命发出去的那份快照 JSON（合成时要用；留到合成成功为止）。</summary>
    private static string? _localPlayerJson;

    /// <summary>
    /// 三幕打完那一刻抓下来的本机快照。进建筑师事件之后 RunState 可能已经收掉，
    /// 现读 ToSave 拿不到东西，所以必须提前抓一份存着。
    /// </summary>
    private static SplitSnapshotPayload? _localPayload;

    /// <summary>测试开关：已经置过「三幕打完」。</summary>
    private static bool _fastTestDone;

    /// <summary>测试开关：已经打过「开关生效」那行日志。</summary>
    private static bool _fastTestAnnounced;

    /// <summary>测试开关：已经进过第一场战斗（快照只抓一次）。</summary>
    private static bool _sawCombat;


    /// <summary>已经合成过决斗局存档（只合成一次）。</summary>
    private static bool _merged;

    /// <summary>已经收到几份对端快照。</summary>
    public static int PeerSnapshotCount => PeerSnapshots.Count;

    /// <summary>新一局开始时把这些跨局状态全清掉。</summary>
    /// <remarks>
    /// <para>
    /// 这些标记都是静态的，而「结算完再开一局」不会重载本 mod 的程序集。留着上一局的值，
    /// <see cref="ShouldTriggerMeeting" /> 第一句就 <c>return false</c>（<c>_merged</c> 还是真），
    /// <see cref="TickFastTest" /> 也会被 <c>_fastTestDone</c> 挡掉 —— 表现就是第二局怎么做都不合流。
    /// </para>
    /// <para>
    /// 2026-11-04 19:12 的日志印证：三次 <c>本局人数：2</c>，只有第一次后面跟着
    /// 「合流测试：开关已生效」，后两次连 Presence 通道都没重开，合流阶段还停在 Meeting。
    /// </para>
    /// </remarks>
    public static void ResetForNewRun()
    {
        PeerSnapshots.Clear();
        _builtDuelSave = false;
        _sinceSnapshot = 0;
        _localPlayerJson = null;
        _localPayload = null;
        _fastTestDone = false;
        _fastTestAnnounced = false;
        _sawCombat = false;
        _merged = false;

        // 上一局的对端坐标台账也要清：合流判据要求「所有对端都 Activity == 3」，
        // 留着一位已经不在的旧对端就永远判不成立（卡在 Meeting）。
        PresenceLedger.Reset();

        // 上一局开起来的「载入局」大厅还挂着（_lobby 是静态的），
        // 不清的话第二局 StartHost 第一句就 return false，什么都不会发生。
        SplitMerge.Reset();

        ModInfo.Info("[SplitMode] 新的一局：合流状态已重置（快照、存档、测试开关标记全清）");
    }


    /// <summary>现在该不该合流：本机打完三幕 + 对端也打完三幕 + 还没合过。</summary>
    public static bool ShouldTriggerMeeting()
    {
        if (_merged || _builtDuelSave || !ModuleSwitches.SplitMeeting)
        {
            return false;
        }

        if (!SplitRuntime.FinishedLastAct
            || !PresenceLedger.TryGetLocal(out var mine)
            || mine.Activity != 3)
        {
            return false;
        }

        // 所有人都得打完才算「都打完了」：三人以上不能只看第一个对端。
        var peers = PresenceLedger.PeerSnapshot();

        return peers.Count > 0 && peers.All(peer => peer.Payload.Activity == 3);
    }

    /// <summary>由每帧的泵调用。</summary>
    public static void Pump(double delta, INetGameService transport)
    {
        if (!ModuleSwitches.SplitMeeting)
        {
            return;
        }

        // 两端都要先把「合流邀请」通道注册好（Sub 是按 opcode 派发的，没注册就等于丢包）——
        // 0.15.0 只在主机侧注册，结果客户端根本收不到邀请。
        SplitMergeChannel.Initialize();

        if (!ShouldTriggerMeeting())
        {
            _sinceSnapshot = 0;
            return;
        }

        _sinceSnapshot += delta;

        if (_sinceSnapshot >= ResendSeconds)
        {
            // 合流期间每秒重发一次：两边触发时刻只差几帧、或者某一方那一下没判定成，
            // 只发一次就会永远凑不齐。
            TrySendSnapshot(transport);
        }

        // 合成必须在主线程做（Pump 就是主线程）：网络线程里碰 RunManager / ModelDb 是错的。
        // 主机要凑齐「除自己以外所有人」的快照才合成（四人局 = 3 份）。
        if (!_builtDuelSave
            && _localPlayerJson is not null
            && PeerSnapshots.Count >= ExpectedPeerCount())
        {
            TryBuildDuelSave(transport);
        }
    }

    /// <summary>应该收到几份对端快照（本局人数 - 自己）。</summary>
    /// <remarks>
    /// 分离期本机的 <c>RunState.Players</c> 只有自己，所以人数只能取 <see cref="SplitRuntime.PartySize"/>
    /// （那个值在开跑时由 <c>SplitSoloRun</c> 从大厅人数记下来）。
    /// </remarks>
    private static int ExpectedPeerCount()
    {
        return Math.Max(1, SplitRuntime.PartySize - 1);
    }

    private static void TrySendSnapshot(INetGameService transport)
    {
        if (!PresenceLedger.TryGetFirstPeer(out var peerNetId, out _))
        {
            return;
        }

        SplitSnapshotChannel.Initialize();

        try
        {
            // 优先用「三幕打完那一刻」抓下来的那份：进建筑师事件之后当局可能已经收掉，
            // ToSave 拿不到东西，现取那条路会一直发不出去，两边就永远凑不齐。
            var payload = _localPayload ?? CaptureLocalSnapshot();

            if (payload is null)
            {
                _sinceSnapshot = 0;
                return;
            }

            var snapshot = payload.Value;

            if (!SplitSnapshotChannel.Send(transport, snapshot))
            {
                return;
            }

            var first = _localPlayerJson is null;

            _sinceSnapshot = 0;
            _localPlayerJson = snapshot.PlayerJson;

            if (first)
            {
                ModInfo.Info(
                    $"[SplitMode] 合流：把快照发给对端 {peerNetId}"
                    + $"（角色={snapshot.CharacterId} 卡组={snapshot.DeckSize} "
                    + $"遗物={snapshot.RelicCount} JSON={snapshot.PlayerJson.Length} 字节）");
            }
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[SplitMode] 合流：取/发快照失败：{ex}");
        }
    }

    /// <summary>
    /// 把本机这一刻的玩家数据抓下来缓存住。三幕打完那一刻调最稳（RunState 还在）。
    /// 只在第一次成功时缓存，之后重复调用直接返回缓存。
    /// </summary>
    public static SplitSnapshotPayload? CaptureLocalSnapshot()
    {
        if (_localPayload is not null)
        {
            return _localPayload;
        }

        if (RunManager.Instance?.ToSave(null) is not { Players.Count: > 0 } save)
        {
            return null;
        }

        try
        {
            var me = save.Players[0];

            _localPayload = new SplitSnapshotPayload(
                JsonSerializer.Serialize(me),
                me.CharacterId?.Entry ?? "?",
                me.Deck.Count,
                me.Relics.Count,
                me.NetId);

            return _localPayload;
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[SplitMode] 合流：抓本机快照失败：{ex}");
            return null;
        }
    }

    /// <summary>对端快照到了（可能在网络线程上调用）。</summary>
    public static void OnPeerSnapshot(ulong peerNetId, SplitSnapshotPayload payload)
    {
        // 按 netId 存：每次重发都会覆盖自己那一份，但「新来了一位」只打一次日志。
        var isNewPeer = !PeerSnapshots.ContainsKey(peerNetId);
        PeerSnapshots[peerNetId] = payload;

        if (!isNewPeer)
        {
            return;
        }

        ModInfo.Info(
            $"[SplitMode] 合流：收到对端 {peerNetId} 的快照"
            + $"（角色={payload.CharacterId} 卡组={payload.DeckSize} 遗物={payload.RelicCount} "
            + $"JSON={payload.PlayerJson.Length} 字节，已收 {PeerSnapshots.Count}/{ExpectedPeerCount()} 份）");

        // 收到对方快照就立刻回一份（不等下一次节流），这样「一边先发、另一边慢半拍」也能马上凑齐。
        _sinceSnapshot = ResendSeconds;
    }

    /// <summary>主机把所有快照合成「决斗局存档」（客户端不合成，它从 LoadRunLobby 拿同一份）。</summary>
    private static void TryBuildDuelSave(INetGameService transport)
    {
        if (_localPlayerJson is not { } localJson)
        {
            return;
        }

        // 客户端不合成、也不自己发起：等主机的合流邀请（见 SplitMergeChannel 的注释）。
        if (transport.Type != NetGameType.Host)
        {
            return;
        }

        if (PeerSnapshots.Count < ExpectedPeerCount())
        {
            return;
        }

        // 主机自己 + 每个对端。netId 一律用真实的 —— LoadRunLobby 是按 netId 认人的，
        // 而单机局快照里的 NetId 恒为 1，不改写的话四个人的 id 会撞在一起。
        var players = new List<(ulong NetId, string PlayerJson)>(PeerSnapshots.Count + 1)
        {
            (transport.NetId, localJson),
        };

        players.AddRange(PeerSnapshots.Select(pair => (pair.Key, pair.Value.PlayerJson)));

        if (!DuelSaveBuilder.TryBuild(players, out var duelSave, out var summary))
        {
            ModInfo.Warn($"[SplitMode] 合流：合成决斗局存档失败：{summary}");
            return;
        }

        _builtDuelSave = true;
        _merged = true;
        SplitRuntime.SetStage(SplitStage.Meeting);

        ModInfo.Info($"[SplitMode] 合流：{summary}");

        if (duelSave is not null && SplitMerge.StartHost(transport, duelSave))
        {
            ModInfo.Info("[SplitMode] 合流：存档已交给 LoadRunLobby，等客户端就位后开局");
        }
    }

    /// <summary>
    /// 测试开关：不爬三幕，<b>打赢第一场战斗</b>就当作「三幕打完」，直接进合流。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须在 <see cref="Pump" /> <b>之前</b>每帧调：<see cref="ShouldTriggerMeeting" /> 第一句就判
    /// <see cref="SplitRuntime.FinishedLastAct" />，这个标志不先置上，Pump 会一直空转。
    /// </para>
    /// <para>
    /// 判「敌人全部倒下」而不是「离开战斗房」：玩家点「放弃本局」时 CurrentRoom 也会变 null，
    /// 那时候 ToSave 已经拿不到东西了，会一路 NPE。
    /// </para>
    /// </remarks>
    public static void TickFastTest()
    {
        if (!ModuleSwitches.SplitMeetingFastTest || _fastTestDone)
        {
            return;
        }

        try
        {
            if (!_fastTestAnnounced)
            {
                _fastTestAnnounced = true;
                ModInfo.Info("[SplitMode] 合流测试：开关已生效，等第一场战斗");
            }

            if (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is not CombatRoom combat)
            {
                return;
            }

            // 进战斗那一刻先抓快照：战死 / 放弃都留得下，后面就不用再读 RunState。
            if (!_sawCombat)
            {
                _sawCombat = true;

                var captured = CaptureLocalSnapshot() is not null;
                ModInfo.Info(
                    $"[SplitMode] 合流测试：进入战斗（CombatRoom）→ 已抓快照（{(captured ? "成功" : "失败")}）");
            }

            if (combat.CombatState is not { } state
                || state.Enemies.Any(enemy => enemy.IsAlive))
            {
                return;
            }

            _fastTestDone = true;
            SplitRuntime.NoteLastActFinished();

            ModInfo.Info("[SplitMode] 合流测试：这场战斗赢了 → 当作「三幕打完」");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[SplitMode] 合流测试判定出错（忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 记一次「已经触发过」。合流改成按「三幕打完」判之后，这条判据不再用节点，
    /// 但镜像决斗那条停用线还在调它 —— 留着当兼容壳。
    /// </summary>
    public static void NoteTriggered()
    {
    }

    /// <summary>重置模块状态（换局 / 重开时调）。</summary>
    public static void Reset()
    {
        PeerSnapshots.Clear();
        _localPlayerJson = null;
        _localPayload = null;
        _builtDuelSave = false;
        _merged = false;
        _sinceSnapshot = 0;
        _fastTestDone = false;
        _fastTestAnnounced = false;
        _sawCombat = false;
    }
}
