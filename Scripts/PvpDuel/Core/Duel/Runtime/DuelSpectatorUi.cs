using System;

using Godot;

using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace PvpDuel.Core.Duel;

/// <summary>观战者本机把战斗 UI 藏起来（他没上场，手牌和能量都不该出现）。</summary>
/// <remarks>
/// <para>
/// 战斗 UI 是<b>单人</b>的：<c>NCombatUi.Hand</c> 是 <c>NPlayerHand</c>（单数）、
/// <c>EnergyCounterContainer</c> 也是单数 —— 每个人屏幕上只有自己那一套。
/// 所以「隐藏观战者 UI」就是把观战者自己这台机器上的手牌 / 能量 / 结束回合按钮藏掉。
/// </para>
/// <para>
/// 不能只设一次：<c>NCombatUi.Enable()</c> 会在回合开始时把交互恢复，换场时节点也可能被重建。
/// 所以这里挂一个每帧的校正，只做可见性赋值（很便宜），<c>Disable()</c> 只在状态翻转时调一次。
/// </para>
/// </remarks>
internal static class DuelSpectatorUi
{
    private static bool _hooked;

    /// <summary>当前是否处在「已隐藏」状态（只用于判断状态翻转，别拿去当真相）。</summary>
    private static bool _hidden;

    /// <summary>挂上每帧回调（幂等）。</summary>
    public static void EnsureRunning()
    {
        if (_hooked)
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        tree.ProcessFrame += OnFrame;
        _hooked = true;
    }

    private static void OnFrame()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            ModInfo.LogOnce($"观战者 UI 校正出错（忽略）：{ex.Message}");
        }
    }

    private static void Tick()
    {
        var spectating = false;

        if (DuelSession.InDuel && DuelSession.Combat is { } state)
        {
            spectating = DuelSession.IsSpectator(LocalContext.GetMe(state));
        }

        if (NCombatRoom.Instance?.Ui is not { } ui)
        {
            return;
        }

        // 每帧都校正：Enable() 会在回合开始时把东西恢复回来，换场时节点也可能被重建。
        ui.Hand.Visible = !spectating;
        ui.EnergyCounterContainer.Visible = !spectating;
        ui.EndTurnButton.Visible = !spectating;

        if (spectating == _hidden)
        {
            return;
        }

        _hidden = spectating;

        if (spectating)
        {
            // 官方那一套：禁牌堆、禁结束回合、禁 ping、取消正在拖的牌。
            ui.Disable();
        }

        ModInfo.Info(
            spectating
                ? "观战者 UI：已隐藏（手牌 / 能量 / 结束回合）"
                : "观战者 UI：已恢复");
    }
}
