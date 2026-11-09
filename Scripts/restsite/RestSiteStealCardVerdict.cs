// ============================================================================
// 友尽尖塔（youjin）：火堆「偷窃」第 2 步 —— 判定 + 转移 + 内疚 + 同步
// ----------------------------------------------------------------------------
// 规则（用户定的）：
//   · 所有人选完后逐个处理，**按 NetId 升序 = 先偷先得**；
//   · 目标牌已被别人拿走 → 什么都拿不到（不给内疚）；
//   · 受害者选的是「休息 / 治愈 / 酣睡」→ 那张牌从受害者牌库删掉、转给小偷；
//   · 受害者选了别的 → 小偷牌库加一张原版 `Cards.Guilty`（内疚）；
//   · 范围 = 整个牌库；不提示受害者。
//
// 同步：选择结果用 RitsuLib Sidecar 广播（Reliable，20 字节），
//   载荷 = thiefNetId(8) + victimNetId(8) + cardIndex(4)。
//   ★ 传**索引**而不是牌本身：牌库内容/顺序两端一致，索引天然对齐。
//
// 判定时机：补 `NRestSiteRoom.OnAfterPlayerSelectedRestSiteOption(option, bool, netId)`
//   —— "X 选了 Y"的原版回调，每端都会收到；数到齐了立刻结算。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.Sidecar;

namespace YouJin.Scripts;

/// <summary>「偷窃」选择和执行的全部状态。</summary>
internal static class StealCardVerdict
{
    /// <summary>小偷 → (受害者, 牌在受害者牌库里的下标)。</summary>
    private static readonly Dictionary<ulong, (ulong Victim, int Index)> Choices = new();

    /// <summary>待转移队列（结算时只排队，下一房间入口才真正改牌库 —— 走同步管线，避免分叉）。</summary>
    private static readonly List<(ulong Thief, ulong Victim, int Index, string Entry)> Pending = new();

    private static readonly object Gate = new();

    /// <summary>每个玩家这次选的选项（netId → option）。</summary>
    private static readonly Dictionary<ulong, RestSiteOption> Chosen = new();

    private static bool _resolved;
    private static bool _resolving;
    private static object? _marker;

    /// <summary>换火堆 / 进新房间时清空。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            if (ReferenceEquals(_marker, MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom.Instance)) return;
            _marker = MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom.Instance;
            Choices.Clear();
            Chosen.Clear();
            _resolved = false;
            _resolving = false;
        }
    }

    /// <summary>记下一个偷窃选择（本地选择或远端广播来的一样走这里）。</summary>
    public static void NoteChoice(ulong thiefNetId, ulong victimNetId, int cardIndex)
    {
        lock (Gate)
            Choices[thiefNetId] = (victimNetId, cardIndex);
    }

    /// <summary>记下"某玩家选了某选项"，齐了就跑结算。</summary>
    public static void NoteChosen(ulong netId, RestSiteOption? option)
    {
        int total;
        int counted;
        bool ready;

        lock (Gate)
        {
            if (option is not null)
                Chosen[netId] = option;

            var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
            total = players?.Count ?? 0;
            counted = Chosen.Count;
            ready = !_resolved && !_resolving && total > 0 && counted >= total;

            StealRelicVerdict.NoteChosen(netId, option);

        if (ready)
                _resolving = true;
        }

        Entry.Logger.Info($"偷牌：NetId={netId} 选了 [{option?.OptionId}]（已选 {counted}/{total}）");

        StealRelicVerdict.NoteChosen(netId, option);

        if (ready)
            _ = ResolveAsync();
            _ = StealRelicVerdict.ResolveIfAnyAsync();
    }

    /// <summary>真正的结算（所有客户端都会跑同一份，纯确定）。</summary>
    private static async Task ResolveAsync()
    {
        try
        {
            var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
            if (players is null)
                return;

            // 快照：按 NetId 升序 = 先偷先得
            List<(ulong Thief, ulong Victim, int Index)> ordered;
            Dictionary<ulong, RestSiteOption> chosenSnapshot;

            lock (Gate)
            {
                ordered = Choices
                    .OrderBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value.Victim, kv.Value.Index))
                    .ToList();

                chosenSnapshot = new Dictionary<ulong, RestSiteOption>(Chosen);
            }

            if (ordered.Count == 0)
            {
                Entry.Logger.Info("偷牌：这次火堆没有人偷东西");
                return;
            }

            var claimed = new HashSet<(ulong, int)>();

            foreach (var (thiefNetId, victimNetId, index) in ordered)
            {
                var thief = players.FirstOrDefault(p => p.NetId == thiefNetId);
                var victim = players.FirstOrDefault(p => p.NetId == victimNetId);

                if (thief is null || victim is null)
                {
                    Entry.Logger.Info($"偷牌：找不到玩家（thief={thiefNetId} victim={victimNetId}），跳过");
                    continue;
                }

                var deck = victim.Deck?.Cards;
                if (deck is null || index < 0 || index >= deck.Count)
                {
                    Entry.Logger.Info($"偷牌：索引 {index} 越界（牌库 {deck?.Count ?? 0} 张），跳过");
                    continue;
                }

                var card = deck[index];
                var rested = IsRestedOption(chosenSnapshot.GetValueOrDefault(victimNetId));

                if (!rested)
                {
                    await GiveGuiltAsync(thief, thiefNetId);
                    continue;
                }

                if (!claimed.Add((victimNetId, index)))
                {
                    Entry.Logger.Info(
                        $"偷牌：NetId={thiefNetId} 想偷的 [{card}] 已被别人先拿走 → 什么也拿不到");
                    continue;
                }

                // ★★ 结算阶段**不改牌库**，只把"待转移"排进队列。
                //
                //   原因（0.9.52 实测）：`RemoveInternal` / `AddInternal` 是牌堆模型层的裸操作，
                //   绕过了游戏的同步记账管线 → 两端克隆出的复制品实例不同 → checksum 对账
                //   立刻 `StateDivergence` → 断线 → LocalCoopClone 控制管道失败 → 崩溃。
                //
                //   所以真正的转移推迟到**下一个房间入口**（`AfterRoomEntered`）再执行，
                //   那时是正常流程点，`CardPileCmd.*` 会走同步管线，两端一致。
                var entryId = ModelDb.GetId(card.GetType());
                Pending.Add((thiefNetId, victimNetId, index, entryId.Entry));

                Entry.Logger.Info(
                    $"偷牌：已排队待转移 —— NetId={thiefNetId} ← NetId={victimNetId} 的 {entryId.Entry}"
                    + $"（下标 {index}，立刻执行）");

                Entry.Logger.Info(
                    $"偷牌：NetId={thiefNetId} 从 NetId={victimNetId} 手里偷走了 [{card}]");
            }

            // ★ 结算完**立刻**执行转移（时机抄 TeammateTrade.TradeService.ApplyCommit）：
            //   不能等到下一房间 —— 下一房间常常就是战斗，`AfterRoomEntered` 触发时
            //   这场战斗的抽牌堆已经建好了，牌虽进了 `player.Deck.Cards`，这一场用不上，
            //   表现就是"转移成功但下次战斗没生效"。
            //   两端都在同一时刻（所有人选完）走到这里、输入相同 → 结果一致，不会分叉。
            await ApplyPendingAsync();
            Entry.Logger.Info("偷牌：本次火堆结算完毕");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷牌：结算出错 {ex}");
        }
        finally
        {
            lock (Gate)
            {
                _resolved = true;
                _resolving = false;
            }
        }
    }

    /// <summary>小偷牌库塞一张原版「内疚」。★ 必须克隆一份可变的，不能直接塞标准模型。</summary>
    private static async Task GiveGuiltAsync(Player thief, ulong thiefNetId)
    {
        try
        {
            var canonical = ModelDb.GetByIdOrNull<CardModel>(ModelDb.GetId(typeof(Guilty)));
            if (canonical is null)
            {
                Entry.Logger.Info("偷牌：拿不到 Guilty 模型，跳过");
                return;
            }

            // 参考 SpireStocks：塞牌用按类型的 AddCurseToDeck<T>，不要自己 Add 标准模型
            await CardPileCmd.AddCurseToDeck<Guilty>(thief);

            Entry.Logger.Info($"偷牌：NetId={thiefNetId} 的牌库里多了一张「内疚」");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷牌：塞内疚失败 {ex}");
        }
    }

    /// <summary>受害者这次选的是不是"休息/治愈/酣睡"（有治疗那一类）。用类型判断，不硬编码字符串。</summary>
    private static bool IsRestedOption(RestSiteOption? option)
        => option is HealRestSiteOption
           or MendRestSiteOption
           || option?.OptionId == DeepSleepRestSiteModel.OptionId;

/// <summary>跨端同步小偷的选择（RitsuLib Sidecar，Reliable）。</summary>
    /// <summary>在下一个房间入口执行排队的转移。用 CardPileCmd（会走同步记账），两端一致。</summary>
    public static async Task ApplyPendingAsync()
    {
        List<(ulong Thief, ulong Victim, int Index, string Entry)> batch;

        lock (Gate)
        {
            if (Pending.Count == 0)
                return;

            batch = new List<(ulong, ulong, int, string)>(Pending);
            Pending.Clear();
        }

        var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
        if (players is null)
            return;

        foreach (var (thiefNetId, victimNetId, _, entry) in batch)
        {
            try
            {
                var thief = players.FirstOrDefault(p => p.NetId == thiefNetId);
                var victim = players.FirstOrDefault(p => p.NetId == victimNetId);
                if (thief is null || victim is null || victim.Deck is null)
                    continue;

                var target = victim.Deck.Cards.FirstOrDefault(c => ModelDb.GetId(c.GetType()).Entry == entry);
                if (target is null)
                {
                    Entry.Logger.Info($"偷牌(转移)：受害者牌库里已找不到 {entry}，跳过");
                    continue;
                }

                var vBefore = victim.Deck.Cards.Count;
                var tBefore = thief.Deck?.Cards?.Count ?? -1;

                // ★ 抄 TeammateTrade.TradeService：牌堆 RemoveInternal(false) + RunState.RemoveCard
                var runState = RunManager.Instance?.DebugOnlyGetState();

                Entry.Logger.Info("偷牌(转移)：步骤1 牌堆移除");
                victim.Deck.RemoveInternal(target, false);
                Entry.Logger.Info($"偷牌(转移)：①后 受害者 {victim.Deck.Cards.Count} 张");

                Entry.Logger.Info("偷牌(转移)：步骤2 RunState.RemoveCard");
                runState?.RemoveCard(target);
                Entry.Logger.Info($"偷牌(转移)：②后 受害者 {victim.Deck.Cards.Count} 张");

                // 搬同一个实例（不克隆）；先登记到小偷名下，再塞进他的牌堆
                var copy = target;
                if (thief.Deck is { } thiefDeck)
                {
                    Entry.Logger.Info("偷牌(转移)：步骤3 RunState.AddCard");
                    runState?.AddCard(copy, thief);
                    Entry.Logger.Info($"偷牌(转移)：③后 小偷 {thief.Deck.Cards.Count} 张");

                    Entry.Logger.Info("偷牌(转移)：步骤4 牌堆添加");
                    thiefDeck.AddInternal(copy, -1, false);
                    Entry.Logger.Info($"偷牌(转移)：④后 小偷 {thief.Deck.Cards.Count} 张");
                }

                Entry.Logger.Info(
                    $"偷牌(转移)：{entry} 转移完成 —— 受害者 {vBefore} → {victim.Deck.Cards.Count} 张，"
                    + $"小偷 {tBefore} → {thief.Deck?.Cards?.Count ?? -1} 张");
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"偷牌(转移)：转移 {entry} 失败 {ex}");
            }
        }
    }
}

/// <summary>判定时机：某玩家选完一个火堆选项。</summary>
[HarmonyPatch(typeof(NRestSiteRoom), "OnAfterPlayerSelectedRestSiteOption")]
internal static class StealCardVerdictPatch
{
    private static void Postfix(RestSiteOption __0, bool __1, ulong __2)
    {
        try
        {
            if (!__1) return; // 取消/失败的选择不算
            StealCardVerdict.NoteChosen(__2, __0);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷牌：判定回调出错（忽略）{ex}");
        }
    }
}
