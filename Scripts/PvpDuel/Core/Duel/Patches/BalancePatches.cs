using System.Threading.Tasks;

using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace PvpDuel.Core.Duel;

/// <summary>平衡①：数出牌，数到上限就自动结束这个回合。</summary>
/// <remarks>
/// 计数点用 <c>Hook.AfterCardPlayed</c>（本体成就也是这么数"单回合打了几张"的）。
/// 强制结束走的是玩家点"结束回合"的同一个入口
/// （<c>RequestEnqueue(new EndPlayerTurnAction(...))</c>，见 <c>NEndTurnButton</c>），所以两端一致。
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
internal static class DuelBalanceCardPlayedPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPlay __2)
    {
        if (__2?.Player is not { } player
            || __2.Card is null
            || !ModuleSwitches.Balance
            || !DuelSession.InDuel
            || !DuelRules.CanRunDuel(player.Creature.CombatState))
        {
            return;
        }

        if (!DuelBalance.NoteCardPlayed(player, __2.Card))
        {
            return;
        }

        if (player.PlayerCombatState is { } playerState)
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new EndPlayerTurnAction(player, playerState.TurnNumber));
        }
    }
}

/// <summary>平衡②：同一张牌打够次数之后，本回合就打不出去了。</summary>
/// <remarks>
/// <para>
/// 本体 <c>CardModel.CanPlay</c> 有两个重载：无参的那个只是转调
/// <c>CanPlay(out UnplayableReason, out AbstractModel)</c>，所以<b>只补后者就能覆盖两条路径</b>。
/// </para>
/// <para>
/// 但 out 形参的 byref 类型<b>写不进特性实参</b>（C# 只允许常量 / <c>typeof</c> / 数组），
/// 而直接写 <c>typeof(UnplayableReason)</c> 又匹配不上（日志里那句
/// <c>Undefined target method for patch method …</c> 就是它）—— 所以走 <see cref="TargetMethod" />
/// 这条口子，在运行时用 <c>MakeByRefType()</c> 解析。
/// </para>
/// <para>Postfix 只绑 <c>__instance</c> / <c>__result</c>，不绑 <c>reason</c>：免得再被参数名/类型卡一次。</para>
/// </remarks>
[HarmonyPatch]
internal static class DuelBalanceCardLockPatch
{
    private static System.Reflection.MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(CardModel),
            nameof(CardModel.CanPlay),
            [typeof(UnplayableReason).MakeByRefType(), typeof(AbstractModel).MakeByRefType()]);
    }

    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (!__result || !DuelSession.InDuel || !DuelBalance.IsCardLocked(__instance))
        {
            return;
        }

        __result = false;
    }
}

/// <summary>平衡③：后手第一次回合的补偿（能量 / 格挡 / 抽牌）。</summary>
/// <remarks>
/// 挂在 <c>Hook.AfterPlayerTurnStart</c>：这时本体的回合开场已经跑完（能量已重置、起手已抽），
/// 所以在这里补能量不会被覆盖、补格挡也不会被回合开始的清格挡吃掉。
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class DuelBalanceCompensationPatch
{
    [HarmonyPostfix]
    private static void Postfix(object __0, object __2)
    {
        if (__0 is not ICombatState state
            || __2 is not Player actor
            || !ModuleSwitches.Balance
            || !DuelSession.InDuel
            || !DuelRules.CanRunDuel(state))
        {
            return;
        }

        var (energy, block, cards) = DuelBalance.CompensationFor(actor, state);

        if (energy == 0 && block == 0 && cards == 0)
        {
            return;
        }

        TaskHelper.RunSafely(ApplyAsync(actor, energy, block, cards));
    }

    private static async Task ApplyAsync(Player actor, int energy, int block, int cards)
    {
        if (energy > 0 && actor.PlayerCombatState is { } playerState)
        {
            playerState.GainEnergy(energy);
        }

        if (block > 0)
        {
            // 0 = 既不吃格挡修正、也不算"被能力加成"，就是白给一坨格挡。
            await CreatureCmd.GainBlock(actor.Creature, block, (ValueProp)0, null);
        }

        if (cards > 0)
        {
            var context = new HookPlayerChoiceContext(
                actor,
                LocalContext.NetId ?? actor.NetId,
                GameActionType.CombatPlayPhaseOnly);

            await CardPileCmd.Draw(context, cards, actor, fromHandDraw: true);
        }

        DuelBalance.MarkCompensated(actor);

        ModInfo.Info(
            $"后手补偿：netId={actor.NetId} +{energy} 能量 / +{block} 格挡 / +{cards} 抽牌"
            + $" → 当前能量={actor.PlayerCombatState?.Energy}");
    }
}

/// <summary>建筑师挑拨：boss 每回合多 1 点能量。</summary>
/// <remarks>
/// <para>
/// 挂点和后手补偿同一个（<c>Hook.AfterPlayerTurnStart</c>）：这时候回合开场已经跑完
/// （能量重置、起手抽牌都做过），补进去的 1 点不会被重置吃掉。
/// </para>
/// <para>
/// 刻意<b>不</b>看 <c>ModuleSwitches.Balance</c>：boss 是建筑师挑拨的玩法，不是「平衡」模块的一部分，
/// 把平衡开关关掉不该让 boss 少一费。
/// </para>
/// <para>
/// ⚠ 也<b>不</b>看 <c>DuelSession.InDuel</c> / <c>DuelRules.CanRunDuel</c>：
/// 挑拨那一场固定是手速混战，手速形态既不 <c>Arm()</c>（所以 InDuel 为假），
/// 3 人以上时 CanRunDuel 也为假 —— 之前就是被这两条挡掉，boss 一次能量都没加上
/// （2026-11-06 18:12 的日志里只有血量那条，没有一条「本回合 +1 能量」）。
/// 现在的判据只有「本局有 boss + 这一位就是 boss」，是否在决斗里交给 BossState 自己认局。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class BossEnergyPatch
{
    [HarmonyPostfix]
    private static void Postfix(object __0, object __2)
    {
        if (__0 is not ICombatState
            || __2 is not Player actor
            || !PvpDuel.Core.Boss.BossState.HasBoss
            || !PvpDuel.Core.Boss.BossState.IsBoss(actor))
        {
            return;
        }

        if (actor.PlayerCombatState is not { } playerState)
        {
            return;
        }

        playerState.GainEnergy(1);

        ModInfo.Info(
            $"建筑师挑拨：boss netId={actor.NetId} 本回合 +1 能量 → 当前能量={playerState.Energy}");
    }
}
