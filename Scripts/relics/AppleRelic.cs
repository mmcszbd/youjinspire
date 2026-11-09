// ============================================================================
// 友尽尖塔（youjin）：分离模式开局遗物「苹果」
// ----------------------------------------------------------------------------
// 规则（用户 2026-11-03 定）：
//   1. 分离模式（SplitSoloRun）在**开局**发给局里的每一个玩家，每人一件；
//   2. 持有者**死亡时**：①先按「当前生命上限」把血回复到 30%（这一步最先执行，
//      所以哪怕这一下是致命伤，也是"原地复活"）；②再把生命上限 -10（最少留 1）；
//      ③然后当作控制台 `win` —— 直接把全场敌人杀掉、结算胜利；
//   3. **无限次数**：不写 WasUsed / 不覆盖 IsUsedUp（原版默认 false），
//      死几次触发几次（上限会一直掉）。
//
// 为什么挂 ShouldDieLate + AfterPreventingDeath（抄原版蜥蜴尾巴 LizardTail）：
//   · Hook.ShouldDie 先跑所有遗物的 ShouldDie，再跑 ShouldDieLate；
//     只要有一个返回 false，这一下就不算死，并且那个模型成为 preventer；
//   · 之后 CreatureCmd.Kill 走的是"阻止死亡"分支：
//       Hook.AfterDeath(wasRemovalPrevented:true) → 我们的 AfterPreventingDeath(creature)
//     → 回血之后 creature.IsDead 已经是 false，所以不会再递归回到死亡流程。
//   ⚠ 顺序要求（用户明确说"血量恢复最先执行"）：回血 → 掉上限 → 判胜，全在
//     AfterPreventingDeath 里按这个次序做完，中间不插别的判定。
//
// win 的实现照抄 MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.WinConsoleCmd：
//   ① 拿走敌人身上的能力；② CreatureCmd.Kill；③ CombatManager.CheckWinCondition()。
//   胜利判定本身是 Enemies.Any(c => c.IsAlive && c.IsPrimaryEnemy)，杀干净就成立。
//
// 获取途径：注册进 DeprecatedRelicPool（原版只装了 DeprecatedRelic 的"回收站"池）。
//   随机遗物只从 SharedRelicPool + 角色池里抽（RelicGrabBag），所以这件遗物
//   **永远不会**出现在战斗奖励/宝箱/商店/事件里 —— 用户要的"只在分离模式开局送"。
//   但必须注册进某个池：RelicModel.Pool 是
//   AllRelicPools.First(p => p.AllRelicIds.Contains(Id))，不在任何池里会抛
//   "Sequence contains no matching element"（和药水那边一模一样的坑）。
//
// 素材：沿用已有的 youjin/images/relics/deathmatch.png（原本"死斗"遗物只做了图和文案、
//   没有 C# 类，所以那件遗物以前其实不存在）。没有 _outline.png，描边直接留空。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.RelicPools;
using PvpDuel.Core.Duel;
using PvpDuel.Core.SplitMode;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>分离模式开局遗物：苹果（死亡即赢下这场战斗，血回 30%，上限 -10，无限次）。</summary>
[RegisterRelic(typeof(DeprecatedRelicPool), StableEntryStem = "APPLE")]
public sealed class AppleRelic : ModRelicTemplate
{
    /// <summary>主图 / 大图：沿用已有的 deathmatch 素材（PCK 里已有，不需要重导）。</summary>
    public const string ArtPath = "res://youjin/images/relics/deathmatch.png";

    /// <summary>每次触发扣掉的生命上限。</summary>
    public const int MaxHpCost = 10;

    /// <summary>复活时回复到「当前上限」的百分比。</summary>
    public const decimal HealPercent = 30m;

    private const string TableName = "relics";

    /// <summary>只影响图鉴配色；它在回收站池里，抽不到。</summary>
    public override RelicRarity Rarity => RelicRarity.Starter;

    /// <summary>图标：主图直接用 deathmatch.png；没有描边图，留空走原版回退。</summary>
    public override RelicAssetProfile AssetProfile => new(
        IconPath: ArtPath,
        IconOutlinePath: null,
        BigIconPath: ArtPath);

    /// <summary>
    /// 死亡拦截（第二遍，跑在 ShouldDie 之后）。
    /// 只有"持有者本人"这一下死亡被拦；其他死亡（敌人、队友）一律放行。
    /// </summary>
    /// <remarks>
    /// ⛔ <b>PVP 战斗里这件遗物整体失效</b>（用户 2026-11-04 明确要求）：
    /// 决斗/镜像决斗/建筑师的背叛里，玩家死了还复活会把"谁赢"永远判不出来
    /// （决斗的胜负是靠"参战者倒下"这一下触发的，见 DuelPlayerDownPatch；
    ///  而决斗里敌方侧本来就是空的，杀敌判胜那条路也走不通）。
    /// 所以这里直接 return true —— 不拦、不回血、不扣上限、不判胜，照常死。
    /// </remarks>
    public override bool ShouldDieLate(Creature creature)
    {
        try
        {
            if (Owner?.Creature is not { } mine || !ReferenceEquals(mine, creature))
                return true;

            if (IsPvpCombat())
            {
                Log($"NetId={Owner.NetId} 处于 PVP 战斗，苹果不生效，按原版照常死亡");
                return true;
            }

            Log($"NetId={Owner.NetId} 触发死亡拦截（HP={creature.CurrentHp}/{creature.MaxHp}），放行复活流程");
            return false;
        }
        catch (Exception ex)
        {
            Log($"ShouldDieLate 异常，按原版处理（照常死）：{ex}");
            return true;
        }
    }

    /// <summary>
    /// 当前这场战斗是不是"玩家对玩家"（PVP）。
    /// 三个来源各管一段，都是各模块自己的静态标志：
    /// <list type="bullet">
    /// <item><see cref="DuelSession.InDuel" />：回合制决斗 / 擂台赛（每场战斗开始时重算）。</item>
    /// <item><see cref="ArchitectBetrayalEvent.BetrayalCombatActive" />：建筑师的背叛（玩家互杀）。</item>
    /// </list>
    /// </summary>
    internal static bool IsPvpCombat()
    {
        try
        {
            return DuelSession.InDuel
                   || ArchitectBetrayalEvent.BetrayalCombatActive;
        }
        catch (Exception ex)
        {
            Log($"PVP 判定异常，按非 PVP 处理：{ex}");
            return false;
        }
    }

    /// <summary>
    /// 拦下死亡之后真正结算：先回血到 30%，再扣 10 点上限，最后直接判胜。
    /// 全程 try/catch —— 这里抛异常会卡住原版的死亡流程（表现是黑屏/卡死）。
    /// </summary>
    public override async Task AfterPreventingDeath(Creature creature)
    {
        try
        {
            if (Owner?.Creature is not { } mine || !ReferenceEquals(mine, creature))
                return;

            Flash();

            var maxBefore = creature.MaxHp;
            var healTo = Math.Max(1, (int)Math.Floor(maxBefore * HealPercent / 100m));

            // ① 最先执行：按「当前上限」把血回到 30%（此刻通常是 0，等于原地复活）。
            await CreatureCmd.SetCurrentHp(creature, healTo);
            Log($"NetId={Owner.NetId} 回血：上限 {maxBefore} → 血回到 {healTo}（当前 {creature.CurrentHp}）");

            // ② 再扣生命上限，最少留 1（避免把自己扣到 0 上限直接死）。
            var maxAfter = Math.Max(1, maxBefore - MaxHpCost);
            if (maxAfter != maxBefore)
                await CreatureCmd.SetMaxHp(creature, maxAfter);

            if (creature.CurrentHp > creature.MaxHp)
                await CreatureCmd.SetCurrentHp(creature, creature.MaxHp);

            Log($"NetId={Owner.NetId} 上限：{maxBefore} → {creature.MaxHp}（血 {creature.CurrentHp}）");

            // ③ 直接赢下这场战斗（等价控制台 win）。
            await WinCombatAsync();
        }
        catch (Exception ex)
        {
            Log($"结算失败（死亡已被拦下，局面保持原样）：{ex}");
        }
    }

    /// <summary>照抄 WinConsoleCmd：清能力 → 逐个杀 → CheckWinCondition。</summary>
    internal static async Task WinCombatAsync()
    {
        var combat = CombatManager.Instance;
        if (combat is null || !combat.IsInProgress)
        {
            Log("不在战斗中，跳过判胜");
            return;
        }

        var state = combat.DebugOnlyGetState();
        if (state is null)
        {
            Log("拿不到战斗状态，跳过判胜");
            return;
        }

        var enemies = state.Enemies.Where(enemy => enemy is not null && enemy.IsAlive).ToList();
        Log($"判胜：场上还有 {enemies.Count} 个活着的敌人");

        foreach (var enemy in enemies)
        {
            enemy.RemoveAllPowersInternalExcept();
            await CreatureCmd.Kill(enemy);
        }

        await combat.CheckWinCondition();
        Log("判胜：已调用 CheckWinCondition");
    }

    /// <summary>
    /// 名字/描述注入 relics 表（和药水那边同一套"表加载完之后覆盖式注入"的做法）。
    /// </summary>
    /// <param name="force">true = 覆盖式（LateLocalization 在 LocManager 加载完表之后调用；改文案不用重导 PCK）。</param>
    public static void EnsureLocalized(bool force = false)
    {
            var id = ModelDb.GetId(typeof(AppleRelic))?.Entry;
            if (string.IsNullOrEmpty(id))
                return;
        LocInject.Merge(TableName, $"{id}.title", force, chinese => new Dictionary<string, string>
        {
            [$"{id}.title"] = chinese ? "苹果" : "Apple",
            [$"{id}.description"] = chinese
                ? "死亡时：先把血量回复到生命上限的 [blue]30%[/blue]（最先结算），"
                  + "然后生命上限 [red]-10[/red]，并直接赢下这场战斗。"
                  + "无限次触发（每次都会掉 10 点上限）。"
                : "On death: heal to [blue]30%[/blue] of your max HP first, "
                  + "then lose [red]10[/red] max HP, and instantly win this combat. "
                  + "Unlimited uses (each one costs 10 max HP).",
        }, "苹果");
    }

    private static void Log(string message) => Entry.Logger.Info($"[苹果] {message}");
}
