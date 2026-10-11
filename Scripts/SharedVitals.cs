// ============================================================================
// 友尽尖塔（youjin）：共享血量 / 共享金币（多人玩法）
// ----------------------------------------------------------------------------
// 玩法：多人局里所有玩家共用一个血池、一个钱包 ——
//   · 谁掉血 / 回血 / 加上限，全队一起变；
//   · 谁赚到 / 花掉金币，全队一起变。
//
// 参考实现：D:\Slay the Spire 2\mods\together（合作模式 mod）。
//   它那边的对应文件是 Core/Shared/Body/BodyMirror.cs 与 Core/Shared/Gold/GoldMirror.cs，
//   本文件把它的**收口点、深度锁、收敛判据**照抄了一遍，只改了两处：
//     1. 成员范围 = 本局全部玩家（RunState.Players），不像 together 那样要先在选人界面配对；
//     2. 只管血量和金币（格挡、卡组、能力、球位、召唤物都不碰）。
//
// 收口点（反编译 sts2.dll 确认过，和 together 完全一致）：
//   · Creature.set_CurrentHp / set_MaxHp —— 血量收口，三条路都汇聚到这里：
//       受伤：CreatureCmd.Damage → Creature.LoseHpInternal → set_CurrentHp
//             （LoseHpInternal 的 IL 里就一行 call Creature.set_CurrentHp）
//       治疗：HealInternal → SetCurrentHpInternal → set_CurrentHp
//       加上限：CreatureCmd.SetMaxHp → SetMaxHpInternal → set_MaxHp（顺带把当前血夹到新上限）
//     两个 setter 都是「值没变就直接 return」（IL 里那两行 bne.un/beq），
//     所以镜像天然收敛，不会两边来回推。
//   · Player.set_Gold —— 金币收口：
//       赚钱：PlayerCmd.GainGold → player.Gold += …
//       花钱：PlayerCmd.LoseGold → player.set_Gold(max(0, Gold - amount))（IL 确认走 setter）
//     商店 / 事件 / 遗物 / 火堆一样，全都过这个属性。
//
// 深度锁（照抄 together 的 _mirrorDepth / _depth）：
//   把值推给别人的时候，对方也会走同一个 setter —— 没有锁就是无限递归。
//   锁只在「我们自己推的那一段」里打开；别人触发的正常变化照旧处理。
//
// 生效范围：
//   · 只在「共享模式」（设置里 mode = 共享模式）+ 多人局（RunState.Players.Count ≥ 2）时介入；
//     这个模式下友尽尖塔原来的玩法（事件 / 火堆选项 / 战利品规则）全部不触发；
//   · 单人局、模式选了别的 = 一行都不改，完全原版；
//   · 「建筑师的背叛」的 PvP 决斗里**停掉共享血量**（那场是「只剩一个人」的收尾战，
//     一条血池会让所有人同时倒），金币照旧共享。
//
// 新局 / 读档的对齐口径（和 together 一样）：
//   · 新局：血池上限 = 各成员上限之和，当前血 = 上限（开局本来就都是满血）；
//           钱包 = 各成员金币之和（原版每人 99 → 两人 198）。
//     为什么要"求和"：怪物打的是**每个玩家各一次**，一条血池要扛 N 倍伤害，
//     不把 N 份血并起来就变成了"人多反而更脆"。
//   · 读档：取最大值对齐。存档里各人本来就是同一个数，取 max 只是为了让对齐幂等
//     —— 求和的话每次重连、每次读档都会再乘一遍人数。
//
// 与另一个参考实现不同的地方（故意的）：
//   · 不共享格挡：格挡是每人每回合自己攒的资源，各挡各的伤害；血池是共享的。
//   · 不镜像宠物：宠物（Creautre.Player 为 null）血量照旧各算各的。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>多人共享血池 / 钱包的镜像内核。</summary>
internal static class SharedVitals
{
    /// <summary>正在"往外推"的层数（> 0 时不再镜像，防递归）。</summary>
    private static int _hpDepth;
    private static int _goldDepth;

    /// <summary>共享血量此刻是否生效（只在「共享模式」下）。</summary>
    public static bool HpSharingActive
        => YouJinSettings.SharedModeEnabled
           && YouJinSettings.SharedHpEnabled
           && !ArchitectBetrayalEvent.BetrayalCombatActive;

    /// <summary>共享金币此刻是否生效（只在「共享模式」下）。</summary>
    public static bool GoldSharingActive
        => YouJinSettings.SharedModeEnabled && YouJinSettings.SharedGoldEnabled;

    /// <summary>最后一次拿到过的 run（`MembersOf` 的兜底，见 together 的"重建窗口"处理）。</summary>
    private static IRunState? _lastRun;

    /// <summary>本局的全员名单；单人局（或还没开局）返回空表。</summary>
    private static IReadOnlyList<Player> MembersOf(IRunState? run)
    {
        if (run is { Players.Count: > 1 })
            _lastRun = run;
        else
            run = _lastRun; // 兜底：这次写入来自重建窗口里的旧实例，RunState 已经拿不到

        var players = run?.Players;
        return players is { Count: > 1 } ? players : Array.Empty<Player>();
    }

    private static IReadOnlyList<Player> MembersOf(Player? player)
        => MembersOf(player?.RunState);

    // ── 镜像入口（被 Harmony 补丁调用）───────────────────────────────────────

    /// <summary>
    /// 某个玩家本体的血量 / 生命上限变了 → 推给其他成员。
    /// setterName 只用来决定日志措辞（两个 setter 的镜像动作是一样的）。
    /// </summary>
    public static void OnBodyStatChanged(Creature? creature, string? setterName)
    {
        try
        {
            if (_hpDepth > 0)
                return;

            // 只认玩家本体：宠物和敌人的 Creature.Player 是 null，不参与共享。
            if (creature is null || creature.Player is not { } owner)
                return;

            if (!HpSharingActive)
                return;

            var players = MembersOf(owner);
            if (players.Count < 2)
                return;

            var pushed = 0;
            MirrorHp(() =>
            {
                foreach (var mate in players)
                {
                    if (ReferenceEquals(mate, owner) || mate.Creature is not { } body)
                        continue;

                    PushMaxHp(creature, body);
                    PushHp(creature, body);
                    pushed++;
                }
            });

            if (pushed > 0 && string.Equals(setterName, "set_MaxHp", StringComparison.Ordinal))
            {
                Entry.Logger.Info(
                    $"共享血量：{creature.LogName} 生命上限变成 {creature.MaxHp}"
                    + $"（当前 {creature.CurrentHp}），已同步给 {pushed} 名队友");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享血量：镜像失败 {ex}");
        }
    }

    /// <summary>某个玩家的金币变了 → 推给其他成员。</summary>
    /// <param name="quietIfNoChange">
    /// true = "这次没有需要同步的差距"时不打日志。
    /// 给 `PlayerCmd.LoseGold` 那条冗余补丁用：它和 setter 补丁会为同一次扣款各触发一次，
    /// 第二次必然是"值已相等"，不打免得刷屏。
    /// </param>
    public static void OnGoldChanged(Player? player, bool quietIfNoChange = false)
    {
        try
        {
            if (_goldDepth > 0)
                return;

            if (player is null || !GoldSharingActive)
                return;

            var players = MembersOf(player);
            if (players.Count < 2)
                return;

            // together 的做法（GoldMirror.OnGoldChanged）：如果这次写入落在"重建窗口里的旧实例"上，
            // 就按 netId 认领回当前实例，用当前实例的值继续镜像 —— 不把旧实例那份存档值推出去。
            var source = player;
            foreach (var candidate in players)
            {
                if (candidate.NetId != player.NetId)
                    continue;

                source = candidate;
                break;
            }

            if (!ReferenceEquals(source, player))
            {
                Entry.Logger.Info(
                    $"共享金币：这次变化来自重建中的旧实例（NetId={player.NetId}），"
                    + "改用当前实例继续镜像");
            }

            var gold = source.Gold;
            var pushed = 0;

            MirrorGold(() =>
            {
                foreach (var mate in players)
                {
                    if (ReferenceEquals(mate, source) || mate.Gold == gold)
                        continue;

                    mate.Gold = gold;
                    pushed++;
                }
            });

            if (pushed > 0)
                Entry.Logger.Info($"共享金币：NetId={source.NetId} 变成 {gold}，已同步给 {pushed} 名队友");
            else if (!quietIfNoChange)
            {
                // 诊断用：值没变 / 队友那边已经是这个数，也记一行，方便对齐两端的数字
                var others = string.Join(
                    "、",
                    players.Select(mate => $"{mate.NetId}={mate.Gold}"));
                Entry.Logger.Info(
                    $"共享金币：NetId={source.NetId} 变成 {gold}（没有需要同步的差距）；当前 {others}");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享金币：镜像失败 {ex}");
        }
    }

    // ── 对齐（开局 / 读档 / 战斗开始）────────────────────────────────────────

    /// <summary>新开一局：血池和钱包都按「各人之和」合并。</summary>
    public static void AlignNewRun(RunState? run)
    {
        var players = MembersOf(run);
        if (players.Count < 2)
            return;

        try
        {
            if (HpSharingActive)
            {
                // 求和必须"全员的身体都在" —— 少一个人的话和会偏小，
                // 这种情况干脆不对齐（战斗开始那个钩子还会再拉平一次）。
                if (!players.All(player => player.Creature is not null))
                {
                    Entry.Logger.Info("共享血量：还有玩家没建好身体，跳过新局求和");
                }
                else
                {
                    var sharedMax = players.Sum(player => player.Creature!.MaxHp);
                    var sharedHp = Math.Min(players.Sum(player => player.Creature!.CurrentHp), sharedMax);

                    if (sharedMax > 0)
                    {
                        MirrorHp(() => ApplyBody(players, sharedMax, sharedHp));

                        Entry.Logger.Info(
                            $"共享血量：新局血池 = {players.Count} 人上限之和 {sharedMax}"
                            + $"（当前 {sharedHp}）");
                    }
                }
            }

            if (GoldSharingActive)
            {
                var sharedGold = players.Sum(player => player.Gold);

                MirrorGold(() =>
                {
                    foreach (var player in players)
                        player.Gold = sharedGold;
                });

                Entry.Logger.Info($"共享金币：新局钱包 = {players.Count} 人之和 {sharedGold}");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享血金币：新局对齐失败 {ex}");
        }
    }

    /// <summary>读档 / 重连：取最大值对齐（幂等，不会乘人数）。</summary>
    public static void AlignLoadedRun(RunState? run)
    {
        var players = MembersOf(run);
        if (players.Count < 2)
            return;

        try
        {
            if (HpSharingActive)
            {
                var maxHp = players.Max(player => player.Creature?.MaxHp ?? 0);
                if (maxHp > 0)
                {
                    var hp = Math.Min(players.Max(player => player.Creature?.CurrentHp ?? 0), maxHp);

                    var changed = 0;
                    MirrorHp(() => changed = ApplyBody(players, maxHp, hp));

                    if (changed > 0)
                    {
                        Entry.Logger.Info(
                            $"共享血量：读档对齐到 {hp}/{maxHp}（修正了 {changed} 名玩家）");
                    }
                }
            }

            if (GoldSharingActive)
            {
                var gold = players.Max(player => player.Gold);
                var changed = 0;

                MirrorGold(() =>
                {
                    foreach (var player in players)
                    {
                        if (player.Gold == gold)
                            continue;

                        player.Gold = gold;
                        changed++;
                    }
                });

                if (changed > 0)
                    Entry.Logger.Info($"共享金币：读档对齐到 {gold}（修正了 {changed} 名玩家）");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享血金币：读档对齐失败 {ex}");
        }
    }

    /// <summary>
    /// 战斗开始时拉平一次（照抄 together 的 BodyMirror.SyncAll）。
    /// 以 RunState.Players 里的第一个人为准 —— 两边客户端的 Players 顺序天然一致，
    /// 所以「谁的值算数」两端算的是同一个答案（绝不能用 LocalContext 之类的本机视角）。
    /// </summary>
    public static void SyncAll(IRunState? run)
    {
        var players = MembersOf(run);
        if (players.Count < 2)
            return;

        try
        {
            var primary = players[0];

            if (HpSharingActive && primary.Creature is { } body)
            {
                var maxHp = body.MaxHp;
                var hp = body.CurrentHp;

                var changed = 0;
                MirrorHp(() => changed = ApplyBody(players, maxHp, hp));

                if (changed > 0)
                {
                    Entry.Logger.Info(
                        $"共享血量：战斗开始拉平到 {hp}/{maxHp}（顺带修正了 {changed} 名玩家的漂移）");
                }
            }

            if (GoldSharingActive)
            {
                // ⚠ 金币的拉平**绝不能"以某个人为准往下削"**：
                // 削低了就等于把另一个人的钱凭空吞掉，之后他在商店怎么花都"不生效"
                // （`PlayerCmd.LoseGold` 是 `max(0, 当前-金额)`，0 再扣还是 0 → 不触发镜像）。
                // 实测踩过：日志里那行「战斗开始拉平到 0（修正了 1 名玩家）」就是这么来的。
                // 改成取最大值 —— 只补不削，永远不会吞钱。
                var gold = players.Max(player => player.Gold);
                var changed = 0;

                MirrorGold(() =>
                {
                    foreach (var player in players)
                    {
                        if (player.Gold == gold)
                            continue;

                        player.Gold = gold;
                        changed++;
                    }
                });

                if (changed > 0)
                    Entry.Logger.Info($"共享金币：战斗开始拉平到 {gold}（修正了 {changed} 名玩家）");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享血金币：战斗开始拉平失败 {ex}");
        }
    }

    // ── 内部工具 ────────────────────────────────────────────────────────────

    /// <summary>把一个血值写给全员，返回真正改动过的人数。</summary>
    private static int ApplyBody(IReadOnlyList<Player> players, int maxHp, int currentHp)
    {
        var changed = 0;

        foreach (var player in players)
        {
            if (player.Creature is not { } body)
                continue;

            if (body.MaxHp != maxHp)
                body.SetMaxHpInternal(maxHp);

            if (body.CurrentHp != currentHp)
                body.SetCurrentHpInternal(currentHp);

            changed++;
        }

        return changed;
    }

    /// <summary>
    /// 把 source 的上限推给 target。
    /// 注意 SetMaxHpInternal 内部还会把当前血夹到新上限（IL 确认），
    /// 所以调用方要跟着再推一次 PushHp，顺序不能反。
    /// </summary>
    private static void PushMaxHp(Creature source, Creature target)
    {
        if (source.MaxHp == target.MaxHp)
            return;

        target.SetMaxHpInternal(source.MaxHp);
    }

    /// <summary>把 source 的当前血推给 target。</summary>
    private static void PushHp(Creature source, Creature target)
    {
        if (source.CurrentHp == target.CurrentHp)
            return;

        target.SetCurrentHpInternal(source.CurrentHp);
    }

    /// <summary>镜像期间上锁：锁里触发的 setter 不再往外推。</summary>
    private static void MirrorHp(Action body)
    {
        if (_hpDepth > 0)
            return;

        _hpDepth++;
        try
        {
            body();
        }
        finally
        {
            _hpDepth--;
        }
    }

    /// <summary>金币版深度锁。</summary>
    private static void MirrorGold(Action body)
    {
        if (_goldDepth > 0)
            return;

        _goldDepth++;
        try
        {
            body();
        }
        finally
        {
            _goldDepth--;
        }
    }
}

/// <summary>补丁安装入口。</summary>
internal static class SharedVitalsPatches
{
    /// <summary>只 patch 本文件里的三个补丁类。</summary>
    public static void Apply()
    {
        var harmony = new Harmony("youjin.sharedvitals");
        PatchOne(harmony, typeof(SharedBodyMirrorPatch), "血量镜像");
        PatchOne(harmony, typeof(SharedGoldMirrorPatch), "金币镜像");
        PatchOne(harmony, typeof(SharedRunStatePatch), "新局/读档对齐");
        // ⛔ 已按需求停用：不再缩队友血条长度（保持原版长度）。
        //    要恢复"共享模式下把队友血条缩到 1/人数"，把下面这行放开即可（补丁类还在文件下面）。
        //    PatchOne(harmony, typeof(SharedPartyHpBarWidthPatch), "队友血条长度缩到 1/人数");
        PatchOne(harmony, typeof(SharedGoldImmediateApplyPatch), "丢金币消息立刻结算");
        PatchOne(harmony, typeof(SharedLoseGoldMirrorPatch), "LoseGold 兜底镜像");
    }

    private static void PatchOne(Harmony harmony, Type type, string label)
    {
        try
        {
            harmony.CreateClassProcessor(type).Patch();
            Entry.Logger.Info($"共享血金币：补丁「{label}」安装完成");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享血金币：补丁「{label}」安装失败 {ex}");
        }
    }
}

/// <summary>
/// ① 血量镜像：挂 Creature 的两个 setter。
/// 用 MethodBase __originalMethod 分辨这次是改了当前血还是改了上限。
/// </summary>
[HarmonyPatch]
internal static class SharedBodyMirrorPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        if (AccessTools.Method(typeof(Creature), "set_CurrentHp") is { } currentHp)
            yield return currentHp;

        if (AccessTools.Method(typeof(Creature), "set_MaxHp") is { } maxHp)
            yield return maxHp;
    }

    private static void Postfix(Creature __instance, MethodBase __originalMethod)
        => SharedVitals.OnBodyStatChanged(__instance, __originalMethod?.Name);
}

/// <summary>② 金币镜像：挂 Player.Gold 的 setter。</summary>
[HarmonyPatch(typeof(Player), "set_Gold")]
internal static class SharedGoldMirrorPatch
{
    private static void Postfix(Player __instance) => SharedVitals.OnGoldChanged(__instance);
}

/// <summary>
/// ②b 冗余保险：`PlayerCmd.LoseGold` 也收一次（照 together 的 `LoseGoldMirrorPatch`）。
///
/// 现在这个版本的 `PlayerCmd.LoseGold` 内部就是 `player.set_Gold(max(0, gold - amount))`（IL 确认），
/// 所以上面那条 setter 补丁已经覆盖了它 —— 这一条只是"万一以后某个版本 / 某个 mod
/// 绕过属性直接改字段"时的兜底。同一次扣款会触发两次镜像，第二次因为值已相等会空转（不打日志）。
/// </summary>
[HarmonyPatch]
internal static class SharedLoseGoldMirrorPatch
{
    /// <summary>参数用精确签名找重载：LoseGold(decimal amount, Player player, GoldLossType type)。</summary>
    private static MethodBase TargetMethod()
        => AccessTools.Method(
            typeof(PlayerCmd),
            "LoseGold",
            new[] { typeof(decimal), typeof(Player), typeof(GoldLossType) });

    /// <summary>参数用位置参数：__1 = player（第一个是 decimal amount）。</summary>
    private static void Postfix(Player __1) => SharedVitals.OnGoldChanged(__1, quietIfNoChange: true);
}

/// <summary>
/// ③ 开局 / 读档对齐：
///   · RunState.CreateForNewRun —— 新局，按「各人之和」合并；
///   · RunState.FromSerializable —— 读档/重连，按「最大值」对齐（幂等）。
/// （和 together 的 RunStateReadyPatch 同一个挂点，就是靠 __originalMethod.Name 分辨的。）
/// </summary>
[HarmonyPatch]
internal static class SharedRunStatePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        if (AccessTools.Method(typeof(RunState), "CreateForNewRun") is { } createForNewRun)
            yield return createForNewRun;

        if (AccessTools.Method(typeof(RunState), "FromSerializable") is { } fromSerializable)
            yield return fromSerializable;
    }

    private static void Postfix(RunState __result, MethodBase __originalMethod)
    {
        if (__result is null)
            return;

        if (string.Equals(__originalMethod?.Name, "CreateForNewRun", StringComparison.Ordinal))
            SharedVitals.AlignNewRun(__result);
        else
            SharedVitals.AlignLoadedRun(__result);
    }
}

/// <summary>
/// ④ 每场战斗开始时拉平一次。
/// 正常情况是个空操作（两边本来就一致）；只在有漂移时才写日志。
/// </summary>
[RegisterSingleton]
public sealed class SharedVitalsCombatModel : HookedSingletonModel
{
    public SharedVitalsCombatModel() : base(HookType.Combat)
    {
    }

    public override Task BeforeCombatStart()
    {
        try
        {
            // ── 自检一行：把"这场战斗到底哪些功能真的生效了"写进日志 ──────────────
            // 起因：很多"XX 失效了"的反馈，其实是设置里的**模式**没选对
            // （模式不是「共享模式」时，共享血量/金币/能量三条补丁全都直接 return，
            //   表现就是"能量加不到队友身上"）。
            // 这一行是只读诊断，不改任何行为。
            Entry.Logger.Info(
                $"状态自检：贪婪={YouJinSettings.GreedyEnabled} 共享={YouJinSettings.SharedEnabled} "
                + $"任务={YouJinSettings.QuestEnabled} 卧底={YouJinSettings.UndercoverPickMode}"
                + $"｜本场生效 共享血量={SharedVitals.HpSharingActive}"
                + $" 共享金币={SharedVitals.GoldSharingActive}"
                + $" 共享能量={SharedEnergy.Active}"
                + $"｜战斗人数={CurrentCombatState?.Players?.Count ?? 0}"
                + "（false = 那条功能这场完全没生效）");

            if (CurrentCombatState?.Players is not { Count: > 0 } players)
                return Task.CompletedTask;

            SharedVitals.SyncAll(players[0].RunState);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享血金币：战斗开始拉平失败 {ex}");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// ⑤ 【已停用】共享模式下把左边那排「自己 + 队友」的血条长度缩到 1/人数。
///
/// ⚠ 现状：**已按需求停用** —— `SharedVitalsPatches.Apply()` 里那一行被注释掉了，
///   所以共享模式下的血条就是原版长度（随"血量上限"变长变短，**不再缩小**）。
///   要恢复 1/人数 的缩小，把 Apply() 里那行放开即可（本类代码保持完好）。
///   （更早还试过"固定 175px"，也已回退：固定之后血条不再体现上限/池子的变化。）
/// ----------------------------------------------------------------------------
/// 只动血条，不动别的：不缩放节点、不改名字/能量/遗物图标的位置和大小
/// （上一版把整块 `NMultiplayerPlayerStateContainer` 一起缩了，左边那排里的遗物图标
///   跟着变形，佩尔之眼那种图标就看着"坏了" —— 这一版只改长度）。
///
/// 原版这里是怎么画血条的（sts2.dll，NMultiplayerPlayerState.UpdateHealthBarWidth）：
///     _healthBar.UpdateWidthRelativeToReferenceValue(_refHpBarWidth, _refHpBarMaxHp);
/// 而 NHealthBar 里那一句就是"宽度 = 血量上限 / 参考上限 × 参考宽度"：
///     size.X = creature.MaxHp / refMaxHp * refWidth;  HpBarContainer.Size = size;
/// 所以只要把参考宽度换成 `_refHpBarWidth / 人数`，血条就正好是原来的 1/N 长，
/// 高度、贴图、前景/背景/格挡那一套全都不用碰。
///
/// ⚠ 曾经试过"把长度固定成 175px"（把参考上限换成自己的 MaxHp，比例恒为 1）——
///   **已回退**：固定长度之后血条不再体现"上限/池子"的变化，实测观感是"血量像被卡住了"。
///   现在恢复成"长度随上限走、只按人数缩"的原行为。
///
/// 生效条件用 `SharedVitals.HpSharingActive`：开着共享血量才缩
/// （那时候每根血条都是同一个数）；关掉共享血量时血条各自有意义，保持原样。
/// </summary>
[HarmonyPatch(typeof(NMultiplayerPlayerState), "UpdateHealthBarWidth")]
internal static class SharedPartyHpBarWidthPatch
{
    private static readonly FieldInfo? HealthBarField =
        AccessTools.Field(typeof(NMultiplayerPlayerState), "_healthBar");

    private static readonly FieldInfo? RefWidthField =
        AccessTools.Field(typeof(NMultiplayerPlayerState), "_refHpBarWidth");

    private static readonly FieldInfo? RefMaxHpField =
        AccessTools.Field(typeof(NMultiplayerPlayerState), "_refHpBarMaxHp");

    private static bool Prefix(NMultiplayerPlayerState __instance)
    {
        try
        {
            if (__instance is null || !SharedVitals.HpSharingActive)
                return true; // 走原版

            var count = __instance.Player?.RunState?.Players?.Count ?? 0;
            if (count < 2)
                return true;

            if (HealthBarField?.GetValue(__instance) is not NHealthBar bar)
                return true;

            if (RefWidthField?.GetValue(null) is not float refWidth || refWidth <= 0f)
                return true;

            if (RefMaxHpField?.GetValue(null) is not float refMaxHp || refMaxHp <= 0f)
                return true;

            bar.UpdateWidthRelativeToReferenceValue(refWidth / count, refMaxHp);
            return false;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享模式：血条长度缩放失败（按原版处理）{ex}");
            return true;
        }
    }
}

/// <summary>
/// ⑥ 商店/奖励里的"丢金币"消息如果撞上战斗标记，就地补扣，别拖到战斗结束。
/// ----------------------------------------------------------------------------
/// 症状：队友在商店买东西，主机那边的金币不动（看着像"金币不共享"）。
///
/// 链路（反编译 + 日志确认）：
///   · 买东西 → `MerchantXxxEntry.OnTryPurchase` → `PlayerCmd.LoseGold`（本地立刻扣）
///     + `RewardSynchronizer.SyncLocalGoldLost`（把 GoldLostMessage 发给对方）。
///   · 对方收到 → `RewardSynchronizer.HandleGoldLostMessage`：
///         if (CombatManager.Instance.IsInProgress) { 塞进 _bufferedMessages; return; }
///         PlayerCmd.LoseGold(...);            // 正常路径
///     而 `_bufferedMessages` 的补发点在 `OnCombatEnded` —— 也就是说，
///     只要这条消息是在"战斗标记还是 true"的时间窗里到的（地图移动那一段实测就是：
///     日志里正跑着 CombatStateSynchronizer 的广播），扣款会被压到**下一场战斗结束**才生效。
///     这期间两台机器的金币数就不一致了 —— 日志证据：商店里 11 笔 GoldLostMessage
///     既没有 `Player ... lost N gold` 那行，也没有我们"共享金币"的镜像行。
///
/// 修法：给 `HandleGoldLostMessage` 挂前缀 —— 如果"标记说在战斗里、但当前房间其实不是战斗房间"，
/// 就自己按原版那句立刻扣掉（`PlayerCmd.LoseGold(..., GoldLossType.Lost)`），
/// 并且**跳过原版**（否则它还会缓冲一份，之后重复扣一次）。
/// 真的在战斗房间里就完全照原版走（保持原版顺序），非共享模式也一律不插手。
/// </summary>
[HarmonyPatch]
internal static class SharedGoldImmediateApplyPatch
{
    private static readonly FieldInfo? PlayerCollectionField =
        AccessTools.Field(typeof(RewardSynchronizer), "_playerCollection");

    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(RewardSynchronizer), "HandleGoldLostMessage");

    /// <summary>参数用 __0/__1（位置参数），免得猜原方法参数叫什么。</summary>
    private static bool Prefix(RewardSynchronizer __instance, GoldLostMessage __0, ulong __1)
    {
        try
        {
            var inProgress = CombatManager.Instance?.IsInProgress == true;

            if (!SharedVitals.GoldSharingActive)
                return true;

            // 诊断：只要这条消息真的走到这里，就先记一行（商店里买东西一共就几条，不会刷屏）
            Entry.Logger.Info(
                $"[诊断] 收到丢金币消息：gold={__0.goldLost} sender={__1} 战斗标记={inProgress}");

            // 原版这条路本来就会立刻扣
            if (!inProgress)
                return true;

            if (PlayerCollectionField?.GetValue(__instance) is not IPlayerCollection players)
            {
                Entry.Logger.Info("[诊断] 丢金币：拿不到 _playerCollection，交回原版");
                return true;
            }

            var player = players.GetPlayer(__1);
            if (player is null)
            {
                Entry.Logger.Info($"[诊断] 丢金币：按 netId={__1} 找不到玩家，交回原版");
                return true;
            }

            // 注意：**不能**再用"当前房间是不是战斗房间"来判 —— 实测地图移动那一段
            // `RunState.CurrentRoom` 还停在上一个战斗房，那样判断会把消息又丢回原版缓冲，
            // 等于没修。共享金币下这条扣款本来就该和买家那台机器同步落地，直接扣。
            TaskHelper.RunSafely(PlayerCmd.LoseGold(__0.goldLost, player, GoldLossType.Lost));
            Entry.Logger.Info(
                $"共享金币：{player.NetId} 丢掉 {__0.goldLost} 金币"
                + $"（立刻结算；结算后 {player.NetId}={player.Gold}）");

            return false;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"共享金币：立刻结算丢金币失败（按原版处理）{ex}");
            return true;
        }
    }
}
