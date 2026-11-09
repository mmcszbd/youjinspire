using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using STS2RitsuLib;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

// ===== 验证用探针（不影响游戏逻辑，只在日志里打印）=====
// 想搞清楚奖励的生命周期：
//   ① TryModifyRewards(玩家, 奖励列表, 房间)  —— 奖励「刚生成」时（更早）
//   ② BeforeCombatRewardOffered(RewardsSet, 房间) —— 奖励「即将展示」时（更晚）
// 只要在日志里看到这两个钩子的触发时机和内容，就知道正式逻辑该写在哪。
[RegisterSingleton]
public class RewardProbe : HookedSingletonModel
{
    public RewardProbe() : base(HookType.Combat)
    {
    }

    // ① 奖励刚生成（还没进 RewardsSet）
    public override bool TryModifyRewards(Player? player, List<Reward>? rewards, AbstractRoom? room)
    {
        Entry.Logger.Info(
            $"[youjin:probe-A] TryModifyRewards 玩家NetId={player?.NetId} 房间={room?.GetType().Name} " +
            $"条目数={rewards?.Count} => {Describe(rewards)}");

        return false;   // false = 我们没有修改（保持原版行为）
    }

    // ② 奖励即将展示
    // ⚠ 这个钩子（BeforeCombatRewardOffered）**只有 0.108+ 才有**，
    //    0.107 的构建（-c Debug107，会定义 DEBUG107）里没有它 → 条件编译掉。
#if !DEBUG107
    public override Task BeforeCombatRewardOffered(RewardsSet rewards, CombatRoom room)
    {
        var gold = rewards.Rewards.OfType<GoldReward>().Sum(g => g.Amount);
        var relicCount = rewards.Rewards.OfType<RelicReward>().Count();

        Entry.Logger.Info(
            $"[youjin:probe-B] BeforeCombatRewardOffered 玩家NetId={rewards.Player?.NetId} " +
            $"角色={rewards.Player?.Character?.Id.Entry} 现有金币={rewards.Player?.Gold} " +
            $"| 条目数={rewards.Rewards.Count} 金币合计={gold} 遗物数={relicCount} => {Describe(rewards.Rewards)}");

        return Task.CompletedTask;
    }
#endif

    private static string Describe(IEnumerable<Reward>? rewards)
    {
        if (rewards is null)
            return "null";

        return string.Join(" | ", rewards.Select(r => r switch
        {
            GoldReward g => $"Gold({g.Amount})",
            RelicReward relic => $"Relic({relic.Relic?.Id.Entry})",
            _ => r.GetType().Name
        }));
    }
}
