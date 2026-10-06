// ============================================================================
// 任务模式（Quest）：界面（阶段 1 的"显示"）
// ----------------------------------------------------------------------------
// 做法：往游戏根节点挂一个 CanvasLayer（Layer=90），右上角放一个面板，
// 列出本层 3 个任务 + 进度 + 奖励。挂根节点而不是挂某个具体界面，
// 是为了在地图/战斗/事件里都能看见；挂在游戏根 Window 下还能继承项目的
// 全局主题（字体）。
//
// 右上角有一个「收起/展开」按钮，收起后只剩一行标题。
// 联机时只显示**本机玩家**的任务（别人看不到）。
// ============================================================================

using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using STS2RitsuLib;

namespace YouJin.Scripts.Quest;

/// <summary>任务面板。</summary>
public static class QuestUi
{
    private const int PanelWidth = 380;

    private static readonly List<IDisposable> Subscriptions = new();
    private static CanvasLayer? _canvas;
    private static PanelContainer? _panel;
    private static VBoxContainer? _rows;
    private static Label? _title;
    private static Label? _hint;
    private static Button? _toggle;
    private static bool _collapsed;
    private static bool _initialized;

    /// <summary>在 Entry.Init 里调用一次。</summary>
    public static void Init()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<RunStartedEvent>(_ => Refresh(), true));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<RunLoadedEvent>(_ => Refresh(), true));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<ActEnteredEvent>(_ => Refresh(), true));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<RunEndedEvent>(_ => Detach(), true));
            QuestService.Changed += Refresh;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：注册界面生命周期失败 {ex}");
        }
    }

    /// <summary>重新读一遍本机玩家的任务书并刷新面板。</summary>
    public static void Refresh()
    {
        try
        {
            if (!YouJinSettings.QuestModeEnabled)
            {
                Detach();
                return;
            }

            // 面板被设置项关掉时，同样不显示（但任务本身照常生成、结算）。
            if (!YouJinSettings.QuestPanelEnabled)
            {
                Detach();
                return;
            }

            var player = QuestService.LocalPlayer();
            var book = QuestService.GetBook(player);
            if (player is null || book is null)
            {
                Detach();
                return;
            }

            EnsureAttached();
            if (_rows is null || _title is null)
                return;

            _title.Text = QuestText.IsChinese
                ? $"任务模式 · 第 {book.ActIndex + 1} 层（{book.CompletedCount}/{book.Quests.Count}）"
                : $"Quests · Act {book.ActIndex + 1} ({book.CompletedCount}/{book.Quests.Count})";

            foreach (var child in _rows.GetChildren())
                child.QueueFree();

            foreach (var quest in book.Quests)
                _rows.AddChild(BuildRow(quest));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：刷新界面失败 {ex}");
        }
    }

    /// <summary>把一个任务渲染成一行。</summary>
    private static Control BuildRow(QuestProgress quest)
    {
        var def = QuestPools.Find(quest.DefId);

        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 1);

        var mark = quest.Completed ? "✔" : "·";
        var title = new Label
        {
            Text = $"{mark} {def?.Title ?? quest.DefId}",
        };
        title.AddThemeColorOverride("font_color", quest.Completed
            ? new Color(0.62f, 0.92f, 0.62f)
            : new Color(0.95f, 0.87f, 0.6f));
        row.AddChild(title);

        if (def is not null)
        {
            var desc = new Label
            {
                Text = def.Describe(quest.Target),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            desc.AddThemeColorOverride("font_color", new Color(0.82f, 0.82f, 0.86f));
            row.AddChild(desc);
        }

        var reward = def?.RewardText(quest.RewardAmount) ?? string.Empty;
        var progress = new Label
        {
            Text = QuestText.IsChinese
                ? $"进度 {quest.Progress}/{quest.Target}    奖励：{reward}"
                : $"Progress {quest.Progress}/{quest.Target}    Reward: {reward}",
        };
        progress.AddThemeColorOverride("font_color", quest.Completed
            ? new Color(0.62f, 0.92f, 0.62f)
            : new Color(0.72f, 0.78f, 0.9f));
        row.AddChild(progress);

        var spacer = new Control { CustomMinimumSize = new Vector2(0, 6) };
        row.AddChild(spacer);

        return row;
    }

    /// <summary>第一次需要显示时，把 CanvasLayer + 面板挂到游戏根节点。</summary>
    private static void EnsureAttached()
    {
        if (_canvas is not null && GodotObject.IsInstanceValid(_canvas))
        {
            if (_panel is not null && GodotObject.IsInstanceValid(_panel))
                _panel.Visible = true;
            return;
        }

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree is null)
            return;

        _canvas = new CanvasLayer
        {
            Name = "YouJinQuestLayer",
            Layer = 90,
        };
        tree.Root.AddChild(_canvas);

        var host = new Control
        {
            Name = "QuestHost",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _canvas.AddChild(host);

        _panel = new PanelContainer { Name = "QuestPanel" };
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _panel.OffsetLeft = -(PanelWidth + 20);
        _panel.OffsetRight = -20;
        _panel.OffsetTop = 70;
        _panel.OffsetBottom = 70;
        _panel.GrowVertical = Control.GrowDirection.End;
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        host.AddChild(_panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        _panel.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        margin.AddChild(column);

        var header = new HBoxContainer();
        column.AddChild(header);

        _title = new Label
        {
            Text = QuestText.IsChinese ? "任务模式" : "Quests",
        };
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _title.AddThemeColorOverride("font_color", new Color(0.98f, 0.92f, 0.7f));
        header.AddChild(_title);

        _toggle = new Button
        {
            Text = QuestText.IsChinese ? "收起" : "Hide",
            FocusMode = Control.FocusModeEnum.None,
        };
        _toggle.Pressed += OnTogglePressed;
        header.AddChild(_toggle);

        _rows = new VBoxContainer();
        column.AddChild(_rows);

        _hint = new Label
        {
            Text = QuestText.IsChinese
                ? "完成本层 Boss 后，奖励在结算界面发放。"
                : "Rewards are granted with the Act boss rewards.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _hint.AddThemeColorOverride("font_color", new Color(0.65f, 0.68f, 0.75f));
        column.AddChild(_hint);

        ApplyCollapsed();
    }

    private static void OnTogglePressed()
    {
        _collapsed = !_collapsed;
        ApplyCollapsed();
    }

    private static void ApplyCollapsed()
    {
        if (_rows is not null && GodotObject.IsInstanceValid(_rows))
            _rows.Visible = !_collapsed;
        if (_hint is not null && GodotObject.IsInstanceValid(_hint))
            _hint.Visible = !_collapsed;
        if (_toggle is not null && GodotObject.IsInstanceValid(_toggle))
            _toggle.Text = _collapsed
                ? (QuestText.IsChinese ? "展开" : "Show")
                : (QuestText.IsChinese ? "收起" : "Hide");
    }

    /// <summary>离开这一局 / 关掉任务模式时移除面板。</summary>
    public static void Detach()
    {
        try
        {
            if (_canvas is not null && GodotObject.IsInstanceValid(_canvas))
                _canvas.QueueFree();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：移除界面失败 {ex}");
        }
        finally
        {
            _canvas = null;
            _panel = null;
            _rows = null;
            _title = null;
            _hint = null;
            _toggle = null;
        }
    }
}
