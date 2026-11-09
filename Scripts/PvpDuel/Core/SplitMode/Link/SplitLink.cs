using Godot;

using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.SplitMode;

/// <summary>分离模式的「保活链路」：把大厅 / 对局那条联机传输留住，让离开对局之后连接还活着。</summary>
/// <remarks>
/// <para>
/// 这是 C2 的地基。本体在 <c>RunManager.CleanUp()</c> 末尾无条件
/// <c>NetService.Disconnect(NetError.Quit, !graceful)</c>（<c>RunManager.cs:1606</c>），
/// 而联机传输平时是<strong>靠别人每帧泵</strong>的：大厅期间是角色选择界面（<c>NCharacterSelectScreen.cs:412</c>），
/// 对局期间是 <c>NRun</c>（<c>NRun.cs:98</c>）。离开对局后没人泵 → Steam P2P 会静默超时。
/// 所以本类只做两件事：<b>① 拦下退出对局时的那一次主动断开；② 自己挂一个常驻 Node 继续泵。</b>
/// </para>
/// <para>
/// <b>泵就是心跳</b>：本体自带的质量统计 <c>NetQualityTracker</c> 是挂在<b>连接</b>上的
/// （在 <c>NetClientGameService</c> / <c>NetHostGameService</c> 构造时就注册了 heartbeat 的收发），
/// 只要有人调 <c>Update()</c>，它每 200ms 就会发一次 <c>HeartbeatRequestMessage</c>、对面自动回
/// <c>HeartbeatResponseMessage</c>。所以不需要我们自己造心跳包——泵住了就有双向流量。
/// </para>
/// <para>
/// 本轮<b>不发任何自定义消息</b>——先把「连接能不能活下来」验掉，再谈通道。
/// 也别指望 RitsuLib 的 Sidecar：它挂在本体 <c>RunManager.NetService</c> 上，而单机局的
/// <c>NetSingleplayerGameService</c> 的 <c>SendMessage</c> / <c>RegisterMessageHandler</c> 是空实现。
/// </para>
/// </remarks>
internal static class SplitLink
{
    /// <summary>保活期间每隔多少秒打一行状态。</summary>
    private const double LogIntervalSeconds = 10.0;

    private static readonly List<ulong> Peers = [];

    private static INetGameService? _transport;
    private static SplitLinkPump? _pump;
    private static double _sinceLog;
    private static double _aliveSeconds;
    private static bool _suppressWindow;

    /// <summary>是否正在保活（开关开着 + 抓到过联机传输 + 没断）。</summary>
    public static bool Armed { get; private set; }

    /// <summary>抓到的那条联机传输（分离期我们自己的通道以后就用它）。</summary>
    public static INetGameService? Transport => _transport;

    /// <summary>链路现在还连着吗。</summary>
    public static bool IsAlive => Armed && _transport is { IsConnected: true };

    /// <summary>是不是「本机正在跑的就是这条联机传输」（跑着的时候本体自己在泵，我们别插一脚）。</summary>
    private static bool GameIsPumpingTransport
    {
        get
        {
            var run = RunManager.Instance;

            return run is { IsInProgress: true } && ReferenceEquals(run.NetService, _transport);
        }
    }

    // ==================== 捕获 ====================

    /// <summary>记下一条联机传输。只认 Host / Client —— 单机服务不会覆盖已抓到的联机链路。</summary>
    public static void Capture(INetGameService? service)
    {
        if (service is null || service.Type is not (NetGameType.Host or NetGameType.Client))
        {
            return;
        }

        if (ReferenceEquals(_transport, service))
        {
            RememberPeers();
            return;
        }

        ReleaseTransport();

        _transport = service;
        service.Disconnected += OnTransportDisconnected;
        RememberPeers();

        ModInfo.Info($"[SplitMode] 已捕获联机传输：{DescribeTransport()}");
    }

    /// <summary>开始保活（幂等；开关没开就什么都不做）。</summary>
    public static void Arm()
    {
        if (Armed || !ModuleSwitches.SplitKeepAlive)
        {
            return;
        }

        if (_transport is null)
        {
            ModInfo.Warn("[SplitMode] 保活没启动：还没捕获到联机传输（得先进过一次联机局）");
            return;
        }

        Armed = true;
        _sinceLog = 0;
        _aliveSeconds = 0;
        EnsurePump();

        ModInfo.Info($"[SplitMode] 保活已开启：{Describe()}");
    }

    /// <summary>停止保活（不会主动断线，只是不再拦断连 / 不再泵）。</summary>
    public static void Disarm(string reason)
    {
        if (!Armed)
        {
            return;
        }

        Armed = false;
        ModInfo.Info($"[SplitMode] 保活已关闭（{reason}），累计保活 {_aliveSeconds:F0} 秒");
    }

    // ==================== 拦断连 ====================

    /// <summary>进入「<c>RunManager.CleanUp</c> 正在跑」这个窗口（只在这个窗口里拦断连）。</summary>
    /// <remarks>
    /// 限定窗口是刻意的：只挡「退出对局」那一次断开，玩家在大厅里按「离开联机」照旧能断，
    /// 免得开关一开就退不出房间。
    /// </remarks>
    public static void BeginSuppressWindow()
    {
        _suppressWindow = true;
    }

    public static void EndSuppressWindow()
    {
        _suppressWindow = false;
    }

    /// <summary>本体要主动断开连接时问这里。返回 <c>true</c> = 放行，<c>false</c> = 拦下（连接留着）。</summary>
    public static bool ShouldSuppressDisconnect(NetError reason)
    {
        if (!Armed || !_suppressWindow || reason != NetError.Quit)
        {
            return false;
        }

        ModInfo.Info("[SplitMode] 拦下退出对局时的主动断开（保活中，连接留着）");

        if (SplitRuntime.Stage == SplitStage.Off)
        {
            SplitRuntime.SetStage(SplitStage.SoloRun);
        }

        return true;
    }

    // ==================== 每帧 ====================

    /// <summary>由常驻 Node 每帧调用：泵传输 + 定期打点。</summary>
    public static void Pump(double delta)
    {
        var transport = _transport;

        if (transport is null)
        {
            return;
        }

        if (!GameIsPumpingTransport)
        {
            try
            {
                transport.Update();
            }
            catch (Exception ex)
            {
                ModInfo.Warn($"[SplitMode] 泵传输出错（不影响其它功能）：{ex.Message}");
            }
        }

        // 坐标同步（Presence）不要求保活开关也开着：只要模块开着、传输还抓着，就照发。
        PresencePublisher.Pump(delta, transport);

        // 合流：双方都打完三幕 → 换快照 → 合成决斗局存档 → 切会话进 PvP。
        // 测试模式（第一场战斗就算打完）要在 Pump 之前判，否则第一帧还在等第三幕。
        SplitMeeting.TickFastTest();
        SplitMeeting.Pump(delta, transport);
        SplitMerge.Pump(delta, transport);

        MapPeerMarker.Pump();

        if (Armed)
        {
            _aliveSeconds += delta;
        }

        _sinceLog += delta;

        if (_sinceLog < LogIntervalSeconds)
        {
            return;
        }

        _sinceLog = 0;

        // 两个开关都关着就没什么可看的，不刷日志。
        if (!Armed && !ModuleSwitches.SplitPresence)
        {
            return;
        }

        // 分离期是从大厅那条传输接管过来的，那时还没有局、抓不到对端 id；这里补一次。
        if (Peers.Count == 0)
        {
            RememberPeers();
        }

        // 坐标同步开着时，把台账也塞进这一行——不看 HUD 也能从 log 判断通道通没通。
        var presence = ModuleSwitches.SplitPresence
            ? $"｜{PresenceLedger.Describe()}｜{SplitPresenceChannel.Describe()}"
            : "";

        ModInfo.Info($"[SplitMode] 链路状态：{Describe()}（已保活 {_aliveSeconds:F0} 秒）{presence}");

        WarnIfPeerSilent();

        if (Armed && !transport.IsConnected)
        {
            Disarm("链路已断开");
        }
    }

    /// <summary>一行状态（日志与 HUD 共用）。</summary>
    public static string Describe()
    {
        var transport = _transport;

        if (transport is null)
        {
            return $"阶段={SplitRuntime.Stage}｜链路=未捕获｜对端=-";
        }

        var alive = Armed ? (transport.IsConnected ? "存活" : "已断") : "未保活";

        return $"阶段={SplitRuntime.Stage}｜链路={alive}｜{DescribeTransport()}｜{DescribePeerHealth(transport)}";
    }

    /// <summary>
    /// 对端的「最近有没有回包」——比 <c>IsConnected</c> 靠谱得多。
    /// </summary>
    /// <remarks>
    /// 实测教训：<b>主机侧</b>哪怕客户端已经不见了，<c>IsConnected</c> 照样是 <c>true</c>
    /// （它表示"我这条会话还在"，不表示"对端还活着"）。
    /// 真正能看出对方死活的，是本体自带心跳的统计（<c>NetQualityTracker</c> 每 200ms 发一次
    /// <c>HeartbeatRequestMessage</c>，对面自动回 <c>HeartbeatResponseMessage</c>）：
    /// <c>PingMsec</c> 是<b>最后一次成功应答</b>时的旧值，<c>PacketLoss</c> 才是"最近还答不答"
    /// （见 <c>ConnectionStats</c>：收到一次就向 0 插值、丢一次就向 1 插值，权重 0.2）。
    /// </remarks>
    private static string DescribePeerHealth(INetGameService transport)
    {
        if (Peers.Count == 0)
        {
            return "对端=-";
        }

        var parts = new List<string>(Peers.Count);

        foreach (var peer in Peers)
        {
            parts.Add($"{peer}({DescribeOnePeer(transport, peer)})");
        }

        return "对端=" + string.Join('；', parts);
    }

    private static string DescribeOnePeer(INetGameService transport, ulong peer)
    {
        try
        {
            if (transport.GetStatsForPeer(peer) is not { } stats)
            {
                return "无统计";
            }

            string age;

            if (stats.LastReceivedTime is { } lastReceived)
            {
                var ageSeconds = (Time.GetTicksMsec() - lastReceived) / 1000.0;
                age = $"{ageSeconds:F1}s 前收到";
            }
            else
            {
                age = "从未收到";
            }

            return $"ping {stats.PingMsec:F0}ms, 丢包 {stats.PacketLoss:P1}, {age}";
        }
        catch (Exception ex)
        {
            return $"取统计失败（{ex.GetType().Name}）";
        }
    }

    /// <summary>对端静默超过这个秒数就提醒（心跳是 200ms 一次，5 秒＝丢了约 25 次）。</summary>
    private const double PeerSilenceWarnSeconds = 5.0;

    private static void WarnIfPeerSilent()
    {
        var transport = _transport;

        if (transport is null || Peers.Count == 0)
        {
            return;
        }

        foreach (var peer in Peers)
        {
            try
            {
                if (transport.GetStatsForPeer(peer)?.LastReceivedTime is not { } lastReceived)
                {
                    continue;
                }

                var silentSeconds = (Time.GetTicksMsec() - lastReceived) / 1000.0;

                if (silentSeconds >= PeerSilenceWarnSeconds)
                {
                    ModInfo.Warn(
                        $"[SplitMode] 对端 {peer} 已经 {silentSeconds:F1} 秒没回包了"
                        + "（连接还挂着但对面可能已经走了，主机侧 IsConnected 不可信）");
                }
            }
            catch (Exception)
            {
                // 取不到统计就当没这回事，观测代码不该影响保活本身。
            }
        }
    }

    private static string DescribeTransport()
    {
        var transport = _transport;

        if (transport is null)
        {
            return "无";
        }

        try
        {
            return $"{transport.Type}(本机 netId={transport.NetId}, 连接={transport.IsConnected})";
        }
        catch (Exception)
        {
            // NetId 在未连接时可能抛（NetClientGameService.NetId 就是这样一个属性）。
            return $"{transport.Type}(本机 netId=不可用, 连接={transport.IsConnected})";
        }
    }

    /// <summary>记下这一局里除本机以外的玩家 id（断开之后统计就靠它，Session 里已经查不到了）。</summary>
    private static void RememberPeers()
    {
        if (RunManager.Instance?.DebugOnlyGetState() is not { } state)
        {
            return;
        }

        ulong localId = 0;

        try
        {
            localId = _transport?.NetId ?? 0;
        }
        catch (Exception)
        {
            localId = 0;
        }

        Peers.Clear();

        foreach (var player in state.Players)
        {
            if (player.NetId != localId)
            {
                Peers.Add(player.NetId);
            }
        }

        // 分离期本机跑的是单机局，State.Players 里只有自己 —— 对端 id 从坐标台账里拿。
        if (Peers.Count == 0 && PresenceLedger.TryGetFirstPeer(out var peerId, out _))
        {
            Peers.Add(peerId);
        }
    }

    private static void OnTransportDisconnected(NetErrorInfo info)
    {
        ModInfo.Warn($"[SplitMode] 联机传输断开：{info.GetReason()}");
        Disarm($"传输断开（{info.GetReason()}）");
    }

    private static void ReleaseTransport()
    {
        if (_transport is not null)
        {
            _transport.Disconnected -= OnTransportDisconnected;
        }

        _transport = null;
        Peers.Clear();
    }

    /// <summary>确保常驻泵挂上（幂等；模块开着就该挂，保活只是让它在离开对局后继续跑）。</summary>
    public static void EnsurePump()
    {
        if (_pump is not null && GodotObject.IsInstanceValid(_pump) && _pump.IsInsideTree())
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is not { } root)
        {
            ModInfo.Warn("[SplitMode] 拿不到场景树，保活泵没挂上");
            return;
        }

        _pump = new SplitLinkPump { Name = "PvpDuelSplitLinkPump" };
        root.AddChildSafely(_pump);
    }
}
