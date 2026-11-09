using System.Reflection;

using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace PvpDuel.Core.Duel;

/// <summary>反射读 internal 的 <c>CombatTurnState</c>（类型本身是 internal，拿不到编译期引用）。</summary>
internal static class DuelTurnState
{
    private static readonly Type? TurnState =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatTurnState");

    private static readonly PropertyInfo? StateProperty =
        TurnState is null ? null : AccessTools.Property(TurnState, "State");

    private static readonly PropertyInfo? ExtraTurnProperty =
        TurnState is null ? null : AccessTools.Property(TurnState, "PlayersTakingExtraTurn");

    public static ICombatState? StateOf(object? turnState)
    {
        return turnState is null || StateProperty is null
            ? null
            : StateProperty.GetValue(turnState) as ICombatState;
    }

    /// <summary>「这一回合只有这些人参与」的名单 —— 本体给额外回合用的那个列表。</summary>
    public static List<Player>? ExtraTurnsOf(object? turnState)
    {
        return turnState is null || ExtraTurnProperty is null
            ? null
            : ExtraTurnProperty.GetValue(turnState) as List<Player>;
    }
}
