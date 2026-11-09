// ============================================================================
// 友尽尖塔（youjin）：事件「公平」的法官
// ----------------------------------------------------------------------------
// 规则：
//   1. 只有多人局（>=2 人）才会抽到，并且只挂在二号层（Hive）和三号层（Glory）的事件池里；
//   2. 三个选项：
//      ① 我的金币被偷了 —— 只有「金币最少」的玩家能选：从最有钱的玩家身上扣钱补给他，
//         直到两人金币数相等（转移 (最多-最少)/2）；
//      ② 我的遗物被抢了 —— 只有「遗物最少」的玩家能选：把遗物最多的玩家
//         **最近获得的那件遗物**判给他；
//      ③ 我的团队和谐友爱 —— 无条件可选：拿 30 金币。
//
// 关于这套事件 API（都是反编译确认过的）：
//   · [RegisterActEvent(typeof(某层))] 把事件塞进那一层的事件池；
//     各层 ActModel.Index：Overgrowth / Underdocks = 0（一层）、Hive = 1（二层）、Glory = 2（三层）。
//   · 选项的灰/亮：EventOption 的构造函数里 IsLocked = (OnChosen == null)，
//     所以条件不满足时把回调传 null，原版就会把它显示成灰色不可点。
//   · 文本：EventModel.LocTable 默认是 "events" 表。
//       标题     = {Id}.title
//       开场白   = {Id}.pages.INITIAL.description
//       选项     = InitialOptionKey("XXX")，再拼 .title / .description
//       结束语   = {Id}.pages.RESULT.description
//     这些键在运行时注入 events 表，不用重新导出 PCK。
//   · 选择是每个客户端都会执行的（EventSynchronizer 广播选项下标），
//     所以跨玩家改钱、改遗物是同步的。
//   · Player.Relics 是按获得顺序追加的（AddRelicInternal 里 index == -1 走 Add），
//     所以「最近获得的遗物」= 最后一个没熔毁的。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
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

/// <summary>多人二层以后的随机事件「公平」的法官。</summary>
[RegisterActEvent(typeof(Hive))]  // 二层
[RegisterActEvent(typeof(Glory))] // 三层
public sealed class FairJudgeEvent : ModEventTemplate
{
    /// <summary>「和谐友爱」给的金币。</summary>
    public const int HarmonyGold = 30;

    /// <summary>
    /// 素材整套套用原版事件「审判」(Trial)：立绘 / 背景 / 特效都按原版那套 id 规则取。
    /// 原版路径规则：立绘 res://images/events/{id}.png、
    /// 背景 res://scenes/events/background_scenes/{id}.tscn、
    /// 特效 res://scenes/vfx/events/{id}_vfx.tscn（不存在也不会炸，HasVfx 会返回 false）。
    /// 布局留空 → 用原版默认事件布局。
    /// </summary>
    public override EventAssetProfile AssetProfile => EventArt.Resolve(
        "fair_judge",
        ImageHelper.GetImagePath("events/trial.png"),
        SceneHelper.GetScenePath("events/background_scenes/trial"),
        SceneHelper.GetScenePath("vfx/events/trial_vfx"));

    /// <summary>只有多人局才会抽到这个事件（单人的话选项①②也没意义）。</summary>
    public override bool IsAllowed(IRunState runState)
        => YouJinSettings.GameplayEnabled && runState.Players.Count > 1;

    /// <summary>
    /// 三个选项。条件不满足的两个把回调传 null —— 原版会把这种选项显示成灰色不可点，
    /// 正是"只有钱最少/遗物最少的人能选"需要的效果。
    /// </summary>
    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        EnsureLocalized();

        Func<Task>? goldHandler = CanClaimGold() ? ClaimGold : null;
        Func<Task>? relicHandler = CanClaimRelic() ? ClaimRelic : null;

        return new List<EventOption>
        {
            new(this, goldHandler, InitialOptionKey("GOLD"), Array.Empty<IHoverTip>()),
            new(this, relicHandler, InitialOptionKey("RELIC"), Array.Empty<IHoverTip>()),
            new(this, TeamHarmony, InitialOptionKey("HARMONY"), Array.Empty<IHoverTip>()),
        };
    }

    /// <summary>自己是钱最少的那位，而且确实有人比自己有钱 → 选项①可点。</summary>
    private bool CanClaimGold()
    {
        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return false;

        var richest = OtherPlayers(run, me).OrderByDescending(player => player.Gold).FirstOrDefault();
        return richest is not null && richest.Gold > me.Gold;
    }

    /// <summary>自己是遗物最少的那位，而且有人遗物更多并且有遗物可抢 → 选项②可点。</summary>
    private bool CanClaimRelic()
    {
        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return false;

        var richest = OtherPlayers(run, me).OrderByDescending(RelicCount).FirstOrDefault();
        return richest is not null
               && RelicCount(richest) > RelicCount(me)
               && NewestRelic(richest) is not null;
    }

    /// <summary>选项①：从最有钱的玩家那里补钱，直到两人一样多。</summary>
    private async Task ClaimGold()
    {
        if (!YouJinSettings.GameplayEnabled)
            return;

        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return;

        // 并列最有钱时随机挑一个
        var richest = PickTied(OtherPlayers(run, me), player => player.Gold, wantMax: true);
        if (richest is not null)
        {
            var before = (Me: me.Gold, Rich: richest.Gold);
            var amount = (richest.Gold - me.Gold) / 2; // 向下取整，结束后差 0 或 1
            if (amount > 0)
            {
                await PlayerCmd.LoseGold(amount, richest, GoldLossType.Lost);
                await PlayerCmd.GainGold(amount, me, false);
                Entry.Logger.Info(
                    $"法官-金币：NetId={me.NetId} 得到 {amount} 金币补偿（{before.Me}→{me.Gold}，{before.Rich}→{richest.Gold}）");
            }
        }

        SetEventFinished(ResultText());
    }

    /// <summary>
    /// 选项②：拿走遗物最多的玩家最近获得的那件遗物，并给自己一件**同款全新遗物**。
    /// 注意不能把原物转手：原版 RelicModel.set_Owner 明确禁止"把已有主人的遗物改主人"
    /// （会抛 InvalidOperationException: Cannot move relic from XXX one owner to another），
    /// 所以流程是：受害者那边 Remove 掉，然后按同一个 ModelId 从规范实例复制一份新的给自己。
    /// </summary>
    private async Task ClaimRelic()
    {
        if (!YouJinSettings.GameplayEnabled)
            return;

        var me = Owner;
        var run = me?.RunState;
        if (me is null || run is null)
            return;

        // 并列遗物最多时随机挑一个
        var victim = PickTied(OtherPlayers(run, me), RelicCount, wantMax: true);
        var taken = victim is null ? null : NewestRelic(victim);
        if (victim is not null && taken is not null)
        {
            var relicId = taken.Id;
            await RelicCmd.Remove(taken);

            var canonical = ModelDb.GetByIdOrNull<RelicModel>(relicId);
            var fresh = canonical?.ToMutable();
            if (fresh is not null)
            {
                await RelicCmd.Obtain(fresh, me, -1);
                Entry.Logger.Info(
                    $"法官-遗物：{relicId.Entry} 从 NetId={victim.NetId} 被夺走，NetId={me.NetId} 获得一件同款新遗物");
            }
            else
            {
                Entry.Logger.Info($"法官-遗物：找不到 {relicId.Entry} 的规范实例，没能补发");
            }
        }

        SetEventFinished(ResultText());
    }

    /// <summary>
    /// 在一组玩家里按某个数值挑目标；并列最高/最低时用本局 RNG 随机挑一个
    /// （联机各端 RNG 同步，结果一致）。
    /// </summary>
    private Player? PickTied(IEnumerable<Player> pool, Func<Player, int> value, bool wantMax)
    {
        var list = pool.ToList();
        if (list.Count == 0)
            return null;

        var target = wantMax ? list.Max(value) : list.Min(value);
        var tied = list.Where(player => value(player) == target).ToList();
        if (tied.Count == 1)
            return tied[0];

        // ⚠ 同上：用具名属性 Niche（0.107 / 0.111 双版本通用）
        var rng = Owner?.RunState?.Rng?.Niche;
        var index = rng is not null ? rng.NextInt(0, tied.Count) : 0;
        return tied[index];
    }

    /// <summary>选项③：大家都很有爱，拿 10 金币走人。</summary>
    private async Task TeamHarmony()
    {
        if (!YouJinSettings.GameplayEnabled)
            return;

        var me = Owner;
        if (me is null)
            return;

        await PlayerCmd.GainGold(HarmonyGold, me, false);
        Entry.Logger.Info($"法官-和谐：NetId={me.NetId} 获得 {HarmonyGold} 金币");
        SetEventFinished(ResultText());
    }

    private static IEnumerable<Player> OtherPlayers(IRunState run, Player me)
        => run.Players.Where(player => player.NetId != me.NetId);

    /// <summary>遗物数量（不算已熔毁的）。</summary>
    private static int RelicCount(Player player)
        => player.Relics.Count(relic => !relic.IsMelted);

    /// <summary>最近获得的遗物：列表按获得顺序追加，取最后一个没熔毁的。</summary>
    private static RelicModel? NewestRelic(Player player)
        => player.Relics.LastOrDefault(relic => !relic.IsMelted);

    private LocString ResultText() => L10NLookup($"{Id.Entry}.pages.RESULT.description");

    /// <summary>把标题、开场白、三个选项、结束语的文本注入 events 表（当前语言）。</summary>
    private void EnsureLocalized()
    {
            var id = Id.Entry;
            var goldKey = InitialOptionKey("GOLD");
            var relicKey = InitialOptionKey("RELIC");
            var harmonyKey = InitialOptionKey("HARMONY");
        LocInject.Merge(LocTable, $"{id}.title", chinese => new Dictionary<string, string>
        {
            [$"{id}.title"] = chinese ? "「公平」的法官" : "The \"Fair\" Judge",
            [$"{id}.pages.INITIAL.description"] = chinese
                ? "一位自称绝对公平的法官拦住去路。他眯着眼打量你们的钱袋和背包，似乎在盘算该怎么「主持公道」。"
                : "A self-proclaimed absolutely fair judge blocks your path, eyeing your coin purses and packs.",

            [$"{goldKey}.title"] = chinese ? "我的金币被偷了" : "My Gold Was Stolen",
            [$"{goldKey}.description"] = chinese
                ? "只有[blue]金币[/blue]最少的人能选：从最有钱的玩家那里扣钱补给你，直到两人金币数相同。"
                : "Only the poorest may choose: take from the richest until both are equal.",

            [$"{relicKey}.title"] = chinese ? "我的遗物被抢了" : "My Relic Was Robbed",
            [$"{relicKey}.description"] = chinese
                ? "只有[gold]遗物[/gold]最少的人能选：夺走遗物最多的玩家最近获得的那件[gold]遗物[/gold]，"
                  + "并补给你一件同款新遗物（他会失去它）。"
                : "Only the player with the fewest relics may choose: claim the most recent relic of the one with the most.",

            [$"{harmonyKey}.title"] = chinese ? "我的团队和谐友爱" : "My Team Is Harmonious",
            [$"{harmonyKey}.description"] = chinese
                ? $"法官很满意，赏你 [blue]{HarmonyGold}[/blue] 金币。"
                : $"The judge is pleased and awards you {HarmonyGold} gold.",

            [$"{id}.pages.RESULT.description"] = chinese
                ? "法官敲下法槌，宣布这很公平。（究竟发生了什么，你们心知肚明。）"
                : "The judge bangs the gavel and declares this fair. (You both know what really happened.)",
        }, "法官事件");
    }
}
