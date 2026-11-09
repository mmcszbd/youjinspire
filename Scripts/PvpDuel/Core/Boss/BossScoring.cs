using System.Collections.Generic;
using System.Linq;

using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.Boss;

/// <summary>一位玩家的「建筑师挑拨」战利品分。</summary>
internal readonly record struct BossScoreEntry(ulong NetId, int Relics, int Gold)
{
    /// <summary>总分 = 遗物分 + 金币（1:1）。</summary>
    public int Total => Relics + Gold;
}

/// <summary>建筑师挑拨·计分：遗物按稀有度折算，金币 1:1。</summary>
/// <remarks>
/// <para>
/// 稀有度用的是本体的 <see cref="RelicRarity" />（实际命名空间
/// <c>MegaCrit.Sts2.Core.Entities.Relics</c>，成员是
/// <c>None / Starter / Common / Uncommon / Rare / Shop / Event / Ancient</c>）。
/// </para>
/// <para>
/// 口径（2026-11-06 与 YG 确认）：普通 100 / 罕见 200 / 稀有 300；
/// 商店、事件、远古三档按 200；<b>初始遗物（Starter）与 None 不计分</b>
/// —— 初始遗物是白送的，算进去等于所有人在同一条起跑线上多 100 分，没有区分度。
/// </para>
/// <para>
/// 全部计算在本地做：联机里每个客户端都握着完整的 <see cref="IRunState" />，
/// 所有玩家的遗物与金币都能读到，两端算出来必然一样，不需要额外开一条广播。
/// 并列时的先后用 <c>NetId</c> 兜底（确定性，不掷骰子），否则两端可能各认一个 boss。
/// </para>
/// </remarks>
internal static class BossScoring
{
    public const int CommonScore = 100;
    public const int UncommonScore = 200;
    public const int RareScore = 300;
    public const int SideScore = 200;

    /// <summary>一件遗物值多少分。</summary>
    public static int ScoreOf(RelicModel? relic)
    {
        return relic?.Rarity switch
        {
            RelicRarity.Common => CommonScore,
            RelicRarity.Uncommon => UncommonScore,
            RelicRarity.Rare => RareScore,
            RelicRarity.Shop or RelicRarity.Event or RelicRarity.Ancient => SideScore,
            _ => 0,
        };
    }

    /// <summary>一位玩家的分数。</summary>
    public static BossScoreEntry ScoreOf(Player player)
    {
        var relics = 0;

        foreach (var relic in player.Relics)
        {
            relics += ScoreOf(relic);
        }

        return new BossScoreEntry(player.NetId, relics, player.Gold);
    }

    /// <summary>按分数从高到低排好（同分按 NetId 升序，保证两端结果一致）。</summary>
    public static List<BossScoreEntry> Rank(IRunState state)
    {
        return state.Players
            .Select(ScoreOf)
            .OrderByDescending(entry => entry.Total)
            .ThenBy(entry => entry.NetId)
            .ToList();
    }
}
