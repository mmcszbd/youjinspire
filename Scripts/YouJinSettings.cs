// ============================================================================
// 友尽尖塔（youjin）：设置项
// ----------------------------------------------------------------------------
// 页面标题「派对模式」。玩法是**可叠加的开关**，不再是互斥的单选：
//   · 贪婪模式 —— 原来「友尽尖塔」那一套：补尾刀拿金币/遗物、火堆「假寐」「酣睡」、
//                三个事件、关底的「建筑师的挑拨」。子开关：补尾刀拿金币。
//   · 共享模式 —— 多人共用一条血池 + 一个钱包 + 一管能量。子开关三个。
//   · 任务模式 —— 每人每层 3 个任务，打完本层 Boss 结算。子开关：
//                谁是卧底（关闭 / 每层随机抽一名玩家）+ 显示任务面板。
//
// 「贪婪模式 + 任务模式」可以同时开（就是所谓的派对模式组合）。
// 旧版本（只有 mode 单选）的设置文件会自动迁移过来。
//
// ★ 所有界面文字都在本文件里，用 Text("中文", "English") 包着；
//   任务本身的文案在 Scripts/quest/QuestDefs.cs，游戏内面板文案在 Scripts/quest/QuestUi.cs。
//
// ⚠ 联机注意：这些开关是**本机设置**（各人存各人的 user://youjin_settings.json），
//   不会由主机下发。两端不一致 → 算出来的血量/金币/任务不一样 → 校验和分叉。
//   要开什么，**每个人都得开一样的**。
// ============================================================================

using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
using YouJin.Scripts.Quest;

namespace YouJin.Scripts;

/// <summary>「谁是卧底」的抽取方式。</summary>
public enum UndercoverPick
{
    /// <summary>关闭：没有卧底，所有人正常拿任务。</summary>
    Off = 0,

    /// <summary>每一层随机抽一名玩家当卧底（单人局自动等于关闭）。</summary>
    RandomEachAct = 1,
}

/// <summary>本 mod 的设置项。</summary>
public static class YouJinSettings
{
    /// <summary>模组 id（和清单/日志里的 youjin 一致）。</summary>
    public const string ModId = "youjin";

    /// <summary>设置页标题。</summary>
    public const string SectionTitle = "派对模式";

    // ---- 旧版本的键名（只用于读设置时迁移）----

    /// <summary>旧版：互斥模式（0 关闭 / 1 友尽尖塔 / 2 共享 / 3 任务 / 4 卧底）。</summary>
    public const string LegacyModeKey = "mode";

    /// <summary>更旧版本：总开关。</summary>
    public const string LegacyGameplayKey = "gameplay_enabled";

    // ---- 现在的键名 ----

    /// <summary>贪婪模式总开关（原来「友尽尖塔」那一套玩法）。</summary>
    public const string GreedyKey = "greedy_enabled";

    /// <summary>贪婪模式子开关：本场战斗的金币+遗物奖励全归击杀者。</summary>
    public const string GreedyLootKey = "greedy_loot_enabled";

    /// <summary>共享模式总开关。</summary>
    public const string SharedKey = "shared_enabled";

    /// <summary>共享血量。</summary>
    public const string SharedHpKey = "shared_hp_enabled";

    /// <summary>共享金币。</summary>
    public const string SharedGoldKey = "shared_gold_enabled";

    /// <summary>共享能量。</summary>
    public const string SharedEnergyKey = "shared_energy_enabled";

    /// <summary>任务模式总开关。</summary>
    public const string QuestKey = "quest_enabled";

    /// <summary>任务模式子开关：谁是卧底。</summary>
    public const string UndercoverKey = "undercover_pick";

    /// <summary>任务模式子开关：右上角任务面板。</summary>
    public const string QuestPanelKey = "quest_panel_enabled";

    /// <summary>建筑师的挑拨：血量倍数（1-5，默认 3）。</summary>
    public const string ArchitectHpKey = "architect_hp_multiplier";

    /// <summary>建筑师的挑拨：开战给所有玩家挂「壁垒」。</summary>
    public const string ArchitectFortitudeKey = "architect_fortitude";

    /// <summary>建筑师的挑拨：每回合出牌上限（0 = 无上限）。</summary>
    public const string ArchitectCardLimitKey = "architect_card_limit";

    private const string SettingsPageId = "settings";
    private const string SavePath = "user://youjin_settings.json";

    private static bool _loaded;
    private static bool _greedyEnabled = true;
    private static bool _greedyLootEnabled = true;
    private static bool _sharedEnabled;
    private static bool _sharedHpEnabled = true;
    private static bool _sharedGoldEnabled = true;
    private static bool _sharedEnergyEnabled = true;
    private static bool _questEnabled;
    private static UndercoverPick _undercoverPick = UndercoverPick.Off;
    private static bool _questPanelEnabled = true;
    private static int _architectHpMultiplier = 3;
    private static bool _architectFortitude;
    private static int _architectCardLimit;

    // ------------------------------------------------------------------
    // 玩法开关
    // ------------------------------------------------------------------

    /// <summary>贪婪模式：原来「友尽尖塔」那一套玩法是否生效。</summary>
    public static bool GreedyEnabled
    {
        get
        {
            EnsureLoaded();
            return _greedyEnabled;
        }
        set
        {
            _greedyEnabled = value;
            Save();
        }
    }

    /// <summary>和旧名字保持兼容：= 贪婪模式是否生效。</summary>
    public static bool GameplayEnabled => GreedyEnabled;

    /// <summary>贪婪模式子开关：本场战斗的金币/遗物全归击杀者。</summary>
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

    /// <summary>共享模式是否生效。</summary>
    public static bool SharedEnabled
    {
        get
        {
            EnsureLoaded();
            return _sharedEnabled;
        }
        set
        {
            _sharedEnabled = value;
            Save();
        }
    }

    /// <summary>和旧名字保持兼容：= 共享模式是否生效。</summary>
    public static bool SharedModeEnabled => SharedEnabled;

    /// <summary>共享血量。</summary>
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

    /// <summary>共享金币。</summary>
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

    /// <summary>共享能量。</summary>
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

    /// <summary>任务模式：每人每层 3 个任务。</summary>
    public static bool QuestEnabled
    {
        get
        {
            EnsureLoaded();
            return _questEnabled;
        }
        set
        {
            _questEnabled = value;
            Save();
            QuestUi.Refresh("quest-toggle");
        }
    }

    /// <summary>和旧名字保持兼容：= 任务模式是否生效。</summary>
    public static bool QuestModeEnabled => QuestEnabled;

    /// <summary>任务系统（= 任务模式）是否生效。</summary>
    public static bool TaskSystemEnabled => QuestEnabled;

    /// <summary>「谁是卧底」的抽取方式。</summary>
    public static UndercoverPick UndercoverPickMode
    {
        get
        {
            EnsureLoaded();
            return _undercoverPick;
        }
        set
        {
            _undercoverPick = value;
            Save();
            QuestUi.Refresh("undercover-toggle");
        }
    }

    /// <summary>卧底玩法是否生效（= 任务模式开着 且 谁是卧底选了"每层随机"）。</summary>
    public static bool UndercoverModeEnabled =>
        QuestEnabled && UndercoverPickMode == UndercoverPick.RandomEachAct;

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
            QuestUi.Refresh("panel-toggle");
        }
    }

    /// <summary>
    /// 「建筑师的挑拨」事件是否生效：贪婪模式或任务模式开着就生效。
    /// （共享模式/全关时保持原版建筑师。）
    /// </summary>
    public static bool ArchitectEventEnabled => GreedyEnabled || QuestEnabled;

    /// <summary>建筑师的挑拨：这场 PvP 里生命上限的倍数（1-5，默认 3）。</summary>
    public static int ArchitectHpMultiplier
    {
        get
        {
            EnsureLoaded();
            return Math.Clamp(_architectHpMultiplier, 1, 5);
        }
        set
        {
            _architectHpMultiplier = Math.Clamp(value, 1, 5);
            Save();
        }
    }

    /// <summary>建筑师的挑拨：开战是否给所有玩家挂「壁垒」（默认关）。</summary>
    public static bool ArchitectFortitudeEnabled
    {
        get
        {
            EnsureLoaded();
            return _architectFortitude;
        }
        set
        {
            _architectFortitude = value;
            Save();
        }
    }

    /// <summary>
    /// 建筑师的挑拨：每回合出牌上限（默认 0 = 无上限）。
    /// 设置里滑杆是 0-30，0 表示无上限，其余按 10-30 用。
    /// </summary>
    public static int ArchitectCardPlayLimit
    {
        get
        {
            EnsureLoaded();
            return Math.Clamp(_architectCardLimit, 0, 30);
        }
        set
        {
            _architectCardLimit = Math.Clamp(value, 0, 30);
            Save();
        }
    }

    /// <summary>真正生效的出牌上限（0 = 无限制；否则至少 10）。</summary>
    public static int EffectiveCardPlayLimit
    {
        get
        {
            var value = ArchitectCardPlayLimit;
            return value <= 0 ? 0 : Math.Max(10, value);
        }
    }

    // ------------------------------------------------------------------
    // 设置页
    // ------------------------------------------------------------------

    /// <summary>把「派对模式」设置页注册进游戏的设置界面。</summary>
    public static void Register()
    {
        try
        {
            var page = new ModSettingsPageBuilder(ModId, SettingsPageId)
                .WithTitle(Text("友尽尖塔", "YouJin Spire"))

                // ---- 总开关 ----
                .AddSection("modes", section => section
                    .WithTitle(Text("玩法", "Gameplay"))
                    .AddToggle(
                        GreedyKey,
                        Text("贪婪模式", "Greedy mode"),
                        ModSettingsBindings.Callback(
                            ModId, GreedyKey, () => GreedyEnabled, value => GreedyEnabled = value, Save, SaveScope.Global),
                        Text(
                            "补尾刀抢金币和遗物、火堆「假寐」「酣睡」、三个专属事件。",
                            "Kill-steal gold and relics, the Laziness/Deep Sleep campfire options, three custom events, "
                            + "and the Architect's Provocation finale."),
                        null)
                    .AddToggle(
                        SharedKey,
                        Text("共享模式", "Shared mode"),
                        ModSettingsBindings.Callback(
                            ModId, SharedKey, () => SharedEnabled, value => SharedEnabled = value, Save, SaveScope.Global),
                        Text(
                            "全队共用一条血池、一个钱包、一管能量。",
                            "One shared HP pool, wallet and energy pool for the whole team."),
                        null)
                    .AddToggle(
                        QuestKey,
                        Text("任务模式", "Quest mode"),
                        ModSettingsBindings.Callback(
                            ModId, QuestKey, () => QuestEnabled, value => QuestEnabled = value, Save, SaveScope.Global),
                        Text(
                            "每人每层 3 个任务（2 个通用 + 1 个角色专属），打完本层 Boss 结算奖励。",
                            "Three tasks per player per act (2 general + 1 character-specific), paid out with the "
                            + "Act boss rewards."),
                        null))

                // ---- 贪婪模式子选项 ----
                .AddSection("greedy", section => section
                    .WithTitle(Text("贪婪模式选项", "Greedy options"))
                    .WithVisibleWhen(() => GreedyEnabled)
                    .AddToggle(
                        GreedyLootKey,
                        Text("补尾刀拿金币", "Last hit takes the gold"),
                        ModSettingsBindings.Callback(
                            ModId, GreedyLootKey, () => GreedyLootEnabled, value => GreedyLootEnabled = value, Save, SaveScope.Global),
                        Text(
                            "本场战斗的金币和遗物奖励全归击杀者。",
                            "All gold and relic rewards from a combat go to whoever got the kill."),
                        null))

                // ---- 共享模式子选项 ----
                .AddSection("shared", section => section
                    .WithTitle(Text("共享模式选项", "Shared options"))
                    .WithVisibleWhen(() => SharedEnabled)
                    .AddToggle(
                        SharedHpKey,
                        Text("共享血量", "Shared HP"),
                        ModSettingsBindings.Callback(
                            ModId, SharedHpKey, () => SharedHpEnabled, value => SharedHpEnabled = value, Save, SaveScope.Global),
                        Text(
                            "全队共用一管血：新开一局时等于各角色上限之和。",
                            "One shared HP pool; on a new run it is the sum of everyone's max HP."),
                        null)
                    .AddToggle(
                        SharedGoldKey,
                        Text("共享金币", "Shared gold"),
                        ModSettingsBindings.Callback(
                            ModId, SharedGoldKey, () => SharedGoldEnabled, value => SharedGoldEnabled = value, Save, SaveScope.Global),
                        Text(
                            "全队共用一个钱包；开局为各角色起始金币之和。",
                            "One shared wallet; on a new run it is the sum of everyone's gold."),
                        null)
                    .AddToggle(
                        SharedEnergyKey,
                        Text("共享能量", "Shared energy"),
                        ModSettingsBindings.Callback(
                            ModId, SharedEnergyKey, () => SharedEnergyEnabled, value => SharedEnergyEnabled = value, Save, SaveScope.Global),
                        Text(
                            "全队共用一管能量；每回合补到各人上限之和。",
                            "One shared energy pool; each turn it refills to the sum of everyone's max energy."),
                        null))

                // ---- 任务模式子选项 ----
                .AddSection("quest", section => section
                    .WithTitle(Text("任务模式选项", "Quest options"))
                    .WithVisibleWhen(() => QuestEnabled)
                    .AddEnumChoice(
                        UndercoverKey,
                        Text("谁是卧底", "Who is the mole"),
                        ModSettingsBindings.Callback(
                            ModId, UndercoverKey, () => UndercoverPickMode, value => UndercoverPickMode = value, Save, SaveScope.Global),
                        UndercoverName,
                        Text(
                            "每层随机抽一名玩家当卧底：他没有自己的任务，靠顶掉别人的任务拿双倍奖励。"
                            + "单人局里没有别人，会自动等于「关闭」。",
                            "Each act, one random player becomes the mole: no tasks of their own, they steal other "
                            + "players' tasks for double rewards. In single player it's automatically Off."),
                        ModSettingsChoicePresentation.Stepper)
                    .AddToggle(
                        QuestPanelKey,
                        Text("显示任务面板", "Show quest panel"),
                        ModSettingsBindings.Callback(
                            ModId, QuestPanelKey, () => QuestPanelEnabled, value => QuestPanelEnabled = value, Save, SaveScope.Global),
                        Text(
                            "在屏幕右上角显示本层的 3 个任务和进度。",
                            "Shows this act's three quests and their progress in the top-right corner."),
                        null))

                // ---- 建筑师的挑拨 ----
                .AddSection("architect", section => section
                    .WithTitle(Text("建筑师的挑拨", "The Architect's Provocation"))
                    .WithVisibleWhen(() => GreedyEnabled || QuestEnabled)
                    .AddIntSlider(
                        ArchitectHpKey,
                        Text("血量倍数", "HP multiplier"),
                        ModSettingsBindings.Callback(
                            ModId, ArchitectHpKey, () => ArchitectHpMultiplier, v => ArchitectHpMultiplier = v, Save, SaveScope.Global),
                        1, 5, 1,
                        v => $"{v}×",
                        Text(
                            "这场 PvP 里所有人生命上限的倍数（默认 3 倍）。",
                            "Multiplier applied to everyone's max HP in the PvP fight (default 3x)."))
                    .AddToggle(
                        ArchitectFortitudeKey,
                        Text("群体壁垒", "Team-wide Barricade"),
                        ModSettingsBindings.Callback(
                            ModId, ArchitectFortitudeKey, () => ArchitectFortitudeEnabled, v => ArchitectFortitudeEnabled = v, Save, SaveScope.Global),
                        Text(
                            "开战给所有玩家挂上「壁垒」（格挡在回合结束时不清空）。默认关闭。",
                            "Grants Barricade to every player when the fight starts (block is not cleared at end of turn). Off by default."))
                    .AddIntSlider(
                        ArchitectCardLimitKey,
                        Text("每回合出牌上限", "Cards per turn limit"),
                        ModSettingsBindings.Callback(
                            ModId, ArchitectCardLimitKey, () => ArchitectCardPlayLimit, v => ArchitectCardPlayLimit = v, Save, SaveScope.Global),
                        0, 30, 1,
                        v => v <= 0 ? (IsChinese ? "无上限" : "Unlimited") : $"{Math.Max(10, v)}",
                        Text(
                            "每人每回合最多打出几张牌；选「无上限」=原版。范围 10-30。",
                            "Max cards each player may play per turn; \"Unlimited\" = vanilla. Range 10-30.")))
                .Build();

            ModSettingsRegistry.RegisterModDisplayName(ModId, Text("友尽尖塔", "YouJin Spire"));
            ModSettingsRegistry.Register(page);

            Entry.Logger.Info(
                $"设置：已注册「派对模式」设置页（贪婪={GreedyEnabled} 共享={SharedEnabled} "
                + $"任务={QuestEnabled} 卧底={UndercoverPickMode} 任务面板={QuestPanelEnabled}）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：注册设置页失败（不影响玩法）{ex}");
        }
    }

    /// <summary>按当前语言给一段设置界面文字（设置界面不走本地化文件）。</summary>
    private static ModSettingsText Text(string chinese, string english)
        => ModSettingsText.Dynamic(() => IsChinese ? chinese : english);

    /// <summary>「谁是卧底」选择器上每一项的名字。</summary>
    private static ModSettingsText UndercoverName(UndercoverPick pick) => pick switch
    {
        UndercoverPick.RandomEachAct => Text("每层随机一名玩家", "One random player each act"),
        _ => Text("关闭", "Off"),
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

    // ------------------------------------------------------------------
    // 读写
    // ------------------------------------------------------------------

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

            // ① 老格式：只有一个互斥的 mode（0 关闭 / 1 友尽尖塔 / 2 共享 / 3 任务 / 4 卧底）。
            if (dict.ContainsKey(LegacyModeKey))
            {
                switch (dict[LegacyModeKey].AsInt32())
                {
                    case 1:
                        _greedyEnabled = true;
                        break;
                    case 2:
                        _greedyEnabled = false;
                        _sharedEnabled = true;
                        break;
                    case 3:
                        _greedyEnabled = false;
                        _questEnabled = true;
                        break;
                    case 4:
                        _greedyEnabled = false;
                        _questEnabled = true;
                        _undercoverPick = UndercoverPick.RandomEachAct;
                        break;
                    default:
                        _greedyEnabled = false;
                        break;
                }
            }
            // ② 更老格式：只有 gameplay_enabled
            else if (dict.ContainsKey(LegacyGameplayKey))
            {
                _greedyEnabled = dict[LegacyGameplayKey].AsBool();
            }

            // ③ 新格式的键优先
            if (dict.ContainsKey(GreedyKey))
                _greedyEnabled = dict[GreedyKey].AsBool();

            if (dict.ContainsKey(SharedKey))
                _sharedEnabled = dict[SharedKey].AsBool();

            if (dict.ContainsKey(QuestKey))
                _questEnabled = dict[QuestKey].AsBool();

            if (dict.ContainsKey(UndercoverKey))
                _undercoverPick = (UndercoverPick)dict[UndercoverKey].AsInt32();

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

            if (dict.ContainsKey(ArchitectHpKey))
                _architectHpMultiplier = Math.Clamp(dict[ArchitectHpKey].AsInt32(), 1, 5);

            if (dict.ContainsKey(ArchitectFortitudeKey))
                _architectFortitude = dict[ArchitectFortitudeKey].AsBool();

            if (dict.ContainsKey(ArchitectCardLimitKey))
                _architectCardLimit = Math.Clamp(dict[ArchitectCardLimitKey].AsInt32(), 0, 30);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：读取失败，按默认处理 {ex}");
        }
    }

    /// <summary>把当前设置写回 user://（改开关时自动调用）。</summary>
    public static void Save()
    {
        try
        {
            var dict = new Godot.Collections.Dictionary
            {
                { GreedyKey, _greedyEnabled },
                { GreedyLootKey, _greedyLootEnabled },
                { SharedKey, _sharedEnabled },
                { SharedHpKey, _sharedHpEnabled },
                { SharedGoldKey, _sharedGoldEnabled },
                { SharedEnergyKey, _sharedEnergyEnabled },
                { QuestKey, _questEnabled },
                { UndercoverKey, (int)_undercoverPick },
                { QuestPanelKey, _questPanelEnabled },
                { ArchitectHpKey, _architectHpMultiplier },
                { ArchitectFortitudeKey, _architectFortitude },
                { ArchitectCardLimitKey, _architectCardLimit },
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
