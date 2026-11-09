using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace PvpDuel.Core.Duel;

/// <summary>决斗站位的<b>纯表现层</b>：P1 左、P2 右、面对面，宠物（含奥斯提）跟着镜像。</summary>
/// <remarks>
/// 只动节点位置 / 朝向 / 绘制层级，不碰任何对局数据（判定用的 Hitbox 与跨端状态都不受影响）。
/// 位置的计算规则在 <see cref="DuelRules.IsRightSidePlayer" />，这里只负责摆。
/// </remarks>

/// <summary>决斗站位：P1 固定左、P2 固定右，两人拉开、面对面。</summary>
/// <remarks>
/// 光搬容器没用：两个容器都锚在正中央，reparent 后全局坐标一模一样，所以位置必须自己写。
/// 朝向照本体 SurroundedPower 的做法镜像 Body.Scale.X（判定用的 Hitbox 不受影响）。
/// 只在 P2 进场时摆一次 —— 那时两个人的节点都已经建好了。
/// </remarks>
[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
internal static class DuelOpponentVisualPatch
{
    private static readonly AccessTools.FieldRef<NCombatRoom, Control> EnemyContainerField =
        AccessTools.FieldRefAccess<NCombatRoom, Control>("_enemyContainer");

    /// <summary>离画面中线的水平距离（480 正好是本体摆单个敌人时的槽位）。</summary>
    private const float SlotX = 480f;

    /// <summary>本体摆位用的高度（玩家、敌人用的都是这个 y）。</summary>
    private const float SlotY = 200f;

    [HarmonyPostfix]
    private static void Postfix(NCombatRoom __instance, Creature __0)
    {
        if (!DuelSession.InDuel || __0 is null)
        {
            return;
        }

        if (__0.CombatState is not { } state || state.Players.Count != 2)
        {
            return;
        }

        if (__0.IsPlayer)
        {
            // 玩家：只在第二位（P2）进场时摆一次 —— 那时两个人的节点都已经建好了。
            if (!ReferenceEquals(state.Players[1].Creature, __0))
            {
                return;
            }

            try
            {
                // 延后一帧：AddCreature 在建节点的过程中调用，本体紧接着还会统一摆位，等它摆完再覆盖。
                Callable.From(() => ApplyDuelLayout(__instance, state)).CallDeferred();
            }
            catch (Exception ex)
            {
                ModInfo.Warn($"决斗站位失败（不影响战斗）：{ex.Message}");
            }

            return;
        }

        // 宠物 / 召唤物：它们的节点是**后来**才建的（实例：缚魂瓶在 BeforeCombatStart 才召唤奥斯提），
        // 所以不能只靠"P2 进场那一帧"那次摆位 —— 那时它还不存在。这里在它自己进场时再摆一次。
        if (__0.PetOwner is not { } petOwner)
        {
            return;
        }

        try
        {
            Callable.From(() => PlacePet(__instance, petOwner, __0, 0)).CallDeferred();
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"宠物站位失败（不影响战斗）：{ex.Message}");
        }
    }

    private static void ApplyDuelLayout(NCombatRoom room, ICombatState state)
    {
        if (!GodotObject.IsInstanceValid(room) || state.Players.Count != 2)
        {
            return;
        }

        var p1 = state.Players[0];
        var p2 = state.Players[1];

        Place(room, p1.Creature, onLeft: true);
        Place(room, p2.Creature, onLeft: false);

        // 宠物（奥斯提之类）是本体在 PositionPlayersAndPets 里按"玩家都在左侧"摆的 ——
        // 我们把玩家挪到 ∓480 之后必须把它们也挪过来，否则会停在原地（用户报的"奥斯提位置不对"）。
        PlacePets(room, state, p1);
        PlacePets(room, state, p2);

        ModInfo.LogOnce(
            $"决斗站位：P1(netId={p1.NetId}) 左 {room.GetCreatureNode(p1.Creature)?.Position}"
            + $"，P2(netId={p2.NetId}) 右 {room.GetCreatureNode(p2.Creature)?.Position}（右侧立绘已反向）");
    }

    private static void Place(NCombatRoom room, Creature creature, bool onLeft)
    {
        if (room.GetCreatureNode(creature) is not { } node)
        {
            return;
        }

        // 右边那位搬到敌方容器：两个容器位置相同，所以这只影响绘制层级（敌方容器画在后面=更靠前）。
        if (!onLeft)
        {
            MoveToEnemyContainer(room, node);
        }

        node.Position = new Vector2(onLeft ? -SlotX : SlotX, SlotY);
        SetFacing(node, faceLeft: !onLeft);

        // 悬停能否命中取决于 Hitbox 的 MouseFilter；原版队友立绘本就可悬停，被关成 Ignore 就补回来。
        if (node.Hitbox is { } hitbox && hitbox.MouseFilter == Control.MouseFilterEnum.Ignore)
        {
            hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
            ModInfo.LogOnce("对手立绘的 Hitbox 原本不可交互，已恢复（否则鼠标点不到）");
        }
    }

    /// <summary>把某位玩家的全部宠物摆到他旁边。</summary>
    private static void PlacePets(NCombatRoom room, ICombatState state, Player owner)
    {
        var index = 0;

        foreach (var pet in state.Allies.Concat(state.Enemies)
                     .Where(unit => !unit.IsPlayer && ReferenceEquals(unit.PetOwner, owner)))
        {
            PlacePet(room, owner, pet, index++);
        }
    }

    /// <summary>把一只宠物摆到主人旁边（右侧玩家镜像到左边、朝向翻转）。</summary>
    private static void PlacePet(NCombatRoom room, Player owner, Creature pet, int index)
    {
        if (room.GetCreatureNode(owner.Creature) is not { } ownerNode
            || room.GetCreatureNode(pet) is not { } petNode)
        {
            return;
        }

        var onLeft = !DuelRules.IsRightSidePlayer(owner);
        var isOsty = pet.Monster is Osty;
        var offset = PetOffset(petNode, index, isOsty);

        if (!onLeft)
        {
            // 右侧玩家是镜像的（朝向 + 位置），宠物也要镜像到他的另一边。
            // 注意：奥斯提的偏移已经由 DuelOstyOffsetPatch 镜像过一次了，这里不能再取反。
            if (!isOsty)
            {
                offset = new Vector2(-offset.X, offset.Y);
            }

            MoveToEnemyContainer(room, petNode);
        }

        petNode.Position = ownerNode.Position + offset;
        SetFacing(petNode, faceLeft: !onLeft);

        ModInfo.LogOnce(
            $"宠物站位：{pet.Monster?.Id.Entry ?? "?"} → {petNode.Position}"
            + $"（主人 {(onLeft ? "左" : "右")}、{(isOsty ? "用奥斯提偏移" : "用通用偏移")}）");
    }

    /// <summary>宠物相对主人的偏移：奥斯提用本体那套（贴着主人右侧、略高）；其它宠物按通用规则摆在旁边。</summary>
    private static Vector2 PetOffset(NCreature petNode, int index, bool isOsty)
    {
        if (isOsty)
        {
            try
            {
                return NCreature.GetOstyOffsetFromPlayer(petNode.Entity);
            }
            catch (Exception ex)
            {
                ModInfo.Warn($"取奥斯提偏移失败，改用通用摆位：{ex.Message}");
            }
        }

        var width = petNode.Visuals?.Bounds.Size.X ?? 0f;

        return new Vector2(width * 0.5f + 20f + index * 70f, 10f);
    }

    /// <summary>搬到敌方容器（两个容器位置相同，只影响绘制层级：敌方容器画在后面=更靠前）。</summary>
    private static void MoveToEnemyContainer(NCombatRoom room, NCreature node)
    {
        var enemyContainer = EnemyContainerField(room);

        if (enemyContainer is not null && node.GetParent() != enemyContainer)
        {
            node.GetParent()?.RemoveChild(node);
            enemyContainer.AddChildSafely(node);
        }
    }

    /// <summary>照本体 SurroundedPower.FaceDirection 的做法翻转朝向（幂等：方向对了就不动）。</summary>
    private static void SetFacing(NCreature node, bool faceLeft)
    {
        FlipX(node.Body, faceLeft);
        FlipX(node.Visuals?.FormVfxHolder, faceLeft);
    }

    private static void FlipX(Node2D? body, bool faceLeft)
    {
        if (body is not null && ((faceLeft && body.Scale.X > 0f) || (!faceLeft && body.Scale.X < 0f)))
        {
            body.Scale *= new Vector2(-1f, 1f);
        }
    }

    private static void FlipX(Control? body, bool faceLeft)
    {
        if (body is not null && ((faceLeft && body.Scale.X > 0f) || (!faceLeft && body.Scale.X < 0f)))
        {
            body.Scale *= new Vector2(-1f, 1f);
        }
    }
}

/// <summary>决斗里右侧玩家（P2）的宠物偏移要镜像到左边。</summary>
/// <remarks>
/// <para>
/// 本体 <c>NCreature.GetOstyOffsetFromPlayer</c> 的偏移是固定朝右的：
/// <c>Vector2.Right * 主人碰撞盒半宽 + Osty.MinOffset/MaxOffset 插值 (150~250, -75)</c>。
/// 决斗里 P2 站在右侧、立绘已镜像，所以他的宠物要镜像到左边，否则会跑到他右外侧。
/// </para>
/// <para>
/// 打这个 Postfix 而不是只在摆位时改：本体还有别的路径会把宠物重新摆到
/// 「主人位置 + 这个偏移」（例如 <c>NCreature</c> 缩放动画结束后的重摆），
/// 从这里改才能保证每条路径都一致。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(NCreature), nameof(NCreature.GetOstyOffsetFromPlayer))]
internal static class DuelOstyOffsetPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __0, ref Vector2 __result)
    {
        if (__0?.PetOwner is not { } owner || !DuelRules.IsRightSidePlayer(owner))
        {
            return;
        }

        __result = new Vector2(0f - __result.X, __result.Y);
    }
}
