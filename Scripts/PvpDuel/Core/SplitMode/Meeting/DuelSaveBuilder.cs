using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

using PvpDuel.Core.Act;

namespace PvpDuel.Core.SplitMode;

/// <summary>
/// 把两张玩家快照合成「决斗局」的存档（合流的第二半）。
/// </summary>
/// <remarks>
/// <para>
/// 做法：拿主机自己那局的存档当骨架（RNG / 赔率 / 共享遗物袋这些都现成且两端一致），只换三样东西：
/// <list type="number">
/// <item><description><c>Players</c> = 两张快照，<c>NetId</c> 改写成双方真实的网络 id（<c>LoadRunLobby</c> 按 netId 认人）；</description></item>
/// <item><description><c>Acts</c> = 只有我们的「PVP 决斗」幕——决斗局不需要普通章节；</description></item>
/// <item><description>地图足迹清空（<c>VisitedMapCoords</c> / <c>MapPointHistory</c> / <c>EventsSeen</c> / <c>PreFinishedRoom</c>），这是一局新局。</description></item>
/// </list>
/// </para>
/// <para>两端拿到的会是同一份存档（客户端从 <c>LoadRunLobby.Run</c> 拿），所以不需要各自合成。</para>
/// </remarks>
internal static class DuelSaveBuilder
{
    /// <summary>合成成功返回存档与一行摘要（摘要写日志用）。</summary>
    /// <param name="players">
    /// 全部参与者的「真实 netId + 该玩家的 SerializablePlayer JSON」。
    /// 顺序不重要（<c>LoadRunLobby</c> 是按 netId 认人的），但 netId 必须逐个对得上，
    /// 否则那个人连进厅时就会被 <c>NotInSaveGame</c> 踢掉。
    /// </param>
    public static bool TryBuild(
        IReadOnlyList<(ulong NetId, string PlayerJson)> players,
        out SerializableRun? save,
        out string summary)
    {
        save = null;
        summary = string.Empty;

        if (players is null || players.Count < 2)
        {
            summary = $"快照不足（{players?.Count ?? 0} 份，至少要 2 份）";
            return false;
        }

        if (RunManager.Instance?.ToSave(null) is not { } skeleton)
        {
            summary = "拿不到本机存档（不在局里？）";
            return false;
        }

        var built = new List<SerializablePlayer>(players.Count);

        foreach (var (netId, playerJson) in players)
        {
            SerializablePlayer? player;

            try
            {
                player = JsonSerializer.Deserialize<SerializablePlayer>(playerJson);
            }
            catch (Exception ex)
            {
                summary = $"快照反序列化失败（netId={netId}）：{ex.Message}";
                return false;
            }

            if (player is null)
            {
                summary = $"快照反序列化结果是空的（netId={netId}）";
                return false;
            }

            // 单机局里每个人的 NetId 都是 1（单机服务固定值），这里改写成真实 id。
            // ⚠ LoadRunLobby 是**按 netId 认人**的（HandleClientLoadJoinRequestMessage /
            // OnConnectedToClientAsHost 都做 Run.Players.FindIndex(p => p.NetId == senderId)），
            // 错一个，那个人当场被 NotInSaveGame 断线。
            player.NetId = netId;
            built.Add(player);
        }

        skeleton.Players = built;

        // 幕必须带「房间表」：正常存档里这一栏是开局 GenerateRooms 填的，
        // 我们光塞一个空的 ActModel 进去，载入时 ValidateRoomsAfterLoad 会抛
        // "RoomSet.Boss not set! You must call GenerateRooms"。
        var duelAct = ModelDb.Act<PvpDuelAct>().ToMutable();
        var runState = RunManager.Instance?.DebugOnlyGetState();

        if (runState is null)
        {
            summary = "拿不到当局状态（没法给决斗幕生成房间）";
            return false;
        }

        duelAct.GenerateRooms(
            new Rng(runState.Rng.Seed, "pvp_duel_duel_act_rooms"),
            runState.UnlockState,
            isMultiplayer: false);

        skeleton.Acts = [duelAct.ToSave()];
        skeleton.CurrentActIndex = 0;
        skeleton.PreFinishedRoom = null;
        skeleton.VisitedMapCoords.Clear();
        skeleton.MapPointHistory.Clear();
        skeleton.EventsSeen.Clear();

        save = skeleton;
        var roster = string.Join(
            " vs ",
            built.Select(p =>
                $"{p.CharacterId?.Entry ?? "?"}(netId={p.NetId} 卡 {p.Deck.Count} 遗物 {p.Relics.Count})"));

        summary =
            $"决斗局存档：{built.Count} 人 —— {roster}｜"
            + $"幕 {skeleton.Acts.Count} 个（{skeleton.Acts[0].Id?.Entry ?? "?"}，"
            + $"房间表 {(skeleton.Acts[0].SerializableRooms is null ? "空!" : "已生成")}）"
            + $"｜进阶 {skeleton.Ascension}｜种子 {skeleton.SerializableRng?.Seed}";

        return true;
    }
}
