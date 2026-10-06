// ============================================================================
// 任务模式（Quest）：存档数据结构
// ----------------------------------------------------------------------------
// 每个玩家一份 PlayerQuestBook，随这一局存档保存（RitsuLib RunSavedData）。
// 字段刻意用简单类型 + 公共属性，交给 System.Text.Json 序列化。
// ============================================================================

using System.Collections.Generic;

namespace YouJin.Scripts.Quest;

/// <summary>一个任务的运行时进度（真正会被存档的部分）。</summary>
public sealed class QuestProgress
{
    /// <summary>对应 QuestDef.Id。</summary>
    public string DefId { get; set; } = string.Empty;

    /// <summary>这个任务是在哪一层生成的（0 起）。</summary>
    public int ActIndex { get; set; }

    /// <summary>生成时定下来的目标数值（以后改 QuestDef 不会影响老存档）。</summary>
    public int Target { get; set; }

    /// <summary>生成时定下来的奖励数值。</summary>
    public int RewardAmount { get; set; }

    /// <summary>当前进度。</summary>
    public int Progress { get; set; }

    /// <summary>是否已完成。</summary>
    public bool Completed { get; set; }

    /// <summary>奖励是否已经发放（阶段 4 用）。</summary>
    public bool RewardGranted { get; set; }

    /// <summary>进度是否发生变化，返回 true 表示已完成（调用方用来触发提示）。</summary>
    public bool AddProgress(int amount)
    {
        if (Completed || amount <= 0)
            return false;

        Progress += amount;
        if (Progress >= Target)
        {
            Progress = Target;
            Completed = true;
            return true;
        }

        return false;
    }
}

/// <summary>一个玩家的任务书：当前层 + 这层的 3 个任务。</summary>
public sealed class PlayerQuestBook
{
    /// <summary>这份任务书对应哪一层（-1 = 还没生成过）。</summary>
    public int ActIndex { get; set; } = -1;

    /// <summary>这一层的 3 个任务。</summary>
    public List<QuestProgress> Quests { get; set; } = new();

    /// <summary>已完成的个数。</summary>
    public int CompletedCount
    {
        get
        {
            var count = 0;
            foreach (var quest in Quests)
            {
                if (quest.Completed)
                    count++;
            }

            return count;
        }
    }
}
