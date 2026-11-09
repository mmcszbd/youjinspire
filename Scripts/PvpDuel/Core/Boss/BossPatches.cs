using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;

namespace PvpDuel.Core.Boss;

/// <summary>建筑师挑拨选出来的 boss：进战斗时按 (人数 − 1) 倍给上限，打完整场再还原。</summary>
/// <remarks>
/// <para>
/// 这段原来是挂在 <see cref="Duel.DuelMode" /> 里的，只对「正好两人」的回合制决斗生效
/// （<see cref="Duel.DuelRules.CanRunDuel" /> 要求参战者恰好 2 位）。但挑拨那一场是
/// <b>固定手速混战</b>，3 人以上时 CanRunDuel 为假、DuelMode 整场早退，boss 加不上血。
/// 所以单独拎出来，只认「谁是 boss」，不认决斗参战人数。
/// </para>
/// <para>
/// 去重按 <see cref="Creature" />：事件战斗的玩家是逐个 <c>AddPlayer</c> 进来的，
/// 每次都只看「这一场里还没处理过的」角色。换一场战斗时清空。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.AddPlayer))]
internal static class BossHpPatch
{
    private static readonly HashSet<Creature> Buffed = new(ReferenceEqualityComparer.Instance);

    private static CombatState? _combat;

    [HarmonyPostfix]
    private static void Postfix(CombatState __instance)
    {
        try
        {
            if (!BossState.HasBoss)
            {
                return;
            }

            var factor = BossState.HpMultiplier;

            if (factor <= 1)
            {
                return;
            }

            if (!ReferenceEquals(_combat, __instance))
            {
                _combat = __instance;
                Buffed.Clear();
            }

            var pending = new List<Creature>();

            foreach (var creature in __instance.PlayerCreatures)
            {
                if (creature.Player is not { } owner || !BossState.IsBoss(owner))
                {
                    continue;
                }

                if (Buffed.Add(creature))
                {
                    pending.Add(creature);
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            TaskHelper.RunSafely(ApplyAsync(pending, factor));
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[建筑师挑拨] boss 加血补丁出错（不影响战斗）：{ex.Message}");
        }
    }

    private static async Task ApplyAsync(List<Creature> targets, int factor)
    {
        foreach (var creature in targets)
        {
            if (creature.Player is not { } owner)
            {
                continue;
            }

            try
            {
                var before = creature.MaxHp;
                var boosted = before * factor;

                // 记的是「加 boss 倍率之前」的上限：还原时只退这一步，
                // 不会把决斗自己的「生命 ×N」一起撤掉。
                BossState.RememberOriginal(owner.NetId, before);

                await CreatureCmd.SetMaxHp(creature, boosted);
                await CreatureCmd.SetCurrentHp(creature, boosted);

                ModInfo.Info(
                    $"[建筑师挑拨] boss netId={owner.NetId} 生命上限 ×{factor} → {boosted}（原 {before}）");
            }
            catch (Exception ex)
            {
                ModInfo.Warn($"[建筑师挑拨] 给 boss 加血失败（不影响战斗）：{ex.Message}");
            }
        }
    }
}
