// ============================================================================
// 友尽尖塔（youjin）：火堆「偷取：遗物」
// ----------------------------------------------------------------------------
// 规则（用户定的）：
//   · 贪婪模式 + 多人局才出现；
//   · 点它 → 选一名队友 → **自动锁定他最近获得的那件遗物**（不用选，比偷牌简单）；
//   · 所有人选完后判定（和偷牌同一时机）：
//       受害者选 休息/治愈/酣睡 → 那件遗物从他身上移除、我获得一件同款；
//       否则                   → 我牌库加一张原版「内疚」；
//   · 先偷先得：按 NetId 升序，同一件已被拿走就空手；
//   · 不提示受害者。
//
// ★ 遗物转移的写法**直接抄本模组的「公平」的法官事件**（`event.cs` FairJudgeEvent，
//   已经跑通过的组合）：
//       await RelicCmd.Remove(taken);              // 从受害者移除
//       await RelicCmd.Obtain(fresh, me, -1);      // 给获得者一件同款（-1 = 追加到末尾）
//   "最近获得的"也照那套口径：`Relics.LastOrDefault(r => !r.IsMelted)`，
//   **熔掉的（IsMelted）不算**。
//
// ★ 为什么不复用偷牌那套消息：偷牌那条同步消息的语义是"牌在牌库里的下标"，
//   硬塞遗物会污染已验证的判定。这里单开一条 (thief, victim, relicIndex)，
//   互不干扰 —— 加代码比改已有代码安全。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;
using STS2RitsuLib.Networking.Sidecar;

namespace YouJin.Scripts;

/// <summary>火堆「偷取：遗物」的模型。</summary>
[RegisterSingleton]
public class StealRelicRestSiteModel : HookedSingletonModel
{
    public const string OptionId = "YOUJIN_STEALRELIC";

    private const string LocTableName = "rest_site_ui";

    public static StealRelicRestSiteModel? Instance { get; private set; }

    public StealRelicRestSiteModel() : base(HookType.Run)
    {
        Instance = this;
        EnsureLocalized();
        StealSync.Relic.Init();
    }

    public override Task AfterRoomEntered(AbstractRoom room)
    {
        if (room is RestSiteRoom)
            StealRelicVerdict.Reset();

        return Task.CompletedTask;
    }

    public override bool TryModifyRestSiteOptions(Player player, ICollection<RestSiteOption> options)
        => YouJinRestSiteOptions.TryAdd(player, options, OptionId, "偷遗物", () =>
        {
            EnsureLocalized();
            StealRelicVerdict.Reset();
            options.Add(new StealRelicRestSiteOption(player));
        });

    /// <summary>选玩家 → 自动锁定他最近获得的遗物。</summary>
    public async Task<bool> StealAsync(Player self, RestSiteOption option)
    {
        try
        {
            // 只让"这个选项真正的主人"弹界面（比 NetId，不用 IsMe —— 那玩意儿在火堆里不可靠）
            var localNetId = LocalContext.GetMe(self.RunState?.Players)?.NetId;
            if (localNetId is null || localNetId != self.NetId)
            {
                Entry.Logger.Info($"偷遗物：跳过（主人 {self.NetId}，本机 {localNetId}）");
                return true;
            }

            var victim = await TeammatePicker.ChooseLocalAsync(self, option);
            if (victim is null)
            {
                Entry.Logger.Info("偷遗物：没选目标（取消）");
                return false;
            }

            var relics = victim.Relics;
            if (relics is null || relics.Count == 0)
            {
                Entry.Logger.Info($"偷遗物：NetId={victim.NetId} 身上没有遗物，本次不结算");
                return false;
            }

            // 和法官事件同一口径：熔掉的不算，取最后一件
            var index = -1;
            for (var i = relics.Count - 1; i >= 0; i--)
            {
                if (!relics[i].IsMelted)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                Entry.Logger.Info($"偷遗物：NetId={victim.NetId} 的遗物全熔了，本次不结算");
                return false;
            }

            StealRelicVerdict.NoteChoice(self.NetId, victim.NetId, index);
            StealSync.Relic.Broadcast(self.NetId, victim.NetId, index);

            Entry.Logger.Info(
                $"偷遗物：NetId={self.NetId} 盯上了 NetId={victim.NetId} 的第 {index} 件 [{relics[index]}]");

            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷遗物：流程出错 {ex}");
            return false;
        }
    }

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
                [$"OPTION_{OptionId}.name"] = chinese ? "偷取：遗物" : "Steal: Relic",
                [$"OPTION_{OptionId}.description"] = chinese
                    ? "偷取一名玩家一件遗物，失败获得一张[blue]内疚[/blue]。"
                    : "Steal a player's most recently obtained relic. On failure, gain a [blue]Guilt[/blue].",
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷遗物：本地化注入失败 {ex}");
        }
    }
}

/// <summary>火堆里的「偷取：遗物」按钮。</summary>
public class StealRelicRestSiteOption : YouJinRestSiteOption
{
    public StealRelicRestSiteOption(Player owner) : base(owner)
    {
    }

    public override string OptionId => StealRelicRestSiteModel.OptionId;

    protected override async Task<bool> ActivateAsync()
    {
        var model = StealRelicRestSiteModel.Instance;
        return model is not null && await model.StealAsync(Owner, this);
    }
}

/// <summary>偷遗物的选择 + 判定。</summary>
internal static class StealRelicVerdict
{
    private static readonly Dictionary<ulong, (ulong Victim, int RelicIndex)> Choices = new();
    private static readonly Dictionary<ulong, RestSiteOption> Chosen = new();
    private static readonly object Gate = new();

    private static bool _resolved;
    private static object? _marker;

    public static void Reset()
    {
        lock (Gate)
        {
            if (ReferenceEquals(_marker, MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom.Instance)) return;
            _marker = MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom.Instance;
            Choices.Clear();
            Chosen.Clear();
            _resolved = false;
        }
    }

    public static void NoteChoice(ulong thiefNetId, ulong victimNetId, int relicIndex)
    {
        lock (Gate)
            Choices[thiefNetId] = (victimNetId, relicIndex);
    }

    public static void NoteChosen(ulong netId, RestSiteOption? option)
    {
        lock (Gate)
        {
            if (option is not null)
                Chosen[netId] = option;
        }
    }

    /// <summary>由偷牌的 NoteChosen 在"所有人都选完"时调用。</summary>
    public static async Task ResolveIfAnyAsync()
    {
        List<(ulong Thief, ulong Victim, int Index)> ordered;
        Dictionary<ulong, RestSiteOption> chosenSnapshot;

        lock (Gate)
        {
            if (_resolved)
                return;

            // ★ 守卫放函数内部：没选完就不结算（调用方可能被插了多份，不能信任它）
            var playersNow = RunManager.Instance?.DebugOnlyGetState()?.Players;
            var totalNow = playersNow?.Count ?? 0;
            if (totalNow <= 0 || Chosen.Count < totalNow)
                return;

            if (Choices.Count == 0)
            {
                _resolved = true;
                return;
            }

            ordered = Choices.OrderBy(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value.Victim, kv.Value.RelicIndex))
                .ToList();
            chosenSnapshot = new Dictionary<ulong, RestSiteOption>(Chosen);
            _resolved = true;
        }

        try
        {
            var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
            if (players is null)
                return;

            foreach (var (thiefNetId, victimNetId, relicIndex) in ordered)
            {
                var thief = players.FirstOrDefault(p => p.NetId == thiefNetId);
                var victim = players.FirstOrDefault(p => p.NetId == victimNetId);
                if (thief is null || victim is null)
                    continue;

                var rested = IsRestedOption(chosenSnapshot.GetValueOrDefault(victimNetId));
                var victimOpt = chosenSnapshot.GetValueOrDefault(victimNetId);
                Entry.Logger.Info($"偷遗物：受害者 NetId={victimNetId} 选了 [{victimOpt?.OptionId}] → rested={rested}");

                if (!rested)
                {
                    await GiveGuiltAsync(thief, thiefNetId);
                    continue;
                }

                var relics = victim.Relics;
                if (relics is null || relicIndex < 0 || relicIndex >= relics.Count)
                {
                    Entry.Logger.Info($"偷遗物：索引 {relicIndex} 越界，跳过");
                    continue;
                }

                var taken = relics[relicIndex];
                var relicId = ModelDb.GetId(taken.GetType());
                var beforeCount = victim.Relics?.Count ?? -1;

                await RelicCmd.Remove(taken);

                var afterCount = victim.Relics?.Count ?? -1;
                Entry.Logger.Info($"偷遗物：受害者遗物 {beforeCount} → {afterCount} 件");

                // ★ 移除没生效就**不给遗物** —— 否则会出现"两边都有"
                //   （用户实测的"只增加不减少"就是复制了）。
                if (beforeCount >= 0 && afterCount >= beforeCount)
                {
                    Entry.Logger.Info(
                        $"偷遗物：RelicCmd.Remove 没生效（{beforeCount} → {afterCount}），为避免复制本次不给遗物");
                    continue;
                }

                var fresh = ModelDb.GetByIdOrNull<RelicModel>(relicId);
                if (fresh is null)
                {
                    Entry.Logger.Info($"偷遗物：找不到 {relicId.Entry} 的规范实例，没能补发");
                    continue;
                }

                // ★ 给的是**可变副本**，不是标准实例 ——
                //   当初塞 Guilty 就是栽在 CanonicalModelException（标准模型不能进局内）。
                var giveCount = thief.Relics?.Count ?? -1;

                try
                {
                    await RelicCmd.Obtain(fresh.ToMutable(), thief, -1);
                }
                catch (Exception ex)
                {
                    Entry.Logger.Info($"偷遗物：Obtain 抛异常 {ex}");
                }

                var nowCount = thief.Relics?.Count ?? -1;
                Entry.Logger.Info($"偷遗物：受害者已失去，小偷遗物 {giveCount} → {nowCount} 件（期望 +1）");

                if (giveCount >= 0 && nowCount <= giveCount)
                {
                    Entry.Logger.Info(
                        $"偷遗物：Obtain 没生效（{giveCount} → {nowCount}）—— 这段跑在火堆结束的游离 Task 里，" +
                        "可能不在动作上下文中；下次试「延迟到下一房间入口再发」");
                }
                else
                {
                    Entry.Logger.Info(
                        $"偷遗物：{relicId.Entry} 从 NetId={victimNetId} 被夺走，NetId={thiefNetId} 获得同款");
                }
            }

            Entry.Logger.Info("偷遗物：本次火堆结算完毕");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷遗物：结算出错 {ex}");
        }
    }

    private static async Task GiveGuiltAsync(Player thief, ulong thiefNetId)
    {
        try
        {
            await CardPileCmd.AddCurseToDeck<Guilty>(thief);
            Entry.Logger.Info($"偷遗物：NetId={thiefNetId} 的牌库里多了一张「内疚」");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"偷遗物：塞内疚失败 {ex}");
        }
    }

    private static bool IsRestedOption(RestSiteOption? option)
        => option is HealRestSiteOption
           or MendRestSiteOption
           || option?.OptionId == DeepSleepRestSiteModel.OptionId;
}

