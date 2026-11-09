// ============================================================================
// 友尽尖塔（youjin）：多人药水「烟雾弹」
// ----------------------------------------------------------------------------
// 规则（多人模式）：
//   1. 战斗中使用：玩家"脱战"—— 不能被指定为目标、不能出牌、不会被敌人攻击；
//      但**不算死亡**：血量保留、不走死亡钩子（不分钱、不触发遗物死亡效果）；
//   2. 这一场按「弃战」处理：不参与结算（用户 2026-10-28 确认：脱战本来就不该拿奖励）。
//      注：PvP（建筑师的挑拨）胜利后本来就没有战斗奖励，直接进终章；
//      普通 PvE 里目前发奖励时 IsAlive 已经恢复，所以实际可能还是会拿到 —— 这条待确认。
//   3. 队友也全倒 → 这一局就结束了（原版"全员阵亡"判定把脱战的人也算死）。
//
// 历史（0.9.66 及以前是错的）：
//   旧实现直接 CreatureCmd.Kill(自己, force: true)，等于真死 —— 观感就是"用了就自杀"。
//   而且 TargetType 是 AnyPlayer，玩家还能点队友（结果死在的是自己），
//   单人局更是白喝送命。用户 2026-10-28 报的就是这两件事。
//
// 现在的实现：
//   · TargetType = TargetType.Self（原版支持：NPotionHolder.UsePotion 里
//     TargetType==1 时直接 target = Owner.Creature，不弹选人界面，也不依赖 CanThrowAtAlly）；
//   · PassesCustomUsabilityCheck 覆写：没有活着的队友 / 不在战斗中 / 派对模式没开时返回 false
//     → 药水弹窗的「使用」按钮直接变灰（原版唯一读取点 NPotionPopup._Ready），
//     杜绝"单人喝了把自己送走、全场没人能打完"；
//   · 效果改成"脱战但不死"：SmokeBombEscape.cs 用一个集合 + Creature.get_IsAlive 后置补丁
//     让原版自己把该玩家当成倒下处理。整套机制、以及为什么不用 CreatureCmd.Escape，
//     都写在 SmokeBombEscape.cs 顶部注释里，改这里之前先看那份。
//   · 获取途径：注册进原版**共享药水池**（SharedPotionPool），按普通稀有药水处理：
//     战斗掉落、奖励、商店都可能出。单人局也会抽到，但按钮是灰的（用不了）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.PotionPools;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>多人向药水：烟雾弹（使用后自己脱战，但不算死亡）。当普通稀有药水处理。</summary>
[RegisterPotion(typeof(SharedPotionPool))]
public sealed class SmokeBombPotion : ModPotionTemplate
{
    /// <summary>稀有度（商店价格按原版药水定价走，未额外改价）。</summary>
    public override PotionRarity Rarity => PotionRarity.Rare;

    /// <summary>只有战斗中才有意义。</summary>
    public override PotionUsage Usage => PotionUsage.CombatOnly;

    /// <summary>
    /// 只能对自己用（原版 TargetType.Self 是正经写法：FairyInABottle 就是 Self）。
    /// NPotionHolder.UsePotion 里 TargetType==1 会直接 target = Owner.Creature，不弹选人界面。
    /// </summary>
    public override TargetType TargetType => TargetType.Self;

    /// <summary>
    /// 「使用」按钮的可用性（原版唯一读取点：NPotionPopup._Ready → 为 false 就把按钮禁用）。
    /// 没有活着的队友就别让他喝：脱战之后没人能打完这场战斗，等于把局面卡死。
    /// </summary>
    public override bool PassesCustomUsabilityCheck => SmokeBombEscape.CanUseNow(Owner);

    /// <summary>
    /// 图标：优先 youjin/images/potions/smoke_bomb.png（+ _outline.png），
    /// 没有就用占位图（观者 mod 的「瓶装奇迹」）。
    /// </summary>
    public override PotionAssetProfile AssetProfile => PotionArt.Resolve("smoke_bomb");

    /// <summary>使用后：脱战——本人退出这场战斗，但不算死（血量与奖励都保留）。</summary>
    protected override async Task OnUse(PlayerChoiceContext choiceContext, Creature? target)
    {
        try
        {
            var player = Owner;
            var combatState = CombatManager.Instance;
            Entry.Logger.Info(
                "烟雾弹：OnUse 进入 — Owner=" + (player is null ? "null" : $"NetId={player.NetId}")
                + $"、creature={(player?.Creature is null ? "null" : $"Hp={player.Creature.CurrentHp} alive={player.Creature.IsAlive}")}"
                + $"、战斗中={combatState is not null && combatState.IsInProgress}"
                + $"、朝向={YouJinSettings.GameplayEnabled}"
                + $"、已有队友={SmokeBombEscape.HasLivingTeammate(player)}");

            if (player is null)
            {
                Entry.Logger.Info("烟雾弹：Owner 为空 —— 这瓶药水没有归属玩家，直接返回（什么都不会发生）");
                return;
            }

            if (!YouJinSettings.GameplayEnabled)
            {
                Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 派对模式总开关（贪婪）关着，直接返回");
                return;
            }

            var creature = player.Creature;
            if (creature is null)
            {
                Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 的 Creature 为空，直接返回");
                return;
            }

            if (!creature.IsAlive || SmokeBombEscape.IsEscaped(creature))
            {
                Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 已经倒下了，无事发生");
                return;
            }

            // 双保险：按钮那边已经挡过一次，这里再挡一次（网络/异常路径兜底）
            if (!SmokeBombEscape.HasLivingTeammate(player))
            {
                Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 没有活着的队友，脱战会把局面卡死，忽略");
                return;
            }

            var combat = CombatManager.Instance;
            if (combat is null || !combat.IsInProgress)
            {
                Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 不在战斗中，无事发生");
                return;
            }

            Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 使用烟雾弹，脱战（不扣血、不扣能量、不触发死亡结算）");
            SmokeBombEscape.MarkEscaped(creature);

            // 仿 CreatureCmd.Kill 死亡善后（<Kill>d__14 → HandlePlayerDeath）做两件事：
            //   1) 把手牌收进弃牌堆/消耗堆；2) 星星清零。
            // ★ 能量**不清零** —— 玩家要求脱战不扣能量（原版 Kill 会 SetEnergy(0)，这里故意不做）。
            await combat.RemoveDeadPlayerCardsFromCombat(combat.CurrentCombatId, player);
            await PlayerCmd.SetStars(0m, player);

            // 正轮到玩家出牌时，必须替他把这回合结束掉 —— 否则结束回合按钮被
            // NEndTurnButton.PlayerCanTakeAction（判 IsAlive）禁用，回合循环会一直等他。
            // 原版 Kill 就是这么处理的（偏移 01E2~0241，条件是 CurrentSide == Player，枚举值 1）。
            if (creature.CombatState?.CurrentSide == CombatSide.Player)
                PlayerCmd.EndTurn(player, false, null);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：结算失败 {ex}");
        }
    }

    /// <summary>名字/描述注入 potions 表（商店里显示名字要用，由 Entry 提前调一次）。</summary>
    /// <param name="force">true = 覆盖式（LocManager 表加载完之后由 LateLocalization 调，改文案不用重导 PCK）。</param>
    public static void EnsureLocalized(bool force = false)
    {
            var id = ModelDb.GetId(typeof(SmokeBombPotion))?.Entry;
            if (string.IsNullOrEmpty(id))
                return;
        LocInject.Merge("potions", $"{id}.title", force, chinese => new Dictionary<string, string>
        {
            [$"{id}.title"] = chinese ? "烟雾弹" : "Smoke Bomb",
            [$"{id}.description"] = chinese
                ? "当众失踪：立刻退出这场战斗（[red]不算死亡[/red]，血量保留；不能被指定、不能出牌，敌人也不会再打你），这一场按弃战结算。"
                  + "全队覆没则一局结束。只能对自己使用；没有活着的队友时用不了。"
                : "Vanish: instantly leave this fight (not death — your HP is kept; untargetable, can't play cards, enemies ignore you); you sit out the rest of it. If the whole team falls, the run ends. Self only; unusable without a living teammate.",
        }, "烟雾弹");
    }
}
