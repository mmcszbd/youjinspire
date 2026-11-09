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
    public static Task<Player?> ChooseTeammateAsync(Player owner)
        => TeammatePicker.ChooseSyncedAsync(
            owner,
            TeammatePicker.OriginCenter(),
            focusController: false,
            room: null,
            tag: "选队友");
}
