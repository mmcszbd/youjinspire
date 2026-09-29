// ============================================================================
// 火堆选项图标补丁
// ----------------------------------------------------------------------------
// 为什么需要它：
//   RestSiteOption.IconPath 是按选项 id 拼出来的：
//       ImageHelper.GetImagePath("ui/rest_site/option_" + OptionId.ToLowerInvariant() + ".png")
//   我们两个自定义选项（假寐 / 酣睡）没有对应贴图，于是这个路径指向一个不存在的资源。
//   原版在"悬停选项"和"选中选项"时会拿 option.Icon 去设置角色头顶的气泡（NThoughtBubbleVfx.SetTexture），
//   拿到 null 就抛 NotImplementedException —— 而且这个异常发生在 ChooseOption 打开 OnSelect 之前，
//   会把整个火堆流程打断（表现就是：点了没反应 / 结算后按键全失灵）。
//   （日志证据：NThoughtBubbleVfx.SetTexture → NRestSiteCharacter.RefreshThoughtBubbleVfx →
//     NRestSiteRoom.OnBeforePlayerSelectedRestSiteOption / OnPlayerChangedHoveredRestSiteOption）
//
// 解法：
//   用 Harmony 给 RestSiteOption.get_IconPath 加个前缀：是我们这两个选项就返回一张**确实存在**的原版
//   贴图路径（假寐 = 金币图标，酣睡 = 火堆休息图标），其它选项走原逻辑。
//   这样 Icon / 预加载 / 头顶气泡全都拿到有效贴图，异常消失。
//   （之前想用 AssetCache.SetAsset 把贴图"登记"到我们那个路径下，实测不生效：缓存会在房间切换时被卸载，
//     重新加载时磁盘上并没有那个文件，所以还是会抛。改成直接改写路径最稳。）
// ============================================================================

using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;

namespace YouJin.Scripts;

/// <summary>给「假寐」「酣睡」换上真实存在的原版图标路径。</summary>
internal static class RestSiteIconPatch
{
    private const string HarmonyId = "youjin.restsite.icons";

    /// <summary>在 mod 初始化时调用一次。</summary>
    public static void Apply()
    {
        try
        {
            var harmony = new Harmony(HarmonyId);
            var patched = 0;

            // Icon 是 public 的，界面（含头顶气泡）用的就是它 —— 主要就靠这个。
            var iconGetter = AccessTools.PropertyGetter(typeof(RestSiteOption), "Icon");
            if (iconGetter is not null)
            {
                harmony.Patch(iconGetter, prefix: new HarmonyMethod(GetPrefix(nameof(IconPrefix))));
                patched++;
            }

            // IconPath 是 private 的，顺手也换掉，免得预加载时报"资源不存在"的警告。
            var iconPathGetter = AccessTools.PropertyGetter(typeof(RestSiteOption), "IconPath");
            if (iconPathGetter is not null)
            {
                harmony.Patch(iconPathGetter, prefix: new HarmonyMethod(GetPrefix(nameof(IconPathPrefix))));
                patched++;
            }

            Entry.Logger.Info($"火堆图标补丁：已应用 {patched} 处（假寐=金币图标，酣睡=休息图标）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"火堆图标补丁：应用失败 {ex}");
        }
    }

    private static MethodInfo GetPrefix(string name)
        => typeof(RestSiteIconPatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>把 Icon 换成原版贴图；贴图取不到时放回原逻辑。</summary>
    private static bool IconPrefix(RestSiteOption __instance, ref Texture2D __result)
    {
        var path = OverrideIconPath(__instance?.OptionId);
        if (path is null)
            return true;

        var texture = PreloadManager.Cache.GetTexture2D(path);
        if (texture is null)
            return true;

        __result = texture;
        return false;
    }

    /// <summary>把我们选项的图标路径改写成真实存在的原版路径。</summary>
    private static bool IconPathPrefix(RestSiteOption __instance, ref string __result)
    {
        var path = OverrideIconPath(__instance?.OptionId);
        if (path is null)
            return true;

        __result = path;
        return false;
    }

    /// <summary>
    /// 我们两个选项的图标：优先用 youjin/images/rest_site/ 里的图，没有就退回原版占位图。
    ///   nap.png        → 假寐（占位：原版金币图标）
    ///   deep_sleep.png → 酣睡（占位：原版火堆「休息」图标）
    /// </summary>
    private static string? OverrideIconPath(string? optionId) => optionId switch
    {
        NapRestSiteModel.OptionId => OwnOrFallback(
            "youjin/images/rest_site/nap.png",
            "ui/reward_screen/reward_icon_money.png"),
        DeepSleepRestSiteModel.OptionId => OwnOrFallback(
            "youjin/images/rest_site/deep_sleep.png",
            "ui/rest_site/option_heal.png"),
        _ => null,
    };

    /// <summary>自己的图存在就用它，否则用原版占位图。</summary>
    private static string OwnOrFallback(string ownRelativePath, string fallbackRelativePath)
    {
        var own = ImageHelper.GetImagePath(ownRelativePath);
        return ResourceLoader.Exists(own) ? own : ImageHelper.GetImagePath(fallbackRelativePath);
    }
}
