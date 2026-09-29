// ============================================================================
// 商店注入：让「被祝福的泉水」固定出现在每一家普通商店里
// ----------------------------------------------------------------------------
// 为什么用补丁：
//   · 商店药水是 MerchantInventory.PopulatePotionEntries() 里
//     PotionFactory.CreateRandomPotionsOutOfCombat(player, 3, rng, null) 随机滚 3 瓶，
//     没有任何"商店专属药水"的标记位（PotionRarity 里也没有 Shop）。
//   · 所以做法是：给这个私有方法加一个后置补丁，等原版滚完之后，
//     把最后那瓶换成我们的泉水 —— **数量还是 3**，UI 槽位不用动。
//   · 补丁加在 PopulatePotionEntries（而不是 CreateForNormalMerchant 的结尾），
//     是因为 CreateForNormalMerchant 在 populate 之后还要给每个 entry 挂
//     PurchaseCompleted → UpdateEntries 的监听；先替换再挂监听，我们的条目才能被正常管理。
//   · 售价：MerchantPotionEntry 是 public 的，MerchantEntry._cost 是 protected，
//     所以子类在 CalcCost() 里直接写 _cost = 150 即可（原版按稀有度定价的逻辑照跑一遍）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;

namespace YouJin.Scripts;

/// <summary>把「被祝福的泉水」塞进商店药水位。</summary>
internal static class BlessedSpringShopPatch
{
    private const string HarmonyId = "youjin.blessedspring";

    /// <summary>在 mod 初始化时调用一次。</summary>
    public static void Apply()
    {
        try
        {
            var target = AccessTools.Method(typeof(MerchantInventory), "PopulatePotionEntries");
            if (target is null)
            {
                Entry.Logger.Info("被祝福的泉水：找不到 MerchantInventory.PopulatePotionEntries，跳过商店注入");
                return;
            }

            var postfix = typeof(BlessedSpringShopPatch).GetMethod(
                nameof(Postfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(postfix));

            // 售价：MerchantPotionEntry 是 sealed（不能继承），所以改成给它的
            // CalcCost 加后置补丁 —— 原版按稀有度算完之后，如果是我们的泉水就写死 150。
            var calcCost = AccessTools.Method(typeof(MerchantPotionEntry), "CalcCost");
            if (calcCost is not null)
            {
                var costPostfix = typeof(BlessedSpringShopPatch).GetMethod(
                    nameof(CostPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                new Harmony(HarmonyId).Patch(calcCost, postfix: new HarmonyMethod(costPostfix));
            }

            Entry.Logger.Info("被祝福的泉水：商店注入 + 定价补丁已应用");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"被祝福的泉水：商店注入补丁应用失败 {ex}");
        }
    }

    /// <summary>
    /// 原版滚完 3 瓶之后，把最后一瓶换成「被祝福的泉水」。
    /// 永远只是"替换"，不增加数量，所以 UI 槽位不用改。
    /// （烟雾弹是普通稀有药水，注册在共享池里，不在这里硬塞。）
    /// </summary>
    private static void Postfix(MerchantInventory __instance)
    {
        try
        {
            if (__instance is null || !YouJinSettings.GameplayEnabled)
                return;

            var player = __instance.Player;
            if (player is null)
                return;

            // 私有字段 _potionEntries（List<MerchantPotionEntry>）
            var entriesField = AccessTools.FieldRefAccess<MerchantInventory, List<MerchantPotionEntry>>("_potionEntries");
            var entries = entriesField(__instance);
            if (entries is null || entries.Count == 0)
            {
                Entry.Logger.Info("youjin 商店药水：这家商店没有药水位，跳过");
                return;
            }

            // 最后一格：被祝福的泉水
            var spring = MakeEntry(typeof(BlessedSpringPotion), player);
            if (spring is not null)
                entries[entries.Count - 1] = spring;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"youjin 商店药水：商店注入失败 {ex}");
        }
    }

    /// <summary>按药水类型造一个商店条目（拿规范实例复制一份该玩家的可变实例）。</summary>
    private static MerchantPotionEntry? MakeEntry(Type potionType, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        var potionId = ModelDb.GetId(potionType);
        var canonical = ModelDb.GetByIdOrNull<PotionModel>(potionId);
        if (canonical is null)
        {
            Entry.Logger.Info($"youjin 商店药水：找不到 {potionType.Name} 的模型，跳过");
            return null;
        }

        return new MerchantPotionEntry(canonical.ToMutable(), player);
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
