// ============================================================================
// 任务模式（Quest）：任务定义 + 任务池
// ----------------------------------------------------------------------------
// 结构：每人每层固定 **2 个通用任务 + 1 个角色专属任务**。
// 难度：简单 / 普通 / 困难 —— 每个任务自带一档难度，抽任务时**先随机滚难度**，
//       再从对应池子里挑一个同难度的（该难度没有可用的就退回"随便挑一个没抽过的"）。
//       所以难度是随机的，但 2+1 的结构是死的。
// 奖励：金币 / 最大生命 / 遗物（困难任务里才有遗物），丰厚程度跟着难度走。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;

namespace YouJin.Scripts.Quest;

/// <summary>任务的统计口径（每个枚举值对应一个/一组游戏钩子）。</summary>
public enum QuestGoal
{
    /// <summary>造成伤害。</summary>
    DealDamage,

    /// <summary>击杀敌人。</summary>
    KillEnemies,

    /// <summary>打出任意牌。</summary>
    PlayCards,

    /// <summary>打出攻击牌。</summary>
    PlayAttacks,

    /// <summary>打出技能牌。</summary>
    PlaySkills,

    /// <summary>打出能力牌。</summary>
    PlayPowers,

    /// <summary>获得格挡。</summary>
    GainBlock,

    /// <summary>在火堆选择「休息」。</summary>
    RestAtCampfire,

    /// <summary>在火堆选择「锻造」（升级卡牌）。</summary>
    SmithCard,

    /// <summary>消耗牌。</summary>
    ExhaustCards,

    /// <summary>弃牌。</summary>
    DiscardCards,

    /// <summary>击败精英。</summary>
    KillElites,

    // ---- 以下为角色专属口径 ----

    /// <summary>获得力量。</summary>
    GainStrength,

    /// <summary>用攻击牌造成的伤害。</summary>
    AttackDamage,

    /// <summary>施加中毒。</summary>
    ApplyPoison,

    /// <summary>充能球。</summary>
    ChannelOrbs,

    /// <summary>激发球。</summary>
    EvokeOrbs,

    /// <summary>获得集中。</summary>
    GainFocus,

    /// <summary>召唤。</summary>
    Summon,

    /// <summary>获得星辰。</summary>
    GainStars,

    /// <summary>锻造。</summary>
    Forge,
}

/// <summary>任务奖励类型。</summary>
public enum QuestRewardKind
{
    /// <summary>金币。</summary>
    Gold,

    /// <summary>最大生命。</summary>
    MaxHp,

    /// <summary>遗物（随机一件，稀有度看难度）。</summary>
    Relic,
}

/// <summary>任务难度。奖励的丰厚程度跟着难度走。</summary>
public enum QuestDifficulty
{
    /// <summary>简单。</summary>
    Easy = 0,

    /// <summary>普通。</summary>
    Normal = 1,

    /// <summary>困难。</summary>
    Hard = 2,
}

/// <summary>一个任务的定义（静态数据，进程内唯一实例）。</summary>
public sealed class QuestDef
{
    /// <summary>稳定 id（存档里记这个，改名不影响老存档）。</summary>
    public required string Id { get; init; }

    /// <summary>统计口径。</summary>
    public required QuestGoal Goal { get; init; }

    /// <summary>难度（简单 / 普通 / 困难）。</summary>
    public required QuestDifficulty Difficulty { get; init; }

    /// <summary>角色专属时用于匹配玩家的角色关键字段（如 IRONCLAD）。</summary>
    public string? CharacterStem { get; init; }

    /// <summary>中文标题。</summary>
    public required string TitleZh { get; init; }

    /// <summary>英文标题。</summary>
    public required string TitleEn { get; init; }

    /// <summary>中文描述，{0} = 目标数值。</summary>
    public required string DescZh { get; init; }

    /// <summary>英文描述，{0} = 目标数值。</summary>
    public required string DescEn { get; init; }

    /// <summary>第 1 层的目标数值。</summary>
    public required int BaseTarget { get; init; }

    /// <summary>每往下一层增加多少。</summary>
    public int PerActGrowth { get; init; }

    /// <summary>奖励类型。</summary>
    public QuestRewardKind Reward { get; init; } = QuestRewardKind.Gold;

    /// <summary>第 1 层的奖励数值（遗物用不到数值）。</summary>
    public int RewardBase { get; init; } = 100;

    /// <summary>每往下一层奖励增加多少。</summary>
    public int RewardPerAct { get; init; } = 50;

    /// <summary>某层的目标数值（actIndex 从 0 起）。</summary>
    public int TargetForAct(int actIndex) => BaseTarget + (PerActGrowth * actIndex);

    /// <summary>某层的奖励数值。</summary>
    public int RewardForAct(int actIndex) => RewardBase + (RewardPerAct * actIndex);

    /// <summary>当前语言下的标题。</summary>
    public string Title => QuestText.IsChinese ? TitleZh : TitleEn;

    /// <summary>当前语言下的难度名。</summary>
    public string DifficultyText => Difficulty switch
    {
        QuestDifficulty.Easy => QuestText.IsChinese ? "简单" : "Easy",
        QuestDifficulty.Hard => QuestText.IsChinese ? "困难" : "Hard",
        _ => QuestText.IsChinese ? "普通" : "Normal",
    };

    /// <summary>当前语言下、填好目标数值的描述。</summary>
    public string Describe(int target)
    {
        try
        {
            return string.Format(QuestText.IsChinese ? DescZh : DescEn, target);
        }
        catch (FormatException)
        {
            return QuestText.IsChinese ? DescZh : DescEn;
        }
    }

    /// <summary>奖励的展示文本。</summary>
    public string RewardText(int amount) => Reward switch
    {
        QuestRewardKind.Relic => QuestText.IsChinese ? "随机遗物" : "a random relic",
        QuestRewardKind.MaxHp => QuestText.IsChinese ? $"最大生命 +{amount}" : $"Max HP +{amount}",
        _ => QuestText.IsChinese ? $"金币 +{amount}" : $"Gold +{amount}",
    };
}

/// <summary>界面文字的语言判断（和 YouJinSettings 一样走 LocManager）。</summary>
public static class QuestText
{
    /// <summary>当前是不是中文。</summary>
    public static bool IsChinese
    {
        get
        {
            try
            {
                return (LocManager.Instance?.Language ?? string.Empty)
                    .StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}

/// <summary>任务池：通用池 + 各角色专属池。</summary>
public static class QuestPools
{
    /// <summary>角色匹配用的关键字段（按字符串包含匹配，兼容 mod 前缀）。</summary>
    public static readonly string[] CharacterStems =
    {
        "IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT", "WATCHER",
    };

    /// <summary>通用型任务池（简单 4 / 普通 4 / 困难 3）。</summary>
    public static readonly IReadOnlyList<QuestDef> General = new List<QuestDef>
    {
        // ---- 简单 ----
        new()
        {
            Id = "gen_play_cards", Goal = QuestGoal.PlayCards,
            Difficulty = QuestDifficulty.Easy,
            TitleZh = "熟练", TitleEn = "Practiced",
            DescZh = "打出 {0} 张牌。", DescEn = "Play {0} cards.",
            BaseTarget = 30, PerActGrowth = 15,
            Reward = QuestRewardKind.Gold, RewardBase = 80, RewardPerAct = 40,
        },
        new()
        {
            Id = "gen_gain_block", Goal = QuestGoal.GainBlock,
            Difficulty = QuestDifficulty.Easy,
            TitleZh = "铜墙铁壁", TitleEn = "Impenetrable",
            DescZh = "获得 {0} 点格挡。", DescEn = "Gain {0} Block.",
            BaseTarget = 200, PerActGrowth = 50,
            Reward = QuestRewardKind.MaxHp, RewardBase = 8, RewardPerAct = 1,
        },
        new()
        {
            Id = "gen_play_attacks", Goal = QuestGoal.PlayAttacks,
            Difficulty = QuestDifficulty.Easy,
            TitleZh = "攻势", TitleEn = "Offensive",
            DescZh = "打出 {0} 张攻击牌。", DescEn = "Play {0} Attack cards.",
            BaseTarget = 18, PerActGrowth = 9,
            Reward = QuestRewardKind.Gold, RewardBase = 80, RewardPerAct = 40,
        },
        new()
        {
            Id = "gen_rest", Goal = QuestGoal.RestAtCampfire,
            Difficulty = QuestDifficulty.Easy,
            TitleZh = "养精蓄锐", TitleEn = "Well Rested",
            DescZh = "在火堆休息 {0} 次。", DescEn = "Rest at a campfire {0} times.",
            BaseTarget = 2, PerActGrowth = 1,
            Reward = QuestRewardKind.MaxHp, RewardBase = 8, RewardPerAct = 1,
        },

        // ---- 普通 ----
        new()
        {
            Id = "gen_deal_damage", Goal = QuestGoal.DealDamage,
            Difficulty = QuestDifficulty.Easy,
            TitleZh = "重创", TitleEn = "Heavy Blows",
            DescZh = "造成 {0} 点伤害。", DescEn = "Deal {0} damage.",
            BaseTarget = 300, PerActGrowth = 100,
            Reward = QuestRewardKind.Gold, RewardBase = 130, RewardPerAct = 60,
        },
        new()
        {
            Id = "gen_play_skills", Goal = QuestGoal.PlaySkills,
            Difficulty = QuestDifficulty.Normal,
            TitleZh = "守势", TitleEn = "Defensive",
            DescZh = "打出 {0} 张技能牌。", DescEn = "Play {0} Skill cards.",
            BaseTarget = 15, PerActGrowth = 7,
            Reward = QuestRewardKind.Gold, RewardBase = 130, RewardPerAct = 60,
        },
        new()
        {
            Id = "gen_smith", Goal = QuestGoal.SmithCard,
            Difficulty = QuestDifficulty.Normal,
            TitleZh = "千锤百炼", TitleEn = "Tempered",
            DescZh = "在火堆锻造（升级卡牌）{0} 次。", DescEn = "Smith at a campfire {0} times.",
            BaseTarget = 2, PerActGrowth = 1,
            Reward = QuestRewardKind.MaxHp, RewardBase = 10, RewardPerAct = 2,
        },
        new()
        {
            Id = "gen_exhaust_cards", Goal = QuestGoal.ExhaustCards,
            Difficulty = QuestDifficulty.Normal,
            TitleZh = "燃尽", TitleEn = "Burn Out",
            DescZh = "消耗 {0} 张牌。", DescEn = "Exhaust {0} cards.",
            BaseTarget = 8, PerActGrowth = 5,
            Reward = QuestRewardKind.Gold, RewardBase = 130, RewardPerAct = 60,
        },

        // ---- 困难 ----
        new()
        {
            Id = "gen_kill_enemies", Goal = QuestGoal.KillEnemies,
            Difficulty = QuestDifficulty.Hard,
            TitleZh = "清场", TitleEn = "Clean Sweep",
            DescZh = "击杀 {0} 个敌人。", DescEn = "Kill {0} enemies.",
            BaseTarget = 10, PerActGrowth = 6,
            Reward = QuestRewardKind.Gold, RewardBase = 200, RewardPerAct = 90,
        },
        new()
        {
            Id = "gen_play_powers", Goal = QuestGoal.PlayPowers,
            Difficulty = QuestDifficulty.Hard,
            TitleZh = "强化", TitleEn = "Empowered",
            DescZh = "打出 {0} 张能力牌。", DescEn = "Play {0} Power cards.",
            BaseTarget = 5, PerActGrowth = 3,
            Reward = QuestRewardKind.MaxHp, RewardBase = 12, RewardPerAct = 3,
        },
        new()
        {
            Id = "gen_kill_elites", Goal = QuestGoal.KillElites,
            Difficulty = QuestDifficulty.Hard,
            TitleZh = "猎杀精英", TitleEn = "Elite Hunter",
            DescZh = "击败 {0} 个精英敌人。", DescEn = "Kill {0} elites.",
            BaseTarget = 2, PerActGrowth = 1,
            Reward = QuestRewardKind.Relic, RewardBase = 1, RewardPerAct = 0,
        },
    };

    /// <summary>角色专属任务池（每个角色简单 / 普通 / 困难各一个）。</summary>
    public static readonly IReadOnlyList<QuestDef> Character = new List<QuestDef>
    {
        // ---- 铁甲战士 ----
        new()
        {
            Id = "ic_exhaust", Goal = QuestGoal.ExhaustCards,
            Difficulty = QuestDifficulty.Easy, CharacterStem = "IRONCLAD",
            TitleZh = "灰烬之路", TitleEn = "Path of Ash",
            DescZh = "消耗 {0} 张牌。", DescEn = "Exhaust {0} cards.",
            BaseTarget = 10, PerActGrowth = 5,
            Reward = QuestRewardKind.Gold, RewardBase = 90, RewardPerAct = 45,
        },
        new()
        {
            Id = "ic_gain_strength", Goal = QuestGoal.GainStrength,
            Difficulty = QuestDifficulty.Normal, CharacterStem = "IRONCLAD",
            TitleZh = "力量至上", TitleEn = "Might Makes Right",
            DescZh = "获得 {0} 点力量。", DescEn = "Gain {0} Strength.",
            BaseTarget = 5, PerActGrowth = 3,
            Reward = QuestRewardKind.MaxHp, RewardBase = 10, RewardPerAct = 2,
        },
        new()
        {
            Id = "ic_attack_damage", Goal = QuestGoal.AttackDamage,
            Difficulty = QuestDifficulty.Hard, CharacterStem = "IRONCLAD",
            TitleZh = "以攻代守", TitleEn = "Attack Is Defense",
            DescZh = "用攻击牌造成 {0} 点伤害。", DescEn = "Deal {0} damage with Attacks.",
            BaseTarget = 300, PerActGrowth = 100,
            Reward = QuestRewardKind.Relic, RewardBase = 1, RewardPerAct = 0,
        },

        // ---- 静默猎手 ----
        new()
        {
            Id = "si_discard", Goal = QuestGoal.DiscardCards,
            Difficulty = QuestDifficulty.Easy, CharacterStem = "SILENT",
            TitleZh = "取舍", TitleEn = "Triage",
            DescZh = "弃掉 {0} 张牌。", DescEn = "Discard {0} cards.",
            BaseTarget = 15, PerActGrowth = 8,
            Reward = QuestRewardKind.MaxHp, RewardBase = 8, RewardPerAct = 1,
        },
        new()
        {
            Id = "si_play_skills", Goal = QuestGoal.PlaySkills,
            Difficulty = QuestDifficulty.Normal, CharacterStem = "SILENT",
            TitleZh = "身法", TitleEn = "Footwork",
            DescZh = "打出 {0} 张技能牌。", DescEn = "Play {0} Skill cards.",
            BaseTarget = 18, PerActGrowth = 9,
            Reward = QuestRewardKind.Gold, RewardBase = 140, RewardPerAct = 60,
        },
        new()
        {
            Id = "si_apply_poison", Goal = QuestGoal.ApplyPoison,
            Difficulty = QuestDifficulty.Hard, CharacterStem = "SILENT",
            TitleZh = "淬毒", TitleEn = "Envenom",
            DescZh = "施加 {0} 层中毒。", DescEn = "Apply {0} Poison.",
            BaseTarget = 45, PerActGrowth = 25,
            Reward = QuestRewardKind.Relic, RewardBase = 1, RewardPerAct = 0,
        },

        // ---- 故障机器人 ----
        new()
        {
            Id = "df_evoke_orbs", Goal = QuestGoal.EvokeOrbs,
            Difficulty = QuestDifficulty.Easy, CharacterStem = "DEFECT",
            TitleZh = "激发", TitleEn = "Evocation",
            DescZh = "激发 {0} 个球。", DescEn = "Evoke {0} Orbs.",
            BaseTarget = 6, PerActGrowth = 3,
            Reward = QuestRewardKind.Gold, RewardBase = 90, RewardPerAct = 45,
        },
        new()
        {
            Id = "df_channel_orbs", Goal = QuestGoal.ChannelOrbs,
            Difficulty = QuestDifficulty.Normal, CharacterStem = "DEFECT",
            TitleZh = "充能", TitleEn = "Charging Up",
            DescZh = "充能 {0} 个球。", DescEn = "Channel {0} Orbs.",
            BaseTarget = 8, PerActGrowth = 4,
            Reward = QuestRewardKind.Gold, RewardBase = 140, RewardPerAct = 60,
        },
        new()
        {
            Id = "df_gain_focus", Goal = QuestGoal.GainFocus,
            Difficulty = QuestDifficulty.Hard, CharacterStem = "DEFECT",
            TitleZh = "聚焦", TitleEn = "Focus",
            DescZh = "获得 {0} 点集中。", DescEn = "Gain {0} Focus.",
            BaseTarget = 4, PerActGrowth = 2,
            Reward = QuestRewardKind.MaxHp, RewardBase = 12, RewardPerAct = 3,
        },

        // ---- 死灵缚者 ----
        new()
        {
            Id = "nb_exhaust", Goal = QuestGoal.ExhaustCards,
            Difficulty = QuestDifficulty.Easy, CharacterStem = "NECROBINDER",
            TitleZh = "献祭", TitleEn = "Sacrifice",
            DescZh = "消耗 {0} 张牌。", DescEn = "Exhaust {0} cards.",
            BaseTarget = 8, PerActGrowth = 4,
            Reward = QuestRewardKind.Gold, RewardBase = 90, RewardPerAct = 45,
        },
        new()
        {
            Id = "nb_summon", Goal = QuestGoal.Summon,
            Difficulty = QuestDifficulty.Normal, CharacterStem = "NECROBINDER",
            TitleZh = "召唤仪式", TitleEn = "Rite of Summoning",
            DescZh = "召唤 {0} 次。", DescEn = "Summon {0} times.",
            BaseTarget = 3, PerActGrowth = 2,
            Reward = QuestRewardKind.MaxHp, RewardBase = 10, RewardPerAct = 2,
        },
        new()
        {
            Id = "nb_kill", Goal = QuestGoal.KillEnemies,
            Difficulty = QuestDifficulty.Hard, CharacterStem = "NECROBINDER",
            TitleZh = "收割", TitleEn = "Reaping",
            DescZh = "击杀 {0} 个敌人。", DescEn = "Kill {0} enemies.",
            BaseTarget = 10, PerActGrowth = 6,
            Reward = QuestRewardKind.Relic, RewardBase = 1, RewardPerAct = 0,
        },

        // ---- 摄政王 ----
        new()
        {
            Id = "rg_forge", Goal = QuestGoal.Forge,
            Difficulty = QuestDifficulty.Easy, CharacterStem = "REGENT",
            TitleZh = "锻造", TitleEn = "Forge",
            DescZh = "锻造 {0} 次。", DescEn = "Forge {0} times.",
            BaseTarget = 4, PerActGrowth = 2,
            Reward = QuestRewardKind.Gold, RewardBase = 90, RewardPerAct = 45,
        },
        new()
        {
            Id = "rg_gain_stars", Goal = QuestGoal.GainStars,
            Difficulty = QuestDifficulty.Normal, CharacterStem = "REGENT",
            TitleZh = "星辰", TitleEn = "Starlight",
            DescZh = "获得 {0} 点星辰。", DescEn = "Gain {0} Stars.",
            BaseTarget = 20, PerActGrowth = 12,
            Reward = QuestRewardKind.Gold, RewardBase = 140, RewardPerAct = 60,
        },
        new()
        {
            Id = "rg_powers", Goal = QuestGoal.PlayPowers,
            Difficulty = QuestDifficulty.Hard, CharacterStem = "REGENT",
            TitleZh = "王权", TitleEn = "Royal Decree",
            DescZh = "打出 {0} 张能力牌。", DescEn = "Play {0} Power cards.",
            BaseTarget = 5, PerActGrowth = 3,
            Reward = QuestRewardKind.Relic, RewardBase = 1, RewardPerAct = 0,
        },

        // ---- 观者（独立 mod 角色）----
        new()
        {
            Id = "wt_stance", Goal = QuestGoal.PlayAttacks,
            Difficulty = QuestDifficulty.Easy, CharacterStem = "WATCHER",
            TitleZh = "制敌先机", TitleEn = "Seize the Initiative",
            DescZh = "打出 {0} 张攻击牌。", DescEn = "Play {0} Attack cards.",
            BaseTarget = 18, PerActGrowth = 9,
            Reward = QuestRewardKind.Gold, RewardBase = 90, RewardPerAct = 45,
        },
        new()
        {
            Id = "wt_scry", Goal = QuestGoal.PlaySkills,
            Difficulty = QuestDifficulty.Normal, CharacterStem = "WATCHER",
            TitleZh = "观照", TitleEn = "Contemplate",
            DescZh = "打出 {0} 张技能牌。", DescEn = "Play {0} Skill cards.",
            BaseTarget = 18, PerActGrowth = 9,
            Reward = QuestRewardKind.MaxHp, RewardBase = 10, RewardPerAct = 2,
        },
        new()
        {
            Id = "wt_exhaust", Goal = QuestGoal.ExhaustCards,
            Difficulty = QuestDifficulty.Hard, CharacterStem = "WATCHER",
            TitleZh = "断舍离", TitleEn = "Letting Go",
            DescZh = "消耗 {0} 张牌。", DescEn = "Exhaust {0} cards.",
            BaseTarget = 10, PerActGrowth = 5,
            Reward = QuestRewardKind.Relic, RewardBase = 1, RewardPerAct = 0,
        },
    };

    /// <summary>玩家角色是否匹配某个关键字段。</summary>
    public static bool MatchesCharacter(Player? player, string stem)
    {
        var entry = player?.Character?.Id.Entry;
        if (!string.IsNullOrEmpty(entry))
        {
            if (entry.Equals(stem, StringComparison.OrdinalIgnoreCase)
                || entry.EndsWith("_" + stem, StringComparison.OrdinalIgnoreCase)
                || entry.Contains(stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 兜底：按角色类的类型名匹配（例如 Ironclad / Silent）。
        var typeName = player?.Character?.GetType().Name;
        return !string.IsNullOrEmpty(typeName)
            && typeName.Contains(stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取某个角色的专属任务池（没有就返回空表）。</summary>
    public static List<QuestDef> ForCharacter(Player? player)
        => Character.Where(d => d.CharacterStem is not null && MatchesCharacter(player, d.CharacterStem)).ToList();

    /// <summary>按 id 找定义（读档后重建展示用）。</summary>
    public static QuestDef? Find(string id)
        => General.FirstOrDefault(d => d.Id == id) ?? Character.FirstOrDefault(d => d.Id == id);
}
