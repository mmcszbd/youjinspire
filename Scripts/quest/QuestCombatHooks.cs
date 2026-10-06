// ============================================================================
// 任务模式（Quest）：战斗进度钩子（阶段 2）
// ----------------------------------------------------------------------------
// 一个 HookType.Combat 的单例，把游戏里的各种**战斗专属**事件翻译成"某玩家的某类任务 +N"。
//
// ⚠ 这一侧只放"反编译确认走 CombatState.IterateHookListeners"的钩子
//   （伤害/出牌/格挡/消耗/弃牌/Power/球/召唤/星辰/锻造）。
//   金币、回复、药水、击杀走的是 IRunState，一局级订阅者任何时候都能收到；
//   挂在战斗级上会漏掉战斗外的那些（奖励界面拿钱、火堆回血…），
//   所以那四条在 QuestRunHooks.cs。
// 所有统计都**从钩子参数里取玩家**（CardPlay.Player / potion.Owner / card.Owner …），
// 取不到才退回 QuestService.ActionOwner()（= 当前正在出牌的那个人，
// 用 ActionExecutor.CurrentlyRunningAction.OwnerId 判定，所有客户端一致）。
//
// 为什么不用 LocalContext.IsMe 之类：那个是"本机视角"，
// 联机时各端会算出不同结果 → 校验和分叉（见交接文档 §4.4-9）。
//
// 每个口径第一次真正收到事件时，QuestService 会打一行 `首次收到进度事件 goal=…`，
// 用来确认钩子确实生效。
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts.Quest;

/// <summary>把战斗事件推进到任务进度。</summary>
[RegisterSingleton]
public class QuestCombatHooks : HookedSingletonModel
{
    public QuestCombatHooks() : base(HookType.Combat)
    {
    }

    private static bool Active => QuestService.Enabled;

    /// <summary>把"生物"换算成"它的主人玩家"（宠物算宠物主）。</summary>
    private static Player? PlayerOf(Creature? creature)
    {
        if (creature is null)
            return null;

        return creature.Player ?? creature.PetOwner;
    }

    private static void Report(Player? player, QuestGoal goal, int amount)
        => QuestService.Report(player, goal, amount);

    // ------------------------------------------------------------------
    // 造成伤害 / 击杀
    // ------------------------------------------------------------------

    /// <summary>
    /// 每次"伤害被造成"都会走这里（致命一击也会走 —— 它不会触发 AfterDamageReceived）。
    /// 参数是 dealer 在前、target 在后（和 AfterDamageReceived 相反）。
    /// </summary>
    public override Task AfterDamageGiven(
        PlayerChoiceContext choiceContext,
        Creature? dealer,
        DamageResult result,
        ValueProp props,
        Creature target,
        CardModel? cardSource)
    {
        if (!Active)
            return Task.CompletedTask;

        // 打在玩家/队友身上不算"造成伤害"（自己人互殴不刷任务）。
        if (target.Player is not null)
            return Task.CompletedTask;

        var amount = result.UnblockedDamage;
        if (amount <= 0)
            return Task.CompletedTask;

        var player = PlayerOf(dealer) ?? QuestService.ActionOwner();
        if (player is null)
            return Task.CompletedTask;

        Report(player, QuestGoal.DealDamage, amount);

        if (cardSource?.Type == CardType.Attack)
            Report(player, QuestGoal.AttackDamage, amount);

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 出牌 / 消耗 / 弃牌
    // ------------------------------------------------------------------

    public override Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay play)
    {
        if (!Active)
            return Task.CompletedTask;

        var player = play.Player;
        if (player is null)
            return Task.CompletedTask;

        Report(player, QuestGoal.PlayCards, 1);

        switch (play.Card?.Type)
        {
            case CardType.Attack:
                Report(player, QuestGoal.PlayAttacks, 1);
                break;
            case CardType.Skill:
                Report(player, QuestGoal.PlaySkills, 1);
                break;
            case CardType.Power:
                Report(player, QuestGoal.PlayPowers, 1);
                break;
        }

        return Task.CompletedTask;
    }

    public override Task AfterCardExhausted(PlayerChoiceContext choiceContext, CardModel card, bool flag)
    {
        if (Active)
            Report(card.Owner, QuestGoal.ExhaustCards, 1);

        return Task.CompletedTask;
    }

    public override Task AfterCardDiscarded(PlayerChoiceContext choiceContext, CardModel card)
    {
        if (Active)
            Report(card.Owner, QuestGoal.DiscardCards, 1);

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 格挡
    // ------------------------------------------------------------------

    public override Task AfterBlockGained(Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        if (Active && amount > 0)
            Report(creature.Player, QuestGoal.GainBlock, (int)amount);

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Power：力量 / 中毒 / 集中
    // ------------------------------------------------------------------

    public override Task AfterPowerAmountChanged(
        PlayerChoiceContext choiceContext,
        PowerModel power,
        decimal amount,
        Creature? applier,
        CardModel? cardSource)
    {
        if (!Active || amount <= 0)
            return Task.CompletedTask;

        var applied = (int)amount;
        var owner = power.Owner;

        switch (power)
        {
            // "获得力量/集中"：加在自己身上，直接认目标玩家。
            case StrengthPower:
                Report(owner?.Player, QuestGoal.GainStrength, applied);
                break;

            case FocusPower:
                Report(owner?.Player, QuestGoal.GainFocus, applied);
                break;

            // "施加中毒"：目标一般是敌人，施加者优先取 power.Applier。
            case PoisonPower:
                Report(
                    PlayerOf(applier) ?? PlayerOf(power.Applier) ?? QuestService.ActionOwner(),
                    QuestGoal.ApplyPoison,
                    applied);
                break;
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 角色专属：球 / 召唤 / 星辰 / 锻造
    // ------------------------------------------------------------------

    public override Task AfterOrbChanneled(PlayerChoiceContext choiceContext, Player player, OrbModel orb)
    {
        if (Active)
            Report(player, QuestGoal.ChannelOrbs, 1);

        return Task.CompletedTask;
    }

    public override Task AfterOrbEvoked(
        PlayerChoiceContext choiceContext,
        OrbModel orb,
        IEnumerable<Creature> targets)
    {
        if (Active)
            Report(orb.Owner ?? QuestService.ActionOwner(), QuestGoal.EvokeOrbs, 1);

        return Task.CompletedTask;
    }

    public override Task AfterSummon(PlayerChoiceContext choiceContext, Player player, decimal amount)
    {
        if (Active && amount > 0)
            Report(player, QuestGoal.Summon, 1);

        return Task.CompletedTask;
    }

    public override Task AfterStarsGained(int amount, Player player)
    {
        if (Active && amount > 0)
            Report(player, QuestGoal.GainStars, amount);

        return Task.CompletedTask;
    }

    public override Task AfterForge(decimal amount, Player player, AbstractModel? source)
    {
        if (Active && amount > 0)
            Report(player, QuestGoal.Forge, 1);

        return Task.CompletedTask;
    }
}
