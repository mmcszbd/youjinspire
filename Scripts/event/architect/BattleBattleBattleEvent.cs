// ============================================================================
// 友尽尖塔（youjin）：事件「战战战！！！」—— 两个挂载点共用的唯一 PvP 事件
// ----------------------------------------------------------------------------
// 挂载点：
//   ① 决斗幕（PvpDuelAct）—— DuelActCreateRoomPatch 直接把房间换成这个事件；
//   ② 最终胜利房间 —— 原版 TheArchitect 被 EventRoom 构造补丁换成这个事件，
//      再由 VictoryRoomPatch 让我们的事件也算「胜利房间」（否则结算变成失败）。
//
// 玩法（三选项，每轮循环）：
//   · 战斗（上台）：报名这一轮。回合制 → 主机从报名者里抽两位开打，其余人观战；
//                   手速   → 所有报名者一起混战。
//   · 观战：这一轮不打。
//   · 结束：全员选结束 → 收场，进原版建筑师房间拿「真胜利」。
//   每轮打完回到本事件页：参战者血量补满，重新投票。
//
// 反编译踩点（实测确认过的，改动前先读）：
//   · 进战斗走 EnterCombatWithoutExitingEvent，不能覆盖 CanonicalEncounter
//     （那条路上事件还没绑 Owner，EventOption 构造会在 AddDetailsTo 里 NRE）。
//   · IsShared 必须 true：EventModel.EnterCombatWithoutExitingEvent 一开头
//     `if (!IsShared) throw`。
//   · LayoutType 必须是 Default：同一个方法里
//     `if (resumeAfterCombat && LayoutType == Combat) throw`。
//   · 共享事件每个玩家槽位各有一份 EventModel 实例，实例各自 ready，
//     EventCombatSynchronizer 全齐才真的进战斗 → 本机要多份实例逐份 ready。
//   · 同一个事件反复开战不会触发 EventSynchronizer.BeforeExitingRoom()
//     （房间没退出过），_states 残留 → 第二轮 ready 抛「already set to ready」。
//     所以每次开战前必须手工 BeforeExitingRoom() + GenerateInternalCombatStateIfNecessary()。
//   · 本地化不能用基类 LocTable：自定义 Act 推出来的表在 LocManager 里不存在，
//     LocString.GetRawText() 直接 NRE → 事件房黑屏。全部落到 "events" 表并运行时注入。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
using PvpDuel.Core;
using PvpDuel.Core.Act;
using PvpDuel.Core.Boss;
using PvpDuel.Core.Contest;
using PvpDuel.Core.Duel;
using PvpDuel.Core.Entry;
using PvpDuel.Core.Interop;
using YouJin.Scripts.Pvp;

namespace YouJin.Scripts;

/// <summary>「战战战！！！」：决斗幕与最终胜利房间共用的 PvP 事件。</summary>
/// <remarks>
/// 原来这是两个事件 —— 决斗幕的「对决邀请」（擂台赛：上台/观战/结束）和胜利房间的
/// 「建筑师的挑拨」（战斗/不战斗）。它们其实是同一件事的两半：都是「投票 → 打一架 → 再投票」。
/// 现在合并成一个事件，按 <see cref="YouJinSettings.PvpStyleSetting" /> 决定怎么打：
/// 手速模式全员混战、回合制模式抽两人单挑，其余人观战。
/// </remarks>
[RegisterActEvent(typeof(PvpDuelAct))]
public sealed class BattleBattleBattleEvent : ModEventTemplate
{
    /// <summary>联机时事件对所有玩家一致（投票必须是大家一起投）。</summary>
    public override bool IsShared => true;

    /// <summary>默认布局：战斗结束后要回到事件里继续投票。</summary>
    public override EventLayoutType LayoutType => EventLayoutType.Default;

    public override EventAssetProfile AssetProfile
    {
        get
        {
            // 标题在事件页渲染时就已经取走，必须在房间刚建、还没画 UI 时就把表写好。
            EnsureOptionLoc();

            return EventArt.Resolve(
                "1",
                ImageHelper.GetImagePath("events/battleworn_dummy.png"),
                SceneHelper.GetScenePath("events/background_scenes/stone_of_all_time"),
                SceneHelper.GetScenePath("events/vfx/stone_of_all_time_vfx"));
        }
    }

    /// <summary>选项与页面文案统一落到本体的 "events" 表。</summary>
    /// <remarks>
    /// 不能用基类的 <c>LocTable</c>：它是按事件注册的 Act 推出来的，<see cref="PvpDuelAct" />
    /// 是自定义幕，推出来的表在 LocManager 里并不存在，<c>LocManager.Instance.GetTable(...)</c>
    /// 返回 null 之后 <c>LocString.GetRawText()</c> 直接 NRE。而 <c>EventOption.ToString()</c>
    /// 会在 <c>EventSynchronizer.BeginEvent</c> 里被 string.Join 调到 → 事件房进不去 → 黑屏。
    /// </remarks>
    private const string OptionLocTable = "events";

    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        EnsureOptionLoc();

        var run = RunManager.Instance?.DebugOnlyGetState();
        var playerCount = run?.Players.Count ?? 0;

        // 建筑师挑拨的计分只认「三层 boss 打完 → 胜利房间」那一次。
        // 决斗幕（PvpDuelAct）在 Act 1 之前，那会儿遗物和金币都还没攒起来，
        // 按分数选出来的 boss 只能算随机，没有意义。
        if (run is not null && run.Act is not PvpDuelAct)
        {
            BossState.Elect(run);
        }

        // 三选项的门槛：只看「PvP 方式」是否启用（≠ Off）+ 人数。
        // 曾经写成 ModuleSwitches.Contest / ModuleSwitches.Core，结果玩家用手速模式
        // （PvP 方式=Speed、主开关=关）时两个条件都假，事件只剩一个「接受对决」，
        // 点下去又被入口开关挡掉、当场收场 —— 战斗根本开不了
        // （2026-11-04 18:37 日志：176 手速=True、207 主=关、1384 只给 BEGIN、1402 忽略）。
        // 手速模式本来就不需要 Core/Invitation，所以门槛必须是 PvpCombatKind。
        // 例外：决斗幕（PvpDuelAct）下仍听「擂台赛」开关 —— 关掉＝老行为单选项。
        var kind = PvpCombatKit.FromSettings();
        var inDuelAct = run?.Act is PvpDuelAct;
        var contest = kind != PvpCombatKind.Off
                      && playerCount >= 2
                      && (!inDuelAct || ModuleSwitches.Contest);

        if (contest && playerCount >= 2)
        {
            PvpContest.Attach(this);
            return BuildContestOptions();
        }

        // 单人局（或擂台赛开关关着）才落到老入口：一个「接受对决」，两人直接开打。
        // 文案键前缀用 InitialOptionKey 的返回值本身：EventModel.OptionKey 拼的是
        // StringHelper.Slugify(类名)，不是 Id.Entry。
        var beginKey = InitialOptionKey("BEGIN");
        return
        [
            new(
                this,
                BeginDuel,
                new LocString(OptionLocTable, $"{beginKey}.title"),
                new LocString(OptionLocTable, $"{beginKey}.description"),
                beginKey,
                []),
        ];
    }

    /// <summary>三个选项。</summary>
    private IReadOnlyList<EventOption> BuildContestOptions()
    {
        return
        [
            ContestOption("CONTEST_FIGHT", OnChoseFight),
            ContestOption("CONTEST_WATCH", OnChoseWatch),
            ContestOption("CONTEST_END", OnChoseEnd),
        ];
    }

    private EventOption ContestOption(string name, Func<Task> onChosen)
    {
        var key = InitialOptionKey(name);

        return new(
            this,
            onChosen,
            new LocString(OptionLocTable, $"{key}.title"),
            new LocString(OptionLocTable, $"{key}.description"),
            key,
            []);
    }

    private Task OnChoseFight() => SubmitVote(PvpContestChannel.ChoiceFight);

    private Task OnChoseWatch() => SubmitVote(PvpContestChannel.ChoiceWatch);

    private Task OnChoseEnd() => SubmitVote(PvpContestChannel.ChoiceEnd);

    /// <summary>投完票就停在「等待其他人选择」页，等主机广播结果。</summary>
    /// <remarks>
    /// ⚠ 这里<b>不能</b> <c>SetEventFinished</c>：事件页一关，<c>EnterCombatWithoutExitingEvent</c>
    /// 就没法再调（它是 <see cref="EventModel" /> 的实例方法），而抽签结果是稍后才到的。
    /// 也不能给空选项 —— <c>SetEventState</c> 见到空列表会把 <c>_isFinished</c> 置真。
    /// </remarks>
    private Task SubmitVote(byte choice)
    {
        PvpContest.SubmitChoice(choice);

        var waitKey = InitialOptionKey("CONTEST_WAIT");
        SetEventState(
            new LocString(OptionLocTable, $"{Id.Entry}.pages.CONTEST_WAIT.description"),
            [
                new EventOption(
                    this,
                    () => Task.CompletedTask,
                    new LocString(OptionLocTable, $"{waitKey}.title"),
                    new LocString(OptionLocTable, $"{waitKey}.description"),
                    waitKey,
                    []),
            ]);

        return Task.CompletedTask;
    }

    /// <summary>主机结果到手（由 <see cref="PvpContest" /> 的主线程泵调用；一架机器只调一次）。</summary>
    /// <remarks>
    /// <para>
    /// 共享事件里<b>每个玩家槽位各有一份 <see cref="EventModel" /> 实例</b>，而
    /// <c>EnterCombatWithoutExitingEvent</c> 是拿本实例的 <c>Owner</c> 去填<b>自己那个槽位</b>的 ready 表。
    /// </para>
    /// <para>
    /// 所以这里只做「准备」：<see cref="DuelSession.SetParticipants" /> 和 <c>plan.Apply()</c> 全局只能做一次，
    /// 重复做会把参战者重复登记、把战斗形态反复 Apply。真正的 ready 交给
    /// <see cref="ReadyPendingCombat" />，对本机<b>每一份</b>实例各做一次。
    /// </para>
    /// </remarks>
    /// <param name="netIdA">参战者 A；手速形态下是哨兵 <c>0</c>（全员混战）。</param>
    /// <param name="netIdB">参战者 B。</param>
    /// <param name="cancel">全员选了结束。</param>
    /// <returns>要打的计划；<c>null</c> = 这一轮不打（已收场 / 条件不满足）。</returns>
    internal PvpCombatPlan? PrepareDraw(ulong netIdA, ulong netIdB, bool cancel)
    {
        try
        {
            return PrepareDrawCore(netIdA, netIdB, cancel);
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[战战战] 开战流程出错（就地收场）：{ex}");
            DuelSession.SetParticipants(null);
            return null;
        }
    }

    /// <summary>让这一份实例把自己的槽位标记成「准备好进战斗」。</summary>
    /// <remarks>
    /// 必须逐份调：3 人局主机端有 3 份实例，只有每份都用各自的 <c>Owner</c> 走一遍
    /// <c>EventCombatSynchronizer.ReadyToEnterCombat</c>，<c>_states</c> 才会 3/3 齐、
    /// <c>EnterCombat</c> 才会触发。
    /// </remarks>
    internal void ReadyPendingCombat(PvpCombatPlan plan)
    {
        try
        {
            // ⚠ 不调 SetEventFinished：事件要留着，打完 Resume 回来继续投票。
            EnterCombatWithoutExitingEvent(plan.Encounter!, [], plan.ResumeAfterCombat);
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[战战战] 这一份实例进战斗失败（槽位会缺 ready）：{ex}");
        }
    }

    /// <summary>就地收场（每份实例各调一次）。</summary>
    internal void FinishContest()
    {
        try
        {
            SetEventFinished(OwnPage("CONTEST_END"));
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[战战战] 收场失败：{ex.Message}");
        }
    }

    /// <summary>把这一份实例的事件页收掉，准备进建筑师房间（每份实例各调一次）。</summary>
    /// <remarks>
    /// 换房间只由第一份实例做（多份都做会把建筑师事件重新推一遍），但「事件已结束」必须
    /// 每份各自标记 —— 否则同步器会警告
    /// 「Beginning new event EVENT.THE_ARCHITECT, but event … is not yet finished!」。
    /// </remarks>
    internal void MarkContestFinished()
    {
        try
        {
            SetEventFinished(OwnPage("FINALE"));
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[战战战] 标记收场失败：{ex.Message}");
        }
    }

    /// <summary>本轮作废、重开投票：把这一份实例重新摆回「三选项」页（每份实例各调一次）。</summary>
    /// <remarks>
    /// 玩家点完选项后页面会被切到 <c>CONTEST_WAIT</c>（「等待其他人选择」），
    /// 那里没有可点的按钮。所以重开一轮时必须把页面摆回初始选项，
    /// 否则大家会停在等待页，票永远收不齐。
    /// </remarks>
    internal void ReopenOptions()
    {
        try
        {
            SetEventState(InitialDescription, GenerateInitialOptionsWrapper());
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[战战战] 重开投票页失败：{ex.Message}");
        }
    }

    private PvpCombatPlan? PrepareDrawCore(ulong netIdA, ulong netIdB, bool cancel)
    {
        if (cancel)
        {
            ModInfo.Info("[战战战] 收场");
            return null;
        }

        var state = RunManager.Instance?.DebugOnlyGetState();
        var players = state?.Players;

        if (state is null || players is null || players.Count < 2)
        {
            ModInfo.Warn("[战战战] 拿不到本局玩家，收场");
            return null;
        }

        // 手速形态：主机广播的是哨兵 0，意思是「所有报名的人都上场」。
        var speed = PvpCombatKit.FromSettings() == PvpCombatKind.Speed;

        if (speed)
        {
            // 手速模式本来就是混战，不登记参战者（SetParticipants 只接受两人）。
            DuelSession.SetParticipants(null);

            var speedPlan = PvpCombatKit.Plan(
                PvpCombatKind.Speed,
                state,
                ModelDb.Encounter<BetrayalEggEncounter>(),
                resumeAfterCombat: true,
                allowFallback: false);

            if (!speedPlan.Ready)
            {
                ModInfo.Warn($"[战战战] 手速形态不可用（{speedPlan.Note}），收场");
                return null;
            }

            speedPlan.Apply();

            ModInfo.Info(
                $"[战战战] 第 {PvpContest.Round} 轮开打：手速形态，{players.Count} 人混战"
                + $"（本机 {PvpContest.InstanceCount} 份实例逐份 ready）");

            return speedPlan;
        }

        Player? first = null;
        Player? second = null;

        foreach (var player in players)
        {
            if (player.NetId == netIdA)
            {
                first = player;
            }
            else if (player.NetId == netIdB)
            {
                second = player;
            }
        }

        if (first is null || second is null)
        {
            ModInfo.Warn($"[战战战] 结果里的 netId 不在本局（{netIdA} / {netIdB}），收场");
            return null;
        }

        // 这两位才是「参战者」；其余人留在场上但冻结（不轮转、不能出牌、不会被选成目标）。
        DuelSession.SetParticipants([first, second]);

        var plan = PvpCombatKit.Plan(
            PvpCombatKind.TurnBased,
            state,
            ModelDb.Encounter<CorpseSlugsWeak>(),
            resumeAfterCombat: true,
            allowFallback: false);

        if (!plan.Ready)
        {
            DuelSession.SetParticipants(null);
            ModInfo.Warn($"[战战战] 回合制形态不可用（{plan.Note}），收场");
            return null;
        }

        plan.Apply();

        var localNetId = RunManager.Instance?.NetService?.NetId ?? 0UL;
        var iAmFighting = localNetId != 0UL && (first.NetId == localNetId || second.NetId == localNetId);

        ModInfo.Info(
            $"[战战战] 第 {PvpContest.Round} 轮开打：netId={first.NetId} vs netId={second.NetId}"
            + $"（本机是 {(iAmFighting ? "参战者" : "观战者")}），本机 {PvpContest.InstanceCount} 份实例逐份 ready");

        return plan;
    }

    // ── 收场：进原版建筑师房间拿真胜利 ──────────────────────────────────────

    /// <summary>已经进过建筑师房间的那一局（多份实例 / 多次调用只进一次）。</summary>
    private static readonly HashSet<object> FinaleEntered = new();

    /// <summary>收场：进原版建筑师房间，走「真胜利」结局。</summary>
    /// <remarks>
    /// <para>
    /// 原来这里调的是 <c>RunManager.WinRun()</c>（直接结算），但胜利房间那个挂载点本来就该
    /// 以建筑师收尾 —— 而原版「建筑师的挑拨」也正是靠进这个房间拿真胜利。
    /// 两个挂载点合并后统一走这条路。
    /// </para>
    /// <para>
    /// 幂等键是「当前房间（或 run）」：本机可能有多份实例，只有一份能真正把房间换掉。
    /// </para>
    /// </remarks>
    internal async Task EnterFinaleAsync()
    {
        try
        {
            var me = Owner;
            var run = me?.RunState;

            if (me is null || run is null)
            {
                return;
            }

            // 幂等键用「这一局」而不是「当前房间」：换房间之后 CurrentRoom 已经是建筑师房，
            // 拿房间当键的话下一份实例会认为「没进过」，把建筑师事件再推一遍
            // （2026-11-04 19:12 日志：THE_ARCHITECT 被开 3 次、VisualOnly 战斗房建了 2 次）。
            var roomKey = (object)run;

            if (!FinaleEntered.Add(roomKey))
            {
                return;
            }

            ModInfo.Info("[战战战] 收场 → 进入建筑师房间（真胜利）");

            // 兜底还原一次再清状态：正常路径上最后一场的 Resume 已经把加成退掉了，
            // 但如果那一场被打断（弃局、断线重连），这里还有一次机会，
            // 否则那位的高上限会跟着他一路进到新的一局。
            await BossState.RestoreAsync(run);
            BossState.Reset();

            // 让建筑师的最后一句台词按这一次的结局走。
            ArchitectBetrayalEvent.ChosenEnding = ArchitectBetrayalEvent.Ending.WatchedShow;

            if (RunManager.Instance is not { } manager)
            {
                return;
            }

            // 这一次要的是原版建筑师房间本身，所以让构造函数补丁别把它换掉。
            ArchitectRoomReplacementPatch.AllowVanillaArchitect = true;

            // 收场页由调用方（PvpContest.DispatchDraw 的 cancel 分支）在换房间之前逐份标记好了。
            // 这里再标一次，同一份实例会抛 "Tried to set event options after event was finished!"。
            await manager.EnterRoom(new EventRoom(ModelDb.Event<TheArchitect>()));
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[战战战] 进建筑师房间失败：{ex}");
        }
        finally
        {
            ArchitectRoomReplacementPatch.AllowVanillaArchitect = false;
        }
    }

    /// <summary>一轮打完回到事件页：补满血、进入下一轮投票。</summary>
    public override async Task Resume(AbstractRoom exitedRoom)
    {
        // 第一句必须 await：EventSynchronizer.ResumeEvents 正在 foreach _events，
        // 同步改动集合会抛 "Collection was modified"（表现：黑屏）。
        await Task.Yield();

        if (!PvpContest.Active)
        {
            return;
        }

        // 手速形态的战斗布场开关只对「刚打完的那一场」生效：留着会让下一轮的战斗
        // 误判成决斗混战（布木桩、队友当目标、收场判据都按手速走）。
        ArchitectBetrayalEvent.BetrayalCombatActive = false;

        // 每轮打完把所有玩家血补满：车轮战不该带着上一轮的残血进下一轮；
        // 而且刚倒下那位在事件页上还挂着 0 血，看着就像结算坏了。
        // 本方法每份实例都会跑一次，但补满是幂等的（满了就跳过）。
        // 顺序要紧：先把 boss 的上限倍数退掉，再按上限补满，否则补出来的是 boss 血量的满血。
        await BossState.RestoreAsync(RunManager.Instance?.DebugOnlyGetState());
        RestoreAllHp();

        // 本机可能有多份实例（3 人局主机端 3 份），每份都会 Resume 一次——轮次只能推一次。
        // 用「本轮已推进」标记而不是 Events[0]：房间重建后 Events[0] 可能是上一轮的对象，
        // 那样第二轮就没人推进，票全被「已经投过了」吞掉。
        PvpContest.OnInstanceResumed();

        // 重新出选项。用 GenerateInitialOptionsWrapper 而不是自己拼：它带 ReplaceNullOptions 等包装。
        SetEventState(InitialDescription, GenerateInitialOptionsWrapper());
    }

    /// <summary>把所有人的血量补满（每轮结束回到事件页时调用）。</summary>
    /// <remarks>
    /// 用 <c>Creature.SetCurrentHpInternal(MaxHp)</c>：本体内部就是
    /// <c>CurrentHp = (int)Math.Min(amount, MaxHp)</c>，不用先动上限
    /// （上限已经被决斗的「生命 ×N」改过了，这里只补当前值）。
    /// </remarks>
    private static void RestoreAllHp()
    {
        try
        {
            if (RunManager.Instance.DebugOnlyGetState() is not { } state)
            {
                return;
            }

            foreach (var player in state.Players)
            {
                var creature = player.Creature;
                var before = creature.CurrentHp;

                if (before == creature.MaxHp)
                {
                    continue;
                }

                creature.SetCurrentHpInternal(creature.MaxHp);

                ModInfo.Info(
                    $"[战战战] {player.NetId} 血量回满 {before} → {creature.CurrentHp}/{creature.MaxHp}");
            }
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[战战战] 回满血量失败（不影响本轮结束）：{ex}");
        }
    }

    /// <summary>把选项文案以 slug 前缀补进 "events" 表（PCK 里只有 Id.Entry 前缀那份）。</summary>
    private void EnsureOptionLoc()
    {
        try
        {
            var loc = LocManager.Instance;
            var table = loc?.GetTable(OptionLocTable);
            if (loc is null || table is null)
            {
                return;
            }

            var chinese = (loc.Language ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var beginKey = InitialOptionKey("BEGIN");
            var fightKey = InitialOptionKey("CONTEST_FIGHT");
            var watchKey = InitialOptionKey("CONTEST_WATCH");
            var endKey = InitialOptionKey("CONTEST_END");
            var waitKey = InitialOptionKey("CONTEST_WAIT");

            var title = chinese ? "战战战！！！" : "FIGHT FIGHT FIGHT!!!";

            var entries = new Dictionary<string, string>
            {
                [$"{beginKey}.title"] = chinese ? "接受对决" : "Accept the duel",
                [$"{beginKey}.description"] = chinese
                    ? "进入决斗：没有怪物，两人轮流行动，谁先倒下谁输。"
                    : "Enter the duel: no monsters, you take turns, whoever falls first loses.",
                [$"{fightKey}.title"] = chinese ? "战斗" : "Fight",
                [$"{fightKey}.description"] = chinese
                    ? "报名这一轮。回合制：主机从报名的人里抽两位单挑；手速：所有报名的人一起混战。"
                    : "Sign up for this round. Turn-based: the host draws two of you. Speed: everyone who signed up brawls.",
                [$"{watchKey}.title"] = chinese ? "观战" : "Watch",
                [$"{watchKey}.description"] = chinese
                    ? "这一轮不打，留在场边看。"
                    : "Sit this round out and watch from the sidelines.",
                [$"{endKey}.title"] = chinese ? "结束" : "End",
                [$"{endKey}.description"] = chinese
                    ? "同意收场。所有人都选结束就散场，一起进建筑师房间。"
                    : "Agree to close it. When everyone picks End, you all go to the Architect.",
                [$"{waitKey}.title"] = chinese ? "等待其他人选择…" : "Waiting for the others…",
                [$"{waitKey}.description"] = chinese
                    ? "你的选择已经提交，等主机公布这一轮。"
                    : "Your choice is in. Waiting for the host.",
                [$"{Id.Entry}.pages.CONTEST_WAIT.description"] = chinese
                    ? "场下有人还在决定。等所有人都选完，主机就来安排这一轮。"
                    : "Someone is still deciding. Once everyone has chosen, the host will set up this round.",
                [$"{Id.Entry}.pages.CONTEST_END.description"] = chinese
                    ? "擂台散了。"
                    : "The contest is over.",
                [$"{Id.Entry}.pages.INITIAL.description"] = chinese
                    ? "周围的观众都在狂欢，快上擂台吧"
                    : "The crowd is roaring. Get in the ring.",
                [$"{Id.Entry}.pages.FINALE.description"] = chinese
                    ? "建筑师鼓了鼓掌，站起身来。"
                    : "The Architect claps, slowly, and stands.",
            };

            // 事件标题：RitsuLib 注册后 Id.Entry 已经带 YOUJIN_EVENT_ 前缀，真正的键是
            // $"{Id.Entry}.title"；另外几种是历史前缀的兜底（写错了也无害）。
            foreach (var key in new[]
                     {
                         $"{Id.Entry}.title",
                         $"YOUJIN_EVENT_{Id.Entry}.title",
                         $"PVP_DUEL_EVENT_{Id.Entry}.title",
                         $"{Id}.title",
                     })
            {
                entries[key] = title;
            }

            table.MergeWith(entries);
        }
        catch (Exception ex)
        {
            ModInfo.Info($"战战战：选项文案注入失败 {ex}");
        }
    }

    /// <summary>页面描述也走 "events" 表（绕开基类可能不存在的 LocTable）。</summary>
    private LocString OwnPage(string pageName) => new(OptionLocTable, $"{Id.Entry}.pages.{pageName}.description");

    private Task BeginDuel()
    {
        // 事件挂进事件池之后任何一局都可能抽到，所以不满足「PvP 开着 + 正好两人」就地收掉。
        // 判定用 PvP 方式而不是入口模块：手速模式的玩家本来就没开入口模块。
        var kind = PvpCombatKit.FromSettings();
        if (kind == PvpCombatKind.Off || !DuelConfig.IsTwoPlayerRun)
        {
            ModInfo.Warn($"战战战事件被触发，但本机不满足「PvP 方式不是关闭 + 正好两人」（PvP 方式={kind}），忽略");
            SetEventFinished(OwnPage("BEGIN"));
            return Task.CompletedTask;
        }

        // 决斗是各自为战：先把可能存在的共生体关系解除，共享卡组会按奇偶分给两边。
        if (ModuleSwitches.TogetherUnbind)
        {
            TogetherInterop.Unbind("battle_battle_battle");
        }

        // 开战走统一门面（PvpCombatKit）：这里**强制**用回合制形态，
        // 并且不允许降级成手速 —— 这个分支本身就是「决斗」。
        var plan = PvpCombatKit.Plan(
            PvpCombatKind.TurnBased,
            RunManager.Instance?.DebugOnlyGetState(),
            ModelDb.Encounter<CorpseSlugsWeak>(),
            resumeAfterCombat: false,
            allowFallback: false);

        if (!plan.Ready)
        {
            ModInfo.Warn($"战战战：回合制形态不可用（{plan.Note}），就地收掉事件页");
            SetEventFinished(OwnPage("BEGIN"));
            return Task.CompletedTask;
        }

        plan.Apply();

        // 占位遭遇：怪物会被决斗逻辑拦下，实际打的是两人轮流行动。
        EnterCombatWithoutExitingEvent(plan.Encounter!, [], plan.ResumeAfterCombat);

        SetEventFinished(OwnPage("BEGIN"));
        return Task.CompletedTask;
    }
}
