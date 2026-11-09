using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace PvpDuel.Core.Duel;

/// <summary>把「对手」也算进敌我关系：决斗里两位玩家互为对手、互不为队友。</summary>
/// <remarks>
/// <para>
/// 本体的 <c>GetOpponentsOf</c> / <c>GetTeammatesOf</c> 是按 <c>Creature.Side</c> 分侧的，
/// 而决斗里两人都留在我方侧 → 「对手」拿到的是空的敌方侧、队友里却混进了对手。
/// </para>
/// <para>
/// <b>雷球就是踩这条</b>：<c>LightningOrb.ApplyLightningDamage</c> 从
/// <c>CombatState.GetOpponentsOf(Owner.Creature)</c> 取候选，取到空表就 <c>return</c> —— 一点伤害都不打。
/// 同一个 API 也是 AOE 攻击（<c>AttackCommand</c>）、毒、若干遗物自动效果的取敌入口，所以这条修好，
/// 决斗里"自动效果打不到人"这一类问题一起解决。
/// </para>
/// <para>
/// 这里只依赖「谁问的」，不看本机视角，所以两端结果一致（不像 <c>HittableEnemies</c> 那份要按本机重算）。
/// 队友那条同时把对手的召唤物排除掉（1v1 里没有队友，否则"给队友上 buff"会变成给对方上 buff）。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.GetOpponentsOf))]
internal static class DuelOpponentsOfPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __0, ref IReadOnlyList<Creature> __result)
    {
        var opponents = DuelRules.OpponentsOf(__0);
        if (opponents.Count == 0)
        {
            return;
        }

        var merged = new List<Creature>(__result ?? []);

        foreach (var unit in opponents)
        {
            if (!merged.Any(creature => ReferenceEquals(creature, unit)))
            {
                merged.Add(unit);
            }
        }

        __result = merged;
    }
}

/// <summary>决斗里对手（和他的召唤物）不算队友。</summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.GetTeammatesOf))]
internal static class DuelTeammatesOfPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __0, ref IReadOnlyList<Creature> __result)
    {
        if (__result is null)
        {
            return;
        }

        var opponents = DuelRules.OpponentsOf(__0);

        if (opponents.Count == 0)
        {
            return;
        }

        __result = [.. __result.Where(unit => !opponents.Any(other => ReferenceEquals(other, unit)))];
    }
}

/// <summary>闸门①：卡牌层——对手（另一位玩家）按 AnyEnemy 放行、不再算 AnyAlly。</summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
internal static class DuelCardTargetValidityPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, Creature? target, ref bool __result)
    {
        if (__instance is null)
        {
            return;
        }

        var allowed = DuelRules.OpponentTargetOverride(
            __instance.TargetType,
            target,
            __instance.Owner?.Creature,
            __instance.CombatState);

        if (allowed is null || allowed.Value == __result)
        {
            return;
        }

        __result = allowed.Value;

        ModInfo.LogOnce(allowed.Value
            ? "目标校验：对手被当作「敌人」放行（AnyEnemy）"
            : "目标校验：对手不再被当作「队友」（AnyAlly）");
    }
}

/// <summary>闸门①的孪生兄弟：药水走的是另一套校验，不一起改会出现「UI 让点、点完用不出去」。</summary>
[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.IsValidTarget))]
internal static class DuelPotionTargetValidityPatch
{
    [HarmonyPostfix]
    private static void Postfix(PotionModel __instance, Creature? target, ref bool __result)
    {
        if (__instance is null)
        {
            return;
        }

        var owner = __instance.Owner?.Creature;

        var allowed = DuelRules.OpponentTargetOverride(
            __instance.TargetType,
            target,
            owner,
            owner?.CombatState);

        if (allowed is null || allowed.Value == __result)
        {
            return;
        }

        __result = allowed.Value;

        ModInfo.LogOnce("目标校验：药水也可以指向对手了");
    }
}

/// <summary>闸门②：选中 UI 层——鼠标悬停/点击对手时让 <c>NTargetManager</c> 认这个目标。</summary>
/// <remarks>
/// 本体把「谁是合法目标」也写死在阵营上（AnyEnemy 要求 Side == Enemy）。
/// 不放行的话鼠标悬停上去会立刻 return，HoveredNode 保持 null，松手只会取消 —— 表现就是「点不到人」。
/// </remarks>
[HarmonyPatch(typeof(NTargetManager), "AllowedToTargetCreature")]
internal static class DuelTargetManagerOpponentPatch
{
    /// <summary>当前正在选的目标类型（private 字段，取不到就整体不生效，不影响原版）。</summary>
    private static readonly AccessTools.FieldRef<NTargetManager, TargetType>? ValidTargetsField =
        AccessTools.Field(typeof(NTargetManager), "_validTargetsType") is null
            ? null
            : AccessTools.FieldRefAccess<NTargetManager, TargetType>("_validTargetsType");

    [HarmonyPostfix]
    private static void Postfix(NTargetManager __instance, Creature creature, ref bool __result)
    {
        if (ValidTargetsField is null || creature is null)
        {
            return;
        }

        if (!DuelRules.IsInTwoPlayerDuel(creature.CombatState) || !DuelRules.IsOpponentOfLocal(creature))
        {
            return;
        }

        var targetType = ValidTargetsField(__instance);

        if (targetType == TargetType.AnyEnemy)
        {
            __result = true;
            ModInfo.LogOnce("选中 UI：对手现在可以悬停/点击选中（当作敌人）");
        }
        else if (targetType == TargetType.AnyAlly)
        {
            // 1v1：对面不是队友，「指向队友」的牌在决斗里没有合法目标。
            __result = false;
        }
    }
}
