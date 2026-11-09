// ============================================================================
// 「建筑师的挑拨」：木桩（建筑师立绘）的身份判定
// ----------------------------------------------------------------------------
// 这里只剩一个判定函数，给摆位 / 拖动等表现层用：问「这个生物是不是那只木桩」。
//
// 背景（反编译确认）：单体目标这条路**不查** `Creature.IsHittable` ——
//   `CardModel.IsValidTarget` 只看 TargetType / IsAlive / Side，
//   `NTargetManager.AllowedToTargetCreature` 同理（只看 TargetType 和 Side）。
//   木桩是 `Side == Enemy` 的活着的单位 → 两者都会放行，
//   所以单体卡/药水/光标本来就能落到它身上。
//
// 原来这里有三个 Prefix 专门拦它（卡牌 / 药水 / 光标），但安装清单里从来没有它们
// ⇒「木桩不可选」实际上一次都没生效过。三个类已删除，拦截将并入统一的目标裁决器，
//   判据仍用下面这个 IsDummy。
// ============================================================================

using MegaCrit.Sts2.Core.Entities.Creatures;

namespace YouJin.Scripts;

/// <summary>判定"这个生物是不是背叛战斗里那只不可选的木桩"。</summary>
internal static class BetrayalDummyTarget
{
    internal static bool IsDummy(Creature? creature)
    {
        if (creature is null)
            return false;

        // ★ 玩家一律不拦 —— 队友之间互相指定是这场战斗的核心，别误伤。
        if (creature.Player is not null)
            return false;

        if (creature.IsDead)
            return false;

        // 和 ArchitectBetrayalCombatModel.ShouldAllowHitting 用同一套判定（判据收口在 PvpCombat）。
        return PvpCombat.IsActiveFor(creature);
    }
}
