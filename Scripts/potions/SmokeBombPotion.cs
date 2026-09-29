// ============================================================================
// 友尽尖塔（youjin）：多人药水「烟雾弹」
// ----------------------------------------------------------------------------
// 规则（多人模式）：
//   1. 战斗中使用：玩家"逃跑"，进入死亡状态 —— 不会被攻击、不能出牌、血条消失；
//   2. 队友赢下这场战斗 → 照常走胜利结算（死人也有奖励集合，原版 OfferRoomEndRewards 是遍历所有人）；
//   3. 队友也全死 → 这一局就结束了，自然拿不到任何奖励。
//
// 反编译踩点：
//   · 原版本来就有"玩家死亡但战斗继续"的状态（多人合作）：玩家生物死后，
//     CombatManager.HandlePlayerDeath(combatId, player) 会做死亡处理
//     （日志里会打 "Player X died, doing death handling"，还会 RemoveDeadPlayerCardsFromCombat 收掉他的手牌），
//     表现就是"不能出牌、不再被指定为目标、血条消失"——正是需求里的效果。
//   · 所以这瓶药水只需要把使用者自己的生物杀掉即可：CreatureCmd.Kill(creature, force: true)。
//     force 的语义（反编译确认）：false 会走 Hook.ShouldDie —— "免死/复活"类效果就卡在那里
//     （比如战士项圈会把人拉回 6 血，于是变成"假死"：不触发死亡结算、战斗后血量还恢复）；
//     true 则跳过这个判定，直接走真正的死亡流程（AfterDeath 等钩子照常触发，
//     所以死亡分钱、遗物复活计数这些才会正常响应）。
//     之后游戏自己的死亡处理会把状态摆好（不能出牌、不再被指定为目标、血条消失），
//     不需要我们手写 UI 或禁用手牌。
//   · 奖励：CombatRoom.OfferRoomEndRewards 是 foreach (player in CombatState.Players) 发的，
//     死掉的玩家依然在列表里，所以队伍胜利时他照常拿奖励；队伍全灭则整局失败，没有结算。
//   · 获取途径：注册进原版**共享药水池**（SharedPotionPool），也就是当一瓶普通稀有药水处理：
//     战斗掉落、奖励、商店都可能出（PotionFactory.GetPotionOptions 读的就是角色池 + 共享池）。
//     注意：正因为进了共享池，单人局也可能抽到它（只是单人没有队友，用了等于自杀）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

/// <summary>多人向药水：烟雾弹（使用后自己退出战斗，进入死亡状态）。当普通稀有药水处理。</summary>
[RegisterPotion(typeof(SharedPotionPool))]
public sealed class SmokeBombPotion : ModPotionTemplate
{
    /// <summary>稀有度（商店价格按原版药水定价走，未额外改价）。</summary>
    public override PotionRarity Rarity => PotionRarity.Rare;

    /// <summary>只有战斗中才有意义。</summary>
    public override PotionUsage Usage => PotionUsage.CombatOnly;

    /// <summary>沿用原版惯例：药水都要选一个玩家当目标（效果只作用于使用者自己）。</summary>
    public override TargetType TargetType => TargetType.AnyPlayer;

    /// <summary>
    /// 图标：优先 youjin/images/potions/smoke_bomb.png（+ _outline.png），
    /// 没有就用占位图（观者 mod 的「瓶装奇迹」）。
    /// </summary>
    public override PotionAssetProfile AssetProfile => PotionArt.Resolve("smoke_bomb");

    /// <summary>使用后：把自己按正常死亡流程处理掉（= 逃跑）。</summary>
    protected override async Task OnUse(PlayerChoiceContext choiceContext, Creature? target)
    {
        try
        {
            var player = Owner;
            if (player is null || !YouJinSettings.GameplayEnabled)
                return;

            var creature = player.Creature;
            if (creature is null)
                return;

            if (!creature.IsAlive)
            {
                Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 已经倒下了，无事发生");
                return;
            }

            Entry.Logger.Info($"烟雾弹：NetId={player.NetId} 使用烟雾弹，退出这场战斗（进入死亡状态）");
            // force: true = 跳过"免死/复活"判定，确保是真死（否则只是假死，不触发死亡相关结算）
            await CreatureCmd.Kill(creature, true);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：结算失败 {ex}");
        }
    }

    /// <summary>名字/描述注入 potions 表（商店里显示名字要用，由 Entry 提前调一次）。</summary>
    public static void EnsureLocalized()
    {
        try
        {
            var loc = LocManager.Instance;
            var table = loc?.GetTable("potions");
            if (loc is null || table is null)
                return;

            var id = ModelDb.GetId(typeof(SmokeBombPotion))?.Entry;
            if (string.IsNullOrEmpty(id) || table.HasEntry($"{id}.title"))
                return;

            var chinese = (loc.Language ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            table.MergeWith(new Dictionary<string, string>
            {
                [$"{id}.title"] = chinese ? "烟雾弹" : "Smoke Bomb",
                [$"{id}.description"] = chinese
                    ? "当众失踪：立刻退出这场战斗（[red]视为倒下[/red]，不会被攻击、不能出牌）。"
                      + "队友赢下战斗则照常结算，全队覆没则一局结束。"
                    : "Vanish: instantly leave this fight (counted as downed — untargetable, can't play cards). If your team wins you still get rewarded; if the whole team falls, the run ends.",
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"烟雾弹：本地化注入失败 {ex}");
        }
    }
}
