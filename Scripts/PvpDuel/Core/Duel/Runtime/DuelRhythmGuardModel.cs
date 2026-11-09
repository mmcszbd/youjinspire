// ============================================================================
// 回合制 PVP：后手保护（「律动残余」）—— 主实现，走本体自己的失血钩子
// ----------------------------------------------------------------------------
// 要的效果：**先手玩家的第 1 个回合里，后手玩家不会因为先手而失去生命**。
//
// 这条钩子就是本体遗物「律动残余」（BeatingRemnant）用的那一条：
//   MegaCrit.Sts2.Core.Models.AbstractModel.ModifyHpLostAfterOsty(
//       Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource) → decimal
//   · 文档原文：「Modify the amount of HP that will be lost by the target.
//                 This runs when damage is being dealt, after block is applied.
//                 ... <returns>New amount of HP to be lost.</returns>」
//   · 它在**格挡结算之后**跑，所以「格挡照常被打掉、只有血不掉」天然成立，
//     不需要像拦 set_CurrentHp 那样去事后改值。
//   · 第 4 个参数 dealer 是「造成这次失血的人」，正好用来区分「先手打的」和
//     「后手自己伤自己」—— 后者不该被保护。
//   BeatingRemnant 只是覆写了它，并用「持有者本人」把范围圈小；我们这里换成
//   「DuelSession 认定的后手」，其余完全同源。
//
// 为什么不做成 Harmony 补丁：`HookedSingletonModel` 是 RitsuLib 给「常驻单例模型」
// 的订阅基类（本项目已有 12 个在用），覆写虚钩子比反射 patch 稳，也不会因为改动
// AbstractModel 的全部子类而误伤别的 model。
//
// 兜底：`Patches/RhythmGuardPatches.cs` 里那条 set_CurrentHp 拦截**保留没删**。
//   钩子正常工作时失血量已经是 0，set_CurrentHp 根本不会被调到（不算掉血），
//   两条路不冲突；万一这条钩子在某些时序下没被调到，那条仍能兜住。
// ============================================================================

using System;

using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace PvpDuel.Core.Duel;

/// <summary>
/// 后手保护（「律动残余」）：先手第 1 回合内，先手造成的失血被压成 0。
/// </summary>
[RegisterSingleton]
public sealed class DuelRhythmGuardModel : HookedSingletonModel
{
    public DuelRhythmGuardModel() : base(HookType.Combat)
    {
    }

    /// <summary>
    /// 改动「即将失去的生命」。返回 0 = 这次完全不失血（格挡已经在之前结算掉了）。
    /// </summary>
    public override decimal ModifyHpLostAfterOsty(
        Creature target,
        decimal amount,
        ValueProp props,
        Creature dealer,
        CardModel cardSource)
    {
        try
        {
            if (amount <= 0m)
            {
                return amount;
            }

            if (!ModuleSwitches.Core || !DuelSession.InDuel)
            {
                return amount;
            }

            // 只在「先手第 1 回合」这个窗口里管，窗口由 DuelSession 按行动序算，两端一致。
            if (DuelSession.RhythmGuardedPlayer is not { } guarded)
            {
                return amount;
            }

            if (target is null)
            {
                return amount;
            }

            // 只保护后手本人（宠物 / 召唤物不享受）。
            if (target.Player is not { } victim || !ReferenceEquals(victim, guarded))
            {
                return amount;
            }

            // 自己伤自己（自伤卡、自身 debuff）不算「先手打过来」，放行。
            if (dealer is not null && ReferenceEquals(dealer, target))
            {
                return amount;
            }

            ModInfo.LogOnce(
                $"后手保护（律动残余）：把一次失血压成 0（原本 {amount}，"
                + $"目标 netId={guarded.NetId}，出手者 {(dealer?.Player is { } d ? $"netId={d.NetId}" : "无（状态/环境伤害）")}"
                + "；格挡仍在之前照常结算）");

            return 0m;
        }
        catch (Exception ex)
        {
            // 兜底：判不出来就按原版失血，绝不因为一个保护把战斗弄坏。
            ModInfo.Warn($"后手保护（钩子）判定失败（按原版失血）：{ex.Message}");
            return amount;
        }
    }
}
