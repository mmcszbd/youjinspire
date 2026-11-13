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
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace YouJin.Scripts;

/// <summary>某个玩家的"按队伍看"的战斗状态视图。</summary>
/// <remarks>
/// ★ 必须**同时**实现 <see cref="ICardScope"/>，这不是可选项。
///
/// 原版 `CardModel.CardScope` 的实现（反编译）是：
///     (ICardScope)CardModel.CombatState ?? (ICardScope)owner.Creature.CombatState
/// 而我们把**有主单位的 `Creature.CombatState` 换成了本视图**（见 Install）——
/// 少实现这个接口，第二次强转就会抛 InvalidCastException。
///
/// 实测事故（2026-10-15，工坊版 0.9.x）：
///   在这场战斗里打「夜魇」→ `NightmarePower.SetSelectedCard` 里
///   `card.CreateClone()` 走 `ICardScope.CloneCard` → 强转失败抛异常；
///   而夜魇的 OnPlay 是"先 Apply&lt;NightmarePower&gt;、后 SetSelectedCard"，
///   于是**能力挂上了、选中的牌没存进去** → 下回合 `BeforeHandDraw` 空引用
///   → 日志 `turn loop died while its combat is in progress; the combat is stuck`
///   → 整场战斗卡死。
///
/// 受影响的不止夜魇：`CardModel.CardScope` 是原版"造卡/复制卡"的统一入口
/// （冰冻蛋/熔岩灯/磨砂镜等改卡遗物、远古之牙、利爪、拿卡奖励还原 ……），
/// 在这场战斗里全都会走同一个强转。
///
/// 真实 `CombatState` 一共只实现 `ICombatState` + `ICardScope` 两个接口，
/// 所以这里两个都实现全了 = 与原版行为完全对齐。
/// </remarks>
internal sealed class BetrayalCombatView : ICombatState, ICardScope
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

    /// <summary>当前有没有生物挂着视图（退出前拆视图的补丁用它做零开销判断）。</summary>
    public static bool HasWrappedCreatures => Wrapped.Count > 0;

    /// <summary>现在挂着视图的单位数（诊断用）。</summary>
    public static int WrappedCount => Wrapped.Count;

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
                var owner = PvpCombat.TeamOf(creature);
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

    /// <summary>
    /// 单个生物还原成真实 CombatState（并从 Wrapped 里摘掉）。
    /// ------------------------------------------------------------------
    /// 为什么需要这个：`CombatState.RemoveCreature(creature, unattach)` 开头就是守卫
    /// `if (creature.CombatState != this) throw new InvalidOperationException("Creature is in a different combat.")`。
    /// 只要生物身上还挂着本视图，任何"把生物移出战斗"的路径都会抛：
    ///   · 放弃本局 / 回主菜单 → `RunManager.CleanUp` → `CombatManager.Reset(graceful)`
    ///     → `foreach (creatures) state.RemoveCreature(c, true)`  ★ 2026-10-28 黑屏的根因
    ///   · 战斗中途某个玩家被单独移出战斗
    /// 所以移除前先在这里还原（补丁见文件末尾 BetrayalRemoveCreatureViewPatch）。
    /// </summary>
    /// <param name="creature">要还原的生物。</param>
    /// <param name="expected">期望的真 state（一般就是正在调 RemoveCreature 的那个），为空则用 _state 兜底。</param>
    public static void RestoreFor(Creature? creature, CombatState? expected = null)
    {
        if (creature is null)
            return;

        if (creature.CombatState is not BetrayalCombatView view)
            return;

        try
        {
            for (var i = 0; i < Wrapped.Count; i++)
            {
                if (!ReferenceEquals(Wrapped[i].Creature, creature))
                    continue;

                creature.CombatState = Wrapped[i].Original ?? expected ?? view.State;
                Wrapped.RemoveAt(i);
                return;
            }

            // 不在列表里（异常路径）→ 直接用真 state 兜底
            creature.CombatState = expected ?? view.State;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：单个还原队伍视图失败（忽略）{ex}");
        }
    }

    // ------------------------------------------------------------------
    // 队伍感知的成员（其余全部转发给真实状态）
    // ------------------------------------------------------------------

    private static Player? TeamOf(Creature? creature) => PvpCombat.TeamOf(creature);

    private bool IsMyTeam(Creature creature) => PvpCombat.IsMyTeam(creature, _owner);

    /// <summary>从我的视角看，这只算不算敌人：别的玩家（含他的宠物）算；无主怪按原版那侧算。</summary>
    private bool IsEnemyOfMe(Creature creature) => PvpCombat.IsEnemyOfMe(creature, _owner);

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
        // ★ 和 HittableEnemies 保持一致：把"不可打击"的木桩滤掉。
            //   群体卡 / 随机攻击卡走的是 AttackCommand.GetPossibleTargets → 这里，
            //   不是 HittableEnemies —— 之前漏了这一步，所以木桩能被它们选中。
            return IsMyTeam(creature) ? Enemies.Where(c => c.IsHittable).ToList() : Allies;
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

    /// <summary>0.113 起 <see cref="ICombatState"/> 要求暴露本场生效的 RunModel 列表，原样转发。</summary>
    public IReadOnlyList<RunModel> RunModels => _state.RunModels;

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

/// <summary>
/// A 档：放弃本局 / 回主菜单 / 游戏结束退出前，先把队伍视图全拆了。
/// ----------------------------------------------------------------------------
/// `RunManager.CleanUp(graceful)` → `CombatManager.Reset(graceful)` 会对每个生物调
/// `state.RemoveCreature(creature, true)`，而它要求 `creature.CombatState == state`。
/// 视图还挂着 → 抛 "Creature is in a different combat." → `NGame.ReturnToMainMenu()`
/// 半路崩 → 黑屏（2026-10-28 定位，见交接文档末节）。
/// 这里在整条链最前面拆掉，正常打完那条路本来就由 AfterCombatEnd 拆，重复调用是安全的
/// （Wrapped 空时 Uninstall 什么都不做）。
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class BetrayalRunCleanupUninstallPatch
{
    private static void Prefix()
    {
        try
        {
            if (BetrayalCombatView.HasWrappedCreatures)
                ArchitectBetrayalCombatModel.ClearBetrayalMarkers("跑路 / 回主菜单");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：退出前拆队伍视图失败（忽略）{ex}");
        }
    }
}

/// <summary>
/// B 档：任何单个生物被移出战斗之前，先把它的视图还原成真 state。
/// 兜"战斗中途某个玩家被单独移出战斗"这种 A 档覆盖不到的情况。
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.RemoveCreature))]
internal static class BetrayalRemoveCreatureViewPatch
{
    private static void Prefix(CombatState __instance, Creature creature)
    {
        try
        {
            if (creature.CombatState is BetrayalCombatView)
                BetrayalCombatView.RestoreFor(creature, __instance);
        }
        catch
        {
            // 前缀里绝不抛：还原失败就交给原版守卫，别把移除流程本身弄坏。
        }
    }
}
