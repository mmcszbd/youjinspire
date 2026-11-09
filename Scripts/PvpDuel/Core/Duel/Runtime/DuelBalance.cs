using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace PvpDuel.Core.Duel;

/// <summary>决斗平衡规则：回合内出牌上限、同卡上限、连续额外回合上限、后手补偿。</summary>
/// <remarks>
/// <para>
/// 无限有两条路：<b>回合内无限</b>（连击/循环）与<b>回合数无限</b>（本体的额外回合被原样放行）。
/// 前者用「出牌数上限 + 同卡上限」堵，后者用「连续额外回合上限」堵；数值全部来自设置，0 = 不限制。
/// </para>
/// <para>
/// 后手补偿的判定只看两件事：这位玩家自己的第一次回合 + 他不是先手方。
/// 先手方 = <c>Players[0]</c>（大厅顺序，两端一致），所以两端算出来一定一样。
/// 这里不读本体的 <c>TurnNumber</c>（决斗里那本账是另一套口径），自己数每个人的回合数。
/// </para>
/// </remarks>
internal static class DuelBalance
{
    private static readonly Dictionary<Player, int> TurnsByPlayer = [];
    private static readonly HashSet<Player> Compensated = [];
    private static readonly Dictionary<Player, int> CardsThisTurn = [];
    private static readonly Dictionary<Player, int> ConsecutiveExtraTurns = [];
    private static readonly Dictionary<CardModel, int> SameCardThisTurn = new(ReferenceEqualityComparer.Instance);

    /// <summary>新的一场战斗：账本清空。</summary>
    public static void Reset()
    {
        TurnsByPlayer.Clear();
        Compensated.Clear();
        CardsThisTurn.Clear();
        ConsecutiveExtraTurns.Clear();
        SameCardThisTurn.Clear();
    }

    /// <summary>轮到自己行动（普通回合）：清空本回合计数，并记下这是他的第几个回合。</summary>
    public static void BeginTurn(Player actor)
    {
        CardsThisTurn.Clear();
        SameCardThisTurn.Clear();
        TurnsByPlayer[actor] = TurnsByPlayer.GetValueOrDefault(actor) + 1;

        foreach (var player in ConsecutiveExtraTurns.Keys.ToList())
        {
            ConsecutiveExtraTurns[player] = 0;
        }
    }

    /// <summary>后手第一次回合要补多少（全 0 = 不补）。</summary>
    public static (int Energy, int Block, int Cards) CompensationFor(Player actor, ICombatState state)
    {
        if (!DuelConfig.RulesEnabled || TurnsByPlayer.GetValueOrDefault(actor) != 1)
        {
            return (0, 0, 0);
        }

        var players = state.Players;

        // 先手方（Players[0]）不补；只在正好两人的决斗里生效。
        if (players.Count != 2 || ReferenceEquals(players[0], actor))
        {
            return (0, 0, 0);
        }

        return (DuelConfig.SecondPlayerEnergy, DuelConfig.SecondPlayerBlock, DuelConfig.SecondPlayerCards);
    }

    /// <summary>记下这位玩家的后手补偿已经发过了（HUD 显示用）。</summary>
    public static void MarkCompensated(Player player)
    {
        Compensated.Add(player);
    }

    /// <summary>这位玩家本场是否已经领过后手补偿。</summary>
    public static bool WasCompensated(Player? player)
    {
        return player is not null && Compensated.Contains(player);
    }

    /// <summary>本体要发一个额外回合：记一笔，并回答「还该不该给」。</summary>
    public static bool AllowExtraTurn(Player player)
    {
        if (!DuelConfig.RulesEnabled || DuelConfig.ExtraTurnLimit == 0)
        {
            return true;
        }

        var count = ConsecutiveExtraTurns.GetValueOrDefault(player) + 1;
        ConsecutiveExtraTurns[player] = count;

        return count <= DuelConfig.ExtraTurnLimit;
    }

    /// <summary>这张牌本回合是不是已经打到上限（打不出去了）。</summary>
    public static bool IsCardLocked(CardModel card)
    {
        if (!DuelConfig.RulesEnabled || DuelConfig.MaxSameCardPerTurn == 0)
        {
            return false;
        }

        return SameCardThisTurn.GetValueOrDefault(card) >= DuelConfig.MaxSameCardPerTurn;
    }

    /// <summary>记一张打出的牌，并回答「这一回合是不是该强制结束了」。</summary>
    public static bool NoteCardPlayed(Player player, CardModel card)
    {
        if (!DuelConfig.RulesEnabled)
        {
            return false;
        }

        SameCardThisTurn[card] = SameCardThisTurn.GetValueOrDefault(card) + 1;

        var cards = CardsThisTurn.GetValueOrDefault(player) + 1;
        CardsThisTurn[player] = cards;

        var limit = DuelConfig.MaxCardsPerTurn;

        return limit != 0 && cards >= limit;
    }

    // ==================== 给 DuelApi / UI 读的用量 ====================

    /// <summary>本回合这位玩家打了几张牌。</summary>
    public static int CardsPlayedThisTurn(Player? player)
    {
        return player is null ? 0 : CardsThisTurn.GetValueOrDefault(player);
    }

    /// <summary>这张牌本回合打了几次。</summary>
    public static int SameCardPlaysThisTurn(CardModel? card)
    {
        return card is null ? 0 : SameCardThisTurn.GetValueOrDefault(card);
    }

    /// <summary>这位玩家本回合里"打得最多的同一张牌"打了几次。</summary>
    public static int MaxSameCardCountThisTurn(Player? player)
    {
        if (player is null)
        {
            return 0;
        }

        var max = 0;

        foreach (var (card, count) in SameCardThisTurn)
        {
            if (ReferenceEquals(card.Owner, player) && count > max)
            {
                max = count;
            }
        }

        return max;
    }

    /// <summary>这位玩家在本场战斗里打过几个回合。</summary>
    public static int TurnsTaken(Player? player)
    {
        return player is null ? 0 : TurnsByPlayer.GetValueOrDefault(player);
    }
}
