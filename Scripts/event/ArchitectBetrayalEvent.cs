// ============================================================================
// 友尽尖塔（youjin）：事件「建筑师的背叛」——替换原版建筑师结局
// ----------------------------------------------------------------------------
// 规则（多人，打完最终 boss 后的胜利房间）：
//   1. 建筑师说「我只能实现一个人的愿望」；
//   2. 所有人投票：「战斗」还是「不战斗」（原生的共享事件投票 UI）；
//   3. 战斗票**超过一半** → 开打：
//        · 所有人回满血（原版战斗开始本来就会满血）；
//        · 场上放一个 99999 血的木桩当「敌人」，战斗才成立；
//        · 队友之间可以互相指定为目标（见 ArchitectBetrayalPatches）；
//        · 只剩最后一个人活着时，木桩被打飞，战斗结束；
//   4. 战斗票没过半（或有人选了不战斗而票数不够）→ 像原版一样，建筑师把大家打死；
//   5. 战斗结束后回到事件，建筑师说「感谢你们让我无聊的生活有点乐趣」，
//      然后把最后一个人也打死 —— 最后调用原版的 RunManager.WinRun()，
//      也就是原版「真胜利」结算（GuaranteeKillAllPlayers + 真胜利画面）。
//
// 反编译踩点（都是实测确认过的）：
//   · 原版 RunManager.EnterNextAct() 里，当 CurrentActIndex 已经是最后一层时，
//     会 new EventRoom(ModelDb.Event<TheArchitect>()) 造出胜利房间。
//     我们用 Harmony 前缀把 EventRoom(EventModel) 构造函数里的 TheArchitect 换成
//     我们自己的事件（TheArchitect 是 sealed，没法继承）。
//   · AbstractRoom.IsVictoryRoom 的判定是「EventRoom 且 CanonicalEvent is TheArchitect」。
//     换成我们的事件后它是 false → 结算画面会变成失败。所以再补一个后置补丁，
//     让「CanonicalEvent 是这个事件」也算胜利房间。
//   · 共享事件（IsShared = true）才能进战斗：
//     EventModel.EnterCombatWithoutExitingEvent 一开头就 `if (!IsShared) throw`。
//   · LayoutType 必须是 Default（不能是 Combat）：同上的方法里有
//     `if (resumeAfterCombat && LayoutType == Combat) throw`，
//     而我们要战斗结束后回到事件里，所以只能走 Default 布局。
//   · 共享事件的原生投票**不是多数决**：EventSynchronizer.ChooseSharedEventOption
//     是 `_multiplayerOptionSelectionRng.NextItem(_playerVotes)` —— 从所有票里**随机抽一票**。
//     所以多数决要我们自己统计：订阅 EventSynchronizer.PlayerVoteChanged，
//     在回调里用公开的 GetPlayerVote(player) 把每张票抄进自己的账本。
//     （注意 ChooseOptionForSharedEvent 第一件事就是 ClearPlayerVotes()，
//      所以不能等选项回调触发时再去读票，必须在投票过程中就抄下来。）
//   · 共享事件的选项回调会在**每个玩家各自的实例**上各跑一次
//     （ChooseOptionForSharedEvent → 对每个 player 调 ChooseOptionForEvent）。
//     这正好是进战斗需要的：EnterCombatWithoutExitingEvent 内部按玩家槽位
//     ReadyToEnterCombat，所有人都 ready 了才会真的开打，所以「进战斗」这条分支
//     **不能**加幂等保护；而「不战斗直接结算」这条必须幂等，否则会调 N 次 WinRun。
//   · 木桩用原版 BattlewornDummyEventV1Encounter（ShouldGiveRewards = false，
//     不产金币/卡牌奖励），血量在开战时由 ArchitectBetrayalCombatModel 改成 99999。
//   · 最后用 RunManager.Instance.WinRun()：它等于 OnEnded(true) + GuaranteeKillAllPlayers()，
//     也就是原版建筑师打死所有人的那套结算，界面显示「真胜利」。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models.Acts;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>多人事件「建筑师的背叛」：替换原版建筑师收尾。</summary>
// 用特性让 RitsuLib 登记（登记出来的 id 是 YOUJIN_EVENT_ARCHITECT_BETRAYAL_EVENT，
// 和本地化键、以及 ModelDb.GetByIdOrNull 的查询保持一致）。
// 千万不要手动 ModelDb.Inject —— RitsuLib 的 ModelDb.Init 补丁会把注入列表里的类型
// 再创建一次，同一个 ID 重复 → AbstractModel 构造函数抛 DuplicateModelException →
// 整个 GameStartup 失败，表现就是「游戏打不开」。
// 挂在 Glory（三层）只是为了有个池子可挂；IsAllowed 恒为 false，所以永远不会被抽到。
[RegisterActEvent(typeof(Glory))]
public sealed class ArchitectBetrayalEvent : ModEventTemplate
{
    /// <summary>投票的结果 —— 决定接下来"遇到建筑师"时他说哪句话。</summary>
    public enum Ending
    {
        /// <summary>还没决定（或者不是我们的事件）。</summary>
        None = 0,

        /// <summary>选项一：先打过一架 → 建筑师说「感谢你们让我在无聊的生活中看了场好戏」。</summary>
        WatchedShow = 1,

        /// <summary>选项二：不战斗 → 建筑师说「那就都去死吧」。</summary>
        AllOfYouDie = 2,
    }

    /// <summary>木桩的血量（基本打不死，只是为了让战斗成立）。</summary>
    public const int DummyHp = 99999;

    /// <summary>选项下标约定：0 = 战斗，1 = 不战斗。</summary>
    public const uint FightVoteIndex = 0;

    /// <summary>
    /// 背叛战斗是否正在进行。ArchitectBetrayalPatches 用它来决定
    /// 「队友能不能被指定为目标」，ArchitectBetrayalCombatModel 用它来决定
    /// 「要不要给木桩加血 / 要不要提前结束战斗」。
    ///
    /// ⚠ 时机很重要：**必须在"玩家选战斗"那一刻就置 true**，
    /// 因为「队友搬到对面」「木桩挪到最右边」这些补丁跑在**战斗布场的最开始**，
    /// 比 `BeforeCombatStart` 还早。之前把它改成"要等确认场上有木桩才算"，
    /// 结果摆位置时开关还是 false → 位置和出牌判定全部失效（线上实测）。
    ///
    /// 防泄漏由 `ArchitectBetrayalCombatModel` 负责：
    ///   · 开战时如果发现场上没有我们的木桩 / 当前房间不是我们那只卵的遭遇，
    ///     就把它清掉（用原始 `_enemies` 字段判定，不会踩到补丁自环）；
    ///   · 进非战斗房间（地图/事件/商店…）时也清一次。
    /// </summary>
    public static bool BetrayalCombatActive { get; set; }

    /// <summary>
    /// 这次投票的结果。ArchitectDialoguePatch 会在建筑师加载对话之后，
    /// 用这个值把建筑师最后一句台词换掉。
    /// </summary>
    public static Ending ChosenEnding { get; set; } = Ending.None;

    /// <summary>
    /// 已经切去建筑师房间的房间。选项回调在每个玩家各自的实例上都会跑一次，
    /// 而切换房间必须只做一次（否则同一个客户端会来回进出好几遍）。
    /// </summary>
    private static readonly HashSet<object> ArchitectRoomEntered = new();

    /// <summary>
    /// 素材：优先用 youjin/images/events/jianzhushi.png 与对应场景（jianzhushi_bg.tscn / jianzhushi_vfx.tscn），
    /// 没放的话回退到原版「永恒之石」那套（确认存在的素材，不会加载失败）。
    /// </summary>
    public override EventAssetProfile AssetProfile => EventArt.Resolve(
        "jianzhushi",
        ImageHelper.GetImagePath("events/stone_of_all_time.png"),
        SceneHelper.GetScenePath("events/background_scenes/stone_of_all_time"),
        SceneHelper.GetScenePath("vfx/events/stone_of_all_time_vfx"));

    // ── 事件本体 ────────────────────────────────────────────────────────────

    /// <summary>必须共享：非共享事件不允许 EnterCombatWithoutExitingEvent。</summary>
    public override bool IsShared => true;

    /// <summary>
    /// 必须是 Default。Combat 布局没法在战斗结束后回到事件
    /// （EnterCombatWithoutExitingEvent 里会直接抛异常）。
    /// </summary>
    public override EventLayoutType LayoutType => EventLayoutType.Default;

    /// <summary>永远不参与随机事件抽取 —— 我们只在胜利房间手动用它。</summary>
    public override bool IsAllowed(IRunState runState) => false;

    /// <summary>两个选项：战斗 / 不战斗。</summary>
    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        EnsureLocalized();

        return new List<EventOption>
        {
            new(this, ChooseFight, InitialOptionKey("FIGHT"), Array.Empty<IHoverTip>()),
            new(this, ChooseRefuse, InitialOptionKey("REFUSE"), Array.Empty<IHoverTip>()),
        };
    }

    /// <summary>
    /// 战斗结束、游戏回到这个事件之后，接着去「遇到建筑师」。
    /// （进战斗时带了 resumeAfterCombat = true，所以这里一定会被调到。）
    /// </summary>
    public override async Task Resume(AbstractRoom room)
    {
        BetrayalCombatActive = false;
        await EnterArchitectRoomAsync(Ending.WatchedShow);
    }

    // ── 两个选项（都走同一套结算，依据是我们自己统计的票数）────────────────

    private Task ChooseFight() => ResolveAsync(myChoiceWantsFight: true);

    private Task ChooseRefuse() => ResolveAsync(myChoiceWantsFight: false);

    /// <summary>
    /// 结算。注意这个方法在每个玩家各自的实例上都会被调用一次，
    /// 「开战」分支需要每次都跑（要凑齐所有人的 ReadyToEnterCombat），
    /// 「不开战」分支必须幂等。
    /// </summary>
    private async Task ResolveAsync(bool myChoiceWantsFight)
    {
        if (!YouJinSettings.GameplayEnabled)
            return;

        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return;

        var total = Math.Max(1, run.Players.Count);

        // 「超过 50% 选战斗」这一步是在 host 上算的（见 MajorityVotePatch）：
        // EventSynchronizer 收齐所有人的票之后，本来会从票里**随机抽一张**
        // （ChooseSharedEventOption 里的 rng.NextItem(_playerVotes)）；
        // 我们在那个 Prefix 里把所有票改写成多数决的结果，于是广播出去的
        // optionIndex 就等于多数票，所有客户端执行的是同一个选项。
        // 所以这里直接用 myChoiceWantsFight，就是「大家投票的结论」。
        //
        // 【千万不要在这里自己统计票数】
        // EventSynchronizer.PlayerVoteChanged 只有 host 能收到别人的票，
        // 客户端之间不互相转发。客户端自己统计会得出「票不够 → 不战斗」，
        // 然后调 WinRun 把整局提前结束（就是之前队友直接结算的原因）。
        var goFight = myChoiceWantsFight;

        Entry.Logger.Info(
            $"建筑师的背叛：投票结论 → {(goFight ? "开战" : "不战斗")}（共 {total} 名玩家）");

        if (goFight)
        {
            try
            {
                BetrayalCombatActive = true;

                // 每个玩家实例都要调一次，EventCombatSynchronizer 会按槽位收集 ready，
                // 全员 ready 之后才真的开打。
                EnterCombatWithoutExitingEvent(
                    ModelDb.Encounter<BetrayalEggEncounter>(),
                    Array.Empty<Reward>(),
                    true);
            }
            catch (Exception ex)
            {
                // 同步失败（拿不到遭遇、房间流程报错等）→ 立刻把开关放回去，
                // 否则这个静态开关会一直挂着，之后每场战斗都被当成背叛战斗。
                BetrayalCombatActive = false;
                ArchitectBetrayalCombatModel.ClearBetrayalMarkers("开战抛异常");
                Entry.Logger.Info($"建筑师的背叛：进入战斗失败，已回滚标记 {ex}");
            }

            return;
        }

        // 不战斗 → 一样去「遇到建筑师」，只是他会说另一句话（「那就都去死吧」）。
        await EnterArchitectRoomAsync(Ending.AllOfYouDie);
    }

    /// <summary>
    /// 切到**原版建筑师房间**，让原版那套演出（建筑师雕像、对话、
    /// 互相攻击动画、把所有人打死、真胜利结算）原样播完。
    ///
    /// 唯一的不同是：进去之前把这次投票的结果记在 ChosenEnding 上，
    /// ArchitectDialoguePatch 会把建筑师的最后一句台词换成对应的文本。
    /// </summary>
    private async Task EnterArchitectRoomAsync(Ending ending)
    {
        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return;

        var roomKey = (object?)run.CurrentRoom ?? run;
        if (!ArchitectRoomEntered.Add(roomKey))
            return;

        ChosenEnding = ending;
        Entry.Logger.Info($"建筑师的背叛：投票结算完成（{ending}）→ 进入建筑师房间");

        try
        {
            if (RunManager.Instance is not { } manager)
                return;

            // 这一次要的是原版建筑师房间本身，所以让构造函数补丁别把它换掉。
            ArchitectRoomReplacementPatch.AllowVanillaArchitect = true;

            SetEventFinished(PageDescription("FINALE"));

            await manager.EnterRoom(new EventRoom(ModelDb.Event<TheArchitect>()));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：进入建筑师房间失败 {ex}");
        }
        finally
        {
            ArchitectRoomReplacementPatch.AllowVanillaArchitect = false;
        }
    }

    // ── 文本 ────────────────────────────────────────────────────────────────

    /// <summary>建筑师最后一句台词的本地化键前缀（写进 "ancients" 表）。</summary>
    private const string ArchitectLinePrefix = "ARCHITECT_BETRAYAL_EVENT.architect";

    /// <summary>
    /// 把「建筑师最后说哪句话」的两条文本注入 ancients 表。
    /// 建筑师的 LocTable 是 "ancients"（不是 "events"），所以这两条要单独放。
    /// 同样也写进了 youjin/localization/{zhs,eng}/ancients.json，这里是兜底。
    /// </summary>
    public static void EnsureArchitectLines()
    {
        try
        {
            var loc = LocManager.Instance;
            var table = loc?.GetTable("ancients");
            if (loc is null || table is null)
                return;

            if (table.HasEntry($"{ArchitectLinePrefix}.WATCHED_SHOW"))
                return;

            var chinese = (loc.Language ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            table.MergeWith(new Dictionary<string, string>
            {
                [$"{ArchitectLinePrefix}.WATCHED_SHOW"] = chinese
                    ? "感谢你们，让我在无聊的生活中看了场好戏。"
                    : "Thank you — you gave my boring life a good show.",
                [$"{ArchitectLinePrefix}.ALL_OF_YOU_DIE"] = chinese
                    ? "那就都去死吧。"
                    : "Then all of you can die.",
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：建筑师台词注入失败 {ex}");
        }
    }

    /// <summary>
    /// 建筑师这次要说的最后一句（配 ArchitectDialoguePatch 用）。
    /// 注意建筑师的本地化表是 "ancients"，跟事件自己的 "events" 表不是一张。
    /// </summary>
    public static LocString ArchitectFinalLine(Ending ending)
    {
        EnsureArchitectLines();

        var suffix = ending == Ending.WatchedShow ? "WATCHED_SHOW" : "ALL_OF_YOU_DIE";
        return new LocString("ancients", $"{ArchitectLinePrefix}.{suffix}");
    }

    /// <summary>
    /// 兜底文案（PCK 里已有键时直接跳过）。
    /// 正常情况下文本走 youjin/localization/{zhs,eng}/events.json。
    /// </summary>
    private void EnsureLocalized()
    {
        try
        {
            var loc = LocManager.Instance;
            var table = loc?.GetTable(LocTable);
            if (loc is null || table is null)
                return;

            var id = Id.Entry;
            if (table.HasEntry($"{id}.title"))
                return;

            var chinese = (loc.Language ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var fightKey = InitialOptionKey("FIGHT");
            var refuseKey = InitialOptionKey("REFUSE");

            table.MergeWith(new Dictionary<string, string>
            {
                [$"{id}.title"] = chinese ? "建筑师的挑拨" : "The Architect's Provocation",
                [$"{id}.pages.INITIAL.description"] = chinese
                    ? "建筑师坐在王座上，指尖捻着一缕光。「一趟旅程，一个愿望，」他说，"
                      + "「可惜你们不止一个人。」他松开手，让那缕光悬在你们之间，"
                      + "「谁走到最后，它就是谁的——你们自己商量。」"
                    : "The Architect sits upon his throne, rolling a thread of light between his fingers. "
                      + "\"One journey, one wish,\" he says, \"but there is more than one of you.\" "
                      + "He lets the thread hang in the air between you. "
                      + "\"Whoever is left standing takes it. Sort it out amongst yourselves.\"",

                [$"{fightKey}.title"] = chinese ? "战斗" : "Fight",
                [$"{fightKey}.description"] = chinese
                    ? "投票决定：只有[blue]超过一半[/blue]的人选这一项，才会真的打起来。"
                      + "所有人恢复满血、生命上限翻三倍，彼此都能被当成目标；"
                      + "只剩一个人站着时，建筑师才会开口。"
                    : "Decided by vote: it only happens if [blue]more than half[/blue] choose it. "
                      + "Everyone heals to full and triples their max HP, and everyone can be targeted. "
                      + "The Architect only speaks once one of you is left standing.",

                [$"{refuseKey}.title"] = chinese ? "不战斗" : "Don't Fight",
                [$"{refuseKey}.description"] = chinese
                    ? "谁也不动手，把这个决定原样交还给建筑师。"
                    : "Nobody lifts a hand. The decision goes back to the Architect, unchanged.",

                [$"{id}.pages.FINALE.description"] = chinese
                    ? "那缕光熄了。建筑师靠在王座上俯视你们，等着看谁还有力气抬头。"
                    : "The thread of light goes out. The Architect leans back on his throne, "
                      + "watching to see who still has the strength to look up.",
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：本地化注入失败 {ex}");
        }
    }
}
