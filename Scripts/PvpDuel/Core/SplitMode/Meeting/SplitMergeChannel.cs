using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

using STS2RitsuLib.Networking.Sidecar;

namespace PvpDuel.Core.SplitMode;

/// <summary>主机开好「载入局」大厅后发的邀请（客户端收到才去请求入厅）。</summary>
/// <param name="PlayerCount">存档里应有几名玩家（客户端拿来核对）。</param>
/// <param name="Note">日志用的一句描述。</param>
internal readonly record struct SplitMergeGoPayload(int PlayerCount, string Note);

/// <summary>
/// 合流邀请通道。
/// </summary>
/// <remarks>
/// 为什么要这条消息：本体的 <c>HandleClientLoadJoinRequestMessage</c> **不做去重**——客户端每重发一次请求，
/// 主机就往大厅里多塞一个人（2026-09-26 实测：重试了 30 次 → "当前就位 32 人"）。
/// 所以客户端**不能自己重试**，得由主机在厅开好之后通知一声，客户端只发一次请求。
/// </remarks>
internal static class SplitMergeChannel
{
    private const string MessageKey = "split_merge_go";

    private static readonly RitsuLibSidecarJsonSerializer<SplitMergeGoPayload> Codec = new();

    private static readonly RitsuLibSidecarMessageDescriptor<SplitMergeGoPayload> Descriptor =
        new(ModInfo.ModId, MessageKey, Codec.Serialize, Codec.Deserialize);

    private static IDisposable? _subscription;

    public static bool Initialized { get; private set; }

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

            ModInfo.Info("[SplitMode] 合流邀请通道就绪");
        }
        catch (Exception ex)
        {
            Initialized = false;
            ModInfo.Error($"[SplitMode] 合流邀请通道初始化失败：{ex}");
        }
    }

    /// <summary>主机广播「来入厅」。</summary>
    public static bool BroadcastInvite(INetGameService transport, SplitMergeGoPayload payload)
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
            return RitsuLibSidecarTypedMessageRegistry.Broadcast(transport, Descriptor, payload);
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[SplitMode] 合流邀请发送失败：{ex.Message}");
            return false;
        }
    }

    private static void OnReceived(RitsuLibSidecarTypedDispatchContext<SplitMergeGoPayload> context)
    {
        ModInfo.Info(
            $"[SplitMode] 合流：收到主机邀请（存档 {context.Message.PlayerCount} 人：{context.Message.Note}）");

        SplitMerge.OnInviteReceived();
    }
}
