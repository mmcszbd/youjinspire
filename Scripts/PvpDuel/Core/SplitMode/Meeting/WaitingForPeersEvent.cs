// ============================================================================
// 友尽尖塔（youjin）· 分离模式：「等一等」—— 三层打完之后的缓冲房
// ----------------------------------------------------------------------------
// 要解决的问题：分离模式是「各自单机局爬三幕 → 都打完再合流」。最后一幕的 BOSS
// 一结算，原版流程就是 RunManager.EnterNextAct → 建筑师事件 → 结局，本机这一局当场
// 收掉。先打完的那一方于是无处可待，合流窗口只剩几十毫秒。
//
// 做法：三层打完时拦住 EnterNextAct（见 SplitLastActPatch），把这一间房换成这个事件；
// 事件里只有「站桩」一个选项，点进去是一场 99999 血木桩的遭遇战（BetrayalEggEncounter，
// 木桩不还手、也打不死），人留在局里等着。对面也打完 → 合流那条线接管，直接换会话。
//
// 为什么不直接 RunManager.EnterRoomDebug 裸开战斗（MirrorDuelEvent 的教训）：
//   ① 没有入口/出口，打完不知道回哪；
//   ② 会被别的战斗模块（建筑师的挑拨 / PVP 决斗）当成自己的场子抢走。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using YouJin.Scripts;

namespace PvpDuel.Core.SplitMode;

/// <summary>分离模式：三层打完之后进来等对端的房间（一场打不死的木桩战）。</summary>
[RegisterActEvent(typeof(Glory))]
public sealed class WaitingForPeersEvent : ModEventTemplate
{
    /// <summary>本体的 "events" 表（基类 LocTable 在自定义幕里不存在）。</summary>
    private const string OptionLocTable = "events";

    /// <summary>选项名（拼进 InitialOptionKey）。</summary>
    private const string WaitOptionName = "WAIT_FIGHT";

    /// <summary>已经进过这个等待房（每局只进一次，回头再触发就放行原版流程）。</summary>
    public static bool Entered { get; private set; }

    /// <summary>新一局开始时清掉。</summary>
    public static void Reset()
    {
        Entered = false;
    }

    /// <summary>标记已进过。</summary>
    public static void MarkEntered()
    {
        if (Entered)
        {
            return;
        }

        Entered = true;
    }

    /// <summary>必须共享：非共享事件不允许 EnterCombatWithoutExitingEvent。</summary>
    public override bool IsShared => true;

    /// <summary>必须是 Default：打完要能回到事件里。</summary>
    public override EventLayoutType LayoutType => EventLayoutType.Default;

    /// <summary>永远不参与随机事件抽取 —— 只在分离模式三层打完时手动进。</summary>
    public override bool IsAllowed(IRunState runState) => false;

    /// <summary>素材优先 youjin/images/events/dengdai.png，没有就回退原版永恒之石。</summary>
    public override EventAssetProfile AssetProfile
    {
        get
        {
            // 标题在事件页渲染时就取走了，必须在这里（房间刚建、还没画 UI）就把表写好。
            EnsureLocalized();

            return EventArt.Resolve(
                "dengdai",
                ImageHelper.GetImagePath("events/stone_of_all_time.png"),
                SceneHelper.GetScenePath("events/background_scenes/stone_of_all_time"),
                SceneHelper.GetScenePath("events/vfx/stone_of_all_time_vfx"));
        }
    }

    /// <summary>只有一个选项：站到木桩前面等着。</summary>
    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        EnsureLocalized();

        var key = InitialOptionKey(WaitOptionName);

        return
        [
            new(
                this,
                ChooseWait,
                new LocString(OptionLocTable, $"{key}.title"),
                new LocString(OptionLocTable, $"{key}.description"),
                key,
                []),
        ];
    }

    private Task ChooseWait() => WaitAsync();

    /// <summary>进木桩遭遇战：99999 血、不还手，纯粹把人留在局里。</summary>
    private async Task WaitAsync()
    {
        var run = Owner?.RunState;
        if (run is null)
        {
            return;
        }

        try
        {
            ModInfo.Info("[SplitMode] 等待合流：进木桩遭遇战（99999 血，打不死）");

            EnterCombatWithoutExitingEvent(
                ModelDb.Encounter<BetrayalEggEncounter>(),
                Array.Empty<Reward>(),
                shouldResumeAfterCombat: true);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            ArchitectBetrayalEvent.BetrayalCombatActive = false;
            ModInfo.Error($"[SplitMode] 等待合流：进战斗失败（留在事件页继续等）：{ex}");
        }
    }

    /// <summary>木桩打完了/被打断后回到事件页：这时候通常已经合流，没合流就再等一轮。</summary>
    public override async Task Resume(AbstractRoom room)
    {
        // ★ 必须让出这一帧：ResumeEvents 正在 foreach _events，同步改动会炸。
        await Task.Yield();

        ModInfo.Info("[SplitMode] 等待合流：回到事件页（对端还没打完就继续等）");
    }

    // ── 文本 ────────────────────────────────────────────────────────────────

    private static LocString OwnPage(string pageName, string entry)
        => new(OptionLocTable, $"{entry}.pages.{pageName}.description");

    /// <summary>把标题、正文、选项文案写进 "events" 表。</summary>
    private void EnsureLocalized()
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
            var key = InitialOptionKey(WaitOptionName);

            var title = chinese ? "等一等" : "Hold On";

            var entries = new Dictionary<string, string>
            {
                [$"{key}.title"] = chinese ? "站桩" : "Stand By",
                [$"{key}.description"] = chinese
                    ? "走到木桩前面站好。对面还没打完，你先在这儿等着。"
                    : "Take your place in front of the dummy. The other side is not done yet.",
                [$"{Id.Entry}.pages.INITIAL.description"] = chinese
                    ? "三层都打完了。远处那扇门还关着——它要等另一边也走到这里。\n\n"
                      + "先站到木桩前面吧，那东西有 99999 点血，你怎么打它都不会倒。"
                    : "All three acts are done. The door ahead is still shut - it waits for the other side.\n\n"
                      + "Stand in front of the dummy. It has 99999 HP; you are not going to break it.",
                [$"{Id.Entry}.pages.FINALE.description"] = chinese
                    ? "门开了。"
                    : "The door opens.",
            };

            // RitsuLib 注册后 Id.Entry 已带 YOUJIN_EVENT_ 前缀，真正的键是 $"{Id.Entry}.title"，
            // 另外几种是历史前缀的兜底（踩过的坑见 ArchitectProvokeEvent 的注释）。
            foreach (var entryKey in new[]
                     {
                         $"{Id.Entry}.title",
                         $"YOUJIN_EVENT_{Id.Entry}.title",
                         $"PVP_DUEL_EVENT_{Id.Entry}.title",
                         $"{Id}.title",
                     })
            {
                entries[entryKey] = title;
            }

            table.MergeWith(entries);
        }
        catch (Exception ex)
        {
            ModInfo.Info($"[SplitMode] 等待合流：文案注入失败 {ex.Message}");
        }
    }
}
