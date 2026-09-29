// ============================================================================
// 药水贴图解析：优先用你自己放进 youjin/images/potions/ 的图，
// 没有就退回占位图（观者 mod 的「瓶装奇迹」）。
//
// 约定文件名（PNG，透明底）：
//   youjin/images/potions/<slug>.png          主图
//   youjin/images/potions/<slug>_outline.png  描边图（可选，没有就用占位描边）
// 目前用到的 slug：
//   blessed_spring  → 被祝福的泉水
//   smoke_bomb      → 烟雾弹
//
// 注意：图片放进去之后要在 Godot 里重新导出一次 PCK 才会进包
// （导出预设是 export_filter="all_resources"，PNG 属于资源，会被自动包含）。
// ============================================================================

using Godot;
using MegaCrit.Sts2.Core.Helpers;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

internal static class PotionArt
{
    /// <summary>占位主图：观者 mod 的瓶装奇迹。</summary>
    private const string FallbackImage = "res://Watcher/images/potions/tres/bottled_miracle.tres";

    /// <summary>占位描边。</summary>
    private const string FallbackOutline = "res://Watcher/images/potions/tres/bottled_miracle_outline.tres";

    /// <summary>按 slug 解析出药水贴图配置（主图/描边各自独立回退）。</summary>
    public static PotionAssetProfile Resolve(string slug)
    {
        var image = $"res://youjin/images/potions/{slug}.png";
        var outline = $"res://youjin/images/potions/{slug}_outline.png";

        return new PotionAssetProfile(
            ImagePath: ResourceLoader.Exists(image) ? image : FallbackImage,
            OutlinePath: ResourceLoader.Exists(outline) ? outline : FallbackOutline);
    }
}

/// <summary>事件贴图解析：优先用 youjin/images/events/、youjin/scenes/events/ 里的素材。</summary>
internal static class EventArt
{
    /// <summary>
    /// 事件立绘 / 背景场景 / 特效场景，各自独立回退到原版某个事件的素材。
    /// 背景和特效必须是 .tscn（要用 Godot 摆场景），立绘是 PNG。
    /// </summary>
    public static EventAssetProfile Resolve(
        string slug,
        string fallbackPortrait,
        string fallbackBackground,
        string fallbackVfx)
    {
        var portrait = $"res://youjin/images/events/{slug}.png";
        var background = $"res://youjin/scenes/events/{slug}_bg.tscn";
        var vfx = $"res://youjin/scenes/events/{slug}_vfx.tscn";

        return new EventAssetProfile(
            InitialPortraitPath: ResourceLoader.Exists(portrait) ? portrait : fallbackPortrait,
            BackgroundScenePath: ResourceLoader.Exists(background) ? background : fallbackBackground,
            VfxScenePath: ResourceLoader.Exists(vfx) ? vfx : fallbackVfx);
    }
}
