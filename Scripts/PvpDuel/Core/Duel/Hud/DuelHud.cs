using Godot;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace PvpDuel.Core.Duel;

/// <summary>决斗限制小抄：战斗界面右上角一行小字，显示用量 / 上限 / 血量差 / 后手补偿状态。</summary>
/// <remarks>
/// 纯本机表现，不参与任何判定（所以两端可以不一样）。开关在设置页「模块 → 决斗限制小抄」。
/// 位置想挪就改 <see cref="TopMargin" /> / <see cref="RightMargin" /> 两个常量。
/// </remarks>
internal static class DuelHud
{
    /// <summary>距顶栏下方的距离（屏幕坐标 y）。</summary>
    private const float TopMargin = 132f;

    /// <summary>距右边缘的距离。</summary>
    private const float RightMargin = 28f;

    /// <summary>文字区宽度：文字右对齐，所以稍微宽一点，长行会往左伸。</summary>
    private const float PanelWidth = 560f;

    private const double RefreshSeconds = 0.25;

    private static Godot.Timer? _timer;
    private static Label? _label;

    /// <summary>进战斗 / 回合开始时调一次（幂等）。</summary>
    public static void EnsureRunning()
    {
        if (_timer is not null && GodotObject.IsInstanceValid(_timer) && _timer.IsInsideTree())
        {
            Refresh();
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is not { } root)
        {
            return;
        }

        _timer = new Godot.Timer
        {
            Name = "PvpDuelLimitHud",
            WaitTime = RefreshSeconds,
            Autostart = true,
            OneShot = false,
        };

        root.AddChild(_timer);
        _timer.Timeout += Refresh;

        Refresh();
    }

    private static void Refresh()
    {
        if (!DuelSession.InDuel
            || !ModuleSwitches.LimitHud
            || DuelSession.Combat is not { } state
            || NCombatRoom.Instance is not { } room
            || !GodotObject.IsInstanceValid(room))
        {
            Detach();
            return;
        }

        if (_label is null || !GodotObject.IsInstanceValid(_label) || !_label.IsInsideTree())
        {
            _label = new Label
            {
                Name = "PvpDuelLimitHud",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 100,
                ClipText = false,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            _label.AddThemeFontSizeOverride("font_size", 16);
            _label.AddThemeColorOverride("font_color", new Color(0.94f, 0.92f, 0.78f));

            room.AddChildSafely(_label);
        }

        // 贴右边缘：位置每帧（0.25 秒）重算一次，窗口尺寸变了也跟着走。
        _label.Size = new Vector2(PanelWidth, 0f);
        _label.Position = new Vector2(
            Math.Max(8f, room.Size.X - PanelWidth - RightMargin),
            TopMargin);

        _label.Text = BuildText(state);
    }

    private static void Detach()
    {
        if (_label is not null && GodotObject.IsInstanceValid(_label))
        {
            _label.QueueFree();
        }

        _label = null;
    }

    private static string BuildText(ICombatState state)
    {
        var me = LocalContext.GetMe(state);
        var other = DuelRules.OtherPlayer(me, state);

        var round = state.RoundNumber;

        var lines = new List<string>
        {
            // 回合上限已在 0.10.8 删掉：决斗只打到有人倒下，不再按回合数判超时。
            $"回合 {round}",
            $"本回合出牌　我 {Usage(DuelBalance.CardsPlayedThisTurn(me), DuelConfig.MaxCardsPerTurn)}"
            + $"　对手 {Usage(DuelBalance.CardsPlayedThisTurn(other), DuelConfig.MaxCardsPerTurn)}",
            $"同卡最多　{Usage(DuelBalance.MaxSameCardCountThisTurn(me), DuelConfig.MaxSameCardPerTurn)}",
            HpLine(me, other),
            CompensationLine(me),
        };

        return string.Join('\n', lines.Where(line => !string.IsNullOrEmpty(line)));
    }

    private static string Usage(int used, int limit)
    {
        return limit == 0 ? $"{used}（不限）" : $"{used}/{limit}";
    }

    private static string HpLine(Player? me, Player? other)
    {
        if (me is null || other is null)
        {
            return "";
        }

        var mine = Ratio(me.Creature.CurrentHp, me.Creature.MaxHp);
        var theirs = Ratio(other.Creature.CurrentHp, other.Creature.MaxHp);

        return $"血量　我 {mine:P0}　对手 {theirs:P0}（差 {mine - theirs:+0%;-0%;0%}）";
    }

    private static string CompensationLine(Player? me)
    {
        if (me is null || !ModuleSwitches.Balance)
        {
            return "";
        }

        if (DuelBalance.WasCompensated(me))
        {
            return "后手补偿　已发";
        }

        return DuelRules.IsRightSidePlayer(me) ? "后手补偿　本回合发" : "后手补偿　本局我方是先手（无）";
    }

    private static double Ratio(int current, int max)
    {
        return max <= 0 ? 0d : (double)current / max;
    }
}
