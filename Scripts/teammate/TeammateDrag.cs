// ============================================================================
// 友尽尖塔（youjin）：拖动队友 —— 阶段 1（★ 纯本地，完全不联网）
// ----------------------------------------------------------------------------
// 玩法：我自己的回合结束之后、还有队友在出牌的那段时间里，可以按住左键
//       抓起任意玩家的立绘拖着走；松手就把它**甩出去** —— 立绘带惯性飞、
//       撞屏幕边缘反弹，**3 秒后瞬移回原位**。
//
// ★ 阶段 1（本机手感）+ 阶段 2（联网）都做完了：
//   · 拖动中每隔 100ms 广播一次位置 → **对面实时看得到你在拖**；
//   · 松手再广播"甩出" → 对面从他松手的位置起跳。
//   联网部分在 `TeammateDragSync.cs`（走 RitsuLib 的 Sidecar，Unreliable）。
//   顺带：因为远端拖动**看得见**，所以"锁"也变成真的了 ——
//   对面正按着的立绘，本机不许抢（见 IsRemotelyDragged）。
//
// ----------------------------------------------------------------------------
// 设计要点（每一条都是反编译确认过的，改之前先读这里）
// ----------------------------------------------------------------------------
// · 窗口 = `IsPlayerReadyToEndTurn(我) && !AllPlayersReadyToEndTurn()`
//   游戏是**同时回合**（所有人一起出牌、各人分别按结束回合），
//   所以这是"我按完了、队友还在打"那段，通常不短。
//   全员 ready → 窗口立刻关；我**撤销**结束回合 → 也立刻关（两处都强制松手）。
//
// · ★ 改的是**外层** `NCreature.Position`（它是 `Control`，布局层）。
//   千万别碰内层 `NCreature.Visuals`（`Node2D`）——
//   反编译 `NCreature.AnimShake` 确认：原版受击抖动 tween 的就是
//   `get_Visuals().Position`。两层分开，各管各的，零冲突。
//
// · 位置写法和 `SharedVitals.UndoScale` 同一个套路：
//   **每帧先"减掉自己上次的贡献"再写新值**（见 ApplyOffset）。这样原版布局
//   重排（`NCombatRoom.PositionPlayersAndPets`）改过位置也不会被我们盖掉。
//
// · ★ 飞行中的立绘**可以再被按住继续拖**：按住会把它从"飞行列表"里摘出来、
//   原位和偏移原样保留，于是从当前位置接着走，不会跳回原位。
//   所以"飞行"用的是**列表**而不是单个槽位 —— 连着甩好几个，各飞各的。
//   踩过的坑：第一版把"正在飞"写成了提前 `return`，导致飞行期间按下没反应、
//   根本抓不住（用户实测指出）。
//
// · 反弹边界用 `GetViewportRect()`（方案"丙"）。
//   游戏是 `canvas_items` 拉伸 + 基准 1920×1080，所以这是**逻辑坐标**，
//   只有两端宽高比不同时尺寸才不一样。
//   ⚠ 刻意接受的取舍：两端立绘的**槽位本来就不同**
//     （`PositionPlayersAndPets` 用 `LocalContext.IsMe` 把"自己"排在最左），
//     所以**弹的位置两端不一样**。远程联机时对面没有参照物、看不出来。
//
// · 输入全部走**轮询**，不挂自定义 Node 子类：
//     - 起手：`Viewport.GuiGetHoveredControl()` 看鼠标底下是不是某个玩家立绘
//       → 天然不抢 UI 的点击（手牌盖在上面时拿到的是手牌，不是立绘）
//     - 按住状态：`Input.IsMouseButtonPressed`
//     - 每帧：`SceneTree.ProcessFrame` 信号
//   立绘提到最前会盖住手牌/UI，但我们**从不改 MouseFilter**，
//   所以被盖住的 UI 照样点得到 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace YouJin.Scripts;

/// <summary>拖动队友（阶段 1：纯本地）。</summary>
internal static class TeammateDrag
{
    // ── 手感参数（先给一组能用的值，进游戏试完再调）────────────────────────
    /// <summary>甩出初速度 = 鼠标瞬时速度 × 这个系数。</summary>
    /// <summary>空气阻力（每秒衰减率）。越大停得越快。</summary>
    private const float Friction = 2.2f;

    /// <summary>撞边弹性（0~1）。1 = 完全弹回，0 = 贴边停住。</summary>
    private const float Restitution = 0.62f;

    /// <summary>松手后多少秒瞬移回原位。</summary>
    private const double FlySeconds = 3.0;

    /// <summary>松手速度低于这个值就不甩了，直接复位（避免"点一下"也飞）。</summary>
    private const float MinFlingSpeed = 90f;

    /// <summary>鼠标瞬时速度的平滑系数（越大越跟手、越抖）。</summary>
    private const float VelocitySmoothing = 14f;

    /// <summary>立绘离屏幕边缘至少留这么多（逻辑像素）。</summary>
    private const float EdgeMargin = 8f;

    // ── 实时同步（远端拖动可见）相关 ────────────────────────────────────────
    /// <summary>拖动中每隔多少毫秒广播一次位置。10Hz × 25 字节 ≈ 250 B/s，可以忽略。</summary>
    private const ulong DragSendIntervalMs = 100;

    /// <summary>超过这么久没收到远端的拖动位置包，就当对方松手了/掉线了，复位。</summary>
    private const ulong RemoteDragTimeoutMs = 700;

    /// <summary>远端拖动位置的平滑系数（10Hz 的包也能看得顺）。</summary>
    private const float RemoteSmoothing = 18f;

    // ── 相撞受击（纯视觉，不碰任何战斗状态）─────────────────────────────────
    /// <summary>同一个生物两次受击反应之间至少隔多久（毫秒）。</summary>
    private const ulong HitReactCooldownMs = 400;

    /// <summary>碰撞矩形往里收多少像素（免得"擦到边"就算撞上）。</summary>
    private const float HitShrink = 6f;

    // ── 一个立绘的"被我们挪到哪了" ──────────────────────────────────────────

    private sealed class Motion
    {
        public required NCreature Node;

        /// <summary>原位（布局给的位置）。外部改动会被 Rebase 重新基准化。</summary>
        public Vector2 Base;

        /// <summary>当前偏移（相对 <see cref="Base"/>）。</summary>
        public Vector2 Offset;

        /// <summary>飞行速度（父容器局部坐标 / 秒）。</summary>
        public Vector2 Velocity;

        /// <summary>已经飞了多久（秒）。</summary>
        public double Elapsed;

        /// <summary>我们上次写进节点的位置；<see cref="HasWritten"/> 为 false 时无意义。</summary>
        public Vector2 LastWritten;

        public bool HasWritten;
    }

    // ── 状态 ────────────────────────────────────────────────────────────────
    private static bool _installed;
    private static bool _wasPressed;
    private static bool _windowWasOpen;

    /// <summary>正在被拖的那一个（鼠标只有一个，所以是单槽）。</summary>
    private static Motion? _drag;

    /// <summary>正在飞的那些（可以同时好几个）。</summary>
    private static readonly List<Motion> Flyers = new();

    /// <summary>按下瞬间的鼠标位置。</summary>
    private static Vector2 _pressMouse;

    /// <summary>按下瞬间它已有的偏移（飞行中再抓住时要接着走，不能跳回 0）。</summary>
    private static Vector2 _grabOffset;

    private static Vector2 _lastMouse;
    private static Vector2 _smoothedVel;
    private static ulong _lastTickMs;
    private static double _dt;
    private static ulong _lastDragSendMs;

    /// <summary>远端正在拖的立绘（对方按住期间，我们平滑跟随他给的位置）。</summary>
    private static readonly List<RemoteDrag> RemoteDrags = new();

    private sealed class RemoteDrag
    {
        public required Motion Motion;

        /// <summary>正在被谁拖（用来打日志/兜底）。</summary>
        public ulong SenderNetId;

        /// <summary>对方最新的目标偏移，我们指数平滑过去。</summary>
        public Vector2 TargetOffset;

        /// <summary>最后一次收到包的时刻（毫秒）。</summary>
        public ulong LastSeenMs;
    }

    /// <summary>在 Entry.Init 里调用一次。</summary>
    public static void Init()
    {
        if (_installed)
            return;

        _installed = true;

        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                Entry.Logger.Info("拖动队友：拿不到 SceneTree，功能不启用");
                return;
            }

            tree.ProcessFrame += OnFrame;
            Entry.Logger.Info("拖动队友：已挂上每帧回调（阶段 1 · 纯本地，不联网）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"拖动队友：初始化失败（功能不启用）{ex}");
        }
    }

    // ── 每帧主循环 ──────────────────────────────────────────────────────────

    private static void OnFrame()
    {
        try
        {
            Step();
        }
        catch (Exception ex)
        {
            // 出错就整个收手 —— ★ 但必须**先把动过的立绘放回原位**，
            // 否则它们会永远歪在那（`Forget` 只清状态、不碰节点）。
            Entry.Logger.Info($"拖动队友：每帧处理出错，收手并复位 {ex}");
            AbandonAndRestore();
        }
    }

    private static void Step()
    {
        var now = Time.GetTicksMsec();
        var dt = _lastTickMs == 0 ? 0.0 : (now - _lastTickMs) / 1000.0;
        _lastTickMs = now;

        if (dt <= 0 || dt > 0.25)
            dt = 1.0 / 60.0; // 掉帧/卡顿后别用超大 dt 积分

        _dt = dt;

        if (NCombatRoom.Instance is not { } room || !GodotObject.IsInstanceValid(room))
        {
            Forget(); // 战斗房间没了（节点大概率也销毁了），清状态即可、不动节点
            return;
        }

        var mouse = room.GetViewport().GetMousePosition();
        var pressed = Input.IsMouseButtonPressed(MouseButton.Left);
        var focused = room.GetWindow()?.HasFocus() ?? true;

        // 鼠标瞬时速度（平滑过，免得单帧抖动把立绘甩得乱飞）
        var raw = (mouse - _lastMouse) / (float)dt;
        _smoothedVel = _smoothedVel.Lerp(raw, 1f - Mathf.Exp(-VelocitySmoothing * (float)dt));
        _lastMouse = mouse;

        var windowOpen = IsWindowOpen();
        LogWindowTransition(windowOpen);

        var pressEdge = pressed && !_wasPressed;
        _wasPressed = pressed;

        // ⓪ ★ 先把网络线程塞进来的远端事件在主线程应用掉（节点操作只能在主线程）
        DrainPending();

        // ① 起手 ★ 必须放在"正在飞"前面：飞行中的立绘也要能被抓住接着拖
        if (pressEdge && focused && windowOpen && _drag is null
            && FindCreatureUnderMouse(room, mouse) is { } target)
        {
            if (IsRemotelyDragged(target))
            {
                // ★ 这就是"锁"：远端正按着它，本机不许抢。
                //   因为现在远端拖动是**看得见**的（立绘真的在动），所以不需要额外的锁定框。
                Entry.Logger.Info(
                    $"拖动队友：{target.Entity?.LogName} 正被远端拖着，本机不抢");
            }
            else
            {
                StartDrag(target, mouse);
            }
        }

        // ② 正在拖：跟手 + 定期广播位置；松手 / 失焦 / 窗口关闭 → 甩出去
        if (_drag is not null)
        {
            if (!GodotObject.IsInstanceValid(_drag.Node))
            {
                _drag = null;
            }
            else if (!pressed || !focused || !windowOpen)
            {
                ReleaseDrag();
            }
            else
            {
                ApplyOffset(_drag, _grabOffset + (mouse - _pressMouse));

                // 实时同步：固定 10Hz 发，**不管有没有移动都发** ——
                // 这样对面可以用"超时"判断我松手了/掉线了（只发变化的话，
                // 我按住不动对面就会误判超时）。
                var nowMs = Time.GetTicksMsec();
                var cid = CombatIdOf(_drag.Node);
                if (cid != 0 && nowMs - _lastDragSendMs >= DragSendIntervalMs)
                {
                    _lastDragSendMs = nowMs;
                    TeammateDragSync.BroadcastDragMove(cid, _drag.Offset);
                }
            }
        }

        // ③ 远端正在拖的：平滑跟随 + 超时兜底复位
        StepRemoteDrags();

        // ④ 所有正在飞的：积分 + 撞边反弹；各自到点瞬移复位
        StepFlyers(dt);

        // ⑤ 相撞受击：拖动中 / 飞行中 / 远端拖动中，撞到别的生物就让它抖一下
        if (_drag is not null)
            CheckCollisions(_drag);

        foreach (var f in Flyers)
            CheckCollisions(f);

        foreach (var rd in RemoteDrags)
            CheckCollisions(rd.Motion);
    }

    // ── 窗口判定 ────────────────────────────────────────────────────────────

    /// <summary>现在允许拖吗（="我结束了、队友还没结束"那段）。</summary>
    private static bool IsWindowOpen()
    {
        // 「建筑师的挑拨」整场禁用（那场队友节点被搬到敌方一侧镜像过，位置基准完全不同）
        if (ArchitectBetrayalEvent.BetrayalCombatActive)
            return false;

        if (CombatManager.Instance is not { } cm)
            return false;

        var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
        if (players is not { Count: > 1 })
            return false; // 单人局没有队友，功能不生效

        var me = LocalContext.GetMe(players);
        if (me is null)
            return false;

        return cm.IsPlayerReadyToEndTurn(me) && !cm.AllPlayersReadyToEndTurn();
    }

    /// <summary>窗口开关只在状态切换时记一行 —— 一进游戏就能从日志确认判定对不对。</summary>
    private static void LogWindowTransition(bool open)
    {
        if (open == _windowWasOpen)
            return;

        _windowWasOpen = open;
        Entry.Logger.Info(open
            ? "拖动队友：★ 窗口开启（我的回合已结束、还有队友在出牌）—— 现在可以按住立绘拖了"
            : "拖动队友：窗口关闭（新回合开始 / 我撤销了结束回合）");
    }

    // ── 起手 / 松手 ─────────────────────────────────────────────────────────

    /// <summary>鼠标底下是不是某个"能拖的立绘"（玩家 / 队友 / 怪物 / 宠物都行）。</summary>
    internal static NCreature? FindCreatureUnderMouse(NCombatRoom room, Vector2 mouse)
    {
        var hovered = room.GetViewport().GuiGetHoveredControl();

        // ① 首选：hovered 就是这个立绘本身、或它的子孙（Hitbox / 血条 / 立绘）。
        //    这样天然不会抢 UI —— 手牌盖在立绘上面时，hovered 拿到的是手牌。
        if (hovered is not null)
        {
            foreach (var nc in room.CreatureNodes)
            {
                if (!IsDraggable(nc))
                    continue;

                if (ReferenceEquals(nc, hovered) || nc.IsAncestorOf(hovered))
                    return nc;
            }

            return null; // 鼠标底下的确实是别的 UI，别抢
        }

        // ② 兜底：hovered 为 null（立绘的 MouseFilter 可能是 Ignore）→ 退化成矩形命中。
        //    取"最上面"的那个（节点树里靠后的）。
        NCreature? found = null;
        foreach (var nc in room.CreatureNodes)
        {
            if (!IsDraggable(nc))
                continue;

            if (nc.GetGlobalRect().HasPoint(mouse))
                found = nc;
        }

        return found;
    }

    /// <summary>
    /// 这个立绘能不能拖：**所有活着的生物**（玩家、队友、怪物、宠物都行）。
    ///
    /// 早期只允许玩家；放开到怪物是安全的，因为：
    ///   · 怪物用 `Creature.CombatId` 标识（不是 `Player.NetId`），两端一致；
    ///   · 战斗中写 `NCreature.Position` 的只有 `_Ready` / `CreateEnemyNodes` /
    ///     `PositionEnemies` / `PositionPlayersAndPets` / `AddCreature` /
    ///     `AdjustCreatureScaleForAspectRatio` —— **没有任何攻击/受击动画**
    ///     （那些动的是内层 `Visuals`），所以偏移层不会和敌人动画打架。
    /// </summary>
    private static bool IsDraggable(NCreature? nc)
        => nc is not null
           && GodotObject.IsInstanceValid(nc)
           && nc.Entity is { IsAlive: true, CombatId: not null }
           // ★ 背叛战斗里那只不可选的木桩（建筑师立绘）既不参与拖动、也不参与右键 Q弹：
           //   它的判定框已被设成 MouseFilter.Ignore，这里再补一道，
           //   免得走到"矩形兜底命中"那条路上去。
           && !BetrayalDummyTarget.IsDummy(nc.Entity);

    private static void StartDrag(NCreature node, Vector2 mouse)
    {
        // ★ 它正在飞？→ 把那一份状态**摘出来接着用**（原位 / 偏移原样保留），
        //   于是"松手之后再按住"是从当前位置继续，不跳回原位、也不中断手感。
        Motion? motion = null;
        for (var i = 0; i < Flyers.Count; i++)
        {
            if (!ReferenceEquals(Flyers[i].Node, node))
                continue;

            motion = Flyers[i];
            Flyers.RemoveAt(i);
            break;
        }

        if (motion is null)
        {
            motion = new Motion
            {
                Node = node,
                Base = node.Position,
                Offset = Vector2.Zero,
                HasWritten = false,
            };
        }
        else
        {
            Rebase(motion); // 飞行期间原版可能重排过，先对齐基准
        }

        motion.Velocity = Vector2.Zero;
        motion.Elapsed = 0;

        _drag = motion;
        _pressMouse = mouse;
        _grabOffset = motion.Offset;
        _smoothedVel = Vector2.Zero;
        _lastDragSendMs = 0; // 让第一个位置包立刻发出去

        // 提到最前，免得被别的立绘盖住（不改 MouseFilter，UI 照样点得到）
        try
        {
            node.MoveToFront();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"拖动队友：提到最前失败（不影响拖动）{ex}");
        }

        Entry.Logger.Info(
            $"拖动队友：开始拖 {node.Entity?.LogName}（原位 {motion.Base}，当前偏移 {motion.Offset}）");
    }

    private static void ReleaseDrag()
    {
        var motion = _drag;
        _drag = null;

        if (motion is null || !GodotObject.IsInstanceValid(motion.Node))
            return;

        var speed = _smoothedVel.Length();

        if (speed < MinFlingSpeed)
        {
            // 太慢 = 只是点了一下，直接复位
            Entry.Logger.Info($"拖动队友：松手速度 {speed:0} 太小，直接复位");
            RestoreBase(motion);
            return;
        }

        motion.Velocity = _smoothedVel;
        motion.Elapsed = 0;
        Flyers.Add(motion);

        Entry.Logger.Info(
            $"拖动队友：甩出 {motion.Node.Entity?.LogName}"
            + $"（速度 {motion.Velocity.Length():0}，方向 {motion.Velocity.Normalized()}，"
            + $"同时在飞 {Flyers.Count} 个）");

        // 阶段 2：把这一次甩飞广播出去，让队友那边也能看到（Unreliable，丢了不影响）
        // 标识用 CombatId —— 玩家和怪物通用
        var combatId = CombatIdOf(motion.Node);
        if (combatId != 0)
            TeammateDragSync.BroadcastFling(combatId, motion.Offset, motion.Velocity);
    }

    // ── 远端拖动 / 回放（阶段 2）────────────────────────────────────────────

    // ── 远端事件：★ 先入队，回主线程再应用 ──────────────────────────────────
    //
    // ⚠️ 网络回调**不保证在主线程**，而 Godot 的节点只能在主线程碰。
    //   所以 Sidecar 的 Handle 里只把事件塞进队列（加锁），
    //   真正的节点操作全部在 Step()（主线程）里做。
    //   第一版直接在回调里写 `node.Position` —— 那是个真缺陷。

    private readonly struct PendingRemote
    {
        public readonly bool IsRelease;
        public readonly uint TargetCombatId;
        public readonly Vector2 Offset;
        public readonly Vector2 Velocity;
        public readonly ulong SenderNetId;

        public PendingRemote(bool isRelease, uint targetCombatId, Vector2 offset, Vector2 velocity, ulong senderNetId)
        {
            IsRelease = isRelease;
            TargetCombatId = targetCombatId;
            Offset = offset;
            Velocity = velocity;
            SenderNetId = senderNetId;
        }
    }

    private static readonly List<PendingRemote> Pending = new();
    private static readonly object PendingLock = new();

    /// <summary>网络线程调用：远端**正在拖**。</summary>
    public static void QueueRemoteMove(uint targetCombatId, Vector2 offset, ulong senderNetId)
    {
        lock (PendingLock)
            Pending.Add(new PendingRemote(false, targetCombatId, offset, Vector2.Zero, senderNetId));
    }

    /// <summary>网络线程调用：远端**松手甩出**。</summary>
    public static void QueueRemoteRelease(uint targetCombatId, Vector2 offset, Vector2 velocity, ulong senderNetId)
    {
        lock (PendingLock)
            Pending.Add(new PendingRemote(true, targetCombatId, offset, velocity, senderNetId));
    }

    /// <summary>主线程（Step 里）把队列里的事件应用掉。</summary>
    private static void DrainPending()
    {
        PendingRemote[] batch;

        lock (PendingLock)
        {
            if (Pending.Count == 0)
                return;

            batch = Pending.ToArray();
            Pending.Clear();
        }

        foreach (var e in batch)
        {
            if (e.IsRelease)
                ApplyRemoteReleaseCore(e.TargetCombatId, e.Offset, e.Velocity, e.SenderNetId);
            else
                ApplyRemoteMoveCore(e.TargetCombatId, e.Offset, e.SenderNetId);
        }
    }

    /// <summary>
    /// 远端**正在拖**（按住期间每 100ms 一个包）—— 本机平滑跟随他的偏移。
    ///
    /// 规则（设计时定的）：
    ///   · 本机**正在拖同一个** → 忽略（这台机器的鼠标说了算）；
    ///   · 本机正在飞同一个 → 让远端接管（把飞行停掉，改成被动跟随）；
    ///   · 远端拖动**不受本机窗口限制**（纯装饰，本机进没进新回合都照显）。
    /// </summary>
    private static void ApplyRemoteMoveCore(uint targetCombatId, Vector2 offset, ulong senderNetId)
    {
        if (NCombatRoom.Instance is not { } room || !GodotObject.IsInstanceValid(room))
            return;

        if (FindCreatureByCombatId(room, targetCombatId) is not { } node)
            return;

        if (_drag is not null && ReferenceEquals(_drag.Node, node))
            return; // 本机正在拖，不理对方的

        var nowMs = Time.GetTicksMsec();
        var rd = FindRemoteDrag(node);

        if (rd is null)
        {
            // ★ 远端接管：如果本机正把它甩飞着，要把那一份状态**摘出来接着用** ——
            //   关键是保留原来的 `Base`！第一版这里直接 `new Motion { Base = node.Position }`，
            //   而 node.Position 是**半空中的位置** → 远端拖动结束后"复位"就复位到半路，
            //   立绘永远歪在那（用户实测报告的正是这个）。
            Motion? takeOver = null;
            for (var i = Flyers.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Flyers[i].Node, node))
                    continue;

                takeOver = Flyers[i];
                Flyers.RemoveAt(i);
                break;
            }

            if (takeOver is not null)
                Rebase(takeOver);

            rd = new RemoteDrag
            {
                Motion = takeOver ?? new Motion
                {
                    Node = node,
                    Base = node.Position,
                    Offset = Vector2.Zero,
                    HasWritten = false,
                },
                SenderNetId = senderNetId,
            };
            RemoteDrags.Add(rd);

            try
            {
                node.MoveToFront();
            }
            catch
            {
                // 提到最前失败无所谓
            }

            Entry.Logger.Info($"拖动队友：远端开始拖 CombatId={targetCombatId}（来自 {senderNetId}）");
        }

        rd.TargetOffset = offset;
        rd.LastSeenMs = nowMs;
    }

    /// <summary>
    /// 远端**松手甩出** —— 本机从他松手时的位置起跳，用他给的速度飞（之后各算各的）。
    /// </summary>
    private static void ApplyRemoteReleaseCore(
        uint targetCombatId, Vector2 offset, Vector2 velocity, ulong senderNetId)
    {
        if (NCombatRoom.Instance is not { } room || !GodotObject.IsInstanceValid(room))
            return;

        if (FindCreatureByCombatId(room, targetCombatId) is not { } node)
        {
            Entry.Logger.Info($"拖动队友：远端甩飞事件找不到 CombatId={targetCombatId} 的立绘，忽略");
            return;
        }

        if (_drag is not null && ReferenceEquals(_drag.Node, node))
        {
            Entry.Logger.Info($"拖动队友：正在拖 CombatId={targetCombatId}，忽略来自 {senderNetId} 的甩飞事件");
            return;
        }

        // 他刚才在拖 → 把那份状态摘出来，用**当前显示到的偏移**当起点（不跳）
        Motion motion;
        var rd = FindRemoteDrag(node);

        if (rd is not null)
        {
            motion = rd.Motion;
            RemoteDrags.Remove(rd);
        }
        else
        {
            // ★ 本机可能正把它甩飞着 → 先把那一份摘出来，别搞出两个 Motion 抢同一个节点
            Motion? existing = null;
            for (var i = Flyers.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Flyers[i].Node, node))
                    continue;

                existing = Flyers[i];
                Flyers.RemoveAt(i);
                break;
            }

            if (existing is not null)
            {
                Rebase(existing);
                motion = existing;
                ApplyOffset(motion, offset);
            }
            else
            {
                motion = new Motion
                {
                    Node = node,
                    Base = node.Position,
                    Offset = offset,
                    HasWritten = false,
                };
                ApplyOffset(motion, offset);
            }
        }

        motion.Velocity = velocity;
        motion.Elapsed = 0;
        Flyers.Add(motion);

        Entry.Logger.Info(
            $"拖动队友：回放远端甩飞 CombatId={targetCombatId}（来自 {senderNetId}，"
            + $"起点偏移({motion.Offset.X:0},{motion.Offset.Y:0}) "
            + $"速度({velocity.X:0},{velocity.Y:0})）");
    }

    /// <summary>远端拖动：平滑跟随 + 超时兜底。</summary>
    private static void StepRemoteDrags()
    {
        var nowMs = Time.GetTicksMsec();

        for (var i = RemoteDrags.Count - 1; i >= 0; i--)
        {
            var rd = RemoteDrags[i];

            if (!GodotObject.IsInstanceValid(rd.Motion.Node))
            {
                RemoteDrags.RemoveAt(i);
                continue;
            }

            if (nowMs - rd.LastSeenMs > RemoteDragTimeoutMs)
            {
                // 对方松手的包丢了 / 他掉线了 → 复位（纯装饰，绝不能让它一直歪着）
                Entry.Logger.Info(
                    $"拖动队友：远端拖动超时（CombatId={CombatIdOf(rd.Motion.Node)}，"
                    + $"来自 {rd.SenderNetId}），复位");
                RestoreBase(rd.Motion);
                RemoteDrags.RemoveAt(i);
                continue;
            }

            var k = 1f - Mathf.Exp(-RemoteSmoothing * (float)_dt);
            ApplyOffset(rd.Motion, rd.Motion.Offset.Lerp(rd.TargetOffset, k));
        }
    }

    private static RemoteDrag? FindRemoteDrag(NCreature node)
    {
        foreach (var rd in RemoteDrags)
        {
            if (ReferenceEquals(rd.Motion.Node, node))
                return rd;
        }

        return null;
    }

    /// <summary>这个立绘现在是不是被远端按着（= 本机不许抢）。</summary>
    private static bool IsRemotelyDragged(NCreature node) => FindRemoteDrag(node) is not null;

    /// <summary>从节点拿跨端标识（`Creature.CombatId`）。拿不到返回 0。</summary>
    internal static uint CombatIdOf(NCreature node) => node.Entity?.CombatId ?? 0;

    /// <summary>按 `CombatId` 找立绘节点（玩家和怪物通用）。</summary>
    private static NCreature? FindCreatureByCombatId(NCombatRoom room, uint combatId)
    {
        if (combatId == 0)
            return null;

        foreach (var nc in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(nc))
                continue;

            if (nc.Entity?.CombatId == combatId)
                return nc;
        }

        return null;
    }

    // ── 飞行 ────────────────────────────────────────────────────────────────

    private static void StepFlyers(double dt)
    {
        for (var i = Flyers.Count - 1; i >= 0; i--)
        {
            var f = Flyers[i];

            if (!GodotObject.IsInstanceValid(f.Node))
            {
                Flyers.RemoveAt(i);
                continue;
            }

            f.Elapsed += dt;

            if (f.Elapsed >= FlySeconds)
            {
                Entry.Logger.Info($"拖动队友：{FlySeconds:0.#} 秒到，{f.Node.Entity?.LogName} 瞬移回原位");
                RestoreBase(f);
                Flyers.RemoveAt(i);
                continue;
            }

            // 解析积分（和帧率无关）：∫ v0·e^(-kt) dt = v0·(1-e^(-kt))/k
            var decay = Mathf.Exp(-Friction * (float)dt);
            var step = f.Velocity * ((1f - decay) / Friction);
            f.Velocity *= decay;

            Rebase(f); // 原版可能刚重排过，先把基准对齐再算目标位置
            var next = f.Base + f.Offset + step;
            var bounds = LocalBounds(f.Node, out var size);

            if (bounds is { } b)
            {
                if (b.Size.X > size.X)
                {
                    if (next.X < b.Position.X)
                    {
                        next.X = b.Position.X;
                        f.Velocity.X = -f.Velocity.X * Restitution;
                    }
                    else if (next.X > b.End.X - size.X)
                    {
                        next.X = b.End.X - size.X;
                        f.Velocity.X = -f.Velocity.X * Restitution;
                    }
                }

                if (b.Size.Y > size.Y)
                {
                    if (next.Y < b.Position.Y)
                    {
                        next.Y = b.Position.Y;
                        f.Velocity.Y = -f.Velocity.Y * Restitution;
                    }
                    else if (next.Y > b.End.Y - size.Y)
                    {
                        next.Y = b.End.Y - size.Y;
                        f.Velocity.Y = -f.Velocity.Y * Restitution;
                    }
                }
            }

            ApplyOffset(f, next - f.Base, alreadyRebased: true);
        }
    }

    // ── 位置写入 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 如果节点现在的位置**不是**我们上次写进去的，说明原版布局重排过了
    /// （`PositionPlayersAndPets` 改了它）→ 把原位重记一下，保持视觉偏移不变。
    /// </summary>
    private static void Rebase(Motion m)
    {
        if (!m.HasWritten)
            return;

        var current = m.Node.Position;
        if (!current.IsEqualApprox(m.LastWritten))
            m.Base = current - m.Offset;
    }

    /// <summary>写 `原位 + 偏移`。</summary>
    private static void ApplyOffset(Motion m, Vector2 offset, bool alreadyRebased = false)
    {
        if (!alreadyRebased)
            Rebase(m);

        m.Offset = offset;
        m.LastWritten = m.Base + offset;
        m.HasWritten = true;
        m.Node.Position = m.LastWritten;
    }

    /// <summary>把立绘放回原位（之后就不再动它了）。</summary>
    private static void RestoreBase(Motion m)
    {
        if (!GodotObject.IsInstanceValid(m.Node))
            return;

        Rebase(m);
        m.Node.Position = m.Base;
    }

    /// <summary>只清状态，不动任何节点（房间没了时用 —— 那些节点多半也已经销毁了）。</summary>
    private static void Forget()
    {
        _drag = null;
        Flyers.Clear();
        RemoteDrags.Clear();
        _smoothedVel = Vector2.Zero;
    }

    /// <summary>
    /// 出错时的收手：**先把被我们挪过的立绘全部放回原位**，再清状态。
    ///
    /// ⚠️ 不能只用 <see cref="Forget"/> —— 那样节点会永远停在偏移位置。
    /// </summary>
    private static void AbandonAndRestore()
    {
        try
        {
            if (_drag is { } d && GodotObject.IsInstanceValid(d.Node))
                RestoreBase(d);

            foreach (var f in Flyers)
            {
                if (GodotObject.IsInstanceValid(f.Node))
                    RestoreBase(f);
            }

            foreach (var rd in RemoteDrags)
            {
                if (GodotObject.IsInstanceValid(rd.Motion.Node))
                    RestoreBase(rd.Motion);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"拖动队友：收手时复位失败（忽略）{ex}");
        }

        Forget();
    }

    // ── 相撞受击（纯视觉）──────────────────────────────────────────────────
    //
    // ★ 刻意**不造成任何伤害**：拖拽/甩飞整个功能是纯装饰的，
    //   一旦真的扣血就会牵扯 HP 同步、checksum 对账、谁算伤害 —— 那是另一件事。
    //   这里"受击"= 被撞的生物做一次原版受击抖动 + 一下 Q弹，纯表现。
    //
    // ★ 三层叠加互不冲突（这就是为什么能随便加）：
    //     拖动     → 外层 `NCreature.Position`
    //     AnimShake → 内层 `Visuals.Position`（原版受击抖动就是这个）
    //     Q弹       → 内层 `Visuals.Scale`
    //
    // ★ 碰撞用 `Hitbox.GetGlobalRect()` 做矩形相交（`NCreature.Hitbox` 是 public）。
    //   两端的包里没有"碰撞事件"，所以**各端各算**：拖动方按鼠标真实位置算，
    //   另一端按插值出来的位置算 —— 结果可能差一点点，但纯表现，不影响任何状态。

    /// <summary>每个生物上次受击的时刻（`CombatId` → 毫秒），用来限流。</summary>
    private static readonly Dictionary<uint, ulong> LastHitMs = new();

    /// <summary>这个正在移动的立绘撞到谁了。</summary>
    private static void CheckCollisions(Motion motion)
    {
        if (NCombatRoom.Instance is not { } room || !GodotObject.IsInstanceValid(room))
            return;

        if (!GodotObject.IsInstanceValid(motion.Node))
            return;

        if (HitRect(motion.Node) is not { } moving)
            return;

        var moverId = CombatIdOf(motion.Node);
        var nowMs = Time.GetTicksMsec();

        foreach (var other in room.CreatureNodes)
        {
            // 复用同一套过滤：活着、有 CombatId、不是背叛战斗里那只不可选的木桩
            if (!IsDraggable(other))
                continue;

            var otherId = CombatIdOf(other);
            if (otherId == 0 || otherId == moverId)
                continue; // 别撞自己

            if (HitRect(other) is not { } rect)
                continue;

            if (!moving.Intersects(rect))
                continue;

            if (LastHitMs.TryGetValue(otherId, out var last) && nowMs - last < HitReactCooldownMs)
                continue;

            LastHitMs[otherId] = nowMs;
            ReactToHit(other, motion.Node);
        }
    }

    /// <summary>拿一个立绘的碰撞矩形（屏幕坐标）；拿不到返回 null。</summary>
    private static Rect2? HitRect(NCreature node)
    {
        try
        {
            if (node.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                return null;

            var rect = hitbox.GetGlobalRect();
            if (rect.Size.X <= 1f || rect.Size.Y <= 1f)
                return null;

            return rect.Grow(-HitShrink);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>让被撞的那个抖一下 + 弹一下（纯视觉）。</summary>
    private static void ReactToHit(NCreature target, NCreature mover)
    {
        try
        {
            // 原版受击抖动（public，动的是内层 Visuals.Position）
            target.AnimShake();

            // 撞上去"咚"一声（随机音高；两端各播各的）
            YoujinSfx.PlayHit();

            Entry.Logger.Info(
                $"拖动队友：{mover.Entity?.LogName} 撞到了 {target.Entity?.LogName}（纯视觉受击）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"拖动队友：触发受击反应失败（忽略）{ex}");
        }
    }

    // ── 坐标工具 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把"屏幕可活动范围"换算成立绘**父容器**的局部坐标（因为 `Position` 是局部的），
    /// 并内缩 <see cref="EdgeMargin"/>。拿不到就返回 null。
    /// </summary>
    private static Rect2? LocalBounds(Control node, out Vector2 nodeSize)
    {
        nodeSize = node.Size;

        if (node.GetParent() is not CanvasItem parent)
            return null;

        var vp = node.GetViewportRect().Grow(-EdgeMargin);
        if (vp.Size.X <= 0 || vp.Size.Y <= 0)
            return null;

        var inv = parent.GetGlobalTransformWithCanvas().AffineInverse();
        var topLeft = inv * vp.Position;
        var size = inv.BasisXform(vp.Size);

        if (size.X <= 0 || size.Y <= 0)
            return null;

        return new Rect2(topLeft, size);
    }
}
