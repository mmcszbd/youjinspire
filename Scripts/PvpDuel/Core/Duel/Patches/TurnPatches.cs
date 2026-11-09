using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace PvpDuel.Core.Duel;

/// <summary>轮流行动：每个玩家回合开始时，把「这一回合的参与者」限定成当前行动者。</summary>
/// <remarks>
/// <para>
/// 为什么不是「跳过另一个人的回合开场」：那条只挡住 SetupPlayerTurn（抽牌/回能），
/// 挡不住 Creature.AfterTurnStart —— 那才是清格挡的地方，它遍历的是这一侧这一回合的参与者，
/// 于是老实现里两个人每个回合开头都被清一次格挡。
/// </para>
/// <para>
/// 本体现在这条 PlayersTakingExtraTurn 在 StartTurn 里决定 creaturesStartingTurn / playersStartingTurn：
/// 只有参与者跑 AfterTurnStart 与 SetupPlayerTurn；没参与的玩家会被本体自动置成「已结束回合」；
/// 回合结束的钩子也只对参与者跑。一句话：每个回合都由本体按「额外回合」的规格跑一遍，参与者只有当前行动者。
/// </para>
/// <para>回合上限的心跳顺便挂在这里：每个回合必然跑到，且与任何快捷键、开关无关。</para>
/// </remarks>
[HarmonyPatch(typeof(CombatManager), "StartTurn")]
internal static class DuelActorTurnPatch
{
    [HarmonyPrefix]
    private static void Prefix(object __0)
    {
        if (!ModuleSwitches.Core || !DuelSession.InDuel)
        {
            return;
        }

        if (DuelTurnState.StateOf(__0) is not { } state)
        {
            return;
        }

        // 左上角那行"限制小抄"：进战斗 / 回合开始时确保挂上并刷新一次。
        DuelHud.EnsureRunning();

        // 观战者那边每帧把战斗 UI 校正回「藏起来」：Enable() 会在回合开始时把它恢复。
        DuelSpectatorUi.EnsureRunning();

        // ⛔ 原来的「回合上限」判定（DuelSession.TryFinishByRoundLimit）已在 0.10.8 删掉：
        //    决斗只打到有人倒下，不再按回合数判超时。

        // 敌方回合不碰：决斗里敌方侧没有单位，那一回合本来就是空跑。
        if (state.CurrentSide != CombatSide.Player)
        {
            return;
        }

        if (DuelTurnState.ExtraTurnsOf(__0) is not { } participants)
        {
            return;
        }

        // 名单非空 = 本体自己发起的额外回合（佩尔之眼之类）：某人多打一轮，不换行动者、不加人。
        // 但"连续额外回合"不设上限就等于可以无限跳过对手，所以这里按设置卡一刀。
        if (participants.Count > 0)
        {
            var extraActor = participants[0];

            if (DuelBalance.AllowExtraTurn(extraActor))
            {
                ModInfo.LogOnce("这一回合是本体的额外回合，原样放行（不换行动者）");
                return;
            }

            ModInfo.Info(
                $"连续额外回合已达上限 {DuelConfig.ExtraTurnLimit}，这一次不再给（netId={extraActor.NetId}）");

            participants.Clear();   // 清空 → 落到下面按正常轮转指定行动者
        }

        DuelSession.BeginActorTurn(state);

        if (DuelSession.Actor is not { } actor)
        {
            return;
        }

        // 本回合的计数只在"正常回合"清零：额外回合不清，否则可以靠刷额外回合把出牌上限洗掉。
        DuelBalance.BeginTurn(actor);
        participants.Add(actor);

        ModInfo.Info($"本回合只有 netId={actor.NetId} 行动（对手不抽牌、不清格挡、手牌留到自己回合）");
    }
}

/// <summary>回合数：没出手的那位不该被算作过了一回合。</summary>
/// <remarks>
/// 本体在「进入我方侧」时对所有玩家 IncrementTurnNumber —— 本体每个回合大家都动，这没问题。
/// 决斗里每回合只有一个人动，照旧全体 +1 的话第二位玩家的首次回合会变成「第 2 回合」，
/// SetupPlayerTurn 里那段「TurnNumber == 1 时把固有牌 Innate 挪到牌堆顶」就永远不跑。
/// 所以只让「即将行动、且不是第一次行动」的那位 +1；本体的额外回合照旧 +1。
/// </remarks>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.IncrementTurnNumber))]
internal static class DuelTurnNumberPatch
{
    /// <summary>PlayerCombatState 里的私有字段 _player。</summary>
    private static readonly AccessTools.FieldRef<PlayerCombatState, Player> OwnerField =
        AccessTools.FieldRefAccess<PlayerCombatState, Player>("_player");

    [HarmonyPrefix]
    private static bool Prefix(PlayerCombatState __instance)
    {
        if (!ModuleSwitches.Core || !DuelSession.InDuel)
        {
            return true;
        }

        var owner = OwnerField(__instance);

        // 判「参战者」而不是「在场人数」：擂台赛里四人全在场，只有两位上场。
        // 以前这里写死 state.Players.Count != 2，四人局整个补丁恒为放行 → 所有人的回合数每回合都 +1。
        if (owner?.Creature.CombatState is not { } state
            || DuelSession.ParticipantsOf(state).Count != 2)
        {
            return true;
        }

        // 观战者没有「自己的回合」，回合数一步都不该动。
        if (DuelSession.IsSpectator(owner))
        {
            ModInfo.LogOnce("跳过观战者的回合数自增（他没上场）");
            return false;
        }

        if (CombatManager.Instance.PlayersTakingExtraTurn.Contains(owner))
        {
            return true;
        }

        // 只给当前行动者 +1 —— 这才是"他自己打过的第几个回合"。
        // （老写法是"下一个行动者 && 已经打过一轮"，等于把这本账记到对手头上，而且第一回合是 0，
        // 本体的固有牌 Innate 判定 TurnNumber == 1 因此永远晚一拍。）
        var counts = ReferenceEquals(DuelSession.Actor, owner);

        if (!counts)
        {
            ModInfo.LogOnce("跳过非行动者的回合数自增（他这一回合没出手）");
        }

        return counts;
    }
}
