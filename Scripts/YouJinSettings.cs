// ============================================================================
// 友尽尖塔（youjin）：设置项
// ----------------------------------------------------------------------------
// 把本 mod 的设置做成一页"友尽尖塔"，挂在游戏设置界面里
// （和「东尼算法 AutoAnthony」那种独立设置页一样）。
//
// 用的是 RitsuLib 自带的设置框架（STS2RitsuLib.Settings，日志里那串
// [RitsuLib-ModSettings] 就是它）：
//     new ModSettingsPageBuilder(modId, pageId).WithTitle(...).AddSection(...).Build()
//     ModSettingsRegistry.Register(page)
//     ModSettingsRegistry.RegisterModDisplayName(modId, text)
// 每个开关的值用一个 binding 接进来：
//     ModSettingsBindings.Callback(modId, dataKey, 读, 写, 存, SaveScope.Global)
// 文本用 ModSettingsText.Dynamic(...)，这样不用再为设置界面单独做本地化文件。
//
// 值本身由本文件自己持久化（user://youjin_settings.json），
// 读不到就一律按"开启"处理，保证默认玩法不受影响。
//
// 玩法是**互斥的模式**（mode），不是一堆并列开关：
//   · Off    —— 关闭：本 mod 一行都不介入；
//   · YouJin —— 友尽尖塔：原来的玩法（抢战利品 / 火堆「假寐」「酣睡」/
//               三个事件 / 「建筑师的背叛」）；
//   · Shared —— 共享模式：多人共用一条血池 + 一个钱包。
//               这个模式下**上面那些事件、火堆选项、战利品规则全都不触发**。
//
// 另外 mode 下面还挂着几个细开关：
//   · greedy_loot_enabled —— 贪婪模式（只在 YouJin 模式里有意义）；
//   · shared_hp_enabled / shared_gold_enabled / shared_energy_enabled —— 共享模式的子开关。
//
// ⚠ 联机注意：这些开关是**本机设置**（各人存各人的 user://youjin_settings.json），
//   不像 together 那样由主机下发。两端开关不一致 → 两端算出来的血量/金币不一样 →
//   校验和分叉。要开共享就**每个人都开**。

// ============================================================================

using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace YouJin.Scripts;

/// <summary>本 mod 的玩法模式（三选一）。</summary>
public enum YouJinMode
{
    /// <summary>关闭：本 mod 完全不介入对局。</summary>
    Off = 0,

    /// <summary>友尽尖塔：原来的玩法（战利品归击杀者、死亡分钱、火堆假寐/酣睡、三个事件、建筑师的背叛）。</summary>
    YouJin = 1,

    /// <summary>共享模式：多人共用一条血池和一个钱包；上面的玩法全部不触发。</summary>
    Shared = 2,

    /// <summary>任务模式：每人每层 3 个任务，完成度在打完本层 Boss 的奖励里结算。</summary>
    Quest = 3,
}

/// <summary>本 mod 的设置项。</summary>
public static class YouJinSettings
{
    /// <summary>模组 id（和清单/日志里的 youjin 一致）。</summary>
    public const string ModId = "youjin";

    /// <summary>设置页/分区标题。</summary>
    public const string SectionTitle = "友尽尖塔";

    /// <summary>玩法模式（三选一）。</summary>
    public const string ModeKey = "mode";

    /// <summary>旧版本的"总开关"键名 —— 只在读设置时用来做兼容迁移。</summary>
    public const string GameplayKey = "gameplay_enabled";

    /// <summary>贪婪模式（补尾刀拿金币）开关。</summary>
    public const string GreedyLootKey = "greedy_loot_enabled";

    /// <summary>共享血量开关（多人局共用一个血池）。</summary>
    public const string SharedHpKey = "shared_hp_enabled";

    /// <summary>共享金币开关（多人局共用一个钱包）。</summary>
    public const string SharedGoldKey = "shared_gold_enabled";

    /// <summary>共享能量开关（多人局共用一个能量池）。</summary>
    public const string SharedEnergyKey = "shared_energy_enabled";

    /// <summary>任务模式：右上角任务面板开关。</summary>
    public const string QuestPanelKey = "quest_panel_enabled";

    private const string SettingsPageId = "settings";
    private const string SavePath = "user://youjin_settings.json";

    private static bool _loaded;
    private static YouJinMode _mode = YouJinMode.YouJin;
    private static bool _greedyLootEnabled = true;
    private static bool _sharedHpEnabled = true;
    private static bool _sharedGoldEnabled = true;
    private static bool _sharedEnergyEnabled = true;
    private static bool _questPanelEnabled = true;

    /// <summary>玩法模式（读不到时按「友尽尖塔」处理）。</summary>
    public static YouJinMode Mode
    {
        get
        {
            EnsureLoaded();
            return _mode;
        }
        set
        {
            _mode = value;
            Save();
        }
    }

    /// <summary>「友尽尖塔」原玩法是否生效（= 模式选了友尽尖塔）。</summary>
    public static bool GameplayEnabled => Mode == YouJinMode.YouJin;

    /// <summary>「共享模式」是否生效（= 模式选了共享模式）。</summary>
    public static bool SharedModeEnabled => Mode == YouJinMode.Shared;

    /// <summary>「任务模式」是否生效（= 模式选了任务模式）。</summary>
    public static bool QuestModeEnabled => Mode == YouJinMode.Quest;

    /// <summary>贪婪模式：本场战斗的金币+遗物奖励全归击杀者。</summary>
    public static bool GreedyLootEnabled
    {
        get
        {
            EnsureLoaded();
            return _greedyLootEnabled;
        }
        set
        {
            _greedyLootEnabled = value;
            Save();
        }
    }

    /// <summary>共享血量：多人局所有玩家共用一个血池（见 SharedVitals.cs）。</summary>
    public static bool SharedHpEnabled
    {
        get
        {
            EnsureLoaded();
            return _sharedHpEnabled;
        }
        set
        {
            _sharedHpEnabled = value;
            Save();
        }
    }

    /// <summary>共享金币：多人局所有玩家共用一个钱包。</summary>
    public static bool SharedGoldEnabled
    {
        get
        {
            EnsureLoaded();
            return _sharedGoldEnabled;
        }
        set
        {
            _sharedGoldEnabled = value;
            Save();
        }
    }

    /// <summary>共享能量：多人局所有玩家共用一个能量池（见 SharedEnergy.cs）。</summary>
    public static bool SharedEnergyEnabled
    {
        get
        {
            EnsureLoaded();
            return _sharedEnergyEnabled;
        }
        set
        {
            _sharedEnergyEnabled = value;
            Save();
        }
    }

    /// <summary>任务模式：是否在右上角显示任务面板。</summary>
    public static bool QuestPanelEnabled
    {
        get
        {
            EnsureLoaded();
            return _questPanelEnabled;
        }
        set
        {
            _questPanelEnabled = value;
            Save();
        }
    }

    /// <summary>把「友尽尖塔」设置页注册进游戏的设置界面。</summary>
    public static void Register()
    {
        try
        {
            var page = new ModSettingsPageBuilder(ModId, SettingsPageId)
                .WithTitle(Text("友尽尖塔", "YouJin Spire"))
                .AddSection("mode", section => section
                    .WithTitle(Text("玩法模式", "Game mode"))
                    .AddEnumChoice(
                        ModeKey,
                        Text("模式", "Mode"),
                        ModSettingsBindings.Callback(
                            ModId,
                            ModeKey,
                            () => Mode,
                            value => Mode = value,
                            Save,
                            SaveScope.Global),
                        ModeName,
                        Text(
                            "关闭：不介入。贪婪模式：补刀拿钱和遗物，关底有新宿之战。"
                            + "共享模式：共用血量、金币、能量。任务模式：每人每层 3 个任务，关底结算。"
                            + "联机时两端要选同一个。",
                            "Off: stays out of the run. YouJin Spire: kill-loot + events. "
                            + "Shared: one HP pool, wallet and energy pool. "
                            + "Quest: everyone gets 3 tasks per act, paid out with the boss rewards. "
                            + "Both players must pick the same mode."),
                        ModSettingsChoicePresentation.Stepper))
                .AddSection("youjin", section => section
                    .WithTitle(Text("贪婪模式", "Greedy mode"))
                    .WithVisibleWhen(() => Mode == YouJinMode.YouJin)
                    .AddToggle(
                        GreedyLootKey,
                        Text("补尾刀拿金币", "Last hit takes the gold"),
                        ModSettingsBindings.Callback(
                            ModId,
                            GreedyLootKey,
                            () => GreedyLootEnabled,
                            value => GreedyLootEnabled = value,
                            Save,
                            SaveScope.Global),
                        Text(
                            "本场战斗的金币和遗物奖励全归击杀者。",
                            "All gold and relic rewards from a combat go to whoever got the kill."),
                        null))
                .AddSection("shared", section => section
                    .WithTitle(Text("共享模式", "Shared mode"))
                    .WithVisibleWhen(() => Mode == YouJinMode.Shared)
                    .AddToggle(
                        SharedHpKey,
                        Text("共享血量", "Shared HP"),
                        ModSettingsBindings.Callback(
                            ModId,
                            SharedHpKey,
                            () => SharedHpEnabled,
                            value => SharedHpEnabled = value,
                            Save,
                            SaveScope.Global),
                        Text(
                            "全队共用一管血：是各角色上限之和。",
                            "One shared HP pool; on a new run it is the sum of everyone's max HP."),
                        null)
                    .AddToggle(
                        SharedGoldKey,
                        Text("共享金币", "Shared gold"),
                        ModSettingsBindings.Callback(
                            ModId,
                            SharedGoldKey,
                            () => SharedGoldEnabled,
                            value => SharedGoldEnabled = value,
                            Save,
                            SaveScope.Global),
                        Text(
                            "全队共用一个钱包；开局为各角色起始金币之和。",
                            "One shared wallet; on a new run it is the sum of everyone's gold."),
                        null)
                    .AddToggle(
                        SharedEnergyKey,
                        Text("共享能量", "Shared energy"),
                        ModSettingsBindings.Callback(
                            ModId,
                            SharedEnergyKey,
                            () => SharedEnergyEnabled,
                            value => SharedEnergyEnabled = value,
                            Save,
                            SaveScope.Global),
                        Text(
                            "全队共用一管能量；每回合补到各人上限之和。",
                            "One shared energy pool; each turn it refills to the sum of everyone's "
                            + "max energy (the counter shows e.g. 6/6)."),
                        null))
                .AddSection("quest", section => section
                    .WithTitle(Text("任务模式", "Quest mode"))
                    .WithVisibleWhen(() => Mode == YouJinMode.Quest)
                    .AddToggle(
                        QuestPanelKey,
                        Text("显示任务面板", "Show quest panel"),
                        ModSettingsBindings.Callback(
                            ModId,
                            QuestPanelKey,
                            () => QuestPanelEnabled,
                            value => QuestPanelEnabled = value,
                            Save,
                            SaveScope.Global),
                        Text(
                            "在屏幕右上角显示本层的 3 个任务和进度。",
                            "Shows this act's three quests and their progress in the top-right corner."),
                        null))
                .Build();

            ModSettingsRegistry.RegisterModDisplayName(ModId, Text("友尽尖塔", "YouJin Spire"));
            ModSettingsRegistry.Register(page);

            Entry.Logger.Info(
                $"设置：已注册「友尽尖塔」设置页（模式={Mode}；"
                + $"贪婪={GreedyLootEnabled} 共享血量={SharedHpEnabled} "
                + $"共享金币={SharedGoldEnabled} 共享能量={SharedEnergyEnabled} "
                + $"任务面板={QuestPanelEnabled}）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：注册设置页失败（不影响玩法）{ex}");
        }
    }

    /// <summary>按当前语言给一段设置界面文字（设置界面不走本地化文件）。</summary>
    private static ModSettingsText Text(string chinese, string english)
        => ModSettingsText.Dynamic(() => IsChinese ? chinese : english);

    /// <summary>模式选择器上每一项的名字。</summary>
    private static ModSettingsText ModeName(YouJinMode mode) => mode switch
    {
        YouJinMode.Off => Text("关闭", "Off"),
        YouJinMode.Shared => Text("共享模式", "Shared mode"),
        YouJinMode.Quest => Text("任务模式", "Quest mode"),
        _ => Text("贪婪模式", "Greedy mode"),
    };

    private static bool IsChinese
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

    /// <summary>第一次真正读设置时再从磁盘加载一次。</summary>
    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;

        try
        {
            if (!Godot.FileAccess.FileExists(SavePath))
                return;

            var text = Godot.FileAccess.GetFileAsString(SavePath);
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (Json.ParseString(text).AsGodotDictionary() is not { } dict)
                return;

            // 新模式优先；老设置文件里只有 gameplay_enabled，按它迁移。
            if (dict.ContainsKey(ModeKey))
                _mode = (YouJinMode)dict[ModeKey].AsInt32();
            else if (dict.ContainsKey(GameplayKey))
                _mode = dict[GameplayKey].AsBool() ? YouJinMode.YouJin : YouJinMode.Off;

            if (dict.ContainsKey(GreedyLootKey))
                _greedyLootEnabled = dict[GreedyLootKey].AsBool();

            if (dict.ContainsKey(SharedHpKey))
                _sharedHpEnabled = dict[SharedHpKey].AsBool();

            if (dict.ContainsKey(SharedGoldKey))
                _sharedGoldEnabled = dict[SharedGoldKey].AsBool();

            if (dict.ContainsKey(SharedEnergyKey))
                _sharedEnergyEnabled = dict[SharedEnergyKey].AsBool();

            if (dict.ContainsKey(QuestPanelKey))
                _questPanelEnabled = dict[QuestPanelKey].AsBool();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：读取失败，按默认（全开）处理 {ex}");
        }
    }

    /// <summary>把当前设置写回 user://（改开关时自动调用）。</summary>
    public static void Save()
    {
        try
        {
            var dict = new Godot.Collections.Dictionary
            {
                { ModeKey, (int)_mode },
                { GreedyLootKey, _greedyLootEnabled },
                { SharedHpKey, _sharedHpEnabled },
                { SharedGoldKey, _sharedGoldEnabled },
                { SharedEnergyKey, _sharedEnergyEnabled },
                { QuestPanelKey, _questPanelEnabled },
            };

            using var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
            if (file is null)
            {
                Entry.Logger.Info("设置：写文件失败（打不开 user://youjin_settings.json）");
                return;
            }

            file.StoreString(Json.Stringify(dict));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：保存失败 {ex}");
        }
    }
}
