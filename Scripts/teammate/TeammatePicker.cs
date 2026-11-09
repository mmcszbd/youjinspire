// ============================================================================
// 友尽尖塔（youjin）：选一名队友（指向选择）
// ----------------------------------------------------------------------------
// 全仓原本有三份同源实现：火堆「假寐」、火堆偷窃（本机版）、事件里的选队友。
// 三份都在 `NTargetManager.StartTargeting` 周围抄同一段代码，差异只有四个变量：
// 起点（按钮位置 / 屏幕中心）、要不要连手柄焦点、要不要播火堆说明动画、走不走同步器。
// 现在收敛到这里，差异用参数表达。
//
// ★ 同步器是严格锁步协议：`ReserveChoiceId` 用自增计数器，要求两端调用次数与顺序
//   完全一致；`SyncLocalChoice` 只在主人端调，非主人端必须 `await WaitForRemoteChoice`。
//   所以：
//     · 有取消/超时/跳过、做不到两端序列严格对齐的流程（火堆偷窃）→ 用 `ChooseLocalAsync`，
//       只在本机弹，结果由 RitsuLib Sidecar 广播，完全不产生 choice id；
//     · 走原版协程的流程（治愈、事件选人）→ 用 `ChooseSyncedAsync`。
//   两边混用会出现「挂起的 choice id 集合不同 → 离开房间对账 StateDivergence → 被踢」，
//   或「某一端卡在 WaitForRemoteChoice → 界面悬挂黑屏」。
// ============================================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace YouJin.Scripts;

internal static class TeammatePicker
{
    /// <summary>
    /// 走原版 <see cref="PlayerChoiceSynchronizer" /> 的选人：主人端本机弹，其余端等结果。
    /// 取消也会同步一个空结果 —— 否则远端永远等不到。
    /// </summary>
    /// <param name="room">火堆房间；非火堆传 null（不播说明动画）。</param>
    /// <param name="focusController">手柄操作时是否连焦点（火堆要，战斗里原版不连）。</param>
    /// <param name="tag">日志前缀。</param>
    public static async Task<Player?> ChooseSyncedAsync(
        Player owner,
        Vector2 origin,
        bool focusController,
        NRestSiteRoom? room,
        string tag)
    {
        var sync = RunManager.Instance?.PlayerChoiceSynchronizer;
        if (sync is null)
            return null;

        var choiceId = sync.ReserveChoiceId(owner);

        if (!LocalContext.IsMe(owner))
        {
            var remote = await sync.WaitForRemoteChoice(owner, choiceId);
            room?.AnimateDescriptionUp();
            var chosen = remote.AsPlayerId() is { } chosenId ? owner.RunState?.GetPlayer(chosenId) : null;
            if (chosen is null)
                Entry.Logger.Info($"{tag}：没有选定目标（远端取消 / 选择结果为空），本次不结算");
            return chosen;
        }

        var target = await PickLocalAsync(owner, origin, focusController, room);

        // 自己的目标自己已经知道，直接用它收尾。
        // 千万别再 await WaitForRemoteChoice(自己)：房主/客机都收不到自己的选择回环，
        // 一 await 就永远卡住（界面从此点不动）。
        sync.SyncLocalChoice(owner, choiceId, PlayerChoiceResult.FromPlayerId(target?.NetId));

        if (target is null)
            Entry.Logger.Info($"{tag}：没有选定目标（取消 / 选择结果为空），本次不结算");

        return target;
    }

    /// <summary>
    /// 只在本机弹「选一名队友」，**不碰同步器**（用于火堆偷窃这种可能取消/超时的流程）。
    /// 调用方负责先判断本机是不是主人，结果自己走 Sidecar 广播。
    /// </summary>
    public static async Task<Player?> ChooseLocalAsync(Player owner, RestSiteOption? option)
    {
        var room = NRestSiteRoom.Instance;
        if (room is null)
            return null;

        return await PickLocalAsync(owner, OriginFor(room, option), true, room);
    }

    /// <summary>火堆选项按钮的中心点；拿不到按钮就退回原点。</summary>
    public static Vector2 OriginFor(NRestSiteRoom? room, RestSiteOption? option)
    {
        var button = room is null || option is null ? null : room.GetButtonForOption(option);
        return button is null ? Vector2.Zero : button.GlobalPosition + button.Size / 2f;
    }

    /// <summary>战斗里没有火堆按钮，原版用屏幕中心当起点。</summary>
    public static Vector2 OriginCenter()
        => NTargetManager.Instance?.GetViewport()?.GetVisibleRect().Size / 2f ?? Vector2.Zero;

    private static async Task<Player?> PickLocalAsync(
        Player owner,
        Vector2 origin,
        bool focusController,
        NRestSiteRoom? room)
    {
        var targetManager = NTargetManager.Instance;
        if (targetManager is null)
            return null;

        room?.AnimateDescriptionDown();

        try
        {
            var usingController = UsingController();

            targetManager.StartTargeting(
                TargetType.AnyPlayer,
                origin,
                usingController ? TargetMode.Controller : TargetMode.ClickMouseToTarget,
                ShouldCancelTargeting,
                node => !IsSelfNode(node, owner));

            if (usingController && focusController && room is not null)
                SetupControllerFocus(room, owner);

            return NodeToPlayer(await targetManager.SelectionFinished());
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"选队友：本机选择失败 {ex}");
            return null;
        }
        finally
        {
            if (room is not null)
            {
                try
                {
                    room.AnimateDescriptionUp();
                }
                catch
                {
                    // 收尾失败无所谓
                }
            }
        }
    }

    private static bool UsingController()
    {
        // ⚠ 0.107 的 NControllerManager 没有 IsUsingDirectionalNavigation（0.108+ 才有），
        //    只有更粗的 IsUsingController → 按版本分开写。
#if DEBUG107
        return NControllerManager.Instance?.IsUsingController == true;
#else
        return NControllerManager.Instance?.IsUsingDirectionalNavigation == true;
#endif
    }

    /// <summary>原版的取消条件：有别的界面弹出来 / 上限容器在用，就别继续指着了。</summary>
    private static bool ShouldCancelTargeting()
        => (NOverlayStack.Instance?.ScreenCount ?? 0) > 0
           || NCapstoneContainer.Instance?.InUse == true;

    /// <summary>自己的角色不能被指。</summary>
    private static bool IsSelfNode(Node? node, Player owner)
        => NodeToPlayer(node) is { } player && player.NetId == owner.NetId;

    /// <summary>手柄操作时给可选角色之间连好上下左右焦点（和原版治愈一样）。</summary>
    private static void SetupControllerFocus(NRestSiteRoom room, Player owner)
    {
        try
        {
            var boxes = room.Characters
                .Where(character => character.Player is not null && character.Player.NetId != owner.NetId)
                .Select(character => character.Hitbox)
                .Where(box => box is not null)
                .ToList();

            if (boxes.Count == 0)
                return;

            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                box.FocusMode = Control.FocusModeEnum.All;
                box.FocusNeighborTop = box.GetPath();
                box.FocusNeighborBottom = box.GetPath();
                box.FocusNeighborLeft = boxes[i > 0 ? i - 1 : boxes.Count - 1].GetPath();
                box.FocusNeighborRight = boxes[i < boxes.Count - 1 ? i + 1 : 0].GetPath();
            }

            boxes[0].GrabFocus();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"选队友：手柄焦点设置失败 {ex.Message}");
        }
    }

    /// <summary>被点中的节点 → 玩家。可能是战斗角色、队友头像或火堆角色节点。</summary>
    private static Player? NodeToPlayer(Node? node) => node switch
    {
        NMultiplayerPlayerState state => state.Player,
        NRestSiteCharacter character => character.Player,
        _ => null,
    };
}
