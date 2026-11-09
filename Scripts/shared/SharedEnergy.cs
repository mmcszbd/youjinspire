// ============================================================================
// 友尽尖塔（youjin）：共享能量（共享模式专用）
// ----------------------------------------------------------------------------
// 玩法：多人局里所有玩家共用一管能量 —— 你在自己回合花的能量，也是队友的。
//
// 每回合的池子 = **各人能量上限之和**（两个 3 能量角色 = 6 点；两个 4 能量 = 8 点）。
// 和共享血池同一个道理：怪是打每个人各一次、牌也是各人各抓一份，
// 只给一份能量会让"人多"变成"更弱"。总量不变、只是可以互相挪用。
//
// 反编译踩点（sts2.dll，全部确认过）：
//   · 能量存在 `PlayerCombatState._energy`，公开属性 `Energy`（get/set）。
//     写它的只有四个方法（IL 里 `PlayerCombatState.set_Energy` 的调用者）：
//       ResetEnergy() / AddMaxEnergyToCurrent() / LoseEnergy(decimal) / GainEnergy(decimal)
//     —— 即"回合开始重置""回合开始累加""出牌扣费""加能量"。
//   · `MaxEnergy` 不是字段，而是每次现算：
//       Hook.ModifyMaxEnergy(player.MaxEnergy, creature.CombatState, player)
//     读它的地方只有三处：ResetEnergy、AddMaxEnergyToCurrent、以及能量球界面
//     `NEnergyCounter.RefreshLabel`（它显示的是 `"{Energy}/{MaxEnergy}"`）。
//   · 回合开始：CombatManager.StartTurn 里按顺序对每个玩家调 SetupPlayerTurn，
//     每个人一进来就 `if (Hook.ShouldPlayerResetEnergy(...)) ResetEnergy(); else AddMaxEnergyToCurrent();`
//     这几下都在同步段里连着跑完（后面才 await AfterEnergyReset / 抽牌），
//     所以"每个玩家各自重置一次"不会穿插进别人出牌。
//   · `AbstractModel.ShouldPlayerResetEnergy` 默认返回 true（能量每回合重置）。
//     原版覆写它的只有一件遗物：**「冰淇淋」`Relics.IceCream`** ——
//         player != Owner            → true（不影响别人）
//         Owner.PlayerCombatState.TurnNumber == 1 → true（第 1 回合照常重置）
//         其余                        → false（不重置，能量累积）
//     `Hook.ShouldPlayerResetEnergy(战斗状态, 玩家)` 是"**任一**监听者返回 false 就 false"。
//     原版走的是这条链：SetupPlayerTurn 里
//         if (Hook.ShouldPlayerResetEnergy(state, player)) ResetEnergy();   // 池子 = 上限
//         else                                            AddMaxEnergyToCurrent(); // 池子 += 上限
//     —— 所以"谁带冰淇淋"直接决定这名玩家走哪条路。
//
// 做法（四条补丁）：
//   ① `PlayerCombatState.MaxEnergy` 的 getter → 返回**全队之和**。
//      这样每回合每人那份 ResetEnergy 都会把池子设成同一个总数（幂等、与顺序无关），
//      界面也顺理成章地显示 "6/6" 而不是 "6/3"。
//      注意要防递归：求和时会去读别人的 MaxEnergy，必须让那几次走原版值
//      （用 _maxEnergyDepth 挡一层）。
//   ② `PlayerCombatState.Energy` 的 setter → 谁变推给全队（和共享金币一个套路 + 深度锁）。
//   ⑤ `PlayerCombatState.Energy` 的 **getter** → 非锚点玩家一律读**锚点（RunState.Players[0]）**
//      那一份。这一条是"单一权威值"的保险：只靠"谁变推给全队"的话，
//      只要有一次没推到（任何原因），两个人的能量就会各留一个数，
//      之后谁先动谁就把对方那个数覆盖掉 —— 表现就是"我加的费被队友先用的那一下吃掉了"。
//      改成"写永远写锚点、读永远读锚点"之后，两边就不存在两个数了。
//   ③④ `ResetEnergy()` 与 `AddMaxEnergyToCurrent()` —— **回合开始的补能**：
//      每个玩家各会走一次（各走哪条由冰淇淋之类的遗物决定），但共享池**一回合只该补一次**：
//        · 正常（队里没人保留能量）：第一次让原版 ResetEnergy 把池子设成"全队上限之和"，
//          后面几次直接跳过 —— 顺便保住"第一次 AfterEnergyReset 里加的费"（茶具那种），
//          否则后面那个人一重置就把它冲掉了；
//        · 队里有人带「冰淇淋」：池子**累加**而不是重置（carry + 全队上限之和），
//          也就是把冰淇淋的效果搬到共享池上；同样只做一次。
//      "本回合补过了"用（战斗对象, TurnNumber）记 —— TurnNumber 只在 SwitchSides 里
//      全队一起 +1，所以它天然就是"第几回合"。
//
// 没做的：
//   · 星星（PlayerCombatState.Stars）没共享 —— 只共享能量本身；
//   · 抽牌 / 手牌 / 手牌上限照旧各人各一份（共享牌堆是另一个 mod "Together" 的玩法）；
//   · 死掉玩家的那份上限还算在池子里（共享血量下本来也不会有人单独死）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace YouJin.Scripts;

/// <summary>多人共享能量池的镜像内核。</summary>
internal static class SharedEnergy
{
    /// <summary>
    /// PlayerCombatState 的宿主玩家（`_player` 是 private 字段，没有公开属性）。
    /// 用 Harmony 的 FieldRef 直接读，比每次反射 GetValue 便宜。
    /// </summary>
    private static readonly AccessTools.FieldRef<PlayerCombatState, Player> OwnerOf =
        AccessTools.FieldRefAccess<PlayerCombatState, Player>("_player");

    /// <summary>
    /// 直接读 `_energy` 字段。
    /// 普通读一律走 `Energy` 属性（那条路我们改成了"读锚点"），
    /// 但镜像内部要比较"这一份自己刚被写成多少"，必须绕过属性读原始字段。
    /// </summary>
    private static readonly AccessTools.FieldRef<PlayerCombatState, int> RawEnergyOf =
        AccessTools.FieldRefAccess<PlayerCombatState, int>("_energy");

    /// <summary>&gt; 0 表示"正在求和"，此时 MaxEnergy 必须走原版值，否则自己递归自己。</summary>
    private static int _maxEnergyDepth;

    /// <summary>正在"往外推"的层数（> 0 时不再镜像，防递归）。</summary>
    private static int _mirrorDepth;

    /// <summary>本回合已经补过能的那个战斗（配合 _refilledTurn 用）。</summary>
    private static object? _refilledCombat;

    /// <summary>已经补过能的回合号。</summary>
    private static int _refilledTurn = int.MinValue;

    // ── 诊断用（只写日志，不改行为）────────────────────────────────────────
    /// <summary>`set_Energy` 之前的值：Prefix 记、Postfix 用。</summary>
    private static int _oldBeforeSet;

    private static bool _oldCaptured;

    /// <summary>
    /// 正在执行 `set_Energy` 的层数（> 0 = 有一次写入还没走完）。
    ///
    /// 为什么需要它：`set_Energy` 的实现是"先写 `_energy` 字段，再触发 EnergyChanged 事件"，
    /// 而事件订阅者（能量计数器、多人面板、CombatStateTracker）会在事件里**再读一次 Energy**。
    /// 那个瞬间本地字段已经是新值、锚点还是旧值（我们的镜像 Postfix 还没跑），
    /// 于是"读锚点"会把**旧值**返回给订阅者 —— 日志里 8 行
    /// `读能量本地=X → 改读锚点=Y` 就是它，UI 上表现为能量数字闪一下旧值。
    /// 写入期间直接返回本地字段（那才是这一份最新的真值），写完再恢复"读锚点"。
    /// </summary>
    private static int _setEnergyDepth;

    /// <summary>"读能量被改道成锚点值" 这条日志的去重键。</summary>
    private static ulong _lastReadLogOwner;
    private static int _lastReadLogLocal = int.MinValue;
    private static int _lastReadLogValue = int.MinValue;

    /// <summary>`set_Energy` 的 Prefix：记下改之前的值。</summary>
    public static void CaptureBeforeSet(PlayerCombatState? state)
    {
        _oldCaptured = false;
        _setEnergyDepth++;

        try
        {
            if (state is null)
                return;

            _oldBeforeSet = RawEnergy(state);
            _oldCaptured = true;
        }
        catch
        {
            // 读不到就算了，日志里显示 "?"
        }
    }

    /// <summary>`set_Energy` 的 Postfix 末尾调用：退出"正在写入"状态。</summary>
    public static void ExitSet()
    {
        if (_setEnergyDepth > 0)
            _setEnergyDepth--;
    }

    // ── 出牌"实际花了多少能量"的口径修正（环绕轨道那类按消耗能量计数的效果用得到）──
    /// <summary>正在出牌的那张牌（= 刚进 `SpendEnergy` 的那张）。</summary>
    private static CardModel? _spendingCard;

    /// <summary>这张牌开始执行时，共享池是多少。</summary>
    private static int _spendingBefore;

    /// <summary>共享池当前的值（共享模式下读锚点，否则读这一份自己）。</summary>
    private static int PoolValue(PlayerCombatState state)
    {
        if (Active)
        {
            var states = StatesOf(state);
            if (states.Count >= 2 && _viewAnchor is { } anchor)
                return RawEnergy(anchor);
        }

        return RawEnergy(state);
    }

    /// <summary>`CardModel.SpendEnergy` 的 Prefix：记下"开始扣费之前池子是多少"。</summary>
    public static void CaptureSpendStart(CardModel? card)
    {
        _spendingCard = null;

        try
        {
            if (card?.Owner?.PlayerCombatState is not { } state)
                return;

            _spendingCard = card;
            _spendingBefore = PoolValue(state);
        }
        catch
        {
            _spendingCard = null;
        }
    }

    /// <summary>
    /// `Hook.AfterEnergySpent(战斗, 牌, amount)` 的 Prefix：
    /// 把 amount 从"卡面费用"改成"共享池实际掉了多少"。
    ///
    /// 原版这里直接传卡面费用，而真正扣多少是 `LoseEnergy` 内部
    /// `Clamp(池子 - 费用, 0, ...)` 算的 —— 单机时两者恒等（不够费的牌打不出来），
    /// 共享能量下两人同回合抢池子时才会分叉：队友先把池子掏空，
    /// 你这张牌照样打出、池子只掉到 0，但环绕轨道会按完整费用记数（白涨）。
    /// 这里按"池子实际减少值"改写，只影响读这个 amount 的效果。
    /// </summary>
    public static void FixEnergySpentAmount(CardModel? card, ref int amount)
    {
        try
        {
            if (card is null || !ReferenceEquals(card, _spendingCard))
                return;

            _spendingCard = null; // 只认一次，别把旧记录留给下一次

            if (!Active)
                return;

            if (card.Owner?.PlayerCombatState is not { } state)
                return;

            var after = PoolValue(state);
            var actual = _spendingBefore - after;

            if (actual < 0)
                actual = 0;
            if (actual > amount)
                actual = amount; // 不可能比卡面费用还多

            if (actual == amount)
                return;

            Entry.Logger.Info(
                $"共享能量：这张牌的费用按实扣改写 {amount} → {actual}（池子 {_spendingBefore}→{after}）");

            amount = actual;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享能量：改写实扣费用失败（按原版处理）{ex}");
        }
    }

    /// <summary>加费 / 扣费的入口日志（谁、请求多少、他本地是多少、锚点是多少）。</summary>
    public static void LogResourceCall(string what, PlayerCombatState? state, decimal amount)
    {
        try
        {
            if (state is null || !Active)
                return;

            var owner = OwnerOf(state);

            Entry.Logger.Info(
                $"共享能量：{what} NetId={owner?.NetId} 数量={amount}"
                + $"｜他本地={RawEnergy(state)}"
                + $"｜锚点={(_viewAnchor is { } anchor ? RawEnergy(anchor).ToString() : "-")}");
        }
        catch
        {
            // 诊断日志绝不能影响游戏
        }
    }

    /// <summary>把全队每人那一份原始值拼成 "A=1、B=2"。</summary>
    private static string TeamRaws(IReadOnlyList<PlayerCombatState> states)
    {
        var sb = new StringBuilder();

        foreach (var member in states)
        {
            if (sb.Length > 0)
                sb.Append('、');

            sb.Append(OwnerOf(member)?.NetId).Append('=').Append(RawEnergy(member));
        }

        return sb.ToString();
    }

    // ── 本场战斗的成员视图缓存（get_Energy 是热路径，不能每次都 LINQ 一遍）──
    private static ICombatState? _viewCombat;
    private static PlayerCombatState[] _viewStates = Array.Empty<PlayerCombatState>();
    private static PlayerCombatState? _viewAnchor;

    /// <summary>共享能量此刻是否生效（只在「共享模式」下）。</summary>
    /// <remarks>
    /// 回合制 PvP 那场（<c>PvpDuel.Core.Duel.DuelSession.InDuel</c>）也要关掉：
    /// 决斗是两人各自为战，共享能量池会让两边同时花同一池能量。
    /// 手速 PvP 靠 <c>BetrayalCombatActive</c> 已经关掉了，这里补上决斗那一条。
    /// </remarks>
    public static bool Active
        => YouJinSettings.SharedModeEnabled
           && YouJinSettings.SharedEnergyEnabled
           && !ArchitectBetrayalEvent.BetrayalCombatActive
           && !PvpDuel.Core.Duel.DuelSession.InDuel;

    /// <summary>本局全员的战斗状态（≥2 人）；单人局 / 拿不到就返回空表。</summary>
    private static IReadOnlyList<PlayerCombatState> StatesOf(PlayerCombatState? state)
    {
        if (state is null)
            return Array.Empty<PlayerCombatState>();

        try
        {
            var player = OwnerOf(state);
            var combat = player?.Creature?.CombatState;
            if (player is null || combat is null)
                return Array.Empty<PlayerCombatState>();

            // 命中缓存（同一场战斗、而且这次的状态就在名单里）。
            if (ReferenceEquals(combat, _viewCombat) && Array.IndexOf(_viewStates, state) >= 0)
                return _viewStates;

            var players = player.RunState?.Players;
            if (players is not { Count: > 1 })
            {
                _viewCombat = combat;
                _viewStates = Array.Empty<PlayerCombatState>();
                _viewAnchor = null;
                return _viewStates;
            }

            var states = new List<PlayerCombatState>(players.Count);
            foreach (var member in players)
            {
                if (member.PlayerCombatState is { } combatState)
                    states.Add(combatState);
            }

            _viewCombat = combat;
            _viewStates = states.ToArray();
            // 锚点 = RunState.Players 里第一个**有战斗状态**的人。
            // 顺序两端天然一致（和 together 的锚点同一个判据），绝不能用本机视角。
            _viewAnchor = _viewStates.Length > 0 ? _viewStates[0] : null;

            return _viewStates;
        }
        catch
        {
            return Array.Empty<PlayerCombatState>();
        }
    }

    // ── ① 上限：全队求和 ────────────────────────────────────────────────────

    /// <summary>
    /// 取"全队能量上限之和"。返回 false 表示这次不接管，调用方走原版 getter。
    /// </summary>
    public static bool TryGetSharedMaxEnergy(PlayerCombatState? state, out int total)
    {
        total = 0;

        // 求和过程中的递归调用（读别人的 MaxEnergy）必须走原版。
        if (state is null || _maxEnergyDepth > 0 || !Active)
            return false;

        var states = StatesOf(state);
        if (states.Count < 2)
            return false;

        _maxEnergyDepth++;
        try
        {
            total = states.Sum(combatState => combatState.MaxEnergy);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享能量：求和失败，回退原版 {ex}");
            total = 0;
        }
        finally
        {
            _maxEnergyDepth--;
        }

        return total > 0;
    }

    // ── ② 能量值：谁变推给全队 ──────────────────────────────────────────────

    /// <summary>某个玩家的能量变了 → 推给其他成员。</summary>
    public static void OnEnergyChanged(PlayerCombatState? state)
    {
        try
        {
            if (state is null || _mirrorDepth > 0)
                return;

            if (!Active)
                return;

            var states = StatesOf(state);
            if (states.Count < 2)
            {
                Entry.Logger.Info(
                    $"共享能量：{OwnerOf(state)?.NetId} 变成 {RawEnergy(state)}，"
                    + $"但只认出 {states.Count} 份战斗状态 —— 这次没镜像");
                return;
            }

            var energy = RawEnergy(state);
            var pushed = 0;

            _mirrorDepth++;
            try
            {
                foreach (var other in states)
                {
                    // 比较用原始字段（不能走属性：属性已经被我们改成"读锚点"了）
                    if (ReferenceEquals(other, state) || RawEnergy(other) == energy)
                        continue;

                    other.Energy = energy;
                    pushed++;
                }

                // 保险：权威值一定落在锚点上，读的人读的就是它。
                if (_viewAnchor is { } anchor
                    && !ReferenceEquals(anchor, state)
                    && RawEnergy(anchor) != energy)
                {
                    anchor.Energy = energy;
                }
            }
            finally
            {
                _mirrorDepth--;
            }

            var was = _oldCaptured ? _oldBeforeSet.ToString() : "?";
            var anchorRaw = _viewAnchor is { } a ? RawEnergy(a).ToString() : "-";

            Entry.Logger.Info(
                $"共享能量：{OwnerOf(state)?.NetId} {was}→{energy}"
                + $"（锚点={anchorRaw}）｜全队[{TeamRaws(states)}]｜推给 {pushed} 人");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享能量：镜像失败 {ex}");
        }
    }

    /// <summary>原始能量字段（不走属性）。</summary>
    private static int RawEnergy(PlayerCombatState state)
    {
        try
        {
            return RawEnergyOf(state);
        }
        catch
        {
            return state.Energy;
        }
    }

    /// <summary>
    /// 非锚点玩家读能量时，直接返回锚点那一份（单一权威值）。
    /// 返回 false = 这次不接管，走原版 getter。
    /// </summary>
    public static bool TryGetSharedEnergy(PlayerCombatState? state, out int value)
    {
        value = 0;

        try
        {
            if (state is null || !Active)
                return false;

            // 写入还没走完（说明就是这次扣费/加费自己触发的读）—— 用本地值，别读锚点旧值。
            if (_setEnergyDepth > 0)
                return false;

            var states = StatesOf(state);
            if (states.Count < 2)
                return false;

            var anchor = _viewAnchor;
            if (anchor is null || ReferenceEquals(anchor, state))
                return false;

            var raw = RawEnergy(anchor);
            if (RawEnergy(state) == raw)
                return false; // 两边本来就一样，省一次接管

            // 诊断：读的时候本地和锚点不一样 —— 这就是"费被谁吃掉"的现场（按值去重，别刷屏）
            var local = RawEnergy(state);
            var ownerId = OwnerOf(state)?.NetId ?? 0;
            if (_lastReadLogOwner != ownerId || _lastReadLogLocal != local || _lastReadLogValue != raw)
            {
                _lastReadLogOwner = ownerId;
                _lastReadLogLocal = local;
                _lastReadLogValue = raw;
                Entry.Logger.Info(
                    $"共享能量：读能量 NetId={ownerId} 本地={local} → 改读锚点={raw}");
            }

            value = raw;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── ③④ 回合开始的补能（ResetEnergy / AddMaxEnergyToCurrent 两条路都走这里）──

    /// <summary>
    /// 回合开始的补能收口。返回 true = 让原版继续；false = 这次已经由我们处理完，跳过原版。
    ///
    /// 一个回合里**每个玩家各会走一次**这条路（走哪条取决于该玩家的
    /// ShouldPlayerResetEnergy），但共享池一回合只该补一次：
    ///   · 已经补过 → 直接跳过（不然要么重复加一份，要么把别人加费的效果冲掉）；
    ///   · 队里有人带「冰淇淋」→ 池子累加（carry + 全队上限之和），把冰淇淋搬到共享池上；
    ///   · 否则 → 放行原版 ResetEnergy，它会用我们补丁算出的 MaxEnergy 把池子设成全队上限之和。
    /// </summary>
    public static bool TryHandleTurnRefill(PlayerCombatState? state)
    {
        try
        {
            if (state is null || !Active)
                return true;

            var states = StatesOf(state);
            if (states.Count < 2)
                return true;

            var combat = OwnerOf(state)?.Creature?.CombatState;
            if (combat is null)
                return true;

            var turn = state.TurnNumber;

            if (ReferenceEquals(_refilledCombat, combat) && _refilledTurn == turn)
                return false;

            _refilledCombat = combat;
            _refilledTurn = turn;

            // 队里没人保留能量 —— 原版 ResetEnergy 会把池子设成 MaxEnergy（= 全队上限之和）。
            if (!TeamKeepsEnergy(states))
                return true;

            if (!TryGetSharedMaxEnergy(state, out var total) || total <= 0)
                return true;

            var before = RawEnergy(state);
            state.Energy = before + total; // 走 setter → 自动镜像给全队

            Entry.Logger.Info($"共享能量：本回合累积（有「能量不重置」类遗物），共享池 {before} + {total} = {state.Energy}");
            return false;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享能量：回合补能失败，按原版处理 {ex}");
            return true;
        }
    }

    /// <summary>
    /// 队伍这一回合是不是"保留能量"（= 有人带冰淇淋这类遗物）。
    /// 直接问原版的钩子：任一模型对任一成员说"别重置"，这一队就保留。
    /// </summary>
    private static bool TeamKeepsEnergy(IReadOnlyList<PlayerCombatState> states)
    {
        foreach (var member in states)
        {
            var owner = OwnerOf(member);
            var combat = owner?.Creature?.CombatState;
            if (owner is null || combat is null)
                continue;

            if (!Hook.ShouldPlayerResetEnergy(combat, owner))
                return true;
        }

        return false;
    }
}

/// <summary>补丁安装入口。</summary>
internal static class SharedEnergyPatches
{
    /// <summary>只 patch 本文件里的三个补丁类。</summary>
    public static void Apply()
    {
        var harmony = new Harmony("youjin.sharedenergy");
        PatchOne(harmony, typeof(SharedEnergyMaxPatch), "能量上限求和");
        PatchOne(harmony, typeof(SharedEnergyMirrorPatch), "能量镜像");
        PatchOne(harmony, typeof(SharedEnergyReadPatch), "能量读取走锚点");
        PatchOne(harmony, typeof(SharedEnergyResetPatch), "回合开始补能（重置路径）");
        PatchOne(harmony, typeof(SharedEnergyAddMaxPatch), "回合开始补能（累加路径）");
        PatchOne(harmony, typeof(SharedEnergyGainProbePatch), "诊断-加费入口");
        PatchOne(harmony, typeof(SharedEnergyLoseProbePatch), "诊断-扣费入口");
        PatchOne(harmony, typeof(SharedEnergySpendStartPatch), "记录出牌前池子");
        PatchOne(harmony, typeof(SharedEnergySpentAmountPatch), "费用按实扣改写");
    }

    private static void PatchOne(Harmony harmony, Type type, string label)
    {
        try
        {
            harmony.CreateClassProcessor(type).Patch();
            Entry.Logger.Info($"共享能量：补丁「{label}」安装完成");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享能量：补丁「{label}」安装失败 {ex}");
        }
    }
}

/// <summary>① `PlayerCombatState.MaxEnergy` → 全队之和（界面和回合重置都读它）。</summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.MaxEnergy), MethodType.Getter)]
internal static class SharedEnergyMaxPatch
{
    private static bool Prefix(PlayerCombatState __instance, ref int __result)
    {
        if (!SharedEnergy.TryGetSharedMaxEnergy(__instance, out var total))
            return true;

        __result = total;
        return false;
    }
}

/// <summary>② `PlayerCombatState.Energy` 的 setter → 推给全队。</summary>
[HarmonyPatch(typeof(PlayerCombatState), "set_Energy")]
internal static class SharedEnergyMirrorPatch
{
    private static void Prefix(PlayerCombatState __instance) => SharedEnergy.CaptureBeforeSet(__instance);

    private static void Postfix(PlayerCombatState __instance)
    {
        try
        {
            SharedEnergy.OnEnergyChanged(__instance);
        }
        finally
        {
            SharedEnergy.ExitSet();
        }
    }
}

/// <summary>诊断：`PlayerCombatState.GainEnergy(amount)`（加费卡/遗物/充能球最终都走这里）。</summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.GainEnergy))]
internal static class SharedEnergyGainProbePatch
{
    private static void Prefix(PlayerCombatState __instance, decimal amount)
        => SharedEnergy.LogResourceCall("加费", __instance, amount);
}

/// <summary>诊断：`PlayerCombatState.LoseEnergy(amount)`（出牌扣费走这里）。</summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.LoseEnergy))]
internal static class SharedEnergyLoseProbePatch
{
    private static void Prefix(PlayerCombatState __instance, decimal amount)
        => SharedEnergy.LogResourceCall("扣费", __instance, amount);
}

/// <summary>口径修正①：`CardModel.SpendEnergy(int)`（private）—— 记下扣费前共享池的值。</summary>
[HarmonyPatch(typeof(CardModel), "SpendEnergy")]
internal static class SharedEnergySpendStartPatch
{
    private static void Prefix(CardModel __instance) => SharedEnergy.CaptureSpendStart(__instance);
}

/// <summary>口径修正②：`Hook.AfterEnergySpent(战斗, 牌, amount)` —— 把 amount 改成实际扣掉的量。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergySpent))]
internal static class SharedEnergySpentAmountPatch
{
    private static void Prefix(CardModel __1, ref int __2) => SharedEnergy.FixEnergySpentAmount(__1, ref __2);
}

/// <summary>⑤ `PlayerCombatState.Energy` 的 getter → 非锚点一律读锚点（单一权威值）。</summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.Energy), MethodType.Getter)]
internal static class SharedEnergyReadPatch
{
    private static bool Prefix(PlayerCombatState __instance, ref int __result)
    {
        if (!SharedEnergy.TryGetSharedEnergy(__instance, out var value))
            return true;

        __result = value;
        return false;
    }
}

/// <summary>③ 回合开始的补能：原版"重置"路径走我们的收口。</summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.ResetEnergy))]
internal static class SharedEnergyResetPatch
{
    private static bool Prefix(PlayerCombatState __instance)
        => SharedEnergy.TryHandleTurnRefill(__instance);
}

/// <summary>④ 回合开始的补能：原版"累加"路径（冰淇淋那种）也走同一个收口。</summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.AddMaxEnergyToCurrent))]
internal static class SharedEnergyAddMaxPatch
{
    private static bool Prefix(PlayerCombatState __instance)
        => SharedEnergy.TryHandleTurnRefill(__instance);
}
