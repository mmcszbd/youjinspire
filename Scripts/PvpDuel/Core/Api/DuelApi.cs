using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using PvpDuel.Core.Duel;

namespace PvpDuel.Api;

/// <summary>给别的 mod 用的决斗接口：改限制数值、整体接管或取消我们的规则、读当前用量。</summary>
/// <remarks>
/// <para>
/// 别的 mod <b>不需要编译期引用</b>：用 RitsuLib 的
/// <c>[ModInterop("pvp_duel", "PvpDuel.Api.DuelApi")]</c> 写一个存根、成员签名逐字照抄即可
/// （本类刻意只用简单类型：<c>bool</c> / <c>int</c> / <c>string</c> 与游戏自带的 <c>Player</c>、<c>CardModel</c>）。
/// </para>
/// <para>
/// 三类用法：
/// <list type="number">
/// <item><description><b>只改数值</b>：<see cref="SetLimits" /> / <see cref="SetCompensation" /> —— 执行还是我们来。</description></item>
/// <item><description><b>整体取消我们的</b>：<see cref="BuiltInRulesEnabled" /> 设 <c>false</c> ——
/// 我们不再执行任何限制（数值与查询照旧可用，方便调用方自己实现）。</description></item>
/// <item><description><b>读用量做 UI</b>：<see cref="CardsPlayedThisTurn" />、<see cref="SameCardPlaysThisTurn" />、
/// <see cref="TurnsTaken" />、<see cref="CurrentRound" />、<see cref="Describe" />。</description></item>
/// </list>
/// </para>
/// <para>
/// 设置页里那些数值是给玩家调参用的，这里是给 mod 用的；两者最终都汇到同一份"生效值"，
/// <see cref="ClearOverrides" /> 可以把控制权交回设置页。
/// </para>
/// </remarks>
public static class DuelApi
{
    /// <summary>我们的规则总开关：设 false = 不再执行任何限制（数值与查询仍然可用）。</summary>
    public static bool BuiltInRulesEnabled
    {
        get => DuelConfig.RulesEnabledOverride ?? true;
        set => DuelConfig.SetRulesEnabled(value);
    }

    /// <summary>覆盖三条限制。每项传 <c>-1</c> 表示该项不改；<c>0</c> = 不限制。</summary>
    public static void SetLimits(int maxCardsPerTurn, int maxSameCardPerTurn, int extraTurnLimit)
    {
        if (maxCardsPerTurn >= 0)
        {
            DuelConfig.SetMaxCardsPerTurn(maxCardsPerTurn);
        }

        if (maxSameCardPerTurn >= 0)
        {
            DuelConfig.SetMaxSameCardPerTurn(maxSameCardPerTurn);
        }

        if (extraTurnLimit >= 0)
        {
            DuelConfig.SetExtraTurnLimit(extraTurnLimit);
        }
    }

    /// <summary>覆盖后手补偿。每项传 <c>-1</c> 表示该项不改。</summary>
    public static void SetCompensation(int energy, int block, int cards)
    {
        if (energy >= 0)
        {
            DuelConfig.SetSecondPlayerEnergy(energy);
        }

        if (block >= 0)
        {
            DuelConfig.SetSecondPlayerBlock(block);
        }

        if (cards >= 0)
        {
            DuelConfig.SetSecondPlayerCards(cards);
        }
    }

    /// <summary>清掉所有覆盖，回到设置页里的值。</summary>
    public static void ClearOverrides()
    {
        DuelConfig.ClearOverrides();
    }

    /// <summary>本回合这位玩家打了几张牌。</summary>
    public static int CardsPlayedThisTurn(Player player)
    {
        return DuelBalance.CardsPlayedThisTurn(player);
    }

    /// <summary>这张牌本回合打了几次。</summary>
    public static int SameCardPlaysThisTurn(CardModel card)
    {
        return DuelBalance.SameCardPlaysThisTurn(card);
    }

    /// <summary>这位玩家在本场战斗里打过几个回合（后手补偿就是按它判"第一回合"的）。</summary>
    public static int TurnsTaken(Player player)
    {
        return DuelBalance.TurnsTaken(player);
    }

    /// <summary>当前回合数（不在决斗中返回 0）。</summary>
    public static int CurrentRound()
    {
        return DuelSession.Combat?.RoundNumber ?? 0;
    }

    /// <summary>本回合出牌上限（生效值，0 = 不限制）。</summary>
    public static int MaxCardsPerTurn()
    {
        return DuelConfig.MaxCardsPerTurn;
    }

    /// <summary>同一张牌每回合上限（生效值，0 = 不限制）。</summary>
    public static int MaxSameCardPerTurn()
    {
        return DuelConfig.MaxSameCardPerTurn;
    }

    /// <summary>连续额外回合上限（生效值，0 = 不限制）。</summary>
    public static int ExtraTurnLimit()
    {
        return DuelConfig.ExtraTurnLimit;
    }

    /// <summary>一行摘要（想显示在 UI 上可以直接用）。</summary>
    public static string Describe()
    {
        return $"回合 {CurrentRound()}｜出牌上限 {MaxCardsPerTurn()}（0=不限）"
               + $"｜同卡上限 {MaxSameCardPerTurn()}（0=不限）"
               + $"｜额外回合上限 {ExtraTurnLimit()}（0=不限）"
               + $"｜规则={(BuiltInRulesEnabled ? "我们执行" : "已交给外部")}";
    }
}
