using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

using STS2RitsuLib.Networking.Sidecar;

namespace PvpDuel.Core.SplitMode;

/// <summary>相遇时交换的「我这局的玩家快照」（合流要用的那一份）。</summary>
/// <param name="PlayerJson">`SerializablePlayer` 的 JSON（卡组/遗物/药水/血量/金币全在里面）。</param>
/// <param name="CharacterId">角色 id（日志用）。</param>
/// <param name="DeckSize">卡组张数（日志用）。</param>
/// <param name="RelicCount">遗物个数（日志用）。</param>
/// <param name="NetId">发送方本机玩家 id（单机局里恒为 1，合流时由主机改写成真实 id）。</param>
internal readonly record struct SplitSnapshotPayload(
    string PlayerJson,
    string CharacterId,
    int DeckSize,
    int RelicCount,
    ulong NetId);

/// <summary>相遇快照的传输通道（和 Presence 一样走 RitsuLib Sidecar，只是换了个消息键）。</summary>
internal static class SplitSnapshotChannel
{
    private const string MessageKey = "split_snapshot";

    private static readonly RitsuLibSidecarJsonSerializer<SplitSnapshotPayload> Codec = new();

    private static readonly RitsuLibSidecarMessageDescriptor<SplitSnapshotPayload> Descriptor =
        new(ModInfo.ModId, MessageKey, Codec.Serialize, Codec.Deserialize);

    private static IDisposable? _subscription;

    public static bool Initialized { get; private set; }

    public static int SentCount { get; private set; }

    public static int ReceivedCount { get; private set; }

    public static void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;

        try
        {
            RitsuLibSidecarTypedMessageRegistry.Register(Descriptor);
            _subscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(Descriptor, OnReceived);

            ModInfo.Info("[SplitMode] 相遇快照通道就绪");
        }
        catch (Exception ex)
        {
            Initialized = false;
            ModInfo.Error($"[SplitMode] 相遇快照通道初始化失败：{ex}");
        }
    }

    public static bool Send(INetGameService transport, SplitSnapshotPayload payload)
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

            return ok;
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[SplitMode] 相遇快照发送失败：{ex.Message}");
            return false;
        }
    }

    private static void OnReceived(RitsuLibSidecarTypedDispatchContext<SplitSnapshotPayload> context)
    {
        ReceivedCount++;
        SplitMeeting.OnPeerSnapshot(context.SenderNetId, context.Message);
    }
}
