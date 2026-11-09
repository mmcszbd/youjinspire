// ============================================================================
// 友尽尖塔（youjin）：火堆「偷窃·看牌库」（第 1 步：只验证链路）
// ----------------------------------------------------------------------------
// 规则（用户定的，已钉死）：
//   · 贪婪模式 + 多人局才出现「偷窃」；
//   · 点它 → 选一名队友 → **打开他的牌库** → 点一张牌当目标；
//   · 所有人选完后判定：被偷的人如果**休息/治愈/酣睡** → 那张牌从他被删、转到我手里；
//     否则 → 我牌库里多一张「内疚」（原版 `Cards.Guilty`）；
//   · **先偷的得手**，后偷同一张的人**什么都拿不到**；
//   · 范围 = 整个牌库；不提示受害者。
//
// ★ 本次（第 1 步）只做前半段，用来验证"能不能打开别人的牌库并点中一张牌"：
//     选项 → 选玩家 → NDeckViewScreen.ShowScreen(受害者) → 点牌记日志
//   判定 / 转移 / 内疚 / 同步是第 2 步。
//
// ----------------------------------------------------------------------------
// 复用与反编译依据（全部确认过）：
//   · **选玩家**：直接复用 `NapRestSiteModel.ChooseTargetPlayerAsync`（假寐已跑通），
//     内部是 NTargetManager 指向选择 + PlayerChoiceSynchronizer 三件套。
//   · **看别人牌库**：`NDeckViewScreen.ShowScreen(Player)` —— **public static**，
//     就是点小头像打开的那个界面，传任意玩家都行。
//   · **拿到点了哪张牌**：`NCardsViewScreen.ShowCardDetail(CardModel)` 是 **protected**，
//     Harmony 能加 Prefix → 左键点牌时我们就拿到那张 CardModel。
//     （玩家夸的"左键能看详情"于是天然保留：点几张都行，**最后点的那张算数**。）
//   · **确认时机**：`NDeckViewScreen.AfterCapstoneClosed()`（public virtual）
//     —— 玩家按返回键关掉界面时提交。
//   · 图标：我们没自己的贴图，走 `RestSiteIconPatch` 换原版占位图
//     （否则头顶气泡拿 null 贴图会抛异常，把火堆流程打断 —— 见那个文件的说明）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rooms;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>火堆「偷窃」：看别人牌库、挑一张牌。第 1 步只记录不结算。</summary>
[RegisterSingleton]
public class StealCardRestSiteModel : HookedSingletonModel
{
    /// <summary>选项 id；本地化键是 OPTION_{OptionId}.name / .description。</summary>
    public const string OptionId = "YOUJIN_STEALCARD";

    private const string LocTableName = "rest_site_ui";

    public static StealCardRestSiteModel? Instance { get; private set; }

    public StealCardRestSiteModel() : base(HookType.Run)
    {
        Instance = this;
        EnsureLocalized();
    }

    /// <summary>进新房间时清状态（换火堆就重新来过）。</summary>
    public override Task AfterRoomEntered(AbstractRoom room)
    {
        // 兜底：正常情况下结算那一刻就转完了（见 RestSiteStealCardVerdict），这里只清理残留。
        _ = StealCardVerdict.ApplyPendingAsync();

        return Task.CompletedTask;
    }

    /// <summary>火堆选项生成时把「偷窃」塞进去（贪婪模式 + 多人局才加）。</summary>
    public override bool TryModifyRestSiteOptions(Player player, ICollection<RestSiteOption> options)
        => YouJinRestSiteOptions.TryAdd(player, options, OptionId, "偷牌", () =>
        {
            EnsureLocalized();
            StealCardVerdict.Reset();
            StealSync.Card.Init();  // 必须调：不调的话 descriptor 是 null，Broadcast 直接 return
            options.Add(new StealCardRestSiteOption(player));
        });

    // ── 第 1 步主流程 ───────────────────────────────────────────────────────

    /// <summary>选玩家 → 打开他的牌库 → 等他点一张牌。</summary>
    public async Task<bool> StealAsync(Player self, RestSiteOption option)
    {
        try
        {
            // ⚠ 这里**不要**加 LocalContext.IsMe(self) 守卫！
            //   0.9.26 加过一次，结果本机玩家也被挡住（选项的 Owner 不保证等于本机玩家），
            //   表现就是"点了偷窃但什么日志都没有"。
            //   NapRestSiteModel 从来没加守卫也一直正常 —— 原版 OnSelect 不会在别的客户端弹 UI，
            //   真正的本地/远程分流在 ChooseTargetPlayerAsync 内部的 IsMe 分支里。
            // ★ 只让这个选项真正的主人去弹界面（LocalContext.IsMe 在此不可靠，改比 NetId）。
            var allPlayers = self.RunState?.Players;
            var localNetId = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(allPlayers)?.NetId;
            // ★ 守卫必须在**弹界面之前**：非主人端必须立刻返回 true。
            //   否则两端都会弹选人界面 —— 主人端弹出的那个没人点，火堆描述面板停在收起状态 → 黑屏。
            //   （0.9.60 的黑屏就是这个：守卫被写在了 ChooseLocalAsync 后面。）
            if (localNetId is null || localNetId != self.NetId)
            {
                Entry.Logger.Info($"偷牌：非主人端，跳过界面（主人 {self.NetId}，本机 {localNetId}）");
                return true;
            }

            var victim = await TeammatePicker.ChooseLocalAsync(self, option);
            if (victim is null)
            {
                Entry.Logger.Info("偷牌：没选目标（取消），本次不结算");
                return false;
            }

            if (victim.NetId == self.NetId)
            {
                Entry.Logger.Info("偷牌：选中了自己，忽略");
                return false;
            }


            var card = await ChooseCardFromDeckUpgradeUiAsync(victim);
            if (card is null)
            {
                Entry.Logger.Info("偷牌：没选中牌（取消/超时），本次不结算");
                return false;
            }

            // 算这张牌在受害者牌库里的下标（两端一致，用它同步最省事）
            var index = -1;
            var deck = victim.Deck?.Cards;
            if (deck is not null)
            {
                for (var i = 0; i < deck.Count; i++)
                {
                    if (ReferenceEquals(deck[i], card))
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0)
            {
                Entry.Logger.Info($"偷牌：选中的 [{card}] 在受害者牌库里找不到（可能已被移除），本次不结算");
                return false;
            }

            StealCardVerdict.NoteChoice(self.NetId, victim.NetId, index);
            StealSync.Card.Broadcast(self.NetId, victim.NetId, index);

            Entry.Logger.Info(
                $"偷牌：NetId={self.NetId} 盯上了 NetId={victim.NetId} 的第 {index} 张 [{card}]");

            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷牌：流程出错 {ex}");
            return false;
        }
    }

    /// <summary>用原版「敲牌」选牌屏（NDeckUpgradeSelectScreen）从受害者牌库里选一张。</summary>
    private static async Task<CardModel?> ChooseCardFromDeckUpgradeUiAsync(Player victim)
    {
        try
        {
            var deck = victim.Deck?.Cards;
            if (deck is null || deck.Count == 0)
            {
                Entry.Logger.Info("偷牌：受害者牌库是空的");
                return null;
            }

            var run = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            if (run is null)
                return null;

            var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1, 1)
            {
                Cancelable = true,
                RequireManualConfirmation = true,
            };

            var screen = NDeckUpgradeSelectScreen.ShowScreen(deck, prefs, run);
            if (screen is null)
            {
                Entry.Logger.Info("偷牌：选牌屏 Create 返回 null");
                return null;
            }

            Entry.Logger.Info($"偷牌：已打开敲牌选牌屏（{victim.NetId} 的牌库，{deck.Count} 张）");

            var task = screen.CardsSelected();
            if (await Task.WhenAny(task, Task.Delay(180000)) != task)
            {
                Entry.Logger.Info("偷牌：选牌超时（界面可能没显示出来）");
                return null;
            }
            var picked = await task;
            var card = picked?.FirstOrDefault();

            Entry.Logger.Info(card is null ? "偷牌：取消了，没选牌" : $"偷牌：选中 [{card}]");
            return card;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷牌：敲牌界面出错 {ex}");
            return null;
        }
    }
    /// <summary>把「偷窃」的名字和描述注入原版 rest_site_ui 表（当前语言）。</summary>
    private static void EnsureLocalized()
    {
        try
        {
            var loc = LocManager.Instance;
            var table = loc?.GetTable(LocTableName);
            if (loc is null || table is null)
                return;

            if (table.HasEntry($"OPTION_{OptionId}.name"))
                return;

            var chinese = (loc.Language ?? string.Empty)
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            table.MergeWith(new Dictionary<string, string>
            {
                [$"OPTION_{OptionId}.name"] = chinese ? "偷窃" : "Steal",
                [$"OPTION_{OptionId}.description"] = chinese
                    ? "偷取一名玩家一张卡牌，失败获得一张[blue]内疚[/blue]。"
                    : "Steal a card from a player. On failure, gain a [blue]Guilt[/blue].",
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷牌：本地化注入失败 {ex}");
        }
    }

}

/// <summary>火堆里的「偷窃」按钮。</summary>
public class StealCardRestSiteOption : YouJinRestSiteOption
{
    public StealCardRestSiteOption(Player owner) : base(owner)
    {
    }

    public override string OptionId => StealCardRestSiteModel.OptionId;

    protected override async Task<bool> ActivateAsync()
    {
        var model = StealCardRestSiteModel.Instance;
        return model is not null && await model.StealAsync(Owner, this);
    }
}
