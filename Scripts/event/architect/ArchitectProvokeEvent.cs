// ============================================================================
// 友尽尖塔（youjin）：事件「建筑师的挑拨」—— 排在「战战战！！！」之前
// ----------------------------------------------------------------------------
// 流程（多人，打完最终 boss 后的胜利房间）：
//   1. 计分：普通遗物 100 / 罕见 200 / 稀有 300 / 商店·事件·远古 200，
//      初始遗物不计，金币 1:1 折成分；并列时按 NetId 排，两端算出来的 boss 一致。
//   2. 分数最高的那位在接下来的 PvP 里当 boss：
//      生命上限 ×(1+n)，n = 总人数 − 2；每回合 +1 能量。
//   3. 事件页只给一个「开打」选项，战斗形态**固定手速混战**，
//      不受设置里的「PvP 方式」影响（YG 2026-11-06 的决定）。
//   4. 打完（或形态不可用）→ 直接把房间换成「战战战！！！」事件，
//      由那边按 PvP 方式循环（战斗/观战/结束），收场再进原版建筑师房间拿真胜利。
//
// 反编译踩点（和 ArchitectBetrayalEvent / BattleBattleBattleEvent 同一套，改前先读）：
//   · IsShared 必须 true：EventModel.EnterCombatWithoutExitingEvent 一开头
//     `if (!IsShared) throw`。
//   · LayoutType 必须是 Default：同一个方法里有
//     `if (resumeAfterCombat && LayoutType == Combat) throw`，
//     而我们要战斗结束后回到事件（再切去战战战），只能走 Default。
//   · 共享事件每个玩家槽位各有一份 EventModel 实例，实例各自 ready；
//     切换房间只能做一次，用 HashSet 按「当前房间」幂等（和旧的
//     ArchitectRoomEntered 一个套路）。
//   · Resume 必须先把执行推到下一帧：EventSynchronizer.ResumeEvents 是
//     `foreach (var e in _events) RunSafely(e.Resume(room))`，
//     同步去 EnterRoom 会改 _events → "Collection was modified" → 黑屏。
//   · 本地化不能用基类 LocTable（自定义幕推出来的表不在 LocManager 里），
//     全部落到 "events" 表并运行时注入；标题三种前缀都写。
//   · 千万不要手动 ModelDb.Inject —— RitsuLib 已经登记过，重复会
//     DuplicateModelException → GameStartup 失败（表现为游戏打不开）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using PvpDuel.Core;
using PvpDuel.Core.Boss;
using YouJin.Scripts.Pvp;

namespace YouJin.Scripts;

/// <summary>「建筑师的挑拨」：胜利房间的第一站 —— 计分、选 boss、打一场手速混战。</summary>
/// <remarks>
/// <para>
/// 这个事件和原来的 <see cref="ArchitectBetrayalEvent" /> 是两件事：旧事件把
/// 「投票 → 打架 → 建筑师收尾」全塞在一个页面里，新版把「挑拨」（计分 + 一场手速混战）
/// 独立出来，排在「战战战！！！」之前，让玩家先看到分数和 boss 是谁。
/// </para>
/// <para>
/// 战斗形态写死 <see cref="PvpCombatKind.Speed" />：不改 <see cref="PvpCombatKit.FromSettings" />
/// 的结果，回合制玩家也会先打这一场手速混战。
/// </para>
/// </remarks>
[RegisterActEvent(typeof(Glory))]
public sealed class ArchitectProvokeEvent : ModEventTemplate
{
    /// <summary>本体的 "events" 表（基类 LocTable 在自定义幕里不存在）。</summary>
    private const string OptionLocTable = "events";

    /// <summary>选项名（拼进 InitialOptionKey）。</summary>
    private const string FightOptionName = "PROVOKE_FIGHT";

    /// <summary>正文模板键的后缀（拼在 <c>{Id.Entry}</c> 后面）：events.json 写这个键就能改文案。</summary>
    private const string FrontTemplateSuffix = ".pages.INITIAL.template";

    private const string FrontTemplateFallbackZh =
        "看着别人怀里的金币，打倒建筑师的目标好像越来越模糊，{who} 发出了咕噜咕噜的声音，"
        + "他想要抢走所有人的金币……";

    private const string FrontTemplateFallbackEn =
        "Eyeing the gold in everyone else's arms, the goal of defeating the Architect "
        + "seems to blur. {who} makes a gurgling sound. He wants to take all of it...";

    /// <summary>已经切去「战战战」的房间（每份实例都会跑一次，切房间只能一次）。</summary>
    private static readonly HashSet<object> HandoffRooms = [];

    /// <summary>本实例是否已经把事件页收掉（SetEventFinished 只对本实例有意义）。</summary>
    private bool _finished;

    // ── 事件本体 ────────────────────────────────────────────────────────────

    /// <summary>必须共享：非共享事件不允许 EnterCombatWithoutExitingEvent。</summary>
    public override bool IsShared => true;

    /// <summary>必须是 Default：打完要回到事件里，再切去战战战。</summary>
    public override EventLayoutType LayoutType => EventLayoutType.Default;

    /// <summary>永远不参与随机事件抽取 —— 只在胜利房间手动用它。</summary>
    public override bool IsAllowed(IRunState runState) => false;

    /// <summary>素材沿用「建筑师的背叛」那套，没有就回退原版永恒之石。</summary>
    public override EventAssetProfile AssetProfile
    {
        get
        {
            // 标题在事件页渲染时就已经取走了，必须在这里（房间刚建、还没画 UI）就把表写好，
            // 放到 GenerateInitialOptions 里已经晚了 —— 那正是「亮闪闪的金币」一直显示成
            // 「建筑师的挑拨」的原因。
            EnsureLocalized(RunManager.Instance?.DebugOnlyGetState());

            return EventArt.Resolve(
                "jianzhushi",
                ImageHelper.GetImagePath("events/stone_of_all_time.png"),
                SceneHelper.GetScenePath("events/background_scenes/stone_of_all_time"),
                SceneHelper.GetScenePath("events/vfx/stone_of_all_time_vfx"));
        }
    }

    /// <summary>只有一个选项：开打。</summary>
    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        var run = RunManager.Instance?.DebugOnlyGetState();

        // 计分 + 选 boss（同一局幂等；不是贪婪模式就直接返回 false）。
        BossState.Elect(run);

        // 分数块是动态文本，所以每次进页面都重写一遍（不能像旧事件那样
        // 「键已存在就跳过」，否则第二次进来还是上一次的分数）。
        EnsureLocalized(run);

        var key = InitialOptionKey(FightOptionName);

        return
        [
            new(
                this,
                ChooseFight,
                new LocString(OptionLocTable, $"{key}.title"),
                new LocString(OptionLocTable, $"{key}.description"),
                key,
                []),
        ];
    }

    private Task ChooseFight() => StartSpeedCombatAsync();

    // ── 战斗 ────────────────────────────────────────────────────────────────

    /// <summary>固定手速混战：木桩站场、所有人一起出牌（不吃设置里的 PvP 方式）。</summary>
    private async Task StartSpeedCombatAsync()
    {
        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
        {
            return;
        }

        try
        {
            var plan = PvpCombatKit.Plan(
                PvpCombatKind.Speed,
                run,
                ModelDb.Encounter<BetrayalEggEncounter>(),
                resumeAfterCombat: true,
                allowFallback: false);

            if (!plan.Ready)
            {
                ModInfo.Warn("[建筑师挑拨] 手速混战不可用（人数不够？）→ 直接进战战战");
                await HandoffAsync();
                return;
            }

            plan.Apply();

            ModInfo.Info("[建筑师挑拨] 开打（固定手速混战）");

            EnterCombatWithoutExitingEvent(
                plan.Encounter!,
                Array.Empty<Reward>(),
                plan.ResumeAfterCombat);
        }
        catch (Exception ex)
        {
            // 开战抛异常 → 把布场开关放回去，否则之后每场战斗都被当成背叛战斗。
            ArchitectBetrayalEvent.BetrayalCombatActive = false;
            ModInfo.Error($"[建筑师挑拨] 开战失败，直接进战战战：{ex}");
            await HandoffAsync();
        }
    }

    /// <summary>战斗结束、回到本事件之后，接力去「战战战！！！」。</summary>
    public override async Task Resume(AbstractRoom exitedRoom)
    {
        // ★ 先推到下一帧：ResumeEvents 正在 foreach，同步改 _events 会黑屏。
        await Task.Yield();

        ArchitectBetrayalEvent.BetrayalCombatActive = false;

        await HandoffAsync();
    }

    /// <summary>收掉本实例的事件页，并让其中一份实例把房间换成「战战战」。</summary>
    private async Task HandoffAsync()
    {
        if (!_finished)
        {
            _finished = true;

            try
            {
                SetEventFinished(OwnPage("FINALE"));
            }
            catch (Exception ex)
            {
                ModInfo.Info($"[建筑师挑拨] 收事件页失败（继续切换房间）：{ex.Message}");
            }
        }

        var run = Owner?.RunState;
        if (run is null)
        {
            return;
        }

        var roomKey = (object?)run.CurrentRoom ?? run;
        if (!HandoffRooms.Add(roomKey))
        {
            return;
        }

        try
        {
            if (RunManager.Instance is not { } manager)
            {
                return;
            }

            ModInfo.Info("[建筑师挑拨] 挑拨结束 → 进入「战战战！！！」");

            await manager.EnterRoom(new EventRoom(ModelDb.Event<BattleBattleBattleEvent>()));
        }
        catch (Exception ex)
        {
            ModInfo.Error($"[建筑师挑拨] 切去战战战失败：{ex}");
            HandoffRooms.Remove(roomKey);
        }
    }

    // ── 文本 ────────────────────────────────────────────────────────────────

    /// <summary>页面描述走 "events" 表（绕开基类可能不存在的 LocTable）。</summary>
    private LocString OwnPage(string pageName) => new(OptionLocTable, $"{Id.Entry}.pages.{pageName}.description");

    /// <summary>把标题、前言、分数块、选项文案写进 "events" 表。</summary>
    private void EnsureLocalized(IRunState? run)
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
            var fightKey = InitialOptionKey(FightOptionName);

            var title = chinese ? "亮闪闪的金币" : "Glittering Gold";

            var entries = new Dictionary<string, string>
            {
                [$"{fightKey}.title"] = chinese ? "开始" : "Start",
                [$"{fightKey}.description"] = chinese ? "开始" : "Start",
                [$"{Id.Entry}.pages.INITIAL.description"] = BuildFrontPage(chinese, run),
                [$"{Id.Entry}.pages.FINALE.description"] = chinese
                    ? "建筑师把光收进袖子里，往前倾了倾身：「还不够。再来。」"
                    : "The Architect tucks the light away and leans forward. 'Not enough. Again.'",
            };

            // 标题：RitsuLib 注册后 Id.Entry 已经带上了 YOUJIN_EVENT_ 前缀
            // （日志里是 EVENT.YOUJIN_EVENT_ARCHITECT_PROVOKE_EVENT），
            // 所以真正的键就是 $"{Id.Entry}.title"。另外几种是历史前缀的兜底。
            // ⚠ 之前只写了 $"{Id}.title" / $"YOUJIN_EVENT_{Id.Entry}.title"，
            // 前者多了 "EVENT." 点号、后者把前缀拼了两遍 —— 正确那个键一次都没写过，
            // 于是标题一直被 events.json 里的旧值压住（2026-11-06 18:5x 的「标题没替换成功」）。
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

            ModInfo.Info(
                $"[建筑师挑拨] 标题写入 entry={Id.Entry} 值={LocString.GetIfExists(OptionLocTable, $"{Id.Entry}.title")?.GetFormattedText() ?? "<无>"}");
        }
        catch (Exception ex)
        {
            ModInfo.Info($"[建筑师挑拨] 文案注入失败 {ex.Message}");
        }
    }

    /// <summary>初始页正文：一句话，<c>{who}</c> 换成 boss 的名字。</summary>
    /// <remarks>
    /// 走模板键 <c>{Id.Entry}.pages.INITIAL.template</c>：events.json 里写这个键就能改文案，
    /// 不用改代码。<c>{who}</c> 会被替换成「玩家 N」，模板里没有占位符就原样显示。
    /// 这里每次进页面都重写 <c>pages.INITIAL.description</c>（因为它含当场的玩家编号），
    /// 但模板本身优先取 json，取不到才用下面的兜底常量。
    /// </remarks>
    private string BuildFrontPage(bool chinese, IRunState? run)
    {
        var who = chinese ? "有人" : "Someone";

        if (run is not null && BossState.HasBoss && BossState.BossNetId is { } bossNetId)
        {
            for (var i = 0; i < run.Players.Count; i++)
            {
                if (run.Players[i].NetId != bossNetId)
                {
                    continue;
                }

                who = chinese ? $"玩家 {i + 1}" : $"Player {i + 1}";
                break;
            }
        }

        var template = ReadFrontTemplate(chinese);
        return template.Replace("{who}", who);
    }

    /// <summary>读正文模板：json 优先，缺了用兜底。</summary>
    private string ReadFrontTemplate(bool chinese)
    {
        try
        {
            var key = $"{Id.Entry}{FrontTemplateSuffix}";
            var text = LocString.GetIfExists(OptionLocTable, key)?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text!;
            }
        }
        catch
        {
            // 表还没建好之类，直接兜底。
        }

        return chinese ? FrontTemplateFallbackZh : FrontTemplateFallbackEn;
    }
}
