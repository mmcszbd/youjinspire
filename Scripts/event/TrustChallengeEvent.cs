// ============================================================================
// 友尽尖塔（youjin）：事件「信任挑战」
// ----------------------------------------------------------------------------
// 规则：
//   1. 多人局、并且**所有人金币都 > 50** 时才会出现；
//   2. 两个选项：
//      ① 放入金币：扣自己 50 金币；
//      ② 假装放入：不花钱；
//   3. 所有人都选完之后统一结算（每次事件只结算一次）：
//      · 选①的人**超过一半** → 所有人各得 150 金币；
//      · 所有人都选② → 所有人各吃一张原版诅咒「羞耻」（Shame）；
//      · 其余情况（有人选①但没过半）→ 什么也不发生。
//
// 实现要点（都反编译确认过）：
//   · 事件是**每人一份实例**（IsShared 默认 false），所以跨玩家的"投票统计"必须放在静态账本里，
//     用 run.CurrentRoom 当 key（一处石洞一份账），结算完就清掉。
//   · 每次选择都会在所有客户端上执行（EventSynchronizer.ChooseOptionForEvent → OptionChosen），
//     所以账本和结算天然同步，不会分叉。
//   · 最后一个人选完时触发结算；结算完用 RunManager.Instance.EventSynchronizer.GetEventForPlayer(player)
//     找到**每个人**的事件实例，统一用同一段结果文案收尾（SetEventFinished 是 protected，
//     所以在自己的事件类里开了个 public 包装 Finish(...)）。
//   · 「超过一半」判定：paid * 2 > total（严格大于 50%）。
//   · 给牌用 CardPileCmd.AddCursesToDeck（内部会校验是不是诅咒；Shame 的 CardType 就是 Curse）。
//   · 文本同样运行时注入 events 表，不需要重新导出 PCK。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>多人事件「信任挑战」。</summary>
[RegisterActEvent(typeof(Hive))]  // 二层
[RegisterActEvent(typeof(Glory))] // 三层
public sealed class TrustChallengeEvent : ModEventTemplate
{
    /// <summary>入洞要交的金币。</summary>
    public const int Cost = 50;

    /// <summary>同心协力时每人拿到的金币。</summary>
    public const int Reward = 150;

    /// <summary>本处石洞的投票账本：key = 房间，value = (玩家 NetId → 是否真的放了钱)。</summary>
    private static readonly Dictionary<object, Dictionary<ulong, bool>> Tallies = new();

    /// <summary>
    /// 素材整套套用原版事件「永恒之石」(StoneOfAllTime)：
    /// 立绘 res://images/events/stone_of_all_time.png、
    /// 背景 res://scenes/events/background_scenes/stone_of_all_time.tscn、
    /// 特效 res://scenes/vfx/events/stone_of_all_time_vfx.tscn（不存在也不会炸）。
    /// </summary>
    public override EventAssetProfile AssetProfile => EventArt.Resolve(
        "trust_challenge",
        ImageHelper.GetImagePath("events/stone_of_all_time.png"),
        SceneHelper.GetScenePath("events/background_scenes/stone_of_all_time"),
        SceneHelper.GetScenePath("vfx/events/stone_of_all_time_vfx"));

    /// <summary>多人 + 所有人金币都超过 50 才会出现。</summary>
    public override bool IsAllowed(IRunState runState)
        => YouJinSettings.GameplayEnabled
           && runState.Players.Count > 1
           && runState.Players.All(player => player.Gold > Cost);

    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        EnsureLocalized();

        return new List<EventOption>
        {
            new(this, PutInGold, InitialOptionKey("PAID"), Array.Empty<IHoverTip>()),
            new(this, Pretend, InitialOptionKey("BLUFF"), Array.Empty<IHoverTip>()),
        };
    }

    private Task PutInGold() => ChooseAsync(paid: true);

    private Task Pretend() => ChooseAsync(paid: false);

    /// <summary>记录本次选择；如果这是最后一个人的选择，就统一结算。</summary>
    private async Task ChooseAsync(bool paid)
    {
        if (!YouJinSettings.GameplayEnabled)
            return;

        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return;

        var roomKey = (object?)run.CurrentRoom ?? run;
        if (!Tallies.TryGetValue(roomKey, out var tally))
        {
            tally = new Dictionary<ulong, bool>();
            Tallies[roomKey] = tally;
        }

        // 同一个人重复点（原版只禁用被选中的那个按钮）时忽略
        if (!tally.TryAdd(me.NetId, paid))
            return;

        if (paid)
        {
            await PlayerCmd.LoseGold(Cost, me, GoldLossType.Spent);
            Entry.Logger.Info($"信任挑战：NetId={me.NetId} 真的放入了 {Cost} 金币");
        }
        else
        {
            Entry.Logger.Info($"信任挑战：NetId={me.NetId} 假装放入");
        }

        if (tally.Count < run.Players.Count)
        {
            Entry.Logger.Info($"信任挑战：已选 {tally.Count}/{run.Players.Count}，等其他人");
            return;
        }

        Tallies.Remove(roomKey);
        await ResolveAsync(run, tally);
    }

    /// <summary>全员选完后的结算。</summary>
    private async Task ResolveAsync(IRunState run, Dictionary<ulong, bool> tally)
    {
        var total = run.Players.Count;
        var paidCount = tally.Values.Count(value => value);
        LocString resultPage;

        if (paidCount * 2 > total)
        {
            // 超过一半的人真的放了钱 → 人人有份
            foreach (var player in run.Players)
                await PlayerCmd.GainGold(Reward, player, false);

            resultPage = Page("SUCCESS");
            Entry.Logger.Info($"信任挑战：{paidCount}/{total} 放了钱（过半）→ 每人 +{Reward} 金币");
        }
        else if (paidCount == 0)
        {
            // 全都在假装 → 全员吃一张「羞耻」
            var curse = ModelDb.GetByIdOrNull<CardModel>(ModelDb.GetId(typeof(Shame)));
            if (curse is not null)
            {
                foreach (var player in run.Players)
                    await CardPileCmd.AddCursesToDeck(new[] { curse }, player);

                Entry.Logger.Info("信任挑战：全员假装 → 每人获得一张「羞耻」");
            }
            else
            {
                Entry.Logger.Info("信任挑战：找不到 Shame 卡模型，没能发牌");
            }

            resultPage = Page("SHAME");
        }
        else
        {
            resultPage = Page("NOTHING");
            Entry.Logger.Info($"信任挑战：{paidCount}/{total} 放了钱（未过半）→ 什么也没发生");
        }

        FinishAll(run, resultPage);
    }

    /// <summary>把每个玩家的事件实例都用同一段结果文案收尾。</summary>
    private static void FinishAll(IRunState run, LocString resultPage)
    {
        try
        {
            var sync = RunManager.Instance?.EventSynchronizer;
            foreach (var player in run.Players)
            {
                if (sync?.GetEventForPlayer(player) is not TrustChallengeEvent instance)
                    continue;

                if (!instance.IsFinished)
                    instance.Finish(resultPage);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"信任挑战：收尾失败 {ex}");
        }
    }

    /// <summary>给外部结算用的包装（SetEventFinished 是 protected）。</summary>
    public void Finish(LocString resultPage) => SetEventFinished(resultPage);

    private LocString Page(string pageName) => L10NLookup($"{Id.Entry}.pages.{pageName}.description");

    /// <summary>把标题、开场白、两个选项和三段结果文案注入 events 表（当前语言）。</summary>
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
            var paidKey = InitialOptionKey("PAID");
            var bluffKey = InitialOptionKey("BLUFF");

            table.MergeWith(new Dictionary<string, string>
            {
                [$"{id}.title"] = chinese ? "信任挑战" : "Trust Challenge",
                [$"{id}.pages.INITIAL.description"] = chinese
                    ? "路边有个深不见底的石洞，洞壁上刻着字：「放钱进来，倘若同心者众，可得三倍回报；若只是虚晃一手，洞里空空如也。」"
                    : "A bottomless stone hole sits by the road. Carved words read: \"Pay in. If enough of you mean it, you'll be repaid threefold. Bluff, and nothing happens.\"",

                [$"{paidKey}.title"] = chinese ? "放入金币" : "Pay In",
                [$"{paidKey}.description"] = chinese
                    ? $"交出 [blue]{Cost}[/blue] 金币。只要超过一半的人真这么做，所有人各得 [blue]{Reward}[/blue] 金币。"
                    : $"Hand over {Cost} gold. If more than half of you truly do it, everyone gains {Reward} gold.",

                [$"{bluffKey}.title"] = chinese ? "假装放入" : "Bluff",
                [$"{bluffKey}.description"] = chinese
                    ? "装模作样地往里扔一把，其实一分钱也不出。如果所有人都这么做，每人会得到一张[red]「羞耻」[/red]。"
                    : "Pretend to toss coins in, paying nothing.",

                [$"{id}.pages.SUCCESS.description"] = chinese
                    ? $"石洞轰隆作响，金币像泉水一样涌出来——每人分到 [blue]{Reward}[/blue] 金币。"
                    : $"The hole rumbles and coins pour out — everyone gains {Reward} gold.",
                [$"{id}.pages.NOTHING.description"] = chinese
                    ? "石洞沉默不语。有人真心，有人装样，谁也没得到什么。"
                    : "The hole stays silent. Some meant it, some didn't, and nobody gained anything.",
                [$"{id}.pages.SHAME.description"] = chinese
                    ? "石洞看穿了你们。每个人手里都多了一张[red]「羞耻」[/red]。"
                    : "The hole sees through all of you. Everyone receives a Shame.",
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"信任挑战：本地化注入失败 {ex}");
        }
    }
}
