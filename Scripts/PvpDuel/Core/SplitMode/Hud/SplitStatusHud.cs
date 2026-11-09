using Godot;

using MegaCrit.Sts2.Core.Helpers;

namespace PvpDuel.Core.SplitMode;

/// <summary>分离模式的状态行（屏幕左上角一行小字，纯本机、只读观测）。</summary>
/// <remarks>
/// 保活实验期间用来看「连接还在不在」：没有它就只能翻 godot.log。
/// 位置想挪就改下面两个常量；关掉开关只是不显示，不影响链路本身。
/// </remarks>
internal static class SplitStatusHud
{
    private const float LeftMargin = 12f;
    private const float TopMargin = 8f;
    private const double RefreshSeconds = 0.5;

    private static Godot.Timer? _timer;
    private static CanvasLayer? _layer;
    private static Label? _label;
    private static bool _deferredScheduled;

    /// <summary>启动时用：mod 初始化那一刻场景树可能还没就绪，推迟一帧再挂（幂等）。</summary>
    public static void EnsureRunningDeferred()
    {
        if (_deferredScheduled)
        {
            return;
        }

        _deferredScheduled = true;

        try
        {
            Callable.From(EnsureRunning).CallDeferred();
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"分离模式状态行没能挂上（不影响其它功能）：{ex.Message}");
        }
    }

    /// <summary>挂上状态行（幂等；开关关着就直接返回）。</summary>
    public static void EnsureRunning()
    {
        if (_timer is not null && GodotObject.IsInstanceValid(_timer) && _timer.IsInsideTree())
        {
            Refresh();
            return;
        }

        if (!ModuleSwitches.SplitStatusHud)
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is not { } root)
        {
            return;
        }

        _layer = new CanvasLayer { Name = "PvpDuelSplitStatusLayer", Layer = 120 };
        _label = new Label
        {
            Name = "PvpDuelSplitStatus",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(LeftMargin, TopMargin),
        };

        _label.AddThemeFontSizeOverride("font_size", 14);
        _label.AddThemeColorOverride("font_color", new Color(0.75f, 0.9f, 1f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _label.AddThemeConstantOverride("outline_size", 4);

        _layer.AddChild(_label);
        root.AddChildSafely(_layer);

        _timer = new Godot.Timer
        {
            Name = "PvpDuelSplitStatusTimer",
            WaitTime = RefreshSeconds,
            Autostart = true,
            OneShot = false,
        };

        root.AddChildSafely(_timer);
        _timer.Timeout += Refresh;

        Refresh();
    }

    private static void Refresh()
    {
        if (_label is null || !GodotObject.IsInstanceValid(_label))
        {
            return;
        }

        if (!ModuleSwitches.SplitStatusHud)
        {
            _label.Visible = false;
            return;
        }

        _label.Visible = true;

        var presence = ModuleSwitches.SplitPresence
            ? $"{PresenceLedger.Describe()}｜{SplitPresenceChannel.Describe()}"
            : "未开启（分离模式 → 同步对方坐标）";

        _label.Text = $"[分离模式] {SplitLink.Describe()}\n[坐标] {presence}";
    }
}
