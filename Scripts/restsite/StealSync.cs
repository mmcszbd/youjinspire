using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.Sidecar;

namespace YouJin.Scripts;

/// <summary>
/// 火堆「偷东西」选择的跨端同步。
/// </summary>
/// <remarks>
/// <para>
/// 偷牌和偷遗物本来各写了一份逐行相同的 Sidecar 通道（注册 / 广播 / 20 字节序列化 /
/// 收包记账），只有 <c>MessageKey</c>、日志前缀和「收包后往哪个判定里记账」不同。
/// 这里合成一条通道，两处各留一个实例。
/// </para>
/// <para>
/// 三条参数是判定正确性的一部分，改之前先想清楚：<c>ShouldBuffer = false</c>（火堆选项
/// 是一次性的，晚到的票没有意义）、<c>Reliable</c>（丢一条就让各端判定不一致）、
/// <c>FailurePolicy.Required</c>（发不出去要报错，而不是静默少一票）。
/// </para>
/// </remarks>
internal sealed class StealChoiceChannel
{
    private readonly string _messageKey;
    private readonly string _tag;
    private readonly Action<ulong, ulong, int> _onReceive;

    private RitsuLibSidecarSyncMessageDescriptor<StealChoice>? _descriptor;
    private bool _initialized;

    public StealChoiceChannel(string messageKey, string tag, Action<ulong, ulong, int> onReceive)
    {
        _messageKey = messageKey;
        _tag = tag;
        _onReceive = onReceive;
    }

    public void Init()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            _descriptor = new RitsuLibSidecarSyncMessageDescriptor<StealChoice>(
                Entry.ModId,
                _messageKey,
                Serialize,
                Deserialize,
                HandleAsync,
                false,                                     // LocationTargeted
                false,                                     // ★ ShouldBuffer = false
                NetTransferMode.Reliable,                  // ★ 不能丢，丢了判定不一致
                null,                                      // Channel
                RitsuLibSidecarSyncFailurePolicy.Required, // ★ 发不出去要报错
                RitsuLibSidecarSyncBroadcastScope.AllConnectedPeers,
                false,                                     // DispatchLocalOnBroadcast
                default,
                true);

            var id = RitsuLibSidecarSyncMessages.Register(_descriptor);
            Entry.Logger.Info($"{_tag}：同步消息已注册（id={id}，Reliable）");
        }
        catch (Exception ex)
        {
            _descriptor = null;
            Entry.Logger.Info($"{_tag}：同步消息注册失败 {ex}");
        }
    }

    /// <summary>把这名玩家的选择广播出去。</summary>
    public void Broadcast(ulong thiefNetId, ulong victimNetId, int index)
    {
        if (_descriptor is null)
            return;

        try
        {
            var run = RunManager.Instance;
            if (run is null)
                return;

            var ok = RitsuLibSidecarSyncMessages.SendToHostAndBroadcast(
                run, _descriptor, new StealChoice(thiefNetId, victimNetId, index));

            Entry.Logger.Info(
                $"{_tag}：广播选择 thief={thiefNetId} victim={victimNetId} index={index}"
                + (ok ? "" : "（发送未被接受！）"));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"{_tag}：广播失败 {ex}");
        }
    }

    private static byte[] Serialize(StealChoice m)
    {
        var b = new byte[StealChoice.Size];
        BitConverter.TryWriteBytes(b.AsSpan(0, 8), m.ThiefNetId);
        BitConverter.TryWriteBytes(b.AsSpan(8, 8), m.VictimNetId);
        BitConverter.TryWriteBytes(b.AsSpan(16, 4), m.Index);
        return b;
    }

    private static StealChoice Deserialize(ReadOnlySpan<byte> s)
    {
        if (s.Length < StealChoice.Size)
            return default;

        return new StealChoice(
            BitConverter.ToUInt64(s.Slice(0, 8)),
            BitConverter.ToUInt64(s.Slice(8, 8)),
            BitConverter.ToInt32(s.Slice(16, 4)));
    }

    private Task HandleAsync(RitsuLibSidecarSyncMessageContext<StealChoice> ctx)
    {
        try
        {
            var m = ctx.Message;
            _onReceive(m.ThiefNetId, m.VictimNetId, m.Index);
            Entry.Logger.Info($"{_tag}：收到远端选择 thief={m.ThiefNetId} victim={m.VictimNetId} index={m.Index}");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"{_tag}：处理远端选择失败（忽略）{ex}");
        }

        return Task.CompletedTask;
    }
}

/// <summary>一次「偷」的选择：谁偷、偷谁、第几个。</summary>
internal readonly struct StealChoice
{
    /// <summary>载荷字节数：两个 NetId + 一个下标。</summary>
    public const int Size = 20;

    public readonly ulong ThiefNetId;
    public readonly ulong VictimNetId;
    public readonly int Index;

    public StealChoice(ulong thiefNetId, ulong victimNetId, int index)
    {
        ThiefNetId = thiefNetId;
        VictimNetId = victimNetId;
        Index = index;
    }
}

/// <summary>两条通道：偷牌、偷遗物。</summary>
internal static class StealSync
{
    public static readonly StealChoiceChannel Card =
        new("rest_steal_card", "偷牌", StealCardVerdict.NoteChoice);

    public static readonly StealChoiceChannel Relic =
        new("rest_steal_relic", "偷遗物", StealRelicVerdict.NoteChoice);
}
