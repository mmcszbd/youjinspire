// ============================================================================
// 友尽尖塔（youjin）：拖动队友 —— 阶段 2（联网：把"甩出"这一次事件广播出去）
// ----------------------------------------------------------------------------
// 两条消息（**同一个 descriptor**，靠 payload 里的 `Phase` 区分）：
//   · `PhaseMove`（拖动中，10Hz）—— 对面**实时看到你在拖**。
//     按住不动也照发：这样对面能用"超时"判断你松手了 / 掉线了
//     （只发变化的话，你按住不动对面就会误判超时、把立绘复位）。
//   · `PhaseRelease`（松手）—— 对面从你松手的位置起跳，之后各算各的物理。
//
// 为什么用 RitsuLib 的 Sidecar 同步，而不是自己造游戏网络消息：
//   游戏原生的 `INetMessageSubtypes` 是一张**硬编码在程序集里的静态类型表**
//   （`_t0.._t16` 那种），mod 加不进新类型。RitsuLib 的
//   `STS2RitsuLib.Networking.Sidecar.RitsuLibSidecarSyncMessages` 就是给 mod
//   用的：声明一个 descriptor（带序列化/反序列化/回调），`Register` 之后
//   用 `Broadcast` 发、`Handle` 收。见 `RitsuLibSidecarSyncMessageDescriptor<T>`
//   的 14 个构造参数。
//
// ★ 关键参数选择（错一个都会出问题）：
//   · `ShouldBuffer = false` —— **绝对不能是 true**。战斗进行中收到的消息会被
//     压到"战斗结束后"才补发（金币 / 遗物那两个坑就是这么来的，见 §6 待办）。
//     我们的窗口本来就在战斗里，设 true 的话队友要等打完才看见。
//   · `Mode = NetTransferMode.Unreliable` —— 丢了就丢了。反正这是纯装饰：
//     丢一条 = 对面少看一次甩飞，**不影响任何状态一致性**（3 秒后都回原位）。
//   · `FailurePolicy = BestEffort` —— 发失败不报错。
//   · `DispatchLocalOnBroadcast = false` —— 发送方自己**不要**再收到一遍
//     （他自己已经在本机起跳了）。
//
// 载荷很小：`netId(8) + 偏移(8) + 速度(8)` = 24 字节。
// 偏移和速度都是**相对该立绘自己原位**的量，所以两端坐标系天然对得上
// （绝对坐标对不上 —— 每个人看自己的角色都排在最左，见 TeammateDrag 顶部说明）。
// ============================================================================

using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using STS2RitsuLib.Networking.Sidecar;

namespace YouJin.Scripts;

/// <summary>拖动队友的联网部分（阶段 2）。</summary>
internal static class TeammateDragSync
{
    private const string MessageKey = "drag_fling";

    /// <summary>消息阶段：拖动中（按住期间每 100ms 一个）。</summary>
    private const byte PhaseMove = 0;

    /// <summary>消息阶段：松手甩出。</summary>
    private const byte PhaseRelease = 1;

    /// <summary>一次拖动/甩飞事件。字段顺序就是线格式顺序。</summary>
    /// <remarks>
    /// ★ 目标用 **`Creature.CombatId`**（`uint`）而不是 `Player.NetId` ——
    ///   这样玩家和怪物能用同一套标识（怪物没有 `Player`）。
    ///   `CombatId` 是战斗内的唯一 id，两端一致；配 `NCombatRoom.GetCreature(uint?)` 用。
    /// </remarks>
    private readonly struct FlingPayload
    {
        public readonly byte Phase;
        public readonly uint TargetCombatId;
        public readonly float OffsetX;
        public readonly float OffsetY;
        public readonly float VelX;
        public readonly float VelY;

        public FlingPayload(
            byte phase, uint targetCombatId,
            float offsetX, float offsetY, float velX, float velY)
        {
            Phase = phase;
            TargetCombatId = targetCombatId;
            OffsetX = offsetX;
            OffsetY = offsetY;
            VelX = velX;
            VelY = velY;
        }
    }

    /// <summary>载荷字节数：phase(1) + combatId(4) + 偏移(8) + 速度(8) = 21。</summary>
    private const int PayloadBytes = 21;

    private static RitsuLibSidecarSyncMessageDescriptor<FlingPayload>? _descriptor;
    private static bool _initialized;

    /// <summary>拖动位置包的发送计数（只用来定期打一行诊断日志）。</summary>
    private static int _moveSendCount;

    /// <summary>在 Entry.Init 里调用一次。</summary>
    public static void Init()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            // 构造参数顺序 = 属性顺序（record 的定位构造）：
            //   ModuleId, MessageKey, Serialize, Deserialize, Handle,
            //   LocationTargeted, ShouldBuffer, Mode, Channel,
            //   FailurePolicy, BroadcastScope, DispatchLocalOnBroadcast,
            //   LogLevel, ShouldBroadcast
            _descriptor = new RitsuLibSidecarSyncMessageDescriptor<FlingPayload>(
                Entry.ModId,                                          // ModuleId
                MessageKey,                                           // MessageKey
                Serialize,                                            // Serialize
                Deserialize,                                          // Deserialize
                HandleAsync,                                          // Handle
                false,                                                // LocationTargeted（不需要按地图坐标过滤）
                false,                                                // ★ ShouldBuffer = false（见文件头说明）
                NetTransferMode.Unreliable,                           // Mode
                null,                                                 // Channel（自动）
                RitsuLibSidecarSyncFailurePolicy.BestEffort,          // FailurePolicy
                RitsuLibSidecarSyncBroadcastScope.AllConnectedPeers,  // BroadcastScope
                false,                                                // ★ DispatchLocalOnBroadcast = false
                default,                                              // LogLevel（默认，不额外刷日志）
                true);                                                // ShouldBroadcast

            var id = RitsuLibSidecarSyncMessages.Register(_descriptor);
            Entry.Logger.Info(
                $"拖动队友：同步消息已注册（id={id}，Unreliable/BestEffort/不缓冲 —— 丢了不影响正确性）");
        }
        catch (Exception ex)
        {
            _descriptor = null;
            Entry.Logger.Info($"拖动队友：同步消息注册失败，本机玩法照常、但队友看不到 {ex}");
        }
    }

    /// <summary>拖动中：把当前位置广播出去（10Hz）。失败也不影响本机。</summary>
    public static void BroadcastDragMove(uint targetCombatId, Vector2 offset)
        => Send(new FlingPayload(PhaseMove, targetCombatId, offset.X, offset.Y, 0f, 0f), dragging: true);

    /// <summary>松手：把"甩出"广播出去。失败也不影响本机效果。</summary>
    public static void BroadcastFling(uint targetCombatId, Vector2 offset, Vector2 velocity)
        => Send(new FlingPayload(PhaseRelease, targetCombatId, offset.X, offset.Y, velocity.X, velocity.Y), dragging: false);

    private static void Send(FlingPayload payload, bool dragging)
    {
        if (_descriptor is null)
            return;

        try
        {
            var run = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            if (run is null)
                return;

            // ★ 用 **SendToHostAndBroadcast** 而不是 Broadcast：
            //   这个游戏的网络拓扑是 **host 权威**（客户端只能把请求发给 host，
            //   由 host 串行化/转发，见日志里的 RequestEnqueueActionMessage）。
            //   客户端直接 Broadcast 到不了其他端 —— 要"发给 host 再由 host 广播"。
            //   主机自己调它也是对的（host=自己 → 广播给所有人）。
            var ok = RitsuLibSidecarSyncMessages.SendToHostAndBroadcast(run, _descriptor, payload);

            if (dragging)
            {
                // 拖动中的包 10Hz，**只记第 1 个和之后每秒 1 行**，免得淹日志；
                // 但必须留痕 —— 否则"对面看不到"时无从判断是没发出去还是没显示。
                _moveSendCount++;
                if (_moveSendCount == 1 || _moveSendCount % 10 == 1)
                {
                    Entry.Logger.Info(
                        $"拖动队友：位置包已发 {_moveSendCount} 个（ok={ok}）"
                        + (ok ? "" : " ← 发送未被接受，队友看不到！"));
                }
            }
            else
            {
                _moveSendCount = 0;
                Entry.Logger.Info(
                    $"拖动队友：已广播甩飞事件 → CombatId={payload.TargetCombatId} "
                    + $"偏移({payload.OffsetX:0},{payload.OffsetY:0}) "
                    + $"速度({payload.VelX:0},{payload.VelY:0})"
                    + (ok ? "" : "（发送未被接受，可能只有自己在玩）"));
            }
        }
        catch (Exception ex)
        {
            if (!dragging)
                Entry.Logger.Info($"拖动队友：广播甩飞事件失败（不影响本机）{ex}");
        }
    }

    // ── 收发 ────────────────────────────────────────────────────────────────

    private static byte[] Serialize(FlingPayload m)
    {
        var b = new byte[PayloadBytes];
        b[0] = m.Phase;
        BitConverter.TryWriteBytes(b.AsSpan(1, 4), m.TargetCombatId);
        BitConverter.TryWriteBytes(b.AsSpan(5, 4), m.OffsetX);
        BitConverter.TryWriteBytes(b.AsSpan(9, 4), m.OffsetY);
        BitConverter.TryWriteBytes(b.AsSpan(13, 4), m.VelX);
        BitConverter.TryWriteBytes(b.AsSpan(17, 4), m.VelY);
        return b;
    }

    private static FlingPayload Deserialize(ReadOnlySpan<byte> s)
    {
        if (s.Length < PayloadBytes)
            return default;

        return new FlingPayload(
            s[0],
            BitConverter.ToUInt32(s.Slice(1, 4)),
            BitConverter.ToSingle(s.Slice(5, 4)),
            BitConverter.ToSingle(s.Slice(9, 4)),
            BitConverter.ToSingle(s.Slice(13, 4)),
            BitConverter.ToSingle(s.Slice(17, 4)));
    }

    private static Task HandleAsync(RitsuLibSidecarSyncMessageContext<FlingPayload> ctx)
    {
        try
        {
            var m = ctx.Message;
            var offset = new Vector2(m.OffsetX, m.OffsetY);
            var velocity = new Vector2(m.VelX, m.VelY);

            if (m.Phase == PhaseMove)
            {
                // 对方**正按着**：塞进队列，等主线程的 Step() 应用
                // （★ 这里可能在网络线程，绝不能直接碰 Godot 节点）
                TeammateDrag.QueueRemoteMove(m.TargetCombatId, offset, ctx.SenderNetId);
            }
            else if (m.Phase == PhaseRelease)
            {
                // 对方**松手了**：同样入队，主线程再从他松手的位置起跳
                TeammateDrag.QueueRemoteRelease(m.TargetCombatId, offset, velocity, ctx.SenderNetId);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"拖动队友：处理远端拖动事件失败（忽略）{ex}");
        }

        return Task.CompletedTask;
    }
}
