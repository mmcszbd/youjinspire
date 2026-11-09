using MegaCrit.Sts2.Core.Runs;

using PvpDuel.Core.Settings;

namespace PvpDuel.Core.Duel;

/// <summary>决斗参数的唯一来源：设置页的玩家值，外加外部 mod 通过 <c>DuelApi</c> 下的覆盖（覆盖优先）。</summary>
/// <remarks>
/// <para>
/// 本类分两半：上面是 <b>只读生效值</b>（全工程都从这里读），下面是 <b>覆盖写入</b>
/// （只由 <c>PvpDuel.Api.DuelApi</c> 调用；<c>null</c> = 该项交回设置页）。
/// 别的地方不要再各算一份"生效值"。
/// </para>
/// <para>所有数值都做上下限夹取：设置页能填出范围外的值，外部 mod 也可能传个离谱的数进来。</para>
/// </remarks>
internal static class DuelConfig
{
    private static PvpSettings Settings => PvpSettingsStore.Current;

    /// <summary>平衡规则总闸门：设置里的模块开关 × 外部 mod 的「是否让我们执行」。</summary>
    public static bool RulesEnabled => ModuleSwitches.Balance && (RulesEnabledOverride ?? true);

    /// <summary>本局是不是「正好两名玩家」—— 决斗唯一的硬前提。</summary>
    /// <remarks>事件是挂进事件池的，单人局也抽得到，所以入口与战斗改造都要问这一条。</remarks>
    public static bool IsTwoPlayerRun => RunManager.Instance.DebugOnlyGetState()?.Players.Count == 2;

    // ==================== 生效值（0 = 关闭该条规则）====================

    /// <summary>单回合出牌上限（达到就自动结束回合）。</summary>
    public static int MaxCardsPerTurn => Math.Clamp(MaxCardsPerTurnOverride ?? Settings.MaxCardsPerTurn, 0, 99);

    /// <summary>同一张牌每回合最多打出几次。</summary>
    public static int MaxSameCardPerTurn =>
        Math.Clamp(MaxSameCardPerTurnOverride ?? Settings.MaxSameCardPerTurn, 0, 99);

    /// <summary>同一玩家连续额外回合上限。</summary>
    public static int ExtraTurnLimit => Math.Clamp(ExtraTurnLimitOverride ?? Settings.ExtraTurnLimit, 0, 99);

    /// <summary>后手第一次回合的额外能量。</summary>
    public static int SecondPlayerEnergy =>
        Math.Clamp(SecondPlayerEnergyOverride ?? Settings.SecondPlayerEnergy, 0, 9);

    /// <summary>后手第一次回合的开局格挡。</summary>
    public static int SecondPlayerBlock =>
        Math.Clamp(SecondPlayerBlockOverride ?? Settings.SecondPlayerBlock, 0, 99);

    /// <summary>后手第一次回合的额外抽牌。</summary>
    public static int SecondPlayerCards =>
        Math.Clamp(SecondPlayerCardsOverride ?? Settings.SecondPlayerCards, 0, 9);

    // ==================== 覆盖值（只有 DuelApi 会写）====================

    /// <summary>外部 mod 的「是否让我们执行规则」；<c>null</c> = 允许。</summary>
    public static bool? RulesEnabledOverride { get; private set; }

    public static int? MaxCardsPerTurnOverride { get; private set; }

    public static int? MaxSameCardPerTurnOverride { get; private set; }

    public static int? ExtraTurnLimitOverride { get; private set; }

    public static int? SecondPlayerEnergyOverride { get; private set; }

    public static int? SecondPlayerBlockOverride { get; private set; }

    public static int? SecondPlayerCardsOverride { get; private set; }

    public static void SetRulesEnabled(bool? enabled)
    {
        RulesEnabledOverride = enabled;
    }

    public static void SetMaxCardsPerTurn(int? limit)
    {
        MaxCardsPerTurnOverride = limit;
    }

    public static void SetMaxSameCardPerTurn(int? limit)
    {
        MaxSameCardPerTurnOverride = limit;
    }

    public static void SetExtraTurnLimit(int? limit)
    {
        ExtraTurnLimitOverride = limit;
    }

    public static void SetSecondPlayerEnergy(int? energy)
    {
        SecondPlayerEnergyOverride = energy;
    }

    public static void SetSecondPlayerBlock(int? block)
    {
        SecondPlayerBlockOverride = block;
    }

    public static void SetSecondPlayerCards(int? cards)
    {
        SecondPlayerCardsOverride = cards;
    }

    /// <summary>清掉全部覆盖，回到设置页里的值。</summary>
    public static void ClearOverrides()
    {
        RulesEnabledOverride = null;
        MaxCardsPerTurnOverride = null;
        MaxSameCardPerTurnOverride = null;
        ExtraTurnLimitOverride = null;
        SecondPlayerEnergyOverride = null;
        SecondPlayerBlockOverride = null;
        SecondPlayerCardsOverride = null;
    }
}
