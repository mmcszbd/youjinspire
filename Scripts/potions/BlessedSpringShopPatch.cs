// ============================================================================
// 商店定价：「被祝福的泉水」在商店里的售价固定 150
// ----------------------------------------------------------------------------
// 这瓶泉水现在是**普通稀有药水**：跟着药水池子随机出现（商店 / 奖励都可能抽到），
// 不再固定往每家商店里塞（原来那个 PopulatePotionEntries 后置补丁已经删掉）。
//
// 只保留定价这一条补丁：
//   · 原版 `MerchantPotionEntry.CalcCost()` 按稀有度算价（GetCost(rarity)）；
//   · 我们的后置补丁在它算完之后，如果是这瓶泉水就把 `MerchantEntry._cost`
//     （protected，用 Harmony 的字段引用写）改成 BlessedSpringPotion.Price = 150。
// ============================================================================

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;

namespace YouJin.Scripts;

/// <summary>给「被祝福的泉水」定商店售价。</summary>
internal static class BlessedSpringShopPatch
{
    private const string HarmonyId = "youjin.blessedspring";

    /// <summary>在 mod 初始化时调用一次。</summary>
    public static void Apply()
    {
        try
        {
            // 售价：MerchantPotionEntry 是 sealed（不能继承），所以给它的
            // CalcCost 加后置补丁 —— 原版按稀有度算完之后，如果是我们的泉水就写死 150。
            var calcCost = AccessTools.Method(typeof(MerchantPotionEntry), "CalcCost");
            if (calcCost is null)
            {
                Entry.Logger.Info("被祝福的泉水：找不到 MerchantPotionEntry.CalcCost，跳过定价补丁");
                return;
            }

            var costPostfix = typeof(BlessedSpringShopPatch).GetMethod(
                nameof(CostPostfix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

            new Harmony(HarmonyId).Patch(calcCost, postfix: new HarmonyMethod(costPostfix));

            Entry.Logger.Info($"被祝福的泉水：商店售价固定为 {BlessedSpringPotion.Price} 的定价补丁已应用（不再固定出现在商店）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"被祝福的泉水：定价补丁应用失败 {ex}");
        }
    }

    /// <summary>把泉水的价格改成固定 150（_cost 是 protected，用 Harmony 的字段引用写进去）。</summary>
    private static void CostPostfix(MerchantPotionEntry __instance)
    {
        try
        {
            if (__instance?.Model is not BlessedSpringPotion)
                return;

            AccessTools.FieldRefAccess<MerchantEntry, int>("_cost")(__instance) = BlessedSpringPotion.Price;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"被祝福的泉水：改写价格失败 {ex.GetType().Name}");
        }
    }
}
