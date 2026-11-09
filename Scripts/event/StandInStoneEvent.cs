// ============================================================================
// 友尽尖塔（youjin）：事件「替身之石」
// ----------------------------------------------------------------------------
// 规则（多人，二/三层随机）：
//   1. 你遇到一尊人形石像；
//   2. 选项一「替身」：从自己牌组里选一张诅咒（**永恒诅咒也能选**），再选一名队友，
//      你把它删掉，队友牌组里多一张一模一样的 —— 让他替你受过；
//   3. 选项二「捡钱」：捡走石像周围的 57 金币；
//   4. 素材套用原版事件「沉没雕像 SunkenStatue」。
//
// 反编译踩点：
//   · 选牌：用 CardSelectCmd.FromDeckGeneric(player, prefs, filter, sortingOrder)。
//     它是**中性**的卡组选择，不像 CardSelectCmd.FromDeckForRemoval（原版"删牌服务"专用）
//     那样会按"不可移除"规则把永恒诅咒滤掉 —— 所以这里能选到永恒诅咒。
//     FromDeckGeneric 的第 4 个参数只是排序键（Func<CardModel,int>），随便给个 0 就行。
//   · 选队友：用 PlayerTargeting.ChooseTeammateAsync（和火堆「治愈」同一套指向选择）。
//   · 给队友加牌：CardPileCmd.AddCursesToDeck(new[]{ 规范实例 }, 队友)，内部会给那个玩家
//     造一份独立实例并放进牌组（原版就要求卡必须是 CardType.Curse，我们选的本来就是诅咒）。
//   · 素材路径规则同其它事件：立绘 res://images/events/{id}.png、
//     背景 res://scenes/events/background_scenes/{id}.tscn、特效 res://scenes/vfx/events/{id}_vfx.tscn。
//     优先用 youjin/images|scenes/events/stand_in_stone* 里的自定义素材（见素材说明.md）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.CardSelection;
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
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>多人事件「替身之石」。</summary>
[RegisterActEvent(typeof(Hive))]  // 二层
[RegisterActEvent(typeof(Glory))] // 三层
public sealed class StandInStoneEvent : ModEventTemplate
{
    /// <summary>选项二捡到的金币。</summary>
    public const int GoldAmount = 57;

    /// <summary>多人局才会出现（选项①需要一名队友接盘）。</summary>
    public override bool IsAllowed(IRunState runState)
        => YouJinSettings.GameplayEnabled && runState.Players.Count > 1;

    /// <summary>素材套用原版「沉没雕像」；有自己放的图就用自己的。</summary>
    public override EventAssetProfile AssetProfile => EventArt.Resolve(
        "stand_in_stone",
        ImageHelper.GetImagePath("events/sunken_statue.png"),
        SceneHelper.GetScenePath("events/background_scenes/sunken_statue"),
        SceneHelper.GetScenePath("events/vfx/sunken_statue_vfx"));

    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        EnsureLocalized();

        Func<Task>? shiftHandler = CanShiftCurse() ? ShiftCurseAsync : null;

        return new List<EventOption>
        {
            new(this, shiftHandler, InitialOptionKey("SHIFT"), Array.Empty<IHoverTip>()),
            new(this, TakeGoldAsync, InitialOptionKey("GOLD"), Array.Empty<IHoverTip>()),
        };
    }

    /// <summary>自己牌组里有诅咒、并且有队友 → 选项①可点。</summary>
    private bool CanShiftCurse()
    {
        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return false;

        if (run.Players.Count <= 1)
            return false;

        return me.Deck.Cards.Any(card => card.Type == CardType.Curse);
    }

    /// <summary>选项一：把自己的诅咒挪给一名队友。</summary>
    private async Task ShiftCurseAsync()
    {
        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null || !YouJinSettings.GameplayEnabled)
            return;

        // 1) 选一张诅咒（中性选择，永恒诅咒也在列表里）
        var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1)
        {
            Cancelable = true,
            RequireManualConfirmation = true,
        };

        var chosen = await CardSelectCmd.FromDeckGeneric(
            me,
            prefs,
            card => card.Type == CardType.Curse,
            _ => 0);

        var curse = chosen?.FirstOrDefault();
        if (curse is null)
        {
            Entry.Logger.Info("替身之石：没有选择诅咒，无事发生");
            SetEventFinished(Page("NOTHING"));
            return;
        }

        var curseId = curse.Id;

        // 2) 选一名队友接盘
        var target = await PlayerTargeting.ChooseTeammateAsync(me);
        if (target is null)
        {
            Entry.Logger.Info("替身之石：没有选定队友，无事发生");
            SetEventFinished(Page("NOTHING"));
            return;
        }

        // 3) 自己删掉，对方拿一张一模一样的
        await CardPileCmd.RemoveFromDeck(curse, false);

        var canonical = ModelDb.GetByIdOrNull<CardModel>(curseId);
        if (canonical is not null)
            await CardPileCmd.AddCursesToDeck(new[] { canonical }, target);

        Entry.Logger.Info(
            $"替身之石：NetId={me.NetId} 把诅咒 {curseId.Entry} 转给了 NetId={target.NetId}");

        SetEventFinished(Page("SUCCESS"));
    }

    /// <summary>选项二：捡走石像周围的钱。</summary>
    private async Task TakeGoldAsync()
    {
        var me = Owner;
        if (me is null || !YouJinSettings.GameplayEnabled)
            return;

        await PlayerCmd.GainGold(GoldAmount, me, false);
        Entry.Logger.Info($"替身之石：NetId={me.NetId} 捡到 {GoldAmount} 金币");
        SetEventFinished(Page("GOLD"));
    }

    private LocString Page(string pageName) => L10NLookup($"{Id.Entry}.pages.{pageName}.description");

    /// <summary>标题 / 开场 / 两个选项 / 两段结果文案注入 events 表。</summary>
    private void EnsureLocalized()
    {
            var id = Id.Entry;
            var shiftKey = InitialOptionKey("SHIFT");
            var goldKey = InitialOptionKey("GOLD");
        LocInject.Merge(LocTable, $"{id}.title", chinese => new Dictionary<string, string>
        {
            [$"{id}.title"] = chinese ? "替身之石" : "The Stand-In Stone",
            [$"{id}.pages.INITIAL.description"] = chinese
                ? "路旁立着一尊人形石像，脚下散落着一些钱币。雕像胸口刻着一行字：「把痛苦推给别人，也是一种活法。」"
                : "A humanoid statue stands by the road, coins scattered at its feet. Words are carved on its chest: \"Pushing your pain onto someone else is also a way to live.\"",

            [$"{shiftKey}.title"] = chinese ? "触碰石像" : "Touch the Stone",
            [$"{shiftKey}.description"] = chinese
                ? "从自己牌组里选一张[red]诅咒[/red]（永恒诅咒也行）交给一名队友：你失去它，他得到一张一模一样的。"
                : "Give one of your Curses (Eternal ones included) to a teammate: you lose it, they gain an identical copy.",

            [$"{goldKey}.title"] = chinese ? "捡起钱币" : "Take the Coins",
            [$"{goldKey}.description"] = chinese
                ? $"捡走石像周围散落的 [blue]{GoldAmount}[/blue] 金币。"
                : $"Pick up the {GoldAmount} gold scattered around the statue.",

            [$"{id}.pages.SUCCESS.description"] = chinese
                ? "石像发出一声低笑。你身上的[red]诅咒[/red]已经转到别人身上——他大概还不知道。"
                : "The statue chuckles. Your curse now belongs to someone else — who probably doesn't know yet.",
            [$"{id}.pages.GOLD.description"] = chinese
                ? "你把石像脚下的钱币收进兜里，石像的表情似乎更阴沉了。"
                : "You pocket the gold at the statue's feet. Its expression seems to darken.",
            [$"{id}.pages.NOTHING.description"] = chinese
                ? "石像纹丝不动。什么也没发生。"
                : "The statue stays still. Nothing happens.",
        }, "替身之石");
    }
}
