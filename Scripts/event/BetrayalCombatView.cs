// ============================================================================
//「建筑师的挑拨」：PvP 的「队伍视图」
// ----------------------------------------------------------------------------
// 思路来自创意工坊 mod「尖塔决斗 SpirePvp」（3813193750）的 PvpCombatView：
//   ★ 真实的 CombatState 一个字都不改（Enemies 里永远只有原版敌人），
//     只把**有主单位**（玩家本体 / 他们的宠物）的 `Creature.CombatState`
//     换成"按他所在队伍看"的视图。
//
// 这样：
//   · 游戏自己读 `creature.CombatState.GetOpponentsOf(...)` / `Enemies` /
//     `HittableEnemies` 的地方（牌、遗物、能力、球的实现都在用）→ 得到"队友算对手" ✓
//   · 真实 `CombatState.Enemies` 里没有伪装成敌人的玩家 →
//     第三方 mod（例如薄荷尖塔的「预计受伤」）遍历真实列表时不会拿到"没有 Monster 的玩家"，
//     也就不会再空引用崩溃、把出牌收尾打断（那正是我们之前分叉被踢的根因）
//   · `CombatManager.IsCombatEnding` 的「敌人全灭」判定走的是真实状态 → 木桩还在 ✓
//
// 旧做法（PvpEnemyListPatch：在出牌窗口往真实 Enemies 里塞队友）停用了（类保留在原地）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Runs;

namespace YouJin.Scripts;

/// <summary>某个玩家的"按队伍看"的战斗状态视图。</summary>
internal sealed class BetrayalCombatView : ICombatState
{
    private readonly CombatState _state;
    private readonly Player _owner;

    private BetrayalCombatView(CombatState state, Player owner)
    {
        _state = state;
        _owner = owner;
    }

    /// <summary>真实状态（需要原版行为时用它）。</summary>
    public CombatState State => _state;

    /// <summary>这个视图是谁的视角。</summary>
    public Player Owner => _owner;

    // ------------------------------------------------------------------
    // 装 / 拆
    // ------------------------------------------------------------------

    private static readonly List<(Creature Creature, ICombatState? Original)> Wrapped = new();

    /// <summary>给场上每个"有主单位"换上本视图。真实状态不动。</summary>
    public static void Install(ICombatState? combat)
    {
        try
        {
            if (combat is not CombatState state)
            {
                Entry.Logger.Info("建筑师的挑拨：战斗状态不是 CombatState，跳过队伍视图");
                return;
            }

            Uninstall();

            foreach (var creature in state.Creatures.ToList())
            {
                var owner = creature.Player ?? creature.PetOwner;
                if (owner is null)
                    continue; // 无主怪（木桩/敌人）保持原样

                Wrapped.Add((creature, creature.CombatState));
                creature.CombatState = new BetrayalCombatView(state, owner);
            }

            Entry.Logger.Info(
                $"建筑师的挑拨：已为 {Wrapped.Count} 个有主单位装上 PvP 队伍视图"
                + "（真实敌人列表保持原样，第三方 mod 不会再看到\"伪装成敌人的玩家\"）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：安装队伍视图失败（按原版继续）{ex}");
        }
    }

    /// <summary>还原真实状态。</summary>
    public static void Uninstall()
    {
        foreach (var (creature, original) in Wrapped)
        {
            try
            {
                creature.CombatState = original;
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"建筑师的挑拨：还原队伍视图失败（忽略）{ex}");
            }
        }

        Wrapped.Clear();
    }

    // ------------------------------------------------------------------
    // 队伍感知的成员（其余全部转发给真实状态）
    // ------------------------------------------------------------------

    private static Player? TeamOf(Creature? creature)
        => creature?.Player ?? creature?.PetOwner;

    private bool IsMyTeam(Creature creature)
        => ReferenceEquals(TeamOf(creature), _owner);

    /// <summary>从我的视角看，这只算不算敌人：别的玩家（含他的宠物）算；无主怪按原版那侧算。</summary>
    private bool IsEnemyOfMe(Creature creature)
    {
        var team = TeamOf(creature);
        if (team is not null)
            return !ReferenceEquals(team, _owner);

        return creature.Side == CombatSide.Enemy;
    }

    public IReadOnlyList<Creature> Allies
        => _state.Creatures.Where(IsMyTeam).ToList();

    public IReadOnlyList<Creature> Enemies
        => _state.Creatures.Where(IsEnemyOfMe).ToList();

    public IReadOnlyList<Creature> HittableEnemies
        => Enemies.Where(c => c.IsHittable).ToList();

    public IReadOnlyList<Creature> GetCreaturesOnSide(CombatSide side)
        => side == CombatSide.Player ? Allies : Enemies;

    public IReadOnlyList<Creature> GetOpponentsOf(Creature creature)
    {
        // 无主怪（木桩）→ 原版（它的对手是玩家侧）
        if (TeamOf(creature) is null)
            return _state.GetOpponentsOf(creature);

        // 自己人 → 我的敌人；别人 → 我的队友（对他而言就是对手）
        return IsMyTeam(creature) ? Enemies : Allies;
    }

    public IReadOnlyList<Creature> GetTeammatesOf(Creature creature)
    {
        if (TeamOf(creature) is null)
            return _state.GetTeammatesOf(creature);

        var same = IsMyTeam(creature) ? Allies : Enemies;
        return same.Where(c => !ReferenceEquals(c, creature)).ToList();
    }

    public IReadOnlyList<Creature> CreaturesOnCurrentSide
        => GetCreaturesOnSide(_state.CurrentSide);

    // ------------------------------------------------------------------
    // 其余成员：一律转发真实状态
    // ------------------------------------------------------------------

    public IRunState RunState => _state.RunState;

    public event Action<ICombatState>? CreaturesChanged
    {
        add => _state.CreaturesChanged += value;
        remove => _state.CreaturesChanged -= value;
    }

    public IReadOnlyList<Creature> Creatures => _state.Creatures;

    public IReadOnlyList<Creature> PlayerCreatures => _state.PlayerCreatures;

    public IReadOnlyList<Player> Players => _state.Players;

    public IReadOnlyList<ModifierModel> Modifiers => _state.Modifiers;

    public MultiplayerScalingModel MultiplayerScalingModel => _state.MultiplayerScalingModel!;

    public int RoundNumber
    {
        get => _state.RoundNumber;
        set => _state.RoundNumber = value;
    }

    public CombatSide CurrentSide
    {
        get => _state.CurrentSide;
        set => _state.CurrentSide = value;
    }

    public EncounterModel Encounter => _state.Encounter!;

    public IReadOnlyList<Creature> EscapedCreatures => _state.EscapedCreatures;

    public T CreateCard<T>(Player player) where T : CardModel => _state.CreateCard<T>(player);

    public CardModel CreateCard(CardModel card, Player player) => _state.CreateCard(card, player);

    public CardModel CloneCard(CardModel card) => _state.CloneCard(card);

    public void AddCard(CardModel card, Player player) => _state.AddCard(card, player);

    public void RemoveCard(CardModel card) => _state.RemoveCard(card);

    public bool ContainsCard(CardModel card) => _state.ContainsCard(card);

    public void AddPlayer(Player player) => _state.AddPlayer(player);

    public Creature CreateCreature(MonsterModel monster, CombatSide side, string? slotName)
        => _state.CreateCreature(monster, side, slotName);

    public void CreatureEscaped(Creature creature) => _state.CreatureEscaped(creature);

    public void RemoveCreature(Creature creature, bool wasKilled)
        => _state.RemoveCreature(creature, wasKilled);

    public bool ContainsCreature(Creature creature) => _state.ContainsCreature(creature);

    public bool ContainsMonster<T>() where T : MonsterModel => _state.ContainsMonster<T>();

    public Creature GetCreature(uint? id) => _state.GetCreature(id)!;

    public Task<Creature?> GetCreatureAsync(uint? id, double timeout)
        => _state.GetCreatureAsync(id, timeout);

    public Player GetPlayer(ulong netId) => _state.GetPlayer(netId)!;

    public IEnumerable<AbstractModel> IterateHookListeners() => _state.IterateHookListeners();

    public void SortEnemiesBySlotName() => _state.SortEnemiesBySlotName();

    public void SetEnemyIndex(Creature creature, int index) => _state.SetEnemyIndex(creature, index);

    public void AddCreature(Creature creature) => _state.AddCreature(creature);

    public bool IsLiveCombat() => _state.IsLiveCombat();
}
