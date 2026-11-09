using System.Collections.Generic;
using System.Threading.Tasks;

using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

using YouJin.Scripts;

namespace PvpDuel.Core.Boss;

/// <summary>建筑师挑拨·boss：谁当 boss、boss 加多少、打完怎么还回去。</summary>
/// <remarks>
/// <para>
/// 分数最高的那位在接下来的 PvP 里当 boss：生命上限 ×(1+n)，n = 总人数 − 2
/// （3 人局 ×2、4 人局 ×3、2 人局 n=0 没有血量加成，只剩每回合 +1 能量）。
/// </para>
/// <para>
/// 状态全部挂在「本局」上：记 <see cref="IRunState" /> 的引用，换局自动作废，
/// 免得上一局的 boss 带到下一局（这个仓库已经被跨局静态残留坑过三次：
/// 对端台账、合流大厅、票箱）。
/// </para>
/// <para>
/// 上限的还原记的是「施加 boss 倍率之前那一刻的上限」，不是角色卡上的原始值：
/// 决斗自己的「生命 ×N」也是叠在上限上的，把上限退回到角色卡原值会把它一起撤掉。
/// </para>
/// </remarks>
internal static class BossState
{
    private static IRunState? _run;
    private static ulong? _bossNetId;
    private static int _playerCount;

    /// <summary>施加 boss 倍率之前的上限（按 NetId 记，用于打完整场后还原）。</summary>
    private static readonly Dictionary<ulong, int> OriginalMaxHp = [];

    public static ulong? BossNetId => _bossNetId;

    /// <summary>本局有没有选出 boss（没有就不必为了加血去动上限）。</summary>
    public static bool HasBoss => _bossNetId is not null;

    /// <summary>BOSS 血量倍率：1+n，n = 人数 − 2。</summary>
    public static int HpMultiplier => _bossNetId is null ? 1 : System.Math.Max(1, _playerCount - 1);

    /// <summary>这位是不是本局的 boss。</summary>
    public static bool IsBoss(Player? player)
    {
        return player is not null
               && _bossNetId is { } boss
               && boss == player.NetId
               && ReferenceEquals(RunManager.Instance?.DebugOnlyGetState(), _run);
    }

    /// <summary>
    /// 选 boss（每次进入建筑师挑拨的事件页时调一次，结果幂等）。
    /// </summary>
    /// <returns>选出来了就 true。</returns>
    public static bool Elect(IRunState? state)
    {
        if (state is null || state.Players.Count < 2)
        {
            return false;
        }

        // 同一局里事件页会被重建很多次（每轮投票都会重新出选项），选一次就够。
        if (ReferenceEquals(_run, state) && _bossNetId is not null)
        {
            return true;
        }

        // 只有贪婪模式才计分（YG 的口径：这一套是「补尾刀抢金币/遗物」的延伸）。
        if (!YouJinSettings.GreedyEnabled)
        {
            ModInfo.Info("[建筑师挑拨] 不是贪婪模式，不计分、不设 boss");
            return false;
        }

        var ranked = BossScoring.Rank(state);

        if (ranked.Count == 0)
        {
            return false;
        }

        _run = state;
        _playerCount = state.Players.Count;
        _bossNetId = ranked[0].NetId;

        ModInfo.Info(
            $"[建筑师挑拨] 计分完成（普通 {BossScoring.CommonScore} / 罕见 {BossScoring.UncommonScore} / "
            + $"稀有 {BossScoring.RareScore} / 商店·事件·远古 {BossScoring.SideScore} / 初始不计，金币 1:1）：");

        foreach (var entry in ranked)
        {
            ModInfo.Info(
                $"[建筑师挑拨]   netId={entry.NetId} 遗物 {entry.Relics} + 金币 {entry.Gold} = {entry.Total}"
                + (entry.NetId == _bossNetId ? "  ← BOSS" : string.Empty));
        }

        ModInfo.Info(
            $"[建筑师挑拨] boss = netId={_bossNetId}，"
            + $"生命上限 ×{HpMultiplier}（人数 {_playerCount}），每回合 +1 能量");

        return true;
    }

    /// <summary>记下这位被加血之前的上限（只记第一次，避免重复乘）。</summary>
    public static void RememberOriginal(ulong netId, int maxHp)
    {
        OriginalMaxHp.TryAdd(netId, maxHp);
    }

    /// <summary>把 boss 的上限还原回去（每场 PvP 打完、回到事件页时调）。</summary>
    public static async Task RestoreAsync(IRunState? state)
    {
        if (OriginalMaxHp.Count == 0 || state is null)
        {
            return;
        }

        foreach (var player in state.Players)
        {
            if (!OriginalMaxHp.TryGetValue(player.NetId, out var original))
            {
                continue;
            }

            var creature = player.Creature;

            if (creature.MaxHp == original)
            {
                continue;
            }

            try
            {
                await CreatureCmd.SetMaxHp(creature, original);
                await CreatureCmd.SetCurrentHp(creature, original);
                ModInfo.Info($"[建筑师挑拨] boss 加成已还原：netId={player.NetId} 上限 → {original}");
            }
            catch (System.Exception ex)
            {
                ModInfo.Warn($"[建筑师挑拨] 还原 boss 上限失败（不影响战斗）：{ex.Message}");
            }
        }

        OriginalMaxHp.Clear();
    }

    /// <summary>换局 / 收场时清干净。</summary>
    public static void Reset()
    {
        _run = null;
        _bossNetId = null;
        _playerCount = 0;
        OriginalMaxHp.Clear();
    }
}
