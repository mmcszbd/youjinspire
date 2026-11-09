using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

using STS2RitsuLib.Networking.Sidecar;

namespace PvpDuel.Core.SplitMode;

/// <summary>在地图上「我在哪、正在干什么」的一行数据（跨机只传这个）。</summary>
/// <param name="ActIndex">第几幕（0 起）。</param>
/// <param name="MapColumn">地图列。</param>
/// <param name="MapRow">地图行。</param>
/// <param name="HasCoord"><c>false</c> = 这一局还没选过地图点。</param>
/// <param name="Activity">0 地图 / 1 房间 / 2 战斗 / 3 已结束。</param>
/// <param name="HpPercent">血量百分比（图标上想画血条就用它）。</param>
/// <param name="Seq">自增序号，用来判断"这条是不是新的"。</param>
internal readonly record struct SplitPresencePayload(
    int ActIndex,
    int MapColumn,
    int MapRow,
    bool HasCoord,
    int Activity,
    int HpPercent,
    int Seq);

/// <summary>分离模式的 Presence 通道：跨机传「坐标 + 状态」，走 RitsuLib 的 Sidecar。</summary>
/// <remarks>
/// <para>
/// 为什么不能自己定义网络消息：本体的 <c>INetMessage</c> 子类型要走编译期生成的 ID 表
/// （<c>INetMessageSubtypes.cs</c>），模组加不进去。Sidecar 正是为此存在的——它用自己的信封借原版传输发原始包，
/// 接收侧由 RitsuLib 的补丁接管，所以模组之间可以自建消息类型。
/// </para>
/// <para>
/// 接收回调可能在网络线程上跑（不是 Godot 主线程），所以这里只做"加锁写台账 + 计数 + 打日志"，
/// 不碰任何 Godot API；时间戳由主线程的泵去盖（见 <see cref="PresenceLedger" />）。
/// </para>
/// </remarks>
internal static class SplitPresenceChannel
{
    private const string MessageKey = "split_presence";

    private static readonly RitsuLibSidecarJsonSerializer<SplitPresencePayload> Codec = new();

    private static readonly RitsuLibSidecarMessageDescriptor<SplitPresencePayload> Descriptor =
        new(ModInfo.ModId, MessageKey, Codec.Serialize, Codec.Deserialize);

    private static IDisposable? _subscription;

    /// <summary>已注册并订阅（注册是幂等的）。</summary>
    public static bool Initialized { get; private set; }

    /// <summary>派生出来的操作码（两端一致：由 mod id + 消息键算出来）。</summary>
    public static ulong Opcode { get; private set; }

    public static int SentCount { get; private set; }

    public static int ReceivedCount { get; private set; }

    public static int SendFailureCount { get; private set; }

    /// <summary>主机替客户端转发出去的条数（三人以上才有意义）。</summary>
    public static int RelayCount { get; private set; }

    /// <summary>收到「自己发的」回环包而被丢掉的条数（不该记进台账）。</summary>
    public static int DroppedSelfCount { get; private set; }

    /// <summary>注册 + 订阅（幂等）。模块一开就调它；运行时打开开关也能立刻用。</summary>
    public static void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;

        try
        {
            Opcode = RitsuLibSidecarTypedMessageRegistry.Register(Descriptor);
            _subscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(Descriptor, OnReceived);

            ModInfo.Info($"[SplitMode] Presence 通道就绪：opcode={Opcode}");
        }
        catch (Exception ex)
        {
            Initialized = false;
            ModInfo.Error($"[SplitMode] Presence 通道初始化失败（坐标同步不可用）：{ex}");
        }
    }

    /// <summary>发一条自己的位置：客户端 → 主机；主机 → 广播给所有对端。</summary>
    public static bool Send(INetGameService transport, SplitPresencePayload payload)
    {
        if (!Initialized)
        {
            Initialize();
        }

        if (!Initialized)
        {
            return false;
        }

        try
        {
            var ok = transport.Type == NetGameType.Host
                ? RitsuLibSidecarTypedMessageRegistry.Broadcast(transport, Descriptor, payload)
                : RitsuLibSidecarTypedMessageRegistry.SendToHost(transport, Descriptor, payload);

            if (ok)
            {
                SentCount++;
            }
            else
            {
                SendFailureCount++;
            }

            return ok;
        }
        catch (Exception ex)
        {
            SendFailureCount++;
            ModInfo.Warn($"[SplitMode] Presence 发送失败：{ex.Message}");
            return false;
        }
    }

    private static void OnReceived(RitsuLibSidecarTypedDispatchContext<SplitPresencePayload> context)
    {
        // 自己发出去的那一份又绕回来了（主机 Broadcast 的回环 / 被转发的客户端收到自己那份）：
        // 记进台账就会把自己当成对端画到地图上，直接丢掉。
        var me = SplitLink.Transport?.NetId ?? 0;
        if (me != 0 && context.SenderNetId == me)
        {
            DroppedSelfCount++;
            return;
        }

        ReceivedCount++;
        PresenceLedger.Apply(context.SenderNetId, context.Message);
        TryRelayToOtherClients(context);
    }

    /// <summary>主机把客户端发来的坐标转发给其余客户端。</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Send" /> 里客户端走的是 <c>SendToHost</c>（只到主机）、主机走 <c>Broadcast</c>。
    /// 两人时刚好够用；<b>三人以上时客户端之间永远收不到对方</b>，地图上就少画人。
    /// 这里在主机侧补一次转发，让每个客户端都能拿到所有人的位置。
    /// </para>
    /// <para>
    /// 转发用的还是同一个 <c>Broadcast</c>，所以会绕回原发送者一次 —— 那条被上面「自己」的判据丢掉。
    /// </para>
    /// </remarks>
    private static void TryRelayToOtherClients(RitsuLibSidecarTypedDispatchContext<SplitPresencePayload> context)
    {
        try
        {
            var transport = SplitLink.Transport;
            if (transport is null || transport.Type != NetGameType.Host)
            {
                return;
            }

            if (RitsuLibSidecarTypedMessageRegistry.Broadcast(transport, Descriptor, context.Message))
            {
                RelayCount++;
            }
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[SplitMode] Presence 转发失败：{ex.Message}");
        }
    }

    /// <summary>一行状态（日志 / HUD 用）。</summary>
    public static string Describe()
    {
        if (!Initialized)
        {
            return "presence 未就绪";
        }

        var relay = RelayCount > 0 ? $" / 转发 {RelayCount}" : "";
        var dropped = DroppedSelfCount > 0 ? $" / 丢弃自身 {DroppedSelfCount}" : "";

        return $"presence 发 {SentCount} / 收 {ReceivedCount} / 失败 {SendFailureCount}{relay}{dropped}";
    }
}
