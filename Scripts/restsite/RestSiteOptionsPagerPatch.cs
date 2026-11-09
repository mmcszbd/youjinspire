// ============================================================================
// 友尽尖塔（youjin）：火堆选项过多 → 分页（"…" 按钮）
// ----------------------------------------------------------------------------
// 问题：`scenes/rooms/rest_site_room.tscn` 里写死的是
//     ChoicesScreen/ChoicesContainer   type = HBoxContainer
//     theme_override_constants/separation = 100
//     alignment = 1（居中）
// 而按钮（`rest_site_button.tscn`）是 250 × 163。**HBoxContainer 不会换行**，
// 所以选项一多就一路往两边撑出屏幕：
//     单行总宽 = n × 250 + (n-1) × 100      →  6 个 = 2000px（1920 屏直接出屏）
// `NRestSiteRoom.UpdateRestSiteOptions` 里也**没有任何位置计算**
// （只 Create + AddChild + 连信号 + 设焦点邻居），位置全交给容器 —— 只能从容器下手。
//
// ----------------------------------------------------------------------------
// 做法：**每页 3 个 + 一个 "…"**（4 个槽位 = 1300px，稳在屏内）
//   · 超过 3 个选项时才接管；≤3 个完全保持原版
//   · "…" 点一下翻下一页，翻到底回到第一页（页码存在节点的 meta 上，
//     所以选完一个选项触发重排后**不会跳回第一页**）
//   · 隐藏用 `Visible = false` —— Godot 的容器会**跳过不可见子节点**，
//     所以行宽会自动收缩，不用手算坐标
//
// ★ 为什么不复用 `NRestSiteButton`：
//   `NRestSiteButton.Create(option)` 必须绑一个**真实的 `RestSiteOption`**，
//   而 `_Ready` 里会调 `Reload()` 去读选项的标题/图标 —— Option 为 null 会炸。
//   所以 "…" 槽位用普通 `Button`：尺寸和真按钮一致（246.786 × 162.879），
//   字体放大，点击行为完全自己控。
//   ⚠ 代价：它不是 `NClickableControl`，**不在原版的键盘焦点链里** ——
//     鼠标点得到，纯键盘/手柄可能选不到它（要补的话得手工接 FocusNeighbor）。
//
// ★ 多人局安全性：纯视觉，不碰任何选项数据。分页里显示的都是原版真按钮，
//   所以选中流程（`AfterSelectingOption` → `RestSiteSynchronizer` 广播）完全不变。
// ============================================================================

using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace YouJin.Scripts;

/// <summary>火堆选项超过 3 个时，改成 3 个一页 + "…" 翻页。</summary>
[HarmonyPatch(typeof(NRestSiteRoom), "UpdateRestSiteOptions")]
internal static class RestSiteOptionsPagerPatch
{
    /// <summary>每页显示几个。</summary>
    private const int PageSize = 3;

    /// <summary>按钮容器的节点路径（来自 rest_site_room.tscn）。</summary>
    private const string ContainerPath = "ChoicesScreen/ChoicesContainer";

    /// <summary>上一页 / 下一页按钮的节点名（每次重排都要先清掉上一轮的）。</summary>
    private const string PrevName = "YoujinRestSitePrev";
    private const string NextName = "YoujinRestSiteNext";

    /// <summary>页码存在房间节点上的 meta key。</summary>
    private const string PageMeta = "youjin_rest_site_page";

    /// <summary>和原版按钮对齐的尺寸（rest_site_button.tscn 的 custom_minimum_size）。</summary>
    private static readonly Vector2 ButtonSize = new(246.786f, 162.879f);

    /// <summary>翻页按钮窄一些，省出空间给真选项。</summary>
    private static readonly Vector2 NavButtonSize = new(110f, 162.879f);

    private static void Postfix(NRestSiteRoom __instance)
    {
        try
        {
            if (__instance is null || !GodotObject.IsInstanceValid(__instance))
                return;

            if (__instance.GetNodeOrNull<Container>(ContainerPath) is not { } container)
            {
                Entry.Logger.Info($"火堆分页：找不到 {ContainerPath}，保持原版");
                return;
            }

            // 容器的直接子节点就是原版按 Options 顺序加进去的按钮。
            //
            // ★ 为什么还要过滤 IsQueuedForDeletion：
            //   原版清理旧按钮用的是 `QueueFreeSafely`，反编译确认它是
            //   「同步 RemoveChildSafely + 延迟 queue_free」——所以正常情况下
            //   旧节点在 Postfix 时已经被摘掉了。这里再过滤一次纯属防御：
            //   万一哪天原版改成自带补丁里同步 QueueFree 而不 RemoveChild，
            //   旧按钮还挂在容器上，就会被当成新按钮一起数进去（页数全错）。
            var buttons = new List<NRestSiteButton>();
            foreach (var child in container.GetChildren())
            {
                if (child is NRestSiteButton b
                    && GodotObject.IsInstanceValid(b)
                    && !b.IsQueuedForDeletion())
                {
                    buttons.Add(b);
                }
            }

            // 清掉上一轮的翻页按钮
            RemoveNavButtons(container);

            if (buttons.Count <= PageSize)
            {
                // 不需要分页：全部显示，保持原版观感
                foreach (var b in buttons)
                    b.Visible = true;

                __instance.SetMeta(PageMeta, 0);
                return;
            }

            var pages = (buttons.Count + PageSize - 1) / PageSize;
            var page = __instance.HasMeta(PageMeta) ? (int)__instance.GetMeta(PageMeta) : 0;
            page = ((page % pages) + pages) % pages;

            // ★★ 翻页按钮**在这一轮只建一次**；之后翻页只改状态（Disabled/Modulate），
            //    不增不删。第一版是"每次翻页删旧的建新的"，而 QueueFree 是延迟的 →
            //    旧按钮这一帧还在 → 新建的撞名被 Godot 自动改名 → 以后按名字删不掉 →
            //    **按钮越翻越多**（用户实测就是这个）。
            var prev = CreateNavButton(PrevName, "‹");
            var next = CreateNavButton(NextName, "›");

            prev.Pressed += () => Flip(__instance, buttons, pages, prev, next, -1);
            next.Pressed += () => Flip(__instance, buttons, pages, prev, next, +1);

            container.AddChild(prev);
            container.AddChild(next);

            ApplyPage(__instance, buttons, page, pages, prev, next);

            Entry.Logger.Info(
                $"火堆分页：{buttons.Count} 个选项 → {pages} 页，每页 {PageSize} 个"
                + $"（当前第 {page + 1} 页，槽位 {(page * PageSize) + 1}~{Math.Min((page + 1) * PageSize, buttons.Count)}）");
        }
        catch (Exception ex)
        {
            // 出任何问题都退回原版（原版行为本身是"全部显示、可能溢出"）
            Entry.Logger.Info($"火堆分页：处理失败，保持原版 {ex}");
        }
    }

    /// <summary>只把"当前页"的状态应用上去（可见性 + 翻页按钮可用性），**不增删任何节点**。</summary>
    private static void ApplyPage(
        NRestSiteRoom room, List<NRestSiteButton> buttons,
        int page, int pages, Button prev, Button next)
    {
        // 夹到合法范围（选项变少时页码可能越界）
        page = ((page % pages) + pages) % pages;
        room.SetMeta(PageMeta, page);

        var first = page * PageSize;

        for (var i = 0; i < buttons.Count; i++)
        {
            var visible = i >= first && i < first + PageSize;
            buttons[i].Visible = visible;
        }

        // 到头置灰（不隐藏 —— 保持行宽稳定，按钮不会左右跳）
        SetNavState(prev, enabled: page > 0);
        SetNavState(next, enabled: page < pages - 1);
    }

    /// <summary>更新翻页按钮的可用状态。</summary>
    private static void SetNavState(Button btn, bool enabled)
    {
        if (!GodotObject.IsInstanceValid(btn))
            return;

        btn.Disabled = !enabled;
        btn.MouseDefaultCursorShape = enabled
            ? Control.CursorShape.PointingHand
            : Control.CursorShape.Arrow;
        btn.Modulate = enabled
            ? new Color(1f, 0.965f, 0.886f)
            : new Color(1f, 0.965f, 0.886f, 0.35f);
    }

    /// <summary>翻一页（delta = -1 上一页 / +1 下一页）。</summary>
    private static void Flip(
        NRestSiteRoom room, List<NRestSiteButton> buttons, int pages,
        Button prev, Button next, int delta)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(room)
                || !GodotObject.IsInstanceValid(prev)
                || !GodotObject.IsInstanceValid(next))
                return;

            var current = room.HasMeta(PageMeta) ? (int)room.GetMeta(PageMeta) : 0;
            var target = current + delta;

            ApplyPage(room, buttons, target, pages, prev, next);

            var actual = room.HasMeta(PageMeta) ? (int)room.GetMeta(PageMeta) : 0;
            Entry.Logger.Info($"火堆分页：翻到第 {actual + 1} 页 / {pages}");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"火堆分页：翻页失败（忽略）{ex}");
        }
    }

    /// <summary>造一个翻页按钮（只造不加，加入容器由调用方做）。</summary>
    private static Button CreateNavButton(string name, string text)
    {
        var btn = new Button
        {
            Name = name,
            Text = text,
            CustomMinimumSize = NavButtonSize,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Modulate = new Color(1f, 0.965f, 0.886f),
        };

        btn.AddThemeFontSizeOverride("font_size", 64);
        return btn;
    }

    private static void RemoveNavButtons(Container container)
    {
        foreach (var child in container.GetChildren())
        {
            if (child is not Button b)
                continue;

            // ★ 用**前缀**匹配，不用全等：
            //   Godot 里 AddChild 撞名会自动改名（`@YoujinRestSitePrev@2`），
            //   第一版按全等匹配就再也删不掉那些被改名的，于是越攒越多。
            var name = b.Name.ToString();
            if (!name.StartsWith(PrevName, StringComparison.Ordinal)
                && !name.StartsWith(NextName, StringComparison.Ordinal))
                continue;

            // ★ 同步 RemoveChild —— QueueFree 是延迟的，只调它的话
            //   这一帧里它还是子节点，会和新建的撞名。
            container.RemoveChild(b);
            b.QueueFree();
        }
    }
}
