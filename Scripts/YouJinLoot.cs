// ============================================================================
// 友尽尖塔（youjin）：多人模式「谁给最后一击，金币+遗物全归谁」
// ----------------------------------------------------------------------------
// 规则（按用户定稿）：
//   ① 「结束战斗的那次击杀」决定归属：本场所有玩家本应拿到的金币总和 + 遗物总和都归击杀者；
//   ② 卡牌、药水不抢，各自照常拿；
//   ③ 被毒死 → 给「上毒最多」的玩家；被灾厄（Doom）炸死 → 给「上灾厄最多」的玩家；
//   ④ 荆棘/反伤/没有任何归属的伤害 → 在存活玩家里随机（用本局 RNG，联机各端结果一致）；
//   ⑤ 击杀者哪怕自己已经死了，战利品照样给他；
//   ⑥ 单人局不做任何改动。
//
// 为什么要有两个类：
//   RitsuLib 的 HookedSingletonModel 一次只能订阅一条钩子流：
//     HookType.Run    → 只收到「一局游戏级」的钩子（奖励列表就是这一类：Hook.ModifyRewards
//                       内部是 runState.IterateHookListeners(null)，压根不会遍历战斗级订阅者）；
//     HookType.Combat → 只收到「战斗级」的钩子（伤害、死亡、Power 层数变化）。
//   而我们的功能两边的钩子都要用，所以拆成「战斗侧记账」+「结算侧分赃」两个单例，
//   中间用静态账本 LootLedger 传递数据（同一个进程内共享，逻辑纯确定性，联机各端算出来一样）。
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>
/// 本场战斗的「战利品账本」。战斗侧单例写入，结算侧单例读取。
/// </summary>
internal static class LootLedger
{
    /// <summary>用来判断"换战斗了"的标记，直接用当前的 CombatState 实例。</summary>
    private static object? _combatKey;

    /// <summary>对某只怪最后一击的来源玩家；null 表示这一击"无主"（中毒跳伤、反伤、环境伤害……）。</summary>
    public static readonly Dictionary<Creature, Player?> LastDamageDealer = new();

    /// <summary>对某只怪累计"上毒"的层数：key = (怪, 玩家)。</summary>
    public static readonly Dictionary<(Creature Target, Player Applier), int> PoisonApplied = new();

    /// <summary>对某只怪累计"施加灾厄"的层数：key = (怪, 玩家)。</summary>
    public static readonly Dictionary<(Creature Target, Player Applier), int> DoomApplied = new();

    /// <summary>结束本场战斗的那次击杀归属（可能为 null = 没识别出来）。</summary>
    public static Player? LastKiller;

    /// <summary>是否成功识别过击杀者（识别不出来时结算侧会走兜底逻辑，避免战利品凭空消失）。</summary>
    public static bool HasKiller;

    /// <summary>换了一场战斗就把上一场的数据清空。</summary>
    public static void BeginCombat(object? combatKey)
    {
        if (combatKey is null || ReferenceEquals(_combatKey, combatKey))
            return;

        _combatKey = combatKey;
        LastDamageDealer.Clear();
        PoisonApplied.Clear();
        DoomApplied.Clear();
        LastKiller = null;
        HasKiller = false;
    }
}

/// <summary>
/// 战斗侧：记录"谁打出了最后一击"、"谁上了多少毒/灾厄"。
/// 订阅战斗钩子（HookType.Combat）。
/// </summary>
[RegisterSingleton]
public class CombatLootTracker : HookedSingletonModel
{
    public CombatLootTracker() : base(HookType.Combat)
    {
    }

    /// <summary>战斗开始时清空上一场的数据。</summary>
    public override Task BeforeCombatStart()
    {
        LootLedger.BeginCombat(CurrentCombatState);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 每次"伤害被造成"时都会走这里，记录这只怪最后一击是谁打的。
    ///
    /// 为什么不用 AfterDamageReceived：
    ///   原版 CreatureCmd.Damage 里，如果这一击把目标打死了（WasTargetKilled 且目标已死），
    ///   会直接跳去处理死亡流程，**跳过 Hook.AfterDamageReceived**；
    ///   只有"没打死"的伤害才会触发 AfterDamageReceived。
    ///   所以致命一击只能在 AfterDamageGiven（无条件调用）里拿到。
    ///
    /// 参数顺序也和 AfterDamageReceived 相反：dealer 在前（第 2 个），target 在后（第 5 个）。
    /// </summary>
    public override Task AfterDamageGiven(
        PlayerChoiceContext choiceContext,
        Creature? dealer,
        DamageResult result,
        ValueProp props,
        Creature target,
        CardModel? cardSource)
    {
        if (!target.IsEnemy)
            return Task.CompletedTask;

        LootLedger.BeginCombat(target.CombatState);

        if (result.UnblockedDamage > 0 || result.WasTargetKilled)
            LootLedger.LastDamageDealer[target] = OwnerPlayerOf(dealer);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 每一方回合开始时清空"最后一击来源"。
    /// 中毒跳伤、灾厄这类无主伤害（dealer == null）在原版里同样什么钩子都不触发，
    /// 不清账的话，上一回合打过这只怪的玩家会被误认成击杀者；
    /// 清掉之后这类死亡会自然落到"上毒最多 / 上灾厄最多"的判定上。
    /// </summary>
    public override Task BeforeSideTurnStart(
        PlayerChoiceContext choiceContext,
        CombatSide side,
        IReadOnlyList<Creature> creatures,
        ICombatState combatState)
    {
        LootLedger.LastDamageDealer.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Power 层数变化：毒、灾厄分别记账，用来实现"毒死给上毒最多的人"。
    /// 施加者优先取 power.Applier（Power 模型自己记着施加者），拿不到再退回到参数里的 applier。
    /// </summary>
    public override Task AfterPowerAmountChanged(
        PlayerChoiceContext choiceContext,
        PowerModel power,
        decimal amount,
        Creature? applier,
        CardModel? cardSource)
    {
        if (amount <= 0)
            return Task.CompletedTask;

        var target = power.Owner;
        if (target is null || !target.IsEnemy)
            return Task.CompletedTask;

        var player = OwnerPlayerOf(power.Applier) ?? OwnerPlayerOf(applier);
        if (player is null)
            return Task.CompletedTask;

        if (power is PoisonPower)
            Add(LootLedger.PoisonApplied, target, player, (int)amount);
        else if (power is DoomPower)
            Add(LootLedger.DoomApplied, target, player, (int)amount);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 敌人即将死亡 → 结算这次击杀的归属，并记成"本场最后一次击杀"。
    /// 因为会不断覆盖，所以留在账本里的天然就是"结束战斗的那一次击杀"。
    /// </summary>
    public override Task BeforeDeath(Creature creature)
    {
        if (!creature.IsEnemy)
            return Task.CompletedTask;

        LootLedger.BeginCombat(creature.CombatState);

        var (killer, reason) = ResolveKiller(creature);
        if (killer is null)
            return Task.CompletedTask;

        LootLedger.LastKiller = killer;
        LootLedger.HasKiller = true;
        Entry.Logger.Info($"击杀归属：{creature.Name} → NetId={killer.NetId}（依据：{reason}）");
        return Task.CompletedTask;
    }

    /// <summary>判断这只怪"算谁打死的"。顺序就是规则里的优先级。</summary>
    private static (Player? Killer, string Reason) ResolveKiller(Creature dead)
    {
        var combat = dead.CombatState;
        var players = combat?.Players;
        if (players is null || players.Count == 0)
            return (null, "没有玩家");

        // ① 最后一击有明确来源（玩家本体，或玩家的宠物）→ 归他。
        if (LootLedger.LastDamageDealer.TryGetValue(dead, out var dealer) && dealer is not null)
            return (dealer, "最后一击的伤害来源");

        // ② 最后一击是"无主伤害"（中毒跳伤、反伤、环境伤害……）：
        //    身上带灾厄的话先认灾厄，再看毒。
        if (dead.Powers.OfType<DoomPower>().Any())
        {
            var doomApplier = BestApplier(LootLedger.DoomApplied, dead);
            if (doomApplier is not null)
                return (doomApplier, "上灾厄最多");
        }

        var poisonApplier = BestApplier(LootLedger.PoisonApplied, dead);
        if (poisonApplier is not null)
            return (poisonApplier, "上毒最多");

        // ③ 实在查不到：在存活玩家里随机一个（用本局 RNG，联机各端结果一致）。
        var random = RandomAlivePlayer(players, combat);
        return (random, random is null ? "没有可分配的玩家" : "无主伤害 → 随机存活玩家");
    }

    /// <summary>
    /// 灾厄（Doom）是原版用 CreatureCmd.Kill 直接"抹杀"的，根本不走伤害流程，
    /// 光靠 BeforeDeath 会认错人。这个钩子在灾厄击杀之后触发，用它做一次权威修正：
    /// 把归属改成"上灾厄最多"的玩家（拿不到就退回灾厄这个 Power 自己的施加者）。
    /// </summary>
    public override Task AfterDiedToDoom(
        PlayerChoiceContext choiceContext,
        IReadOnlyList<Creature> creatures)
    {
        foreach (var creature in creatures)
        {
            LootLedger.BeginCombat(creature.CombatState);

            var killer = BestApplier(LootLedger.DoomApplied, creature)
                         ?? OwnerPlayerOf(creature.Powers.OfType<DoomPower>().FirstOrDefault()?.Applier);
            if (killer is null)
                continue;

            LootLedger.LastKiller = killer;
            LootLedger.HasKiller = true;
            Entry.Logger.Info($"灾厄击杀归属：{creature.Name} → NetId={killer.NetId}（依据：上灾厄最多）");
        }

        return Task.CompletedTask;
    }

    /// <summary>在账本里找出对这只怪施加层数最多的玩家。</summary>
    private static Player? BestApplier(
        Dictionary<(Creature Target, Player Applier), int> table,
        Creature target)
    {
        Player? best = null;
        var bestAmount = 0;

        foreach (var pair in table)
        {
            if (!ReferenceEquals(pair.Key.Target, target))
                continue;

            if (pair.Value <= bestAmount)
                continue;

            bestAmount = pair.Value;
            best = pair.Key.Applier;
        }

        return best;
    }

    /// <summary>在存活玩家里随机挑一个；没有存活的就退回所有玩家。</summary>
    private static Player? RandomAlivePlayer(IReadOnlyList<Player> players, ICombatState? combat)
    {
        var alive = players.Where(player => player.Creature is null || player.Creature.IsAlive).ToList();
        var pool = alive.Count > 0 ? alive : players.ToList();
        if (pool.Count == 0)
            return null;

        var rng = combat?.RunState?.Rng?.GetRng(RunRngType.Niche);
        var index = rng is not null ? rng.NextInt(0, pool.Count) : 0;
        return pool[index];
    }

    /// <summary>把生物换算成"它属于哪个玩家"（宠物算宠物主）。敌人返回 null。</summary>
    private static Player? OwnerPlayerOf(Creature? creature)
        => creature?.Player ?? creature?.PetOwner;

    private static void Add(
        Dictionary<(Creature Target, Player Applier), int> table,
        Creature target,
        Player applier,
        int amount)
    {
        var key = (target, applier);
        table[key] = table.TryGetValue(key, out var current) ? current + amount : amount;
    }
}

/// <summary>
/// 结算侧：把所有人的金币/遗物奖励从各自奖励列表里拿出来，塞进击杀者那一份。
/// 订阅一局游戏级钩子（HookType.Run），因为奖励钩子只发给 run 级订阅者。
/// </summary>
[RegisterSingleton]
public class CombatLootSplitter : HookedSingletonModel
{
    /// <summary>当前正在处理的战斗房间，用来识别"换房间 = 开始新的分赃"。</summary>
    private object? _roomKey;

    private int _potGold;
    private readonly List<RelicModel> _potRelics = new();
    private bool _handedOut;

    public CombatLootSplitter() : base(HookType.Run)
    {
    }

    /// <summary>
    /// 贪婪模式：所有"获得金币" +20%。
    ///
    /// 反编译确认：PlayerCmd.GainGold 在把钱给玩家之前会先走
    ///     Hook.ModifyGoldGained(runState, player, amount, out modifiers)
    /// 逐个监听者问"这笔要改成多少"，我们只要在贪婪模式开着时乘 1.2 就行。
    /// 原版没有任何遗物/能力实现这个钩子（搜索只有基类声明 + 分发器 + GainGold 一个调用点），
    /// 是官方留的开放插槽。
    ///
    /// 注意副作用：玩家之间的转账（假寐偷钱、死亡分钱、法官事件）也走 GainGold，
    /// 所以那些"获得"同样会 +20% —— 假寐偷 30%，小偷实际到手 30% × 1.2 = 36%，
    /// 而被偷的人只掉 30%。如果要严格"钱守恒"，就得改成只在战斗奖励里加成。
    /// </summary>
    public override decimal ModifyGoldGained(Player player, decimal amount)
    {
        if (!YouJinSettings.GameplayEnabled || !YouJinSettings.GreedyLootEnabled)
            return amount;

        if (amount <= 0m)
            return amount;

        return amount * 1.2m;
    }

    /// <summary>
    /// 每个玩家生成奖励列表时各调用一次（原版：RewardsSet.GenerateWithoutOffering → Hook.ModifyRewards）。
    /// 此时金币/遗物奖励已经 Populate 完成，金额和遗物都是确定的了，正好可以搬走。
    /// 返回值 true = 我们改过这个列表（游戏会因此回调 AfterModifyingRewards）。
    /// </summary>
    public override bool TryModifyRewards(Player? player, List<Reward>? rewards, AbstractRoom? room)
    {
        // 总开关或「贪婪模式」关了，就完全按原版走（各拿各的奖励）
        if (!YouJinSettings.GameplayEnabled || !YouJinSettings.GreedyLootEnabled)
            return false;

        if (player is null || rewards is null || room is not CombatRoom combatRoom)
            return false;

        // 单人局没有抢的必要，保持原样。
        if (combatRoom.CombatState is null || combatRoom.CombatState.Players.Count <= 1)
            return false;

        if (!ReferenceEquals(_roomKey, combatRoom))
        {
            _roomKey = combatRoom;
            _potGold = 0;
            _potRelics.Clear();
            _handedOut = false;
        }

        var changed = false;

        // 倒着遍历，方便一边扫一边删。
        for (var i = rewards.Count - 1; i >= 0; i--)
        {
            switch (rewards[i])
            {
                case GoldReward gold:
                    _potGold += gold.Amount;
                    rewards.RemoveAt(i);
                    changed = true;
                    break;

                case RelicReward { Relic: not null } relic:
                    _potRelics.Add(relic.Relic);
                    rewards.RemoveAt(i);
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            Entry.Logger.Info(
                $"收取 NetId={player.NetId} 的战利品 → 当前奖池：金币 {_potGold}，遗物 {_potRelics.Count} 件");
        }

        return changed;
    }

    /// <summary>
    /// 每个玩家的奖励列表即将展示给本人时各调用一次。
    /// 所有玩家的奖励列表都已经生成完毕，所以这时候奖池一定是完整的。
    /// 我们在击杀者这一份里补上合并后的金币和遗物（新建 Reward，归属写击杀者）。
    /// </summary>
    public override Task BeforeCombatRewardOffered(RewardsSet rewards, CombatRoom room)
    {
        if (!YouJinSettings.GameplayEnabled || !YouJinSettings.GreedyLootEnabled)
            return Task.CompletedTask;

        if (_handedOut || !ReferenceEquals(_roomKey, room))
            return Task.CompletedTask;

        var target = rewards.Player;
        if (target is null)
            return Task.CompletedTask;

        // 正常情况下只给击杀者这一份。
        // 万一没识别出击杀者（例如中途加过 mod），就退化成"第一个被展示奖励的玩家"，
        // 宁可给错人，也不能让奖励凭空消失。
        var killer = LootLedger.HasKiller ? LootLedger.LastKiller : null;
        if (killer is not null && !ReferenceEquals(killer, target))
            return Task.CompletedTask;

        _handedOut = true;

        if (_potGold > 0)
            rewards.Rewards.Add(new GoldReward(_potGold, target, false));

        foreach (var relic in _potRelics)
            rewards.Rewards.Add(new RelicReward(relic, target));

        if (_potGold > 0 || _potRelics.Count > 0)
        {
            Entry.Logger.Info(
                $"全部战利品 → NetId={target.NetId}：金币 {_potGold}，遗物 {_potRelics.Count} 件");
        }

        return Task.CompletedTask;
    }
}
