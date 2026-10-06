// ============================================================================
// 任务模式（Quest）：界面
// ----------------------------------------------------------------------------
// ★ 面板挂在 **NRun** 节点上（RitsuLib 的 NodeAttachment），不挂在全局根节点。
//   这样：
//     · 新开一局、读档继续 —— 只要运行场景建起来，面板就跟着建出来；
//     · 退回主菜单 / 结束这一局 —— 场景销毁，面板自动跟着消失，
//       不会再出现"退出到主菜单面板还挂在那"的问题。
//
// ★ 刷新策略改成"只补不拆"：
//   拿不到本机玩家 / 任务书时**保留面板**、显示"读取中…"，等下一次事件再刷。
//   （旧版在这里直接 Detach()，读档那条路径上没有后续事件，面板就再也回不来了。）
//
// 面板本身还是老样子：右上角，列出本层 3 个任务 + 进度 + 奖励，可「收起」。
// 联机时只显示**本机玩家**的任务。
// ============================================================================

using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Scaffolding.Godot.NodeAttachments;

namespace YouJin.Scripts.Quest;

/// <summary>任务面板。</summary>
public static class QuestUi
{
    private const int PanelWidth = 380;
    private const string AttachmentId = "quest_panel";

    private static readonly List<IDisposable> Subscriptions = new();
    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static VBoxContainer? _rows;
    private static Label? _title;
    private static Label? _hint;
    private static Button? _toggle;
    private static bool _collapsed;
    private static bool _initialized;
    private static bool _loggedDataPending;
    private static bool _generating;

    /// <summary>在 Entry.Init 里调用一次。</summary>
    public static void Init()
    {
        if (_initialized)
            return;

        _initialized = true;

        // ① 面板随"运行场景"创建/销毁（NRun 是整局游戏的场景根 Control）。
        try
        {
            ModNodeAttachmentRegistry
                .For(Entry.ModId)
                .RegisterReadyChild<NRun, CanvasLayer>(
                    AttachmentId,
                    _ => BuildLayer(),
                    new NodeAttachmentOptions
                    {
                        Name = "YouJinQuestLayer",
                        UniqueNameInOwner = true,
                    });

            Entry.Logger.Info("任务模式：已注册任务面板挂载点（挂在本局运行场景 NRun 上）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：注册面板挂载点失败 {ex}");
        }

        // ② 各种"可能有新数据"的时机都刷一遍（只刷，不拆）。
        try
        {
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<RunStartedEvent>(_ => Refresh("RunStarted"), true));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<RunLoadedEvent>(_ => Refresh("RunLoaded"), true));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<ActEnteredEvent>(_ => Refresh("ActEntered"), true));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<RunEndedEvent>(_ => Refresh("RunEnded"), true));
            QuestService.Changed += () => Refresh("QuestChanged");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：注册界面生命周期失败 {ex}");
        }
    }

    /// <summary>重新读一遍本机玩家的任务书并刷新面板（面板不存在时什么都不做）。</summary>
    public static void Refresh(string reason = "manual")
    {
        try
        {
            if (_layer is null || !GodotObject.IsInstanceValid(_layer)
                || _panel is null || !GodotObject.IsInstanceValid(_panel))
            {
                // 面板还没建（或在主菜单）：不动任何东西，等运行场景自己把面板带出来。
                return;
            }

            var visible = YouJinSettings.QuestModeEnabled && YouJinSettings.QuestPanelEnabled;
            _layer.Visible = visible;
            if (!visible)
                return;

            var player = QuestService.LocalPlayer();
            var book = player is null ? null : QuestService.GetBook(player);

            // 数据还没就绪（读档/开局的时序窗口）：保留面板，显示"读取中…"。
            if (player is null || book is null)
            {
                if (!_loggedDataPending)
                {
                    _loggedDataPending = true;
                    Entry.Logger.Info(
                        $"任务模式：面板刷新({reason})时数据未就绪 —— 保留面板等待下次刷新"
                        + $"（玩家={player is not null}，任务书={book is not null}）");
                }

                ShowPlaceholder();
                return;
            }

            // 有玩家、但这一层还没任务（例如打到一半才切成任务模式）：补生成一次。
            var run = RunManager.Instance?.DebugOnlyGetState();
            if (run is not null
                && (book.ActIndex != run.CurrentActIndex || book.Quests.Count != QuestService.QuestsPerAct))
            {
                if (!_generating)
                {
                    _generating = true;
                    try
                    {
                        QuestService.EnsureGeneratedForAct(run);
                    }
                    finally
                    {
                        _generating = false;
                    }

                    book = QuestService.GetBook(player) ?? book;
                }
            }

            _loggedDataPending = false;

            if (_rows is null || _title is null)
                return;

            _title.Text = QuestText.IsChinese
                ? $"任务模式 · 第 {book.ActIndex + 1} 层（{book.CompletedCount}/{book.Quests.Count}）"
                : $"Quests · Act {book.ActIndex + 1} ({book.CompletedCount}/{book.Quests.Count})";

            foreach (var child in _rows.GetChildren())
                child.QueueFree();

            if (book.Quests.Count == 0)
            {
                _rows.AddChild(PlaceholderRow());
                return;
            }

            foreach (var quest in book.Quests)
                _rows.AddChild(BuildRow(quest));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：刷新界面失败({reason}) {ex}");
        }
    }

    // ------------------------------------------------------------------
    // 建面板
    // ------------------------------------------------------------------

    /// <summary>由挂载点在 NRun 就绪时调用：造出整层 UI（CanvasLayer + 面板）。</summary>
    private static CanvasLayer BuildLayer()
    {
        var layer = new CanvasLayer
        {
            Name = "YouJinQuestLayer",
            Layer = 90,
        };

        var host = new Control
        {
            Name = "QuestHost",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(host);

        _panel = BuildPanel();
        host.AddChild(_panel);
        _layer = layer;

        _collapsed = false;
        _loggedDataPending = false;

        Entry.Logger.Info("任务模式：任务面板已随本局运行场景创建");

        // 下一帧再刷一次，避开 _Ready 这一帧里"场景还在装配"的时序。
        Callable.From(() => Refresh("PanelReady")).CallDeferred();

        return layer;
    }

    private static PanelContainer BuildPanel()
    {
        var panel = new PanelContainer { Name = "QuestPanel" };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        panel.OffsetLeft = -(PanelWidth + 20);
        panel.OffsetRight = -20;
        panel.OffsetTop = 70;
        panel.OffsetBottom = 70;
        panel.GrowVertical = Control.GrowDirection.End;
        panel.GrowHorizontal = Control.GrowDirection.Begin;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        panel.AddChild(margin);

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
        return panel;
    }

    /// <summary>显示"读取中…"占位（不拆面板）。</summary>
    private static void ShowPlaceholder()
    {
        if (_title is null || _rows is null)
            return;

        _title.Text = QuestText.IsChinese ? "任务模式 · 读取中…" : "Quests · loading…";

        foreach (var child in _rows.GetChildren())
            child.QueueFree();

        _rows.AddChild(PlaceholderRow());
    }

    private static Label PlaceholderRow()
    {
        var label = new Label
        {
            Text = QuestText.IsChinese ? "正在读取本层任务…" : "Loading this act's quests…",
        };
        label.AddThemeColorOverride("font_color", new Color(0.7f, 0.72f, 0.78f));
        return label;
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
}
