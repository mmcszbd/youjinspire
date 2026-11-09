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
using MegaCrit.Sts2.Core.Random;
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

    /// <summary>任务系统是否生效（任务模式或卧底模式）。</summary>
    public static bool Enabled => YouJinSettings.TaskSystemEnabled;

    /// <summary>
    /// **本层**的卧底是谁 —— 每层重新抽一次。
    ///
    /// 用局内种子 + "层数"当流名随机抽一名玩家：种子属于局内状态、
    /// 玩家列表按 NetId 排序，所以各端算出来是同一个人。
    /// 返回 0 = 本层没有卧底（单人局禁用卧底玩法，没有别人可偷）。
    /// </summary>
    public static ulong UndercoverNetId(RunState? run, int actIndex)
    {
        if (run is null || run.Players.Count <= 1)
            return 0;

        var ids = run.Players.Select(p => p.NetId).OrderBy(id => id).ToList();
        var rng = new Rng(run.Rng.Seed, $"{Entry.ModId}/undercover/act{actIndex}");
        return ids[rng.NextInt(ids.Count)];
    }

    /// <summary>这名玩家是不是**本层**的卧底。</summary>
    public static bool IsUndercover(Player? player)
    {
        if (player is null || !YouJinSettings.UndercoverModeEnabled)
            return false;

        var run = player.RunState as RunState;
        if (run is null)
            return false;

        var mole = UndercoverNetId(run, run.CurrentActIndex);
        return mole != 0 && player.NetId == mole;
    }

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
    /// 外部（例如发奖励时）直接改了任务书对象之后，调用这个把改动写回存档。
    ///
    /// ⚠ 必须把**改过的那一份对象**传进来。之前这里是自己 GetBook() 再存一次，
    ///   结果标记（RewardGranted）没能生效、奖励被挂了两遍（线上实测）。
    /// </summary>
    public static void SaveBookFor(Player? player, PlayerQuestBook? book)
    {
        if (player is null || book is null)
            return;

        SaveBook(player, book);
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

                // 卧底没有自己的任务：把任务书清空并标记到本层，避免反复重算。
                if (IsUndercover(player))
                {
                    // 只在**换层**的时候清空 —— 同一层里卧底偷到的任务会记在这里，
                    // 不能因为"数量不为 0"就反复清掉。
                    if (book.ActIndex != actIndex)
                    {
                        book.ActIndex = actIndex;
                        book.Quests = new List<QuestProgress>();
                        SaveBook(player, book);
                        Entry.Logger.Info(
                            $"卧底模式：第 {actIndex + 1} 层卧底 = NetId={player.NetId}（不生成自己的任务）");
                    }

                    continue;
                }

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
        // 随机源：用「玩家自己的 RNG 种子 + 固定流名」现场造一个流。
        //   · 种子来自 player.PlayerRng.Seed —— 属于局内状态，各端（含读档）完全一致；
        //   · 流名只跟层数有关 —— 同一个玩家同一层，算多少次都是同一组任务。
        // 不用 RitsuLib 的 GetModPlayerRng：那是**有状态、会往前走**的流，
        // 万一某端因为时序重算一次，抽出来的东西就偏了，联机会分叉。
        var rng = new Rng(player.PlayerRng.Seed, $"{Entry.ModId}/quests/act{actIndex}");

        var characterPool = QuestPools.ForCharacter(player);
        var generalPool = QuestPools.General.ToList();

        var picked = new List<QuestDef>();

        // 结构固定：2 个通用任务 + 1 个角色专属任务。
        // 难度是随机的：每个位置先滚一档难度，再从对应池子里挑同难度的。
        for (var slot = 0; slot < QuestsPerAct; slot++)
        {
            var isCharacterSlot = slot == QuestsPerAct - 1 && characterPool.Count > 0;
            var pool = isCharacterSlot ? characterPool : generalPool;

            var def = PickForRandomDifficulty(rng, pool, picked)
                      ?? PickForRandomDifficulty(rng, generalPool, picked);

            if (def is not null)
                picked.Add(def);
        }

        // 打乱展示顺序：抽的时候角色任务固定在最后一个位置，展示时不一定排最后。
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
                return $"[{def?.DifficultyText ?? "?"}]{def?.Title ?? q.DefId} 0/{q.Target} {def?.RewardText(q.RewardAmount)}";
            })));
    }

    /// <summary>
    /// 先随机滚一档难度，再从那档里挑一个还没抽过的；
    /// 该难度没有可用的就退回"随便挑一个没抽过的"。
    /// </summary>
    private static QuestDef? PickForRandomDifficulty(Rng rng, List<QuestDef> pool, List<QuestDef> used)
    {
        var difficulty = (QuestDifficulty)rng.NextInt(3);

        var candidates = pool
            .Where(d => d.Difficulty == difficulty && used.All(u => u.Id != d.Id))
            .ToList();

        if (candidates.Count == 0)
            candidates = pool.Where(d => used.All(u => u.Id != d.Id)).ToList();

        return candidates.Count == 0 ? null : rng.NextItem(candidates);
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

            // 卧底：自己的行动不推进"自己的任务"，而是去顶掉别人的任务。
            if (IsUndercover(player))
            {
                ReportMoleAction(player, goal, amount);
                return finished;
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

    /// <summary>
    /// 卧底的一次行动：拿去顶掉**别人**的任务。
    ///   · 卧底的行动会推进其他每个玩家身上同口径的任务进度（谁先完成算谁的）；
    ///   · 一旦被卧底顶完成 → 原主那条标记 Stolen（永久失效、拿不到奖励），
    ///     同时按**双倍**奖励记到卧底自己的账上（面板只显示已到手的那些）。
    /// </summary>
    private static void ReportMoleAction(Player mole, QuestGoal goal, int amount)
    {
        if (!YouJinSettings.UndercoverModeEnabled || mole.RunState is not RunState run)
            return;

        var moleBook = GetBook(mole);
        if (moleBook is null)
            return;

        var stolenTitles = new List<string>();

        foreach (var victim in run.Players)
        {
            if (victim.NetId == mole.NetId)
                continue;

            var victimBook = GetBook(victim);
            if (victimBook is null)
                continue;

            var changed = false;

            foreach (var quest in victimBook.Quests)
            {
                if (quest.Completed || quest.Stolen)
                    continue;

                var def = QuestPools.Find(quest.DefId);
                if (def is null || def.Goal != goal)
                    continue;

                var completed = quest.AddProgress(amount);
                changed = true;

                if (!completed)
                    continue;

                // 顶掉了：原主这条永久失效。
                quest.Stolen = true;

                // 记到卧底账上，奖励翻倍。
                moleBook.Quests.Add(new QuestProgress
                {
                    DefId = quest.DefId,
                    ActIndex = victimBook.ActIndex,
                    Target = quest.Target,
                    RewardAmount = Math.Max(1, quest.RewardAmount) * 2,
                    Progress = quest.Target,
                    Completed = true,
                    RewardGranted = false,
                    StolenFrom = victim.NetId,
                });

                stolenTitles.Add($"{def.Title}（原主 NetId={victim.NetId}）");
            }

            if (changed)
                SaveBook(victim, victimBook);
        }

        if (stolenTitles.Count == 0)
            return;

        SaveBook(mole, moleBook);
        Changed?.Invoke();

        Entry.Logger.Info(
            $"卧底模式：NetId={mole.NetId} 顶掉了 {stolenTitles.Count} 条任务 → "
            + string.Join("、", stolenTitles) + "（奖励翻倍）");
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
