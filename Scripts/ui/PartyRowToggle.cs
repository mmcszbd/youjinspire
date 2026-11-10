// ============================================================================
// 多人局「自己 + 队友」小卡片：F7 三档显隐
// ----------------------------------------------------------------------------
// 需求：局内按一下就能把左上角那排小卡片收起来 / 整个关掉，再按回来。
//
// 三档（按 F7 循环）：
//   ① 正常     —— 原版样子（头像 + 名字 + 血条 + 能量/牌数）
//   ② 收起头像 —— 只藏 %CharacterIcon，名字/血条/计数都还在（少占一块地方）
//   ③ 整行隐藏 —— 整排 Visible = false（视线全清空）
//
// 原版其实自带一个「整排一键隐藏」：NMultiplayerPlayerStateContainer._Input 里
// 判断 DebugHotkey.hideMpHealthBars，按下就 Visible = !Visible（两档）。那个键是
// 调试热键、只管全显/全隐，没有「收起头像」这一档，所以这里另做一个三档的。
// 两者互不注册同一个 action：原版那个键按下去是它自己改 Visible，我们下次按 F7
// 会按当前档位重新摆一遍（隐蔽 side effect：原版刚隐藏过，按 F7 到「正常」会重新显示）。
//
// 实现要点（都是从 sts2.dll 反编译确认的）：
//   · 每一行是 NMultiplayerPlayerState : Control，头像节点唯一名 %CharacterIcon；
//   · 整排是 NMultiplayerPlayerStateContainer : Control，子节点里就是那些行；
//   · 单人局 Initialize 直接 return（不建行）⇒ 单人局按 F7 无副作用，只弹提示；
//   · 只动 Visible，不碰 Node 大小/位置 ⇒ 不会像缩放容器那样把遗物图标压变形。
//
// 纯本地视觉，不同步、不进存档：谁按谁生效，不影响任何玩法判定。
// ============================================================================

using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace YouJin.Scripts;

/// <summary>小卡片的三档显隐。</summary>
internal enum PartyRowMode
{
    /// <summary>原版样子。</summary>
    Full = 0,

    /// <summary>藏掉头像，名字/血条/计数保留。</summary>
    Compact = 1,

    /// <summary>整排都不显示。</summary>
    Hidden = 2,
}

/// <summary>多人局左上角小队卡片的三档显隐（F7 循环）。</summary>
internal static class PartyRowToggle
{
    private const string HarmonyId = "youjin.partyrow";

    /// <summary>切换键。想换键只改这一处。</summary>
    private const Key ToggleKey = Key.F7;

    /// <summary>见过的容器（进出树时登记/注销），按 F7 时逐个摆状态。</summary>
    private static readonly List<NMultiplayerPlayerStateContainer> Containers = new();

    private static PartyRowMode _mode = PartyRowMode.Full;

    /// <summary>在 mod 初始化时调用一次。</summary>
    public static void Apply()
    {
        try
        {
            var harmony = new Harmony(HarmonyId);
            var patched = 0;

            var enter = AccessTools.Method(typeof(NMultiplayerPlayerStateContainer), "_EnterTree");
            if (enter is not null)
            {
                harmony.Patch(enter, postfix: new HarmonyMethod(GetPostfix(nameof(EnterTreePostfix))));
                patched++;
            }

            var exit = AccessTools.Method(typeof(NMultiplayerPlayerStateContainer), "_ExitTree");
            if (exit is not null)
            {
                harmony.Patch(exit, postfix: new HarmonyMethod(GetPostfix(nameof(ExitTreePostfix))));
                patched++;
            }

            var input = AccessTools.Method(typeof(NMultiplayerPlayerStateContainer), "_Input");
            if (input is not null)
            {
                harmony.Patch(input, postfix: new HarmonyMethod(GetPostfix(nameof(InputPostfix))));
                patched++;
            }

            Entry.Logger.Info($"小队卡片显隐：已挂钩 {patched}/3（热键 {ToggleKey}）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"小队卡片显隐：挂钩失败 {ex}");
        }
    }

    private static System.Reflection.MethodInfo GetPostfix(string name) =>
        AccessTools.Method(typeof(PartyRowToggle), name);

    // ── 补丁体 ──────────────────────────────────────────────────────────

    private static void EnterTreePostfix(NMultiplayerPlayerStateContainer __instance)
    {
        try
        {
            if (!Containers.Contains(__instance))
                Containers.Add(__instance);

            // 新进树的容器直接跟上当前档位（换房间/重进战斗都保持一致）。
            ApplyTo(__instance, _mode);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"小队卡片显隐：登记容器失败 {ex}");
        }
    }

    private static void ExitTreePostfix(NMultiplayerPlayerStateContainer __instance)
    {
        Containers.Remove(__instance);
    }

    private static void InputPostfix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
                return;

            if (key.Keycode != ToggleKey && key.PhysicalKeycode != ToggleKey)
                return;

            Advance();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"小队卡片显隐：切换失败 {ex}");
        }
    }

    // ── 状态 ────────────────────────────────────────────────────────────

    private static void Advance()
    {
        _mode = (PartyRowMode)(((int)_mode + 1) % 3);

        // 容器可能已经释放（回主菜单等），先剔掉再摆。
        Containers.RemoveAll(c => !GodotObject.IsInstanceValid(c));
        foreach (var container in Containers)
            ApplyTo(container, _mode);

        Notify();
    }

    private static void ApplyTo(NMultiplayerPlayerStateContainer container, PartyRowMode mode)
    {
        if (!GodotObject.IsInstanceValid(container))
            return;

        // 整排：隐藏档直接关掉容器；另外两档保证是显示的（原版调试键可能刚关过）。
        container.Visible = mode != PartyRowMode.Hidden;

        foreach (var child in container.GetChildren())
        {
            if (child is not NMultiplayerPlayerState row)
                continue;

            row.Visible = true;

            // 「收起」只藏头像这一块，名字和血条留着 —— 这正是和「整行隐藏」的区别。
            var icon = row.GetNodeOrNull<TextureRect>("%CharacterIcon");
            if (icon is not null)
                icon.Visible = mode == PartyRowMode.Full;
        }
    }

    private static void Notify()
    {
        var text = _mode switch
        {
            PartyRowMode.Full => "小队卡片：正常",
            PartyRowMode.Compact => "小队卡片：收起头像",
            _ => "小队卡片：隐藏",
        };

        Entry.Logger.Info($"[PartyRow] {text}");

        try
        {
            if (NGame.Instance is { } game)
                game.AddChildSafely(NFullscreenTextVfx.Create(text));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"小队卡片显隐：提示条失败（不影响功能）{ex}");
        }
    }
}
