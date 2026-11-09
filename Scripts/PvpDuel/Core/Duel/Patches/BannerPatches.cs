using HarmonyLib;

using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace PvpDuel.Core.Duel;

/// <summary>决斗里两个「名不副实」的回合横幅。</summary>
/// <remarks>
/// 轮流是按本体的「额外回合」机制实现的，而 UI 按「这一回合是不是额外回合」选文案 ——
/// NPlayerTurnBanner 看到参与者名单非空就写「额外回合」。另外每个玩家回合之后都会走一遍敌方回合
/// （决斗里敌方侧空着，那一回合只是空跑），于是每回合都闪一次「敌方回合」。两个都只是文案。
/// </remarks>
internal static class DuelBanner
{
    public static bool ShouldOverride()
    {
        return ModuleSwitches.Banners && DuelSession.InDuel;
    }
}

/// <summary>把「额外回合」改回「玩家回合」。</summary>
[HarmonyPatch(typeof(NPlayerTurnBanner), "_Ready")]
internal static class DuelPlayerTurnBannerPatch
{
    private static readonly AccessTools.FieldRef<NPlayerTurnBanner, MegaLabel> LabelField =
        AccessTools.FieldRefAccess<NPlayerTurnBanner, MegaLabel>("_label");

    [HarmonyPostfix]
    private static void Postfix(NPlayerTurnBanner __instance)
    {
        if (!DuelBanner.ShouldOverride())
        {
            return;
        }

        LabelField(__instance)?.SetTextAutoSize(
            new LocString("gameplay_ui", "PLAYER_TURN").GetFormattedText());
    }
}

/// <summary>敌方侧一个单位都没有时，不要弹「敌方回合」横幅。</summary>
[HarmonyPatch(typeof(NEnemyTurnBanner), "Create")]
internal static class DuelEnemyTurnBannerPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref NEnemyTurnBanner? __result)
    {
        if (!DuelBanner.ShouldOverride())
        {
            return true;
        }

        // 返回 null 是安全的：加节点的地方用的是 AddChildSafely，它自带 null 判断。
        __result = null;
        ModInfo.LogOnce("决斗里不再弹「敌方回合」横幅（那一回合没有敌人）");
        return false;
    }
}
