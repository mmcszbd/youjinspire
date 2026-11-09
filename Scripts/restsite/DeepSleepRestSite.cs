// ============================================================================
// 友尽尖塔（youjin）：火堆「酣睡」
// ----------------------------------------------------------------------------
// 规则：
//   1. 多人局的火堆选项，一开始就亮、随时能点；
//   2. 自己：回复 100% 生命（回满），并且最大生命 +5；
//   3. 代价：**随机一名队友**（不会是自己）的牌组里多一张原版诅咒牌「睡眠不佳」
//      （PoorSleep，不可打出+保留）——用本局 RNG 抽，联机各端结果一致；
//   4. 单人局不出现（没有队友可坑，纯收益太强）。
//
// 用到的原版接口（都确认过）：
//   · CreatureCmd.GainMaxHp(creature, 5) —— 加最大生命（同时会加等量当前生命）。
//   · CreatureCmd.Heal(creature, 差额, true) —— 回血，和原版火堆休息同一套调用。
//   · 原版就有 PoorSleep 这张卡（不可打出 + 保留的诅咒），
//     拿它的规范实例：ModelDb.GetByIdOrNull<CardModel>(ModelDb.GetId(typeof(PoorSleep)))。
//   · 塞进牌组：CardPileCmd.AddCursesToDeck(new[]{ curse }, player)
//     —— 内部会先校验"是不是诅咒"，然后 player.RunState.CreateCard(...) 造一份该玩家的实例，
//        再 Add(card, PileType.Deck, CardPilePosition.Bottom, ...)，自带卡牌飞入预览。
//   · 选项的灰/亮、标题/描述、图标机制和「假寐」一样：
//     标题 = rest_site_ui 表的 OPTION_{OptionId}.name；描述 = .description（运行时注入）；
//     图标 = ui/rest_site/option_{id小写}.png，靠 AssetCache 登记原版贴图当占位符。
// ============================================================================

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>火堆「酣睡」：自己回满血 + 最大生命 +5，随机一名队友吃一张「睡眠不佳」。</summary>
[RegisterSingleton]
public class DeepSleepRestSiteModel : HookedSingletonModel
{
    /// <summary>最大生命增加量。</summary>
    public const int MaxHpGain = 5;

    /// <summary>选项 id；本地化键是 OPTION_{OptionId}.name / .description。</summary>
    public const string OptionId = "YOUJIN_DEEPSLEEP";

    private const string LocTableName = "rest_site_ui";

    public static DeepSleepRestSiteModel? Instance { get; private set; }

    public DeepSleepRestSiteModel() : base(HookType.Run)
    {
        Instance = this;
        EnsureLocalized();
    }

    /// <summary>多人局才把这个选项加进火堆（加入时就是亮的）。</summary>
    public override bool TryModifyRestSiteOptions(Player player, ICollection<RestSiteOption> options)
        => YouJinRestSiteOptions.TryAdd(player, options, OptionId, "酣睡", () =>
        {
            EnsureLocalized();
            options.Add(new DeepSleepRestSiteOption(player));
        });

    /// <summary>
    /// 结算：先加最大生命，再回满，最后随机挑一名队友（不含自己）塞一张「睡眠不佳」。
    /// 所有客户端都会跑同样的逻辑，纯确定，不会不同步。
    /// </summary>
    public async Task DeepSleepAsync(Player self)
    {
        var run = self.RunState;
        if (run is null || self.Creature is null)
            return;

        // 1) 最大生命 +5（原版同时会把当前生命一起加 5）
        await CreatureCmd.GainMaxHp(self.Creature, MaxHpGain);

        // 2) 回满血（按加完上限之后的差额算，正好 100%）
        var missing = self.Creature.MaxHp - self.Creature.CurrentHp;
        if (missing > 0)
            await CreatureCmd.Heal(self.Creature, missing, true);

        Entry.Logger.Info(
            $"酣睡：NetId={self.NetId} 回满生命并 +{MaxHpGain} 最大生命（现在 {self.Creature.CurrentHp}/{self.Creature.MaxHp}）");

        // 3) 随机一名队友（不含自己）拿一张「睡眠不佳」
        var curse = ModelDb.GetByIdOrNull<CardModel>(ModelDb.GetId(typeof(PoorSleep)));
        if (curse is null)
        {
            Entry.Logger.Info("酣睡：找不到 PoorSleep 卡模型，跳过塞诅咒");
            return;
        }

        var candidates = run.Players.Where(player => player.NetId != self.NetId).ToList();
        if (candidates.Count == 0)
        {
            Entry.Logger.Info("酣睡：没有队友，跳过塞诅咒");
            return;
        }

        // ⚠ 用"具名属性"Niche，而不是 GetRng(RunRngType.Niche)：
        //    GetRng 在 0.107 里是 private（0.108+ 才公开），而 Niche 属性两个版本都有 ✓
        var rng = run.Rng?.Niche;
        var index = rng is not null ? rng.NextInt(0, candidates.Count) : 0;
        var victim = candidates[index];

        await CardPileCmd.AddCursesToDeck(new[] { curse }, victim);
        Entry.Logger.Info($"酣睡：随机选中队友 NetId={victim.NetId}，他的牌组里多了一张「睡眠不佳」");
    }

    /// <summary>把「酣睡」的名字和描述注入原版 rest_site_ui 表（当前语言）。</summary>
    private static void EnsureLocalized()
    {
        LocInject.Merge(LocTableName, $"OPTION_{OptionId}.name", chinese => new Dictionary<string, string>
        {
            [$"OPTION_{OptionId}.name"] = chinese ? "酣睡" : "Deep Sleep",
            [$"OPTION_{OptionId}.description"] = chinese
                ? $"回复至满血，最大生命 [blue]+{MaxHpGain}[/blue]；代价是随机一名队友的[gold]牌组[/gold]里"
                  + "多一张[red]「睡眠不佳」[/red]。"
                : $"Heal to full and gain {MaxHpGain} max HP, but one random teammate gets a Poor Sleep in their deck.",
        }, "酣睡");
    }
}

/// <summary>火堆里的「酣睡」按钮。</summary>
public class DeepSleepRestSiteOption : YouJinRestSiteOption
{
    public DeepSleepRestSiteOption(Player owner) : base(owner)
    {
    }

    public override string OptionId => DeepSleepRestSiteModel.OptionId;

    protected override async Task<bool> ActivateAsync()
    {
        if (DeepSleepRestSiteModel.Instance is not null)
            await DeepSleepRestSiteModel.Instance.DeepSleepAsync(Owner);

        return true;
    }

    // 标题/描述/图标都不能重写（基类这三样不是 virtual）：
    //   · 标题 = rest_site_ui 表里的 OPTION_{OptionId}.name，描述 = .description（模型里已注入）；
    //   · 图标 = ui/rest_site/option_{OptionId小写}.png —— 这个文件我们并没有，
    //     由 RestSiteIconPatch 把它改写成原版火堆贴图的真实路径，否则原版设置头顶气泡时会抛异常。
}
