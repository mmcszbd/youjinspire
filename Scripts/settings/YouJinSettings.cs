// ============================================================================
// 友尽尖塔（youjin）：设置项
// ----------------------------------------------------------------------------
// 页面标题「派对模式」。两组开关都是**切换式（单选）**，不是可叠加的布尔：
//   ① 玩法（四选一，外加「关闭」）：
//      · 贪婪模式 —— 原来「友尽尖塔」那一套：补尾刀拿金币/遗物、火堆「假寐」「酣睡」、
//                    三个事件、关底的「建筑师的挑拨」。子开关：补尾刀拿金币。
//      · 共享模式 —— 多人共用一条血池 + 一个钱包 + 一管能量。子开关三个。
//      · 任务模式 —— 每人每层 3 个任务，打完本层 Boss 结算。子开关：
//                    谁是卧底（关闭 / 每层随机抽一名玩家）+ 显示任务面板。
//      · 分离模式 —— 两人各自开一局自己的单机局（同种子），最后合流打 PvP。
//   ② PvP 方式（二选一，外加「关闭」）：手速模式（本 mod 自己的 PvP） / 回合制模式（PVP 决斗）。
//
// 2026-11-01 改：这两组原来是各自独立的布尔开关，玩家会把四个玩法同时开、
// 把两套 PvP 同时开，然后分不清是哪个在起作用。现在改成互斥单选：
// 打开一个自动关掉同组其他项（实现上只有一个枚举字段，不是四个布尔）。
// 分离模式依赖回合制 PvP —— 选分离模式会自动把 PvP 方式切成回合制；
// 反过来把 PvP 方式切出手速/关闭时，分离模式也会一起让出来。
//
// 旧版本（mode 单选 / 四个布尔 / pvp_speed_mode 布尔）的设置文件会自动迁移过来。
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
using PvpDuel.Core.Settings;
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

/// <summary>「玩法」这一组：四选一（外加关闭），互斥。</summary>
public enum PartyMode
{
    /// <summary>关闭：四个玩法都不启用，就是原版。</summary>
    Off = 0,

    /// <summary>贪婪模式（原来「友尽尖塔」那一套）。</summary>
    Greedy = 1,

    /// <summary>共享模式（共享血池 / 钱包 / 能量）。</summary>
    Shared = 2,

    /// <summary>任务模式（每人每层 3 个任务）。</summary>
    Quest = 3,

    /// <summary>分离模式（两人各爬各的图，最后合流打 PvP）。依赖回合制 PvP。</summary>
    Split = 4,
}

/// <summary>「PvP 方式」这一组：二选一（外加关闭），互斥。</summary>
public enum PvpStyle
{
    /// <summary>关闭：两套 PvP 都不启用。</summary>
    Off = 0,

    /// <summary>手速模式：友尽尖塔自己的 PvP（同一回合一起出牌、同时结束回合）。</summary>
    Speed = 1,

    /// <summary>回合制模式：二合一进来的「PVP 决斗」（一人一回合，严格轮流）。</summary>
    TurnBased = 2,
}

/// <summary>本 mod 的设置项。</summary>
public static class YouJinSettings
{
    /// <summary>模组 id（和清单/日志里的 youjin 一致）。</summary>
    public const string ModId = "youjin";

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

    /// <summary>建筑师的挑拨：这场 PvP 里屏蔽第三方 mod 的「受到的伤害」统计（薄荷尖塔兼容）。</summary>
    public const string ArchitectMintyCompatKey = "architect_minty_compat";

    /// <summary>
    /// 手速模式：场上要不要放那块「结实的卵」当木桩。
    /// 默认 <c>false</c> = 不放（纯玩家对决，场上只有人）；打开 = 退回旧形态（有木桩撑着胜负判定）。
    /// </summary>
    public const string SpeedDummyKey = "speed_mode_dummy";

    /// <summary>「玩法」这一组的选择（四选一，互斥）。</summary>
    public const string PartyModeKey = "party_mode";

    /// <summary>「PvP 方式」这一组的选择（二选一，互斥）。</summary>
    public const string PvpStyleKey = "pvp_style";

    /// <summary>旧版：手速模式的独立布尔开关（只用于迁移，新存档不再读它）。</summary>
    public const string PvpSpeedModeKey = "pvp_speed_mode";

    /// <summary>旧版：手速模式第一轮保护的布尔开关（只用于迁移成 <see cref="SpeedFirstTurnGuardTurnsKey" />）。</summary>
    public const string SpeedFirstTurnGuardKey = "speed_first_turn_guard";

    /// <summary>
    /// 手速模式的保护窗口：<b>前几回合</b>双方都不掉血（<c>0</c> = 不保护）。
    /// 所有玩家都还没结束第 N 回合之前，掉血一律压成 0（格挡照常被打掉）。
    /// </summary>
    public const string SpeedFirstTurnGuardTurnsKey = "speed_first_turn_guard_turns";

    private const string SettingsPageId = "settings";
    private const string SavePath = "user://youjin_settings.json";

    private static bool _loaded;
    private static PartyMode _partyMode = PartyMode.Greedy;
    private static PvpStyle _pvpStyle = PvpStyle.Speed;
    private static bool _greedyLootEnabled = true;
    private static bool _sharedHpEnabled = true;
    private static bool _sharedGoldEnabled = true;
    private static bool _sharedEnergyEnabled = true;
    private static UndercoverPick _undercoverPick = UndercoverPick.Off;
    private static bool _questPanelEnabled = true;
    private static int _architectHpMultiplier = 3;
    private static bool _architectFortitude;
    private static int _architectCardLimit;
    private static bool _architectMintyCompat = true;

    /// <summary>手速模式要不要那块木桩（默认不要：场上只有两个人）。</summary>
    private static bool _speedModeDummy;

    /// <summary>手速模式保护窗口：前几回合双方不掉血（0 = 不保护，默认 1）。</summary>
    private static int _speedFirstTurnGuardTurns = 1;

    // ------------------------------------------------------------------
    // 玩法开关
    // ------------------------------------------------------------------

    /// <summary>
    /// 「玩法」这一组的选择：四选一，互斥。
    /// 走这里改，同组其它玩法自动关掉（以前是四个独立布尔，能同时开）。
    /// </summary>
    public static PartyMode PartyModeSetting
    {
        get
        {
            // 本局有房主同步来的设置时，本局一律用房主的值；本机配置（以及设置页显示）保持原样。
            if (PvpSettingsSync.PartyModeFor() is { } syncedMode)
            {
                return syncedMode;
            }

            EnsureLoaded();
            return _partyMode;
        }
        set
        {
            var next = NormalizeMode(value);

            _partyMode = next;
            SyncPvpStore();
            Save();
            QuestUi.Refresh("party-mode");
        }
    }

    /// <summary>
    /// 「PvP 方式」这一组的选择：二选一，互斥。
    /// 手速模式 ↔ 建筑师的挑拨；回合制模式 ↔ PVP 决斗。
    /// </summary>
    public static PvpStyle PvpStyleSetting
    {
        get
        {
            // 本局有房主同步来的设置时，本局一律用房主的值；本机配置（以及设置页显示）保持原样。
            if (PvpSettingsSync.StyleFor() is { } syncedStyle)
            {
                return syncedStyle;
            }

            EnsureLoaded();
            return _pvpStyle;
        }
        set
        {
            var next = NormalizeStyle(value);

            _pvpStyle = next;
            SyncPvpStore();
            Save();
        }
    }

    /// <summary>把越界的枚举值夹回合法范围（存档被手改过也炸不了）。</summary>
    private static PartyMode NormalizeMode(PartyMode mode)
        => mode is PartyMode.Off or PartyMode.Greedy or PartyMode.Shared or PartyMode.Quest or PartyMode.Split
            ? mode
            : PartyMode.Off;

    /// <summary>把越界的枚举值夹回合法范围。</summary>
    private static PvpStyle NormalizeStyle(PvpStyle style)
        => style is PvpStyle.Off or PvpStyle.Speed or PvpStyle.TurnBased ? style : PvpStyle.Off;

    /// <summary>
    /// 把两组单选的结果同步到「PVP 决斗」自己那套设置里。
    /// 决斗的补丁只问 <c>PvpSettings</c>（<c>ModuleSwitches.Core</c> / <c>SplitMode</c>），
    /// 所以这里的两个布尔是<b>投影</b>，不是独立的开关。
    /// </summary>
    internal static void SyncPvpStore()
    {
        try
        {
            // 写的是本机那一份（Local）：本局生效值可能来自房主（Current），
            // 往那儿写会把房主的快照改掉，客户端在局内点开关就会跟房主分叉。
            var settings = PvpDuel.Core.Settings.PvpSettingsStore.Local;
            settings.Enabled = _pvpStyle == PvpStyle.TurnBased;
            settings.ModuleSplitMode = _partyMode == PartyMode.Split;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：同步「PVP 决斗」开关失败（决斗会用默认值）{ex.Message}");
        }
    }

    /// <summary>贪婪模式：原来「友尽尖塔」那一套玩法是否生效（= 玩法选了它）。</summary>
    public static bool GreedyEnabled
    {
        get
        {
            EnsureLoaded();
            return _partyMode == PartyMode.Greedy;
        }
        set => PartyModeSetting = value
            ? PartyMode.Greedy
            : _partyMode == PartyMode.Greedy ? PartyMode.Off : _partyMode;
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

    /// <summary>共享模式是否生效（= 玩法选了它）。</summary>
    public static bool SharedEnabled
    {
        get
        {
            EnsureLoaded();
            return _partyMode == PartyMode.Shared;
        }
        set => PartyModeSetting = value
            ? PartyMode.Shared
            : _partyMode == PartyMode.Shared ? PartyMode.Off : _partyMode;
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

    /// <summary>任务模式：每人每层 3 个任务（= 玩法选了它）。</summary>
    public static bool QuestEnabled
    {
        get
        {
            EnsureLoaded();
            return _partyMode == PartyMode.Quest;
        }
        set => PartyModeSetting = value
            ? PartyMode.Quest
            : _partyMode == PartyMode.Quest ? PartyMode.Off : _partyMode;
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
    /// PvP 方式是不是<b>手速模式</b> —— 友尽尖塔自己的 PvP。
    /// 所有人在同一回合里出牌、同时结束回合，谁手快谁先结算（第 5 幕「建筑师的挑拨」）。
    /// 另一套是<b>回合制模式</b>（PVP 决斗，一人一回合）。两组互斥，见 <see cref="PvpStyleSetting" />。
    /// </summary>
    public static bool PvpSpeedModeEnabled
    {
        get
        {
            EnsureLoaded();
            return _pvpStyle == PvpStyle.Speed;
        }
        set => PvpStyleSetting = value
            ? PvpStyle.Speed
            : _pvpStyle == PvpStyle.Speed ? PvpStyle.Off : _pvpStyle;
    }

    /// <summary>
    /// 「建筑师的挑拨」事件是否生效：<b>只要 PvP 方式不是「关闭」就生效</b>。
    /// （2026-11-01 改过两次：
    ///   ① 原来还要求贪婪/任务模式开着，结果"手速模式"这个开关名不副实 ——
    ///      关掉贪婪和任务之后，手速模式开着也打不了；
    ///   ② 现在这场挑拨**按 PvP 方式打两种形态**（手速=木桩同手牌；回合制=交给内置决斗轮流行动，
    ///      见交接文档 §4.1.1），所以判据不能再只认手速 —— 否则选了回合制，这场事件连选项都不动。）
    /// 关掉时保持原版建筑师结尾。单人局仍然不会触发（见 ArchitectRoomReplacementPatch 的人数判据）。
    /// </summary>
    public static bool ArchitectEventEnabled => PvpStyleSetting != PvpStyle.Off;

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
    /// 建筑师的挑拨：这场 PvP 里是否屏蔽第三方 mod 的「受到的伤害」统计（默认开）。
    /// 关掉就等于放任薄荷尖塔在那场战斗里去读 Player 生物的 Monster（null）→ 报错，
    /// 详见 Scripts\compat\MintySpire2Compat.cs。
    /// </summary>
    public static bool ArchitectMintyCompatEnabled
    {
        get
        {
            EnsureLoaded();
            return _architectMintyCompat;
        }
        set
        {
            _architectMintyCompat = value;
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

    /// <summary>
    /// 手速模式要不要那块木桩（<b>默认不要</b>）。
    /// </summary>
    /// <remarks>
    /// 关（默认）= 纯玩家对决：怪物一律不进战斗，胜负由「还有几个人活着」判
    /// （见 <c>Scripts\event\BarePvpCombat.cs</c>）。
    /// 开 = 退回 0.10.6 之前的形态：摆一只「结实的卵」当敌方锚点撑着
    /// <c>CombatManager.IsCombatEnding</c>，收场靠「只剩 1 人 → 打飞卵」。
    /// 两条路共用同一套目标判定（队友照样能被打），区别只在有没有那块靶子。
    /// </remarks>
    public static bool SpeedModeDummyEnabled
    {
        get
        {
            EnsureLoaded();
            return _speedModeDummy;
        }
        set
        {
            _speedModeDummy = value;
            Save();
        }
    }

    /// <summary>
    /// 手速模式的保护窗口：所有玩家都还没结束第 N 回合之前，双方掉血一律压成 0（格挡照常）。
    /// <c>0</c> = 不保护。范围 0~3。
    /// </summary>
    public static int SpeedFirstTurnGuardTurns
    {
        get
        {
            EnsureLoaded();
            return _speedFirstTurnGuardTurns;
        }
        set
        {
            _speedFirstTurnGuardTurns = Math.Clamp(value, 0, 3);
            Save();
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
            // 第四种模式（PVP 决斗）的设置数据必须先注册好：下面构建页面时
            // .AddDuelModeSections() 会立刻读 PvpSettingsStore.Current。
            // 这一步在 Entry.Init 里比 PvpDuel.Main.Initialize() 更早，所以显式来一发。
            try
            {
                PvpDuel.Core.Settings.PvpSettingsStore.Initialize();
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"设置：PVP 决斗的设置存储没起来（第四种模式会退回默认值）{ex}");
            }

            var page = new ModSettingsPageBuilder(ModId, SettingsPageId)
                .WithTitle(Loc("yj.page.title", "友尽尖塔"))

                // ---- 玩法：四选一（互斥）----
                .AddSection("modes", section => section
                    .WithTitle(Loc("yj.sec.modes", "玩法"))
                    .AddEnumChoice(
                        PartyModeKey,
                        Loc("yj.opt.party_mode", "玩法模式"),
                        ModSettingsBindings.Callback(
                            ModId, PartyModeKey, () => PartyModeSetting, value => PartyModeSetting = value, Save, SaveScope.Global),
                        PartyModeName,
                        Loc("yj.desc.party_mode", 
                            "五选一，不能同时开：贪婪（补尾刀抢金币/遗物、火堆假寐/酣睡、三个专属事件）"
                            + "｜共享（全队一条血池、一个钱包、一管能量）"
                            + "｜任务（每人每层 3 个任务，打完本层 Boss 结算）"
                            + "｜分离（两人同种子各开一局单机局，最后合流打 PvP；选它会自动把 PvP 方式切成回合制）"
                            + "｜追杀（同一个联机局里投票可以分裂成多条分支，各走各的路）。"
                            + "选「关闭」= 原版玩法。"),
                        ModSettingsChoicePresentation.Stepper))


                // ---- PvP 方式：两套完全不同的 PvP，二选一（互斥）----
                .AddSection("pvp", section => section
                    .WithTitle(Loc("yj.sec.pvp", "PvP 方式"))
                    .AddEnumChoice(
                        PvpStyleKey,
                        Loc("yj.opt.pvp_style", "PvP 方式"),
                        ModSettingsBindings.Callback(
                            ModId, PvpStyleKey, () => PvpStyleSetting, value => PvpStyleSetting = value, Save, SaveScope.Global),
                        PvpStyleName,
                        Loc("yj.desc.pvp_style", 
                            "二选一，不能同时开："
                            + "手速模式 = 同一回合里所有人一起出牌、同时结束回合，谁手快谁先结算（第 5 幕胜利房换成"
                            + "「建筑师的挑拨」）；"
                            + "回合制模式 = 一人一回合严格轮流、没有怪物（第 3 幕打完后多一幕「PVP 决斗」，走「对决邀请」事件）。"
                            + "单人局 / 三人局两套都不生效。⚠ 两端都要开一样的。"),
                        ModSettingsChoicePresentation.Stepper))

                // ---- 手速模式的选项（建筑师的挑拨）：只跟自己的开关走，不依赖贪婪/任务 ----
                .AddSection("architect", section => section
                    .WithTitle(Loc("yj.sec.architect", "手速模式的选项（建筑师的挑拨）"))
                    .WithVisibleWhen(() => PvpSpeedModeEnabled)
                    // 2026-11-06：「保留木桩（旧形态）」删了 —— 手速模式只剩纯玩家对决这一种形态。
                    // 「第一轮保护」从布尔改成 0~3 回合的滑条：默认 1 回合。
                    .AddIntSlider(
                        SpeedFirstTurnGuardTurnsKey,
                        Loc("yj.opt.first_turn_guard", "开局保护：前几回合不掉血"),
                        ModSettingsBindings.Callback(
                            ModId, SpeedFirstTurnGuardTurnsKey, () => SpeedFirstTurnGuardTurns, v => SpeedFirstTurnGuardTurns = v, Save, SaveScope.Global),
                        0, 3, 1,
                        v => v <= 0
                            ? (IsChinese ? "不保护" : "Off")
                            : (IsChinese ? $"{v} 回合" : $"{v} turn(s)"),
                        Loc("yj.desc.first_turn_guard", "所有玩家都还没结束第 N 回合之前，双方掉血一律压成 0（格挡照常被打掉）。0 = 从第一帧就开始正常掉血。"))
                    .AddIntSlider(
                        ArchitectHpKey,
                        Loc("yj.opt.hp_multiplier", "血量倍数"),
                        ModSettingsBindings.Callback(
                            ModId, ArchitectHpKey, () => ArchitectHpMultiplier, v => ArchitectHpMultiplier = v, Save, SaveScope.Global),
                        1, 5, 1,
                        v => $"{v}×",
                        Loc("yj.desc.hp_multiplier", "这场 PvP 里所有人生命上限的倍数（默认 3 倍）。"))
                    .AddToggle(
                        ArchitectFortitudeKey,
                        Loc("yj.opt.team_barricade", "群体壁垒"),
                        ModSettingsBindings.Callback(
                            ModId, ArchitectFortitudeKey, () => ArchitectFortitudeEnabled, v => ArchitectFortitudeEnabled = v, Save, SaveScope.Global))
                    .AddIntSlider(
                        ArchitectCardLimitKey,
                        Loc("yj.opt.cards_per_turn", "每回合出牌上限"),
                        ModSettingsBindings.Callback(
                            ModId, ArchitectCardLimitKey, () => ArchitectCardPlayLimit, v => ArchitectCardPlayLimit = v, Save, SaveScope.Global),
                        0, 30, 1,
                        v => v <= 0 ? (IsChinese ? "无上限" : "Unlimited") : $"{Math.Max(10, v)}",
                        Loc("yj.desc.cards_per_turn", "每人每回合最多打出几张牌；选「无上限」=原版。范围 10-30。"))
                    .AddToggle(
                        ArchitectMintyCompatKey,
                        Loc("yj.opt.suppress_damage", "屏蔽伤害统计"),
                        ModSettingsBindings.Callback(
                            ModId, ArchitectMintyCompatKey, () => ArchitectMintyCompatEnabled, v => ArchitectMintyCompatEnabled = v, Save, SaveScope.Global)))

                // ---- 回合制 PVP 的选项 + 数值：紧挨着自己的开关 ----
                .AddDuelModeSections()

                .AddSplitModeSections()

                // ---- 测试工具：点一下即执行的跳房间 / 秒赢（默认折叠）----
                .AddTestToolSections()

                // ---- 贪婪模式子选项 ----
                .AddSection("greedy", section => section
                    .WithTitle(Loc("yj.sec.greedy", "贪婪模式选项"))
                    .WithVisibleWhen(() => GreedyEnabled)
                    .AddToggle(
                        GreedyLootKey,
                        Loc("yj.opt.last_hit_gold", "补尾刀拿金币"),
                        ModSettingsBindings.Callback(
                            ModId, GreedyLootKey, () => GreedyLootEnabled, value => GreedyLootEnabled = value, Save, SaveScope.Global),
                        null))

                // ---- 共享模式子选项 ----
                .AddSection("shared", section => section
                    .WithTitle(Loc("yj.sec.shared", "共享模式选项"))
                    .WithVisibleWhen(() => SharedEnabled)
                    .AddToggle(
                        SharedHpKey,
                        Loc("yj.opt.shared_hp", "共享血量"),
                        ModSettingsBindings.Callback(
                            ModId, SharedHpKey, () => SharedHpEnabled, value => SharedHpEnabled = value, Save, SaveScope.Global),
                        null)
                    .AddToggle(
                        SharedGoldKey,
                        Loc("yj.opt.shared_gold", "共享金币"),
                        ModSettingsBindings.Callback(
                            ModId, SharedGoldKey, () => SharedGoldEnabled, value => SharedGoldEnabled = value, Save, SaveScope.Global),
                        null)
                    .AddToggle(
                        SharedEnergyKey,
                        Loc("yj.opt.shared_energy", "共享能量"),
                        ModSettingsBindings.Callback(
                            ModId, SharedEnergyKey, () => SharedEnergyEnabled, value => SharedEnergyEnabled = value, Save, SaveScope.Global),
                        null))

                // ---- 任务模式子选项 ----
                .AddSection("quest", section => section
                    .WithTitle(Loc("yj.sec.quest", "任务模式选项"))
                    .WithVisibleWhen(() => QuestEnabled)
                    .AddEnumChoice(
                        UndercoverKey,
                        Loc("yj.opt.mole", "谁是卧底"),
                        ModSettingsBindings.Callback(
                            ModId, UndercoverKey, () => UndercoverPickMode, value => UndercoverPickMode = value, Save, SaveScope.Global),
                        UndercoverName,
                        Loc("yj.desc.mole", 
                            "每层随机抽一名玩家当卧底：他没有自己的任务，靠顶掉别人的任务拿双倍奖励。"
                            + "单人局里没有别人，会自动等于「关闭」。"),
                        ModSettingsChoicePresentation.Stepper)
                    .AddToggle(
                        QuestPanelKey,
                        Loc("yj.opt.quest_panel", "显示任务面板"),
                        ModSettingsBindings.Callback(
                            ModId, QuestPanelKey, () => QuestPanelEnabled, value => QuestPanelEnabled = value, Save, SaveScope.Global),
                        null))


                 .Build();

            ModSettingsRegistry.RegisterModDisplayName(ModId, Loc("yj.opt.enabled", "友尽尖塔"));
            ModSettingsRegistry.Register(page);

            Entry.Logger.Info(
                $"设置：已注册「派对模式」设置页（玩法={PartyModeSetting} 卧底={UndercoverPickMode} "
                + $"任务面板={QuestPanelEnabled}）");
            Entry.Logger.Info(
                $"设置：PvP 方式（{PvpStyleSetting}｜手速={PvpSpeedModeEnabled} 回合制={PvpDuelUi.TurnBasedEnabled} "
                + $"分离={PvpDuelUi.SplitModeEnabled}）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"设置：注册设置页失败（不影响玩法）{ex}");
        }
    }

    /// <summary>
    /// 设置界面文字：从本地化表 <c>settings_ui</c>（<c>youjin/localization/zhs|eng/settings_ui.json</c>）取。
    /// 改文案只动那两个 json，不用改代码；表里缺键时用 <paramref name="chineseFallback" /> 顶上，不会空白。
    /// </summary>
    /// <remarks>
    /// ⚠ 表名必须是**原版已有的表**：RitsuLib 只把文件名等于原版表名的 json 并进原版表，
    /// 叫 <c>settings.json</c> 会被静默跳过（<c>The loc table='settings' does not exist!</c>）。
    /// </remarks>
    internal static ModSettingsText Loc(string key, string chineseFallback)
        => ModSettingsText.LocString("settings_ui", key, chineseFallback);

    /// <summary>「谁是卧底」选择器上每一项的名字。</summary>
    private static ModSettingsText UndercoverName(UndercoverPick pick) => pick switch
    {
        UndercoverPick.RandomEachAct => Loc("yj.pick.mole_random", "每层随机一名玩家"),
        _ => Loc("yj.pick.off", "关闭"),
    };

    /// <summary>「玩法模式」选择器上每一项的名字。</summary>
    private static ModSettingsText PartyModeName(PartyMode mode) => mode switch
    {
        PartyMode.Greedy => Loc("yj.pick.greedy", "贪婪模式"),
        PartyMode.Shared => Loc("yj.pick.shared", "共享模式"),
        PartyMode.Quest => Loc("yj.pick.quest", "任务模式"),
        PartyMode.Split => Loc("yj.pick.split", "分离模式（各爬各的图）"),
        _ => Loc("yj.pick.off_vanilla", "关闭（原版玩法）"),
    };

    /// <summary>「PvP 方式」选择器上每一项的名字。</summary>
    private static ModSettingsText PvpStyleName(PvpStyle style) => style switch
    {
        PvpStyle.Speed => Loc("yj.pick.speed", "手速模式（友尽尖塔自己的 PvP）"),
        PvpStyle.TurnBased => Loc("yj.pick.turn_based", "回合制模式（PVP 决斗）"),
        _ => Loc("yj.pick.off_pvp", "关闭（不打 PvP）"),
    };

    internal static bool IsChinese
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

            // 老存档里那三个独立布尔，只用来「迁移」成一个玩法模式，之后不再单独保存。
            var greedy = true;   // 老默认：贪婪开着
            var shared = false;
            var quest = false;

            // ① 老格式：只有一个互斥的 mode（0 关闭 / 1 友尽尖塔 / 2 共享 / 3 任务 / 4 卧底）。
            if (dict.ContainsKey(LegacyModeKey))
            {
                switch (dict[LegacyModeKey].AsInt32())
                {
                    case 1:
                        greedy = true;
                        break;
                    case 2:
                        greedy = false;
                        shared = true;
                        break;
                    case 3:
                        greedy = false;
                        quest = true;
                        break;
                    case 4:
                        greedy = false;
                        quest = true;
                        _undercoverPick = UndercoverPick.RandomEachAct;
                        break;
                    default:
                        greedy = false;
                        break;
                }
            }
            // ② 更老格式：只有 gameplay_enabled
            else if (dict.ContainsKey(LegacyGameplayKey))
            {
                greedy = dict[LegacyGameplayKey].AsBool();
            }

            // ③ 四个布尔的旧存档：多个同时开着时按「越特别的越优先」取一个
            //    （贪婪是默认开着的底子，共享/任务都是玩家主动开的）。
            if (dict.ContainsKey(GreedyKey))
                greedy = dict[GreedyKey].AsBool();

            if (dict.ContainsKey(SharedKey))
                shared = dict[SharedKey].AsBool();

            if (dict.ContainsKey(QuestKey))
                quest = dict[QuestKey].AsBool();

            // ④ 新的单选键优先；没有就从旧布尔迁移。
            _partyMode = dict.ContainsKey(PartyModeKey)
                ? NormalizeMode((PartyMode)dict[PartyModeKey].AsInt32())
                : NormalizeMode(quest ? PartyMode.Quest
                    : shared ? PartyMode.Shared
                    : greedy ? PartyMode.Greedy
                    : PartyMode.Off);

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

            if (dict.ContainsKey(ArchitectMintyCompatKey))
                _architectMintyCompat = dict[ArchitectMintyCompatKey].AsBool();

            // 手速模式的木桩：设置页那一行已删，读回来也强制关（旧存档里的 true 不再生效）。
            _speedModeDummy = false;

            // 手速模式保护窗口：新键优先；旧存档只有布尔键 → true 迁成 1 回合、false 迁成关闭。
            if (dict.ContainsKey(SpeedFirstTurnGuardTurnsKey))
                _speedFirstTurnGuardTurns = Math.Clamp(dict[SpeedFirstTurnGuardTurnsKey].AsInt32(), 0, 3);
            else if (dict.ContainsKey(SpeedFirstTurnGuardKey))
                _speedFirstTurnGuardTurns = dict[SpeedFirstTurnGuardKey].AsBool() ? 1 : 0;

            // ⑤ PvP 方式：新的单选键优先，否则从旧的 pvp_speed_mode 布尔迁移。
            _pvpStyle = dict.ContainsKey(PvpStyleKey)
                ? NormalizeStyle((PvpStyle)dict[PvpStyleKey].AsInt32())
                : dict.ContainsKey(PvpSpeedModeKey) && !dict[PvpSpeedModeKey].AsBool()
                    ? PvpStyle.TurnBased
                    : PvpStyle.Speed;

            // ⑥ 追杀模式不依赖任何 PvP 方式，读档不需要额外约束。

            // ⑦ 把两组单选的结果投影回「PVP 决斗」自己那套设置。
            SyncPvpStore();
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
                // 新的单选键（真正生效的两组开关）
                { PartyModeKey, (int)_partyMode },
                { PvpStyleKey, (int)_pvpStyle },

                // 旧键：只为「退回老版本还能读懂」，值由上面两个单选投影出来。
                { GreedyKey, _partyMode == PartyMode.Greedy },
                { SharedKey, _partyMode == PartyMode.Shared },
                { QuestKey, _partyMode == PartyMode.Quest },
                { PvpSpeedModeKey, _pvpStyle == PvpStyle.Speed },

                { GreedyLootKey, _greedyLootEnabled },
                { SharedHpKey, _sharedHpEnabled },
                { SharedGoldKey, _sharedGoldEnabled },
                { SharedEnergyKey, _sharedEnergyEnabled },
                { UndercoverKey, (int)_undercoverPick },
                { QuestPanelKey, _questPanelEnabled },
                { ArchitectHpKey, _architectHpMultiplier },
                { ArchitectFortitudeKey, _architectFortitude },
                { ArchitectCardLimitKey, _architectCardLimit },
                { ArchitectMintyCompatKey, _architectMintyCompat },
                { SpeedDummyKey, false },
                { SpeedFirstTurnGuardKey, _speedFirstTurnGuardTurns > 0 },
                { SpeedFirstTurnGuardTurnsKey, _speedFirstTurnGuardTurns },
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
