// ============================================================================
// 友尽尖塔（youjin）：把「PVP 决斗」的设置项接到自己的设置页上
// ----------------------------------------------------------------------------
// 二合一之后，原来独立注册的「PVP 决斗」设置页被撤掉，UI 用**扩展方法**接到
// 友尽尖塔自己的设置页里（调用点在 YouJinSettings.Register 的链尾）。
//
// 摆位原则（2026-11-01 改，第二版）：
//   · 两组开关都变成**互斥单选**（Stepper）：玩法四选一（贪婪/共享/任务/分离），
//     PvP 方式二选一（手速/回合制），都带「关闭」项，不能同时开。
//   · 本文件只负责「回合制那一侧」的子选项；两个选项节各自跟随单选结果折叠：
//       手速模式的选项 = YouJinSettings 里的 architect 节（可见性看 PvpStyleSetting == Speed）
//       回合制 PVP 选项 = 这里的 duel / duel_numbers（可见性看 TurnBasedEnabled）
//       分离模式选项     = 这里的 split（可见性看 SplitModeEnabled）
//   · 开关的「真相」在 YouJinSettings（<c>PartyModeSetting</c> / <c>PvpStyleSetting</c>）；
//     <c>PvpSettings.Enabled</c> 与 <c>PvpSettings.ModuleSplitMode</c> 是它写下来的投影，
//     因为决斗那边的补丁只问 PvpSettings（ModuleSwitches.Core / SplitMode）。
//
// ★ 这里只暴露「会改变玩法、玩家真的需要动」的那几项。原设置页里的长说明、
//   纯观测项（横幅 / 限制小抄 / 详细日志）刻意没搬：默认值就是能用的状态，
//   要调再回源码改默认值（PvpSettings.cs），别在界面上堆 20 个开关。
// ★ 数值项单独放在一个子节里，因为 AddString 没有 visibleWhen 参数（只有 AddToggle 有）；
//   想「折叠起来」只能靠整节 WithVisibleWhen。
// ============================================================================

using System;
using System.Globalization;

using MegaCrit.Sts2.Core.Rooms;
using PvpDuel.Core.Settings;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace YouJin.Scripts;

/// <summary>把「PVP 决斗」的设置项接到友尽尖塔设置页上。</summary>
internal static class PvpDuelUi
{
    /// <summary>回合制节里「血量倍数」那一行的 id（值仍存在 YouJinSettings 的 architect_hp_multiplier）。</summary>
    internal const string DuelHpKey = "duel_hp_multiplier";

    /// <summary>回合制节里「群体壁垒」那一行的 id（值仍存在 YouJinSettings 的 architect_fortitude）。</summary>
    internal const string DuelFortitudeKey = "duel_fortitude";

    /// <summary>空保存动作：这两项的值由 <see cref="YouJinSettings" /> 的 setter 自己落盘。</summary>
    private static readonly Action NoSave = static () => { };

    /// <summary>
    /// 设置页读写的那一份：永远是<b>本机</b>配置（<see cref="PvpSettingsStore.Local" />）。
    /// 本局实际生效的设置在 <see cref="PvpSettingsStore.Current" />（可能来自房主），
    /// 这样客户端在局内动这里只改自己的文件，不会把本局改成两端不一致。
    /// </summary>
    private static PvpSettings Settings => PvpSettingsStore.Local;

    /// <summary>
    /// 回合制 PvP（PVP 决斗）是否选中（给 <c>WithVisibleWhen</c> 用）。
    /// 2026-11-01 改：两套 PvP 现在是互斥单选，「真相」在 <see cref="YouJinSettings.PvpStyleSetting" />；
    /// <c>PvpSettings.Enabled</c> 只是它的投影（由 YouJinSettings.SyncPvpStore 写）。
    /// </summary>
    public static bool TurnBasedEnabled => YouJinSettings.PvpStyleSetting == PvpStyle.TurnBased;

    /// <summary>分离模式是否选中（给 <c>WithVisibleWhen</c> 用）。</summary>
    public static bool SplitModeEnabled => YouJinSettings.PartyModeSetting == PartyMode.Split;

    /// <summary>分离模式自己的子选项（没开分离模式时整节折叠）。</summary>
    public static ModSettingsPageBuilder AddSplitModeSections(this ModSettingsPageBuilder page)
    {
        return page.AddSection("split", section => section
            .WithTitle(Loc("pvpui.sec.split_mode", "分离模式选项"))
            .WithVisibleWhen(() => SplitModeEnabled)
            // 2026-11-06：「大厅按开始各自开单机局」这个开关删了 —— 分离模式本来就是各开单机局，
            // 关掉只会得到「分离了但还在同一局」这种没法用的状态。判定改成恒开
            // （ModuleSwitches.SplitSoloRun => SplitMode）。
            .AddToggle(
                "module_split_meeting_fast_test",
                Loc("pvpui.opt.split_meeting_fast_test", "合流测试：打赢第一场就合流"),
                Toggle(s => s.ModuleSplitMeetingFastTest, (s, v) => s.ModuleSplitMeetingFastTest = v),
                // 「狭路相逢（镜像决斗）」那一行已随整个镜像模块一起删除。
                visibleWhen: () => SplitModeEnabled));
    }

    /// <summary>
    /// 测试工具：一行一个「点一下即执行」的开关 —— 跳各种房间 + 秒赢当前战斗。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 设置面板只有开关和输入框，没有按钮控件，所以这里用 <see cref="OneShot" />：
    /// 读永远返回关，只在你把它点亮的那一次执行动作 —— 点一下、动作跑、面板重画时读回关，
    /// 开关自己弹回去。
    /// </para>
    /// <para>
    /// 动作实现在 <c>Scripts/TestTools.cs</c>，等价于控制台的 <c>room &lt;ID&gt;</c> 与 <c>win</c>。
    /// </para>
    /// </remarks>
    public static ModSettingsPageBuilder AddTestToolSections(this ModSettingsPageBuilder page)
    {
        return page
            .AddSection("test_tools", section => section
                .WithTitle(Loc("pvpui.sec.test_tools", "测试工具"))
                .AddToggle(
                    "module_test_tools",
                    Loc("pvpui.opt.test_tools", "显示测试用的快捷开关"),
                    Toggle(s => s.ModuleTestTools, (s, v) => s.ModuleTestTools = v)))
            .AddSection("test_tools_actions", section => section
                .WithTitle(Loc("pvpui.sec.test_actions", "测试：跳房间 / 秒赢"))
                .WithVisibleWhen(() => Settings.ModuleTestTools)
                .AddToggle(
                    "test_win",
                    Loc("pvpui.opt.test_win", "秒赢当前战斗"),
                    OneShot("test_win", TestTools.WinCombat))
                .AddToggle(
                    "test_room_monster",
                    Loc("pvpui.opt.test_room_monster", "跳到：普通战斗房"),
                    OneShot("test_room_monster", () => TestTools.Jump(RoomType.Monster)))
                .AddToggle(
                    "test_room_elite",
                    Loc("pvpui.opt.test_room_elite", "跳到：精英房"),
                    OneShot("test_room_elite", () => TestTools.Jump(RoomType.Elite)))
                .AddToggle(
                    "test_room_boss",
                    Loc("pvpui.opt.test_room_boss", "跳到：Boss 房"),
                    OneShot("test_room_boss", () => TestTools.Jump(RoomType.Boss)))
                .AddToggle(
                    "test_room_treasure",
                    Loc("pvpui.opt.test_room_treasure", "跳到：宝箱房"),
                    OneShot("test_room_treasure", () => TestTools.Jump(RoomType.Treasure)))
                .AddToggle(
                    "test_room_shop",
                    Loc("pvpui.opt.test_room_shop", "跳到：商店"),
                    OneShot("test_room_shop", () => TestTools.Jump(RoomType.Shop)))
                .AddToggle(
                    "test_room_event",
                    Loc("pvpui.opt.test_room_event", "跳到：事件房"),
                    OneShot("test_room_event", () => TestTools.Jump(RoomType.Event)))
                .AddToggle(
                    "test_room_rest",
                    Loc("pvpui.opt.test_room_rest", "跳到：火堆"),
                    OneShot("test_room_rest", () => TestTools.Jump(RoomType.RestSite)))
                .AddToggle(
                    "test_room_map",
                    Loc("pvpui.opt.test_room_map", "回到：地图"),
                    OneShot("test_room_map", () => TestTools.Jump(RoomType.Map))));
    }

    /// <summary>
    /// 「点一下即执行」的假开关：读永远是关，只在你把它点亮的那一次执行动作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每个 id 各自一条持久化键，免得几行共用一个值、互相顶掉。
    /// </para>
    /// <para>
    /// ⚠ 键名必须是 store 里<b>已经注册</b>的那一个（<see cref="PvpSettingsStore.DataKey" />）。
    /// 拼一条新键（<c>settings_test_win</c>）RitsuLib 建条目时会直接毙掉：
    /// <c>KeyNotFoundException: Data key 'settings_test_win' is not registered.</c>
    /// 读恒关、写只执行动作，所以十行共用同一个键也不会互相顶掉取值。
    /// </para>
    /// </remarks>
    private static ModSettingsValueBinding<PvpSettings, bool> OneShot(string id, Action act)
    {
        return new ModSettingsValueBinding<PvpSettings, bool>(
            PvpSettingsStore.StoreModId,
            PvpSettingsStore.DataKey,
            SaveScope.Global,
            static _ => false,
            (_, value) =>
            {
                if (value)
                {
                    PvpDuel.Core.ModInfo.Info($"[测试工具] 触发：{id}");
                    act();
                }
            });
    }

    /// <summary>回合制 PVP 的选项 + 数值两节（没开回合制模式时整节折叠）。</summary>
    public static ModSettingsPageBuilder AddDuelModeSections(this ModSettingsPageBuilder page)
    {
        return page
            .AddSection("duel", section => section
                .WithTitle(Loc("pvpui.sec.turn_based", "回合制 PVP 选项"))
                .WithVisibleWhen(() => TurnBasedEnabled)
                // 2026-11-06：「额外幕 / 擂台赛 / 平衡」三个开关删了 —— 它们本来就是这套玩法
                // 的组成部分，关掉只会得到半截体验（没有决斗幕、事件只剩单选项、没有出牌上限）。
                // 判定改成恒开：ModuleSwitches.ExtraAct / Contest / Balance。
                // ★ 这两项和「手速模式的选项」里的是**同一份值**（YouJinSettings 的
                //   architect_hp_multiplier / architect_fortitude），这里只是第二个入口。
                //   key 用 duel_* 是为了同一页里不出现重复行 id；值的真相仍在 YouJinSettings，
                //   改这边也会立刻写回 youjin_settings.json（setter 自带 Save）。
                .AddIntSlider(
                    DuelHpKey,
                    Loc("pvpui.opt.hp_multiplier", "血量倍数（与手速模式共用）"),
                    ModSettingsBindings.Callback(
                        YouJinSettings.ModId,
                        DuelHpKey,
                        () => YouJinSettings.ArchitectHpMultiplier,
                        v => YouJinSettings.ArchitectHpMultiplier = v,
                        NoSave,
                        SaveScope.Global),
                    1,
                    5,
                    1,
                    v => $"{v}×",
                    Loc("pvpui.desc.hp_multiplier", 
                        "双方生命上限的倍数（改这里等于改「手速模式的选项 → 血量倍数」）。"
                        + "回合制没有回合上限了，想打久一点就把它调高。⚠ 两端必须一致。"))
                .AddToggle(
                    DuelFortitudeKey,
                    Loc("pvpui.opt.team_barricade", "群体壁垒（与手速模式共用）"),
                    ModSettingsBindings.Callback(
                        YouJinSettings.ModId,
                        DuelFortitudeKey,
                        () => YouJinSettings.ArchitectFortitudeEnabled,
                        v => YouJinSettings.ArchitectFortitudeEnabled = v,
                        NoSave,
                        SaveScope.Global)))
            .AddSection("duel_numbers", section => section
                .WithTitle(Loc("pvpui.sec.duel_numbers", "决斗数值"))
                .WithVisibleWhen(() => TurnBasedEnabled && Settings.ModuleBalance)
                .AddString(
                    "max_cards_per_turn",
                    Loc("pvpui.num.max_cards_per_turn", "单回合出牌上限（0 = 不限制）"),
                    Number(s => s.MaxCardsPerTurn, (s, v) => s.MaxCardsPerTurn = v, 0, 99),
                    placeholder: Loc("pvpui.ph.max_cards_per_turn", "例如 15"),
                    maxLength: 2,
                    description: Loc("pvpui.desc.max_cards_per_turn", "一个回合内打出这么多张就自动结束你的回合（本体「时间吞噬者」就是这么卡 12 张的）。"),
                    valueValidationVisual: InRange(0, 99))
                .AddString(
                    "max_same_card_per_turn",
                    Loc("pvpui.num.max_same_card_per_turn", "同一张牌每回合上限（0 = 不限制）"),
                    Number(s => s.MaxSameCardPerTurn, (s, v) => s.MaxSameCardPerTurn = v, 0, 99),
                    placeholder: Loc("pvpui.ph.max_same_card_per_turn", "例如 3"),
                    maxLength: 2,
                    description: Loc("pvpui.desc.max_same_card_per_turn", 
                        "同一张牌本回合打够这么多下就打不出去，专治「两张牌互刷」的经典无限。按牌本身算，同名卡各自计数。"),
                    valueValidationVisual: InRange(0, 99))
                .AddString(
                    "extra_turn_limit",
                    Loc("pvpui.num.extra_turn_limit", "连续额外回合上限（0 = 不限制）"),
                    Number(s => s.ExtraTurnLimit, (s, v) => s.ExtraTurnLimit = v, 0, 99),
                    placeholder: Loc("pvpui.ph.extra_turn_limit", "例如 1"),
                    maxLength: 2,
                    description: Loc("pvpui.desc.extra_turn_limit", "本体发起的「额外回合」（佩尔之眼那类）连着给同一个人的次数上限，超了就不给。"),
                    valueValidationVisual: InRange(0, 99))
                .AddString(
                    "second_energy",
                    Loc("pvpui.num.second_energy", "后手补偿：额外能量（0 = 不给）"),
                    Number(s => s.SecondPlayerEnergy, (s, v) => s.SecondPlayerEnergy = v, 0, 9),
                    placeholder: Loc("pvpui.ph.second_energy", "例如 1"),
                    maxLength: 1,
                    description: Loc("pvpui.desc.second_energy", "后手方（大厅顺序里的第二位）在自己第一个回合多拿这么多能量。"),
                    valueValidationVisual: InRange(0, 9)));
    }

    /// <summary>布尔项：直接读写 <see cref="PvpSettings" />（RitsuLib 负责落盘）。</summary>
    private static ModSettingsValueBinding<PvpSettings, bool> Toggle(
        Func<PvpSettings, bool> read,
        Action<PvpSettings, bool> write)
    {
        return new ModSettingsValueBinding<PvpSettings, bool>(
            PvpSettingsStore.StoreModId,
            PvpSettingsStore.DataKey,
            SaveScope.Global,
            read,
            write);
    }

    /// <summary>整数项：显示与写回都夹在范围内（界面上是输入框）。</summary>
    private static ModSettingsValueBinding<PvpSettings, string> Number(
        Func<PvpSettings, int> read,
        Action<PvpSettings, int> write,
        int min,
        int max)
    {
        return new ModSettingsValueBinding<PvpSettings, string>(
            PvpSettingsStore.StoreModId,
            PvpSettingsStore.DataKey,
            SaveScope.Global,
            settings => Math.Clamp(read(settings), min, max).ToString(CultureInfo.InvariantCulture),
            (settings, value) =>
            {
                if (int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    write(settings, Math.Clamp(parsed, min, max));
                }
            });
    }

    /// <summary>输入框的即时校验（填了范围外的值只标红，不写回）。</summary>
    private static Func<string?, bool> InRange(int min, int max)
    {
        return value =>
            int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= min
            && parsed <= max;
    }

    /// <summary>
    /// 设置界面文字：从本地化表 <c>settings_ui</c>（<c>youjin/localization/zhs|eng/settings_ui.json</c>）取。
    /// 改文案只动那两个 json，不用改代码；表里缺键时用 <paramref name="chineseFallback" /> 顶上，不会空白。
    /// </summary>
    /// <remarks>
    /// ⚠ 表名必须是**原版已有的表**：RitsuLib 扫 mod 的 <c>localization/&lt;lang&gt;/*.json</c> 时，
    /// 只把文件名等于原版表名的文件 <c>Merging with base loc table</c>；叫 <c>settings.json</c> 会被整个跳过，
    /// 查键直接抛 <c>LocException: The loc table='settings' does not exist!</c>（11-06 的日志实证）。
    /// </remarks>
    private static ModSettingsText Loc(string key, string chineseFallback)
        => ModSettingsText.LocString("settings_ui", key, chineseFallback);
}
