// ============================================================================
// 友尽尖塔（youjin）：死亡分钱
// ----------------------------------------------------------------------------
// 规则：
//   1. 一名玩家真正死亡时，把他身上的金币平分给**还活着的队友**；
//   2. 除不尽的零头留在他自己身上（不销毁、也不多给谁）；
//   3. 单人局、没金币、没有活着的队友 → 什么都不做；
//   4. 「被复活遗物救回来」不算死亡，不会分钱。
//
// 反编译踩点：
//   · 钩子选 AfterDeath(PlayerChoiceContext, Creature, bool wasRemovalPrevented, float deathAnimLength)
//     —— 它在死亡真正结算完之后触发；被预防的死亡走的是 AfterPreventingDeath（复活类效果），
//        不会进 AfterDeath，所以"复活不算死"是天然成立的。
//   · 死亡是战斗级事件（Hook.AfterDeath 遍历的是 CombatState 的监听者），
//     所以这个单例用 HookType.Combat。
//   · 只处理玩家：Creature.Player 非空即玩家本体（宠物走 PetOwner，这里不算）。
//   · 金币接口沿用原版：PlayerCmd.LoseGold(金额, 玩家, GoldLossType.Lost) /
//     PlayerCmd.GainGold(金额, 玩家, wasStolenBack=false)。
//     它俩内部就是直接改 player.Gold（GainGold 里能读到 player.Gold += amount），
//     每个客户端都会跑同样的逻辑，纯确定，不会不同步。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>玩家死亡时把金币平分给存活队友。</summary>
[RegisterSingleton]
public class DeathGoldSplitModel : HookedSingletonModel
{
    public DeathGoldSplitModel() : base(HookType.Combat)
    {
    }

    public override async Task AfterDeath(
        PlayerChoiceContext choiceContext,
        Creature creature,
        CardModel? source,
        bool wasRemovalPrevented,
        float deathAnimLength)
    {
        try
        {
            if (!YouJinSettings.GameplayEnabled)
                return;

            var player = creature.Player;
            if (player is null)
                return; // 敌人或宠物死了不分钱

            var run = player.RunState;
            if (run is null || run.Players.Count <= 1)
                return;

            var gold = player.Gold;
            if (gold <= 0)
                return;

            var mates = run.Players
                .Where(mate => mate.NetId != player.NetId && mate.Creature is { IsAlive: true })
                .ToList();

            if (mates.Count == 0)
            {
                Entry.Logger.Info($"死亡分钱：NetId={player.NetId} 死了，但没有活着的队友，金币留着");
                return;
            }

            var share = gold / mates.Count;
            if (share <= 0)
            {
                Entry.Logger.Info($"死亡分钱：NetId={player.NetId} 只有 {gold} 金币，不够平分给 {mates.Count} 人，跳过");
                return;
            }

            await PlayerCmd.LoseGold(share * mates.Count, player, GoldLossType.Lost);

            foreach (var mate in mates)
                await PlayerCmd.GainGold(share, mate, false);

            var remainder = gold - share * mates.Count;
            Entry.Logger.Info(
                $"死亡分钱：NetId={player.NetId} 死亡，{gold} 金币平分给 {mates.Count} 名队友，每人 {share}"
                + (remainder > 0 ? $"（零头 {remainder} 留在他身上）" : string.Empty));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"死亡分钱：结算失败 {ex}");
        }
    }
}
