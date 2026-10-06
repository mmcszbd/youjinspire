// ============================================================================
// 任务模式（Quest）：生成 + 读写 + 进度入口
// ----------------------------------------------------------------------------
// 阶段 1：每层开始给每个玩家生成 3 个互不相同的任务，并写进本局存档。
// 阶段 2 起，各种游戏钩子只要调用 QuestService.Report* 系列就能推进度。
//
// 生成用 RitsuLib 的「每人独立 RNG」：
//     RitsuLibFramework.GetModPlayerRng(player, modId, key)
// 它由局内种子 + 玩家 id 派生，所有客户端算出来一致 —— 阶段 5 的联机同步
// 主要靠这条确定性路径，而不是把结果发来发去。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace YouJin.Scripts.Quest;

/// <summary>任务系统的核心服务。</summary>
public static class QuestService
{
    /// <summary>本 mod 的存档槽位 id。</summary>
    private const string SlotId = "quest_book";

    /// <summary>每层生成几个任务。</summary>
    public const int QuestsPerAct = 3;

    private static readonly object Sync = new();
    private static readonly HashSet<QuestGoal> SeenGoals = new();
    private static PlayerRunSavedData<PlayerQuestBook>? _books;
    private static bool _registered;

    /// <summary>任务书发生变化（生成/进度更新）时触发，UI 用来刷新。</summary>
    public static event Action? Changed;

    /// <summary>在 Entry.Init 里调用一次：注册本局存档槽位。</summary>
    public static void Init()
    {
        lock (Sync)
        {
            if (_registered)
                return;

            try
            {
                _books = RunSavedDataStore
                    .For(Entry.ModId)
                    .RegisterPerPlayer(
                        SlotId,
                        () => new PlayerQuestBook(),
                        new RunSavedDataOptions
                        {
                            SchemaVersion = 1,
                            WritePolicy = RunSavedDataWritePolicy.WhenSet,
                            SyncLobbyOnChange = true,
                        });

                _registered = true;
                Entry.Logger.Info("任务模式：已注册局内存档槽位 quest_book（每人一份）");
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"任务模式：注册存档槽位失败 {ex}");
            }
        }
    }

    /// <summary>任务模式是否生效。</summary>
    public static bool Enabled => YouJinSettings.QuestModeEnabled;

    /// <summary>取某个玩家的任务书（没有就建一份空的并写回）。</summary>
    public static PlayerQuestBook? GetBook(Player? player)
    {
        if (player is null || _books is null)
            return null;

        try
        {
            var run = player.RunState as RunState;
            if (run is null)
                return null;

            if (_books.TryGet(run, player.NetId, out var book) && book is not null)
                return book;

            book = new PlayerQuestBook();
            _books.Set(run, player.NetId, book);
            return book;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：读取任务书失败 NetId={player?.NetId} {ex}");
            return null;
        }
    }

    /// <summary>把任务书写回存档。</summary>
    private static void SaveBook(Player player, PlayerQuestBook book)
    {
        try
        {
            if (_books is null || player.RunState is not RunState run)
                return;

            _books.Set(run, player.NetId, book);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：保存任务书失败 NetId={player.NetId} {ex}");
        }
    }

    /// <summary>
    /// 进入某一层时调用：确保这一局里每个玩家都有「本层 3 个任务」。
    /// 已经生成过就跳过（幂等 —— 读档、重连都安全）。
    /// </summary>
    public static void EnsureGeneratedForAct(RunState? run)
    {
        if (!Enabled || run is null)
            return;

        var actIndex = run.CurrentActIndex;
        foreach (var player in run.Players)
        {
            try
            {
                var book = GetBook(player);
                if (book is null)
                    continue;

                // 已经生成过就跳过；但如果存档里留着已经删掉的任务定义（id 找不到），
                // 说明是老版本的任务池，重新生成一份。
                if (book.ActIndex == actIndex
                    && book.Quests.Count == QuestsPerAct
                    && book.Quests.All(q => QuestPools.Find(q.DefId) is not null))
                    continue;

                Generate(player, book, actIndex);
                SaveBook(player, book);
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"任务模式：为 NetId={player.NetId} 生成任务失败 {ex}");
            }
        }

        Changed?.Invoke();
    }

    /// <summary>为某个玩家生成某一层的 3 个任务。</summary>
    private static void Generate(Player player, PlayerQuestBook book, int actIndex)
    {
        var rng = RitsuLibFramework.GetModPlayerRng(player, Entry.ModId, $"quests/act{actIndex}");

        var characterPool = QuestPools.ForCharacter(player);
        var generalPool = QuestPools.General.ToList();

        var picked = new List<QuestDef>();

        // 设计：每层至少给 1 个角色专属任务（如果这个角色有专属池）。
        if (characterPool.Count > 0)
        {
            var candidates = characterPool.ToList();
            rng.Shuffle(candidates);
            picked.Add(candidates[0]);
        }

        // 其余名额从通用池 + 角色池里随机补齐，保证 3 个互不相同。
        var rest = generalPool
            .Concat(characterPool)
            .Where(d => picked.All(p => p.Id != d.Id))
            .ToList();
        rng.Shuffle(rest);

        foreach (var def in rest)
        {
            if (picked.Count >= QuestsPerAct)
                break;

            picked.Add(def);
        }

        // 打乱展示顺序，让角色任务不一定排第一。
        rng.Shuffle(picked);

        book.ActIndex = actIndex;
        book.Quests = picked.Select(def => new QuestProgress
        {
            DefId = def.Id,
            ActIndex = actIndex,
            Target = Math.Max(1, def.TargetForAct(actIndex)),
            RewardAmount = Math.Max(1, def.RewardForAct(actIndex)),
            Progress = 0,
            Completed = false,
            RewardGranted = false,
        }).ToList();

        var characterKey = player.Character?.Id.Entry ?? "?";
        Entry.Logger.Info(
            $"任务模式：第 {actIndex + 1} 层为 NetId={player.NetId}（{characterKey}）生成任务 → "
            + string.Join(" | ", book.Quests.Select(q =>
            {
                var def = QuestPools.Find(q.DefId);
                return $"{q.DefId}({def?.Title ?? "?"}) 0/{q.Target}";
            })));
    }

    /// <summary>给某个玩家推进某类任务的进度。返回本次完成的任务。</summary>
    public static List<QuestProgress> Report(Player? player, QuestGoal goal, int amount)
    {
        var finished = new List<QuestProgress>();
        if (!Enabled || player is null || amount <= 0)
            return finished;

        try
        {
            // 每个统计口径第一次真正收到事件时打一行日志，方便确认钩子生效。
            lock (SeenGoals)
            {
                if (SeenGoals.Add(goal))
                {
                    Entry.Logger.Info(
                        $"任务模式：首次收到进度事件 goal={goal} amount={amount}"
                        + $"（NetId={player.NetId}）—— 钩子已生效");
                }
            }

            var book = GetBook(player);
            if (book is null)
                return finished;

            var changed = false;
            foreach (var quest in book.Quests)
            {
                if (quest.Completed)
                    continue;

                var def = QuestPools.Find(quest.DefId);
                if (def is null || def.Goal != goal)
                    continue;

                changed = true;
                if (quest.AddProgress(amount))
                    finished.Add(quest);
            }

            if (!changed)
                return finished;

            SaveBook(player, book);
            Changed?.Invoke();

            foreach (var quest in finished)
            {
                var def = QuestPools.Find(quest.DefId);
                Entry.Logger.Info(
                    $"任务模式：NetId={player.NetId} 完成任务「{def?.Title ?? quest.DefId}」"
                    + $"（{quest.Progress}/{quest.Target}）");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"任务模式：推进进度失败 NetId={player?.NetId} goal={goal} {ex}");
        }

        return finished;
    }

    /// <summary>只推进"当前正在出牌的那个玩家"的进度（跨客户端一致，阶段 2 用）。</summary>
    public static List<QuestProgress> ReportForActionOwner(QuestGoal goal, int amount)
    {
        if (!Enabled)
            return new List<QuestProgress>();

        var owner = ActionOwner();
        return owner is null ? new List<QuestProgress>() : Report(owner, goal, amount);
    }

    /// <summary>
    /// 当前正在执行动作的玩家（所有客户端结果一致）。
    /// 用 RunManager.ActionExecutor.CurrentlyRunningAction.OwnerId 反查，
    /// 不要用 LocalContext.IsMe —— 那会各算各的。
    /// </summary>
    public static Player? ActionOwner()
    {
        try
        {
            var run = RunManager.Instance?.DebugOnlyGetState();
            var action = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
            if (run is null || action is null)
                return null;

            var ownerId = action.OwnerId;
            return run.Players.FirstOrDefault(p => p.NetId == ownerId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本机玩家（UI 显示用）。</summary>
    public static Player? LocalPlayer()
    {
        try
        {
            var run = RunManager.Instance?.DebugOnlyGetState();
            if (run is null)
                return null;

            return MegaCrit.Sts2.Core.Context.LocalContext.GetMe(run.Players);
        }
        catch
        {
            return null;
        }
    }
}
