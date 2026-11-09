using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace PvpDuel.Core.Duel;

/// <summary>决斗的判定规则（纯函数，不改任何状态）。</summary>
/// <remarks>
/// <para>
/// 两条口径在这里收口，补丁只调用、不自己判断：
/// ① 这场战斗能不能按决斗跑；② 决斗里「敌人 = 对面那位玩家<b>和他的召唤物</b>」。
/// </para>
/// <para>
/// 后一条是本体的一个盲区：<c>CombatState.GetOpponentsOf</c> / <c>GetTeammatesOf</c> 都按
/// <c>Creature.Side</c> 分侧，而决斗里两人都留在我方侧 —— 于是「对面」是空的（雷球、AOE、毒
/// 这些自动效果拿不到目标，直接什么都不做）、队友里却混进了对手。
/// </para>
/// </remarks>
internal static class DuelRules
{
    /// <summary>这场战斗具备决斗的硬前提（平衡模块之外的总闸门 + 参战者正好两人）。</summary>
    /// <remarks>
    /// 「参战者」和「在场」是两件事：普通两人局两者相同，擂台赛里四人全在场、只有两位参战。
    /// 所以这里不能直接数 <c>combat.Players.Count</c>。
    /// </remarks>
    public static bool CanRunDuel(ICombatState? state)
    {
        return ModuleSwitches.Core
               && state is { } combat
               && DuelSession.ParticipantsOf(combat).Count == 2;
    }

    /// <summary>本机正在打的就是这场两人决斗。</summary>
    public static bool IsInTwoPlayerDuel(ICombatState? state)
    {
        return DuelSession.InDuel && CanRunDuel(state);
    }

    /// <summary>两人决斗里「另一位玩家」这一位（不是玩家 / 不是决斗 / 问的人是观战者时返回 null）。</summary>
    public static Player? OtherPlayer(Player? me, ICombatState? state)
    {
        if (me is null || state is not { } combat || !IsInTwoPlayerDuel(combat))
        {
            return null;
        }

        // 观战者没有「对面」：他不是这一轮的参战者，任何东西都不该以他为「谁问的」来算对手。
        if (DuelSession.IsSpectator(me))
        {
            return null;
        }

        foreach (var player in DuelSession.ParticipantsOf(combat))
        {
            if (!ReferenceEquals(player, me))
            {
                return player;
            }
        }

        return null;
    }

    /// <summary>
    /// 这个单位的「对面」：另一位玩家 + 他名下的召唤物。
    /// </summary>
    /// <remarks>
    /// 自动效果（球位被动、AOE、毒、遗物触发）都该拿这份名单，而不是空的敌方侧。
    /// 只依赖「谁问的」，不看本机视角 → 两端算出来一致。
    /// </remarks>
    public static IReadOnlyList<Creature> OpponentsOf(Creature? creature)
    {
        if (creature is not { IsPlayer: true } || creature.CombatState is not { } state)
        {
            return [];
        }

        if (OtherPlayer(creature.Player, state) is not { } opponent)
        {
            return [];
        }

        var result = new List<Creature>();

        foreach (var unit in AllCreaturesOf(state))
        {
            if (ReferenceEquals(unit.Player, opponent) || ReferenceEquals(unit.PetOwner, opponent))
            {
                result.Add(unit);
            }
        }

        return result;
    }

    /// <summary>这个单位是不是「相对 <paramref name="owner" /> 的对面单位」（含对手的召唤物）。</summary>
    public static bool IsOpponentUnit(Creature? target, Creature? owner)
    {
        if (target is null || target.IsDead || owner?.CombatState is not { } state)
        {
            return false;
        }

        if (OtherPlayer(owner.Player, state) is not { } opponent)
        {
            return false;
        }

        return ReferenceEquals(target.Player, opponent) || ReferenceEquals(target.PetOwner, opponent);
    }

    /// <summary>这个单位是不是本机的对手（含对手的召唤物；认不出本机时退回 Players 顺序）。</summary>
    public static bool IsOpponentOfLocal(Creature? creature)
    {
        if (creature is null || creature.IsDead || creature.CombatState is not { } state)
        {
            return false;
        }

        if (OtherPlayer(LocalContext.GetMe(state), state) is not { } opponent)
        {
            return false;
        }

        return ReferenceEquals(creature.Player, opponent) || ReferenceEquals(creature.PetOwner, opponent);
    }

    /// <summary>两人决斗里这位玩家是不是「右边那位」（= 不是 <c>Players[0]</c>）。</summary>
    /// <remarks>决斗站位固定 P1 左、P2 右；宠物要跟着镜像到另一侧，用这个判断。</remarks>
    public static bool IsRightSidePlayer(Player? player)
    {
        if (player?.Creature.CombatState is not { } state || !IsInTwoPlayerDuel(state))
        {
            return false;
        }

        var participants = DuelSession.ParticipantsOf(state);
        return participants.Count >= 2 && !ReferenceEquals(participants[0], player);
    }

    /// <summary>「对手算不算合法目标」的唯一入口（卡牌与药水共用）。</summary>
    /// <returns>需要改写时给出新结果；<c>null</c> 表示不插手，维持原版判断。</returns>
    /// <remarks>
    /// 光重算 HittableEnemies 不够：卡牌校验 CardModel.IsValidTarget 与选中 UI
    /// NTargetManager.AllowedToTargetCreature 都看 Creature.Side，两人都在我方侧，
    /// 于是「对面」被判成不合法、悬停判定直接 return（表现：点不到人）。
    /// 判定用「谁打谁」、不依赖本机视角，两端算出来一定一致。
    /// </remarks>
    public static bool? OpponentTargetOverride(
        TargetType targetType,
        Creature? target,
        Creature? owner,
        ICombatState? state)
    {
        if (target is null
            || targetType is not (TargetType.AnyEnemy or TargetType.AnyAlly)
            || !IsInTwoPlayerDuel(state)
            || !IsOpponentUnit(target, owner))
        {
            return null;
        }

        // AnyEnemy：放行；AnyAlly：否掉（1v1 里对面不是队友，否则「给友方上 buff」会变成给对方上 buff）。
        return targetType == TargetType.AnyEnemy;
    }

    private static IEnumerable<Creature> AllCreaturesOf(ICombatState state)
    {
        return state.Allies.Concat(state.Enemies);
    }
}
