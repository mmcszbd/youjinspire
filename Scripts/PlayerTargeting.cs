// ============================================================================
// 通用"选一名队友"工具
// ----------------------------------------------------------------------------
// 和火堆「治愈 / 假寐」用的是同一套原版指向选择（NTargetManager + PlayerChoiceSynchronizer），
// 但这里不依赖 NRestSiteRoom（事件房间里没有那个节点），箭头起点取视口中心。
//
// 流程（照抄原版 MendRestSiteOption.OnSelect，并修掉了"自己等自己"的坑）：
//   1. ReserveChoiceId 占一个选择号；
//   2. 本机玩家：StartTargeting → 点角色 → SelectionFinished → NodeToPlayer →
//      SyncLocalChoice（取消就同步一个空结果，免得远端一直等）→ 直接返回，不再等自己的回环；
//   3. 其他客户端：等 WaitForRemoteChoice 拿到结果。
// ============================================================================

using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace YouJin.Scripts;

internal static class PlayerTargeting
{
    /// <summary>
    /// 让 owner 选一名队友（不能选自己）。选不到（取消 / 没法选）返回 null。
    /// 所有客户端都要参与这个协程，返回结果一致。
    /// </summary>
    public static async Task<Player?> ChooseTeammateAsync(Player owner)
    {
        var sync = RunManager.Instance?.PlayerChoiceSynchronizer;
        if (sync is null)
            return null;

        var choiceId = sync.ReserveChoiceId(owner);
        Player? target = null;

        if (LocalContext.IsMe(owner))
        {
            var targetManager = NTargetManager.Instance;
            if (targetManager is null)
                return null;

            var origin = targetManager.GetViewport()?.GetVisibleRect().Size / 2f ?? Vector2.Zero;
        // ⚠ 0.107 的 NControllerManager 没有 IsUsingDirectionalNavigation（0.108+ 才有），
        //    只有更粗的 IsUsingController → 按版本分开写。
#if DEBUG107
        var usingController = NControllerManager.Instance?.IsUsingController == true;
#else
        var usingController = NControllerManager.Instance?.IsUsingDirectionalNavigation == true;
#endif

            targetManager.StartTargeting(
                TargetType.AnyPlayer,
                origin,
                usingController ? TargetMode.Controller : TargetMode.ClickMouseToTarget,
                ShouldCancelTargeting,
                node => NodeToPlayer(node) is not { } player || player.NetId != owner.NetId);

            var node = await targetManager.SelectionFinished();
            target = NodeToPlayer(node);

            // 取消时也同步空结果，否则远端会一直等
            sync.SyncLocalChoice(owner, choiceId, PlayerChoiceResult.FromPlayerId(target?.NetId));

            if (target is null)
                Entry.Logger.Info("选队友：没有选定任何人（取消）");

            // 自己选完就已经知道结果了，不能再等自己的回环（那会永远卡住）
            return target;
        }

        var result = await sync.WaitForRemoteChoice(owner, choiceId);
        if (result.AsPlayerId() is { } chosenId)
            target = owner.RunState?.GetPlayer(chosenId);

        return target;
    }

    /// <summary>原版的取消条件：有别的界面弹出来就别继续指着了。</summary>
    private static bool ShouldCancelTargeting()
        => (NOverlayStack.Instance?.ScreenCount ?? 0) > 0
           || NCapstoneContainer.Instance?.InUse == true;

    /// <summary>被点中的节点 → 玩家。</summary>
    private static Player? NodeToPlayer(Node? node) => node switch
    {
        NMultiplayerPlayerState state => state.Player,
        NRestSiteCharacter character => character.Player,
        _ => null,
    };
}
