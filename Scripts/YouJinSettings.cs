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
// 两个开关：
//   · gameplay_enabled  —— 整个模组总开关；
//   · greedy_loot_enabled —— 贪婪模式（补尾刀拿金币）：本场金币+遗物奖励全归击杀者。
// ============================================================================

using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace YouJin.Scripts;

/// <summary>本 mod 的设置项。</summary>
public static class YouJinSettings
{
    /// <summary>模组 id（和清单/日志里的 youjin 一致）。</summary>
    public const string ModId = "youjin";

    /// <summary>设置页/分区标题。</summary>
    public const string SectionTitle = "友尽尖塔";

    /// <summary>整个模组的总开关。</summary>
    public const string GameplayKey = "gameplay_enabled";

    /// <summary>贪婪模式（补尾刀拿金币）开关。</summary>
    public const string GreedyLootKey = "greedy_loot_enabled";

    private const string SettingsPageId = "settings";
    private const string SavePath = "user://youjin_settings.json";

    private static bool _loaded;
    private static bool _gameplayEnabled = true;
    private static bool _greedyLootEnabled = true;

    /// <summary>总开关：关掉后本 mod 的玩法全部失效（读不到时按开启）。</summary>
    public static bool GameplayEnabled
    {
        get
        {
            EnsureLoaded();
            return _gameplayEnabled;
        }
        set
        {
            _gameplayEnabled = value;
            Save();
        }
    }

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

    /// <summary>把「友尽尖塔」设置页注册进游戏的设置界面。</summary>
    public static void Register()
    {
        try
        {
            var page = new ModSettingsPageBuilder(ModId, SettingsPageId)
                .WithTitle(Text("友尽尖塔", "YouJin Spire"))
                .AddSection("general", section => section
                    .WithTitle(Text("玩法开关", "Gameplay"))
                    .AddToggle(
                        GameplayKey,
                        Text("启用友尽尖塔", "Enable YouJin Spire"),
                        ModSettingsBindings.Callback(
                            ModId,
                            GameplayKey,
                            () => GameplayEnabled,
                            value => GameplayEnabled = value,
                            Save,
                            SaveScope.Global),
                        Text(
                            "关掉后：击杀者独吞战利品、死亡分钱、火堆「假寐」「酣睡」，"
                            + "以及「建筑师的背叛」等事件都不会生效。",
                            "When off: kill-loot, death gold split, the rest-site options "
                            + "and the Architect betrayal event all stop working."),
                        null)
                    .AddToggle(
                        GreedyLootKey,
                        Text("贪婪模式（补尾刀拿金币）", "Greedy mode (last hit takes the gold)"),
                        ModSettingsBindings.Callback(
                            ModId,
                            GreedyLootKey,
                            () => GreedyLootEnabled,
                            value => GreedyLootEnabled = value,
                            Save,
                            SaveScope.Global),
                        Text(
                            "开启后：多人局里本场战斗的金币和遗物奖励全部归击杀者。"
                            + "关掉则按原版各拿各的。",
                            "When on: in multiplayer, all gold and relic rewards from a combat "
                            + "go to whoever got the kill. When off, vanilla behaviour."),
                        null))
                .Build();

            ModSettingsRegistry.RegisterModDisplayName(ModId, Text("友尽尖塔", "YouJin Spire"));
            ModSettingsRegistry.Register(page);

            Entry.Logger.Info("设置：已注册「友尽尖塔」设置页（总开关 + 贪婪模式）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：注册设置页失败（不影响玩法）{ex}");
        }
    }

    /// <summary>按当前语言给一段设置界面文字（设置界面不走本地化文件）。</summary>
    private static ModSettingsText Text(string chinese, string english)
        => ModSettingsText.Dynamic(() => IsChinese ? chinese : english);

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

            if (dict.ContainsKey(GameplayKey))
                _gameplayEnabled = dict[GameplayKey].AsBool();

            if (dict.ContainsKey(GreedyLootKey))
                _greedyLootEnabled = dict[GreedyLootKey].AsBool();
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
                { GameplayKey, _gameplayEnabled },
                { GreedyLootKey, _greedyLootEnabled },
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
