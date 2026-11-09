using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.SplitMode;

/// <summary>把「我在哪、在干什么」定期发给对端（分离期的唯一业务流量）。</summary>
/// <remarks>
/// <para>
/// 采样点选在每帧的泵里，而不是去挂钩子：分离期本机跑的是单机局，读自己的状态最省事。
/// 节流到 250ms 一次，并且每次都发（哪怕坐标没变）——Presence 是可丢通道，对面可能刚起来或刚丢过包。
/// </para>
/// <para>心跳不用我们操心：本体的 <c>NetQualityTracker</c> 每 200ms 自己发一次（见 <see cref="SplitLink" />）。</para>
/// <para>
/// <b>三幕打完之后本机这一局会被收掉</b>（拿不到 <c>RunState</c>），但链接还活着。
/// 这时候要用最后缓存的坐标继续发 <c>Activity = 3</c>，否则对面永远等不到「两个人都打完了」。
/// </para>
/// </remarks>
internal static class PresencePublisher
{
    private const double IntervalSeconds = 0.25;

    private static double _sinceSend;
    private static int _sequence;
    private static int _lastLoggedFailureCount;

    private static int _lastActIndex;
    private static int _lastColumn = -1;
    private static int _lastRow = -1;
    private static bool _lastHasCoord;
    private static int _lastHpPercent;

    public static void Pump(double delta, INetGameService transport)
    {
        if (!ModuleSwitches.SplitPresence)
        {
            return;
        }

        SplitPresenceChannel.Initialize();
        PresenceLedger.NoteMainThreadPump();

        _sinceSend += delta;

        if (_sinceSend < IntervalSeconds)
        {
            return;
        }

        _sinceSend = 0;

        if (!TryReadLocal(out var payload))
        {
            return;
        }

        PresenceLedger.SetLocal(payload);

        if (SplitPresenceChannel.Send(transport, payload))
        {
            return;
        }

        if (SplitPresenceChannel.SendFailureCount == _lastLoggedFailureCount)
        {
            return;
        }

        _lastLoggedFailureCount = SplitPresenceChannel.SendFailureCount;

        ModInfo.Warn($"[SplitMode] Presence 发不出去（第 {_lastLoggedFailureCount} 次）——连接可能已经断了");
    }

    /// <summary>读本机当前的位置与状态（拿不到当局就返回 false）。</summary>
    private static bool TryReadLocal(out SplitPresencePayload payload)
    {
        payload = default;

        // 三幕打完之后本机这一局已经收掉了：用最后缓存的坐标继续发「已打完」，
        // 否则对端永远收不到 Activity = 3，合流就永远触发不了。
        if (SplitRuntime.FinishedLastAct)
        {
            payload = new SplitPresencePayload(
                _lastActIndex,
                _lastColumn,
                _lastRow,
                _lastHasCoord,
                3,
                _lastHpPercent,
                ++_sequence);

            return true;
        }

        if (RunManager.Instance?.DebugOnlyGetState() is not { } state)
        {
            return false;
        }

        var location = state.MapLocation;
        var coord = location.coord;

        var activity = CombatManager.Instance.IsInProgress
            ? 2
            : state.CurrentRoom is not null ? 1 : 0;

        var hpPercent = HpPercent(LocalContext.GetMe(state));

        _lastActIndex = location.actIndex;
        _lastColumn = coord?.col ?? -1;
        _lastRow = coord?.row ?? -1;
        _lastHasCoord = coord.HasValue;
        _lastHpPercent = hpPercent;

        payload = new SplitPresencePayload(
            location.actIndex,
            _lastColumn,
            _lastRow,
            _lastHasCoord,
            activity,
            hpPercent,
            ++_sequence);

        return true;
    }

    private static int HpPercent(Player? player)
    {
        if (player?.Creature is not { } creature || creature.MaxHp <= 0)
        {
            return 0;
        }

        return (int)Math.Round(100.0 * creature.CurrentHp / creature.MaxHp);
    }
}
