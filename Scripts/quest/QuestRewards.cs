// ============================================================================
// 任务模式（Quest）：任务奖励（阶段 4）
// ----------------------------------------------------------------------------
// 打完本层 Boss 后，把这一层**已完成且没发过**的任务奖励挂进奖励界面。
//
// 两种奖励：
//   · 金币   → 直接用原版 GoldReward（能正常显示、能正常结算）；
//   · 最大生命 → RitsuLib 的自定义奖励 ModCustomReward：原版没有"加上限"这种奖励，
//                自己写一个，结算时走 CreatureCmd.GainMaxHp。
//
// 多个奖励用 RitsuLib 的 LinkedRewardSets.Create(...) 包成一个"关联奖励组"
// （TakeAll：点一次全拿），就是任务模式面板上那句"本层任务奖励"。
//
// 发放标记（QuestProgress.RewardGranted）写进本局存档，退出重进不会重复发。
//
// ⚠ 自定义奖励必须在游戏反序列化奖励之前注册，所以注册放在 Entry.Init()。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Combat.Rewards;
using STS2RitsuLib.Ui.Toast;

namespace YouJin.Scripts.Quest;

/// <summary>自定义奖励：最大生命 +N。</summary>
public sealed class QuestMaxHpReward : ModCustomReward
{
    /// <summary>模组内的本地 id（注册后会变成 YOUJIN_REWARD_QUEST_MAX_HP）。</summary>
    public const string LocalId = "quest_max_hp";

    private readonly int _amount;

    public QuestMaxHpReward(Player player, int amount)
        : base(player)
    {
        _amount = Math.Max(1, amount);
        QuestRewards.EnsureMaxHpText(_amount);
    }

    /// <summary>描述文本的本地化键（按数值分，注入到游戏的 gameplay_ui 表里）。</summary>
    protected override string DescriptionLocKey => QuestRewards.MaxHpKey(_amount);

    /// <summary>RitsuLib 给这个自定义奖励动态分配的 RewardType（注册时拿到）。</summary>
    public override RewardType ModRewardType => QuestRewards.MaxHpRewardType;

    /// <summary>图鉴"已见过"标记：这个奖励不进图鉴，什么都不用做。</summary>
    public override void MarkContentAsSeen()
    {
    }

    /// <summary>选中时真正加上限。</summary>
    protected override async Task<bool> OnSelect()
    {
        try
        {
            await CreatureCmd.GainMaxHp(Player.Creature, _amount);
            Entry.Logger.Info($"任务模式：发放奖励 最大生命 +{_amount}（NetId={Player.NetId}）");
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：发放最大生命奖励失败（NetId={Player.NetId}）{ex}");
            return false;
        }
    }

    /// <summary>存档用：只存数值，工厂里再还原。</summary>
    public override string ToModRewardJson() => _amount.ToString();
}

/// <summary>任务奖励的注册与发放。</summary>
public static class QuestRewards
{
    private const string DescriptionLocTable = "gameplay_ui";

    /// <summary>一次最多挂几条奖励 —— 防止任何 bug 再把奖励界面撑爆（线上炸过一次）。</summary>
    private const int MaxItemsPerOffer = 6;

    /// <summary>
    /// 「这一局这个玩家的这一层已经挂过奖励了」的幂等标记。
    /// 键 = 局种子 + NetId + 层数；同一个进程里 Boss 房会被建两次
    /// （ActiveCombat → FinishedCombat），两次都会调 BeforeCombatRewardOffered，
    /// 光靠任务书里的 RewardGranted 挡不住（实测漏了）。
    /// </summary>
    private static readonly HashSet<string> AttachedKeys = new();

    private static bool _initialized;
    private static string _maxHpIdStem = "YOUJIN_REWARD_QUEST_MAX_HP";

    /// <summary>注册时由 RitsuLib 分配的奖励类型，QuestMaxHpReward 需要它。</summary>
    public static RewardType MaxHpRewardType { get; private set; }

    /// <summary>在 Entry.Init 里调用一次：注册自定义奖励类型。</summary>
    public static void Init()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            var definition = ModRewardRegistry
                .For(Entry.ModId)
                .RegisterOwned(QuestMaxHpReward.LocalId, Factory);

            if (!string.IsNullOrEmpty(definition.Id))
                _maxHpIdStem = definition.Id;

            MaxHpRewardType = definition.RewardType;

            Entry.Logger.Info($"任务模式：已注册自定义奖励 quest_max_hp（id={_maxHpIdStem}）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：注册自定义奖励失败 {ex}");
        }
    }

    /// <summary>反序列化时把存档里的数值还原成奖励对象。</summary>
    private static Reward Factory(SerializableReward serializable, Player player, string? payload)
    {
        var amount = 1;
        if (!string.IsNullOrWhiteSpace(payload))
            int.TryParse(payload.Trim(), out amount);

        return new QuestMaxHpReward(player, Math.Max(1, amount));
    }

    /// <summary>某个数值对应的本地化键（每个数值一条）。</summary>
    public static string MaxHpKey(int amount) => $"{_maxHpIdStem}_{amount}";

    /// <summary>
    /// 本层 Boss 打完 → **直接把已完成的任务奖励发到手上**（不走奖励界面）。
    /// 每层都这样发；幂等 —— 发过的带 RewardGranted 标记 + 进程内 AttachedKeys 双保险，不会重复发。
    /// </summary>
    public static async Task GrantDirectlyAsync(CombatRoom room)
    {
        try
        {
            if (!QuestService.Enabled || room.RoomType != RoomType.Boss)
                return;

            var run = RunManager.Instance?.DebugOnlyGetState();
            if (run is null)
                return;

            foreach (var player in run.Players)
            {
                var book = QuestService.GetBook(player);
                if (book is null)
                    continue;

                var key = AttachKey(run, player.NetId, book.ActIndex);
                if (AttachedKeys.Contains(key))
                {
                    Entry.Logger.Info(
                        $"任务模式：第 {book.ActIndex + 1} 层 NetId={player.NetId} 的任务奖励已经发过了，跳过重复发放");
                    continue;
                }

                var pending = book.Quests
                    .Where(q => q.Completed && !q.RewardGranted && !q.Stolen
                                && QuestPools.Find(q.DefId) is not null)
                    .ToList();

                if (pending.Count == 0)
                    continue;

                var children = Cap(BuildChildren(player, pending, book.ActIndex));
                if (children.Count == 0)
                    continue;

                foreach (var reward in children)
                    await reward.SelectUnsynchronized();

                foreach (var quest in pending)
                    quest.RewardGranted = true;

                QuestService.SaveBookFor(player, book);
                AttachedKeys.Add(key);

                Entry.Logger.Info(
                    $"任务模式：第 {book.ActIndex + 1} 层 Boss 打完，直接发放任务奖励 → NetId={player.NetId}，共 {pending.Count} 项"
                    + $"（{string.Join("、", pending.Select(q => QuestPools.Find(q.DefId)?.Title ?? q.DefId))}）");

                Notify(player, pending);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：直接发放任务奖励失败 {ex}");
        }
    }

    /// <summary>
    /// 把这一批任务奖励变成奖励对象（数量跟着存档里存的值走），
    /// 并且**在返回之前就把每条奖励 populate 好**。
    /// </summary>
    private static List<Reward> BuildChildren(
        Player player,
        IEnumerable<QuestProgress> quests,
        int actIndex)
    {
        var children = new List<Reward>();

        foreach (var quest in quests)
        {
            var def = QuestPools.Find(quest.DefId);
            if (def is null)
                continue;

            var amount = Math.Max(1, quest.RewardAmount);

            switch (def.Reward)
            {
                case QuestRewardKind.MaxHp:
                    children.Add(new QuestMaxHpReward(player, amount));
                    break;

                case QuestRewardKind.Relic:
                    // 遗物**固定 1 件**（卧底偷来的翻倍 = 2 件）。
                    // ⚠ 千万别用存档里的 RewardAmount 当件数：遗物任务的数值字段是历史遗留，
                    //   曾经是默认的 100 → 一次挂了 100 个遗物，把奖励界面炸黑屏（线上实测）。
                    var relicCount = quest.StolenFrom != 0 ? 2 : 1;
                    for (var i = 0; i < relicCount; i++)
                        children.Add(new RelicReward(RelicRarity.Uncommon, player));
                    break;

                default:
                    children.Add(new GoldReward(amount, player, false));
                    break;
            }
        }

        // ★ 关键一步：奖励必须"生成时就填好"，否则奖励界面会炸。
        //   原版奖励都是 RewardsSet 生成时 populate 过的；我们直接 new 出来的 RelicReward
        //   里面 _relic 还是 null，NRewardsScreen._Ready 会对每条奖励调 MarkContentAsSeen()
        //   → MarkRelicAsSeen(relic=null) → NullReferenceException → 奖励界面坏掉/黑屏。
        //   （0.6.7 只修了"件数"，populate 这步当时只写在最后一层直接发放那条路径上。）
        //
        //   随机源用「玩家种子 + 固定流名」，各端滚出来的是同一件（联机一致）。
        var prepared = new List<Reward>();

        for (var i = 0; i < children.Count; i++)
        {
            var reward = children[i];

            try
            {
                reward.SetRng(new Rng(
                    player.PlayerRng.Seed,
                    $"{Entry.ModId}/questreward/act{actIndex}/{i}"));

                if (!reward.IsPopulated)
                    reward.Populate();

                if (!reward.IsPopulated)
                {
                    Entry.Logger.Info(
                        $"任务模式：奖励 {reward.GetType().Name} 没能初始化，已丢弃"
                        + "（宁可不发，也不能让奖励界面炸掉）");
                    continue;
                }

                prepared.Add(reward);
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"任务模式：初始化奖励 {reward.GetType().Name} 失败，已丢弃 {ex}");
            }
        }

        return prepared;
    }

    /// <summary>奖励条目数上限保险：超过就截断并记日志，避免再把奖励界面撑爆。</summary>
    private static List<Reward> Cap(List<Reward> children)
    {
        if (children.Count <= MaxItemsPerOffer)
            return children;

        Entry.Logger.Info(
            $"任务模式：本次要挂 {children.Count} 条奖励，超过上限 {MaxItemsPerOffer}，"
            + $"只挂前 {MaxItemsPerOffer} 条（防界面炸）");

        return children.Take(MaxItemsPerOffer).ToList();
    }

    /// <summary>「这一局、这个玩家、这一层」的幂等键。</summary>
    private static string AttachKey(RunState run, ulong netId, int actIndex)
        => $"{run.Rng.Seed}:{netId}:{actIndex}";

    /// <summary>给本机玩家弹一条提示（别人的奖励不用提示）。</summary>
    private static void Notify(Player player, List<QuestProgress> granted)
    {
        try
        {
            if (!LocalContext.IsMe(player))
                return;

            var text = string.Join(
                "、",
                granted.Select(q => QuestPools.Find(q.DefId)?.Title ?? q.DefId));

            RitsuToastService.ShowInfo(
                QuestText.IsChinese ? "任务奖励已发放" : "Quest rewards granted",
                QuestText.IsChinese ? $"最终层任务奖励：{text}" : text,
                null!);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：任务奖励提示失败 {ex}");
        }
    }

    /// <summary>把"最大生命 +N"的文案注入游戏的 gameplay_ui 表（运行时合并，不用重导 PCK）。</summary>
    public static void EnsureMaxHpText(int amount)
    {
        var value = Math.Max(1, amount);
        var key = MaxHpKey(value);
        LocInject.Merge(DescriptionLocTable, key, chinese => new Dictionary<string, string>
        {
            [key] = chinese
                ? $"本层任务奖励：最大生命 +{value}"
                : $"Act quest reward: Max HP +{value}",
        }, $"任务模式：{key}");
    }

}
