// ============================================================================
// 卡牌/能力文本的"晚置注入"补丁
// ----------------------------------------------------------------------------
// 为什么需要它：
//   游戏的初始化顺序是
//     OneTimeInitialization.ExecuteVeryEarly  → ModManager.Initialize → 跑各 mod 的 [ModInitializer]
//     OneTimeInitialization.ExecuteEssential  → LocManager.Initialize()
//   也就是说 **mod 初始化时 Localization 的表根本还没加载**，
//   在 Entry.Init 里往 LocManager 的 cards / powers 表 merge 是**空转**（Instance 还是 null）。
//   于是以前改卡牌文案只能改 PCK 里的 json 再重新导出 PCK。
//
// 现在做法：给 LocManager 加**后置补丁**，等它加载完表之后再注入（并且是**覆盖式**），
//   ⇒ 改卡牌/能力文案**只要重新编译 dll，不用重新导出 PCK**。
//
//   挂钩两处：
//     · LocManager.SetLanguage(string)  —— 真正加载表的地方（切语言也会再走一遍）
//     · LocManager.Initialize()         —— 保险（万一以后版本改成在这里加载）
// ============================================================================

using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using YouJin.Scripts.Cards;

namespace YouJin.Scripts;

/// <summary>等 LocManager 加载完本地化表之后，再注入本 mod 的卡牌/能力文本（覆盖式）。</summary>
internal static class LateLocalizationPatches
{
    private const string HarmonyId = "youjin.lateloc";

    /// <summary>在 mod 初始化时调用一次。</summary>
    public static void Apply()
    {
        try
        {
            var harmony = new Harmony(HarmonyId);
            var patched = 0;

            // 真正加载表的地方；切语言也会再走一遍
            var setLanguage = AccessTools.Method(typeof(LocManager), "SetLanguage", new[] { typeof(string) });
            if (setLanguage is not null)
            {
                harmony.Patch(setLanguage, postfix: new HarmonyMethod(GetPostfix(nameof(AfterTablesLoaded))));
                patched++;
            }

            // 保险：万一某天改成在 Initialize 里加载
            var initialize = AccessTools.Method(typeof(LocManager), "Initialize", Type.EmptyTypes);
            if (initialize is not null)
            {
                harmony.Patch(initialize, postfix: new HarmonyMethod(GetPostfix(nameof(AfterInit))));
                patched++;
            }

            Entry.Logger.Info($"[youjin] 卡牌/能力文本晚置补丁：已应用 {patched} 处");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[youjin] 卡牌/能力文本晚置补丁失败 {ex}");
        }
    }

    private static MethodInfo GetPostfix(string name)
        => typeof(LateLocalizationPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void AfterTablesLoaded(string language)
    {
        MpCardLocalization.ApplyOverrides();
        OverlayPotionText();
        OverlayRelicText();
    }

    private static void AfterInit()
    {
        MpCardLocalization.ApplyOverrides();
        OverlayPotionText();
        OverlayRelicText();
    }

    /// <summary>
    /// 遗物（目前只有分离模式的「苹果」）的名字/描述同样走"表加载完之后覆盖式注入"。
    /// 理由和药水一模一样：Entry.Init 跑在 LocManager.Initialize 之前，那时 merge 是空转。
    /// </summary>
    private static void OverlayRelicText()
    {
        try
        {
            AppleRelic.EnsureLocalized(force: true);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[youjin] 遗物文案晚置注入失败 {ex}");
        }
    }

    /// <summary>
    /// 药水的名字/描述也走"表加载完之后覆盖式注入"。
    /// 为什么：Entry.Init 跑得比 LocManager.Initialize 早（见文件头），原来在 Init 里 merge 是空转，
    /// 所以实际生效的一直是 PCK 里 `youjin/localization/*/potions.json` 的旧文案 ——
    /// 改一句话都得重导 PCK。这里改成覆盖式，改文案只要重新 build。
    /// </summary>
    private static void OverlayPotionText()
    {
        try
        {
            SmokeBombPotion.EnsureLocalized(force: true);
            BlessedSpringPotion.EnsureLocalized(force: true);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[youjin] 药水文案晚置注入失败 {ex}");
        }
    }
}
