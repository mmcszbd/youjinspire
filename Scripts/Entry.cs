using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using YouJin.Scripts.Quest;

namespace YouJin.Scripts;

[ModInitializer(nameof(Init))]
public class Entry
{
    public const string ModId = "youjin";
    public static readonly Logger Logger = STS2RitsuLib.RitsuLibFramework.CreateLogger(ModId);

    public static void Init()
    {
        var assembly = Assembly.GetExecutingAssembly();
        STS2RitsuLib.RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

#if DEBUG107
        Logger.Info(
            "★ 本构建针对游戏 0.107.1（配置 Debug107）。已知差异："
            + "0.107 没有 BeforeCombatRewardOffered 钩子，所以「贪婪模式·补刀分赃」暂不生效；"
            + "共享血量/金币/能量、事件、遗物等其余玩法照常工作。");
#endif

        // 事件「建筑师的背叛」的登记交给 RitsuLib 的自动发现
        // （ModTypeDiscoveryHub 会扫描所有 Mod*Template 子类，包括它）。
        //
        // 这里**绝对不要**再手动 ModelDb.Inject：
        // RitsuLib 的注入列表已经包含这个类型，ModelDb.Init 会再 new 一次，
        // 而 AbstractModel 的构造函数一发现同 ID 重复就抛 DuplicateModelException，
        // 直接炸掉 NGame.GameStartup() —— 表现就是「游戏打不开」。
        // 所以这里只是把算出来的 ID 打到日志里，方便对齐本地化键名。
        try
        {
            Logger.Info(
                $"建筑师的背叛：事件 ID = {ModelDb.GetId(typeof(ArchitectBetrayalEvent))}"
                + "（登记由 RitsuLib 自动完成，不要再手动 Inject）");
        }
        catch (Exception ex)
        {
            Logger.Info($"建筑师的背叛：读取事件 ID 失败 {ex}");
        }

        // 把原版建筑师房间换成我们的事件 + 战斗里放开队友目标。
        ArchitectBetrayalPatches.Apply();

        // 给「假寐」「酣睡」这两个火堆选项换上真实存在的原版图标路径。
        // 不改的话它们指向不存在的贴图，原版设置头顶气泡时会抛异常，把火堆流程打断。
        RestSiteIconPatch.Apply();

        // 多人局左上角「自己 + 队友」那排小卡片：F7 三档显隐（正常 → 收起头像 → 整行隐藏）。
        // 纯本地视觉，不同步、不进存档；原版那个调试键只管整排全显/全隐，这里多给一档「收起头像」。
        PartyRowToggle.Apply();

        // 把本 mod 的玩法总开关注册进游戏设置的「模组配置」分区
        // （BonModConfig 那套；没装 BonModConfig 时会自动跳过，不影响玩）。
        YouJinSettings.Register();

        // 商店药水「被祝福的泉水」：注入名字/描述 + 把它的商店售价固定为 150
        // （随机稀有药水，不再固定出现在每间商店）。
        BlessedSpringPotion.EnsureLocalized();
        SmokeBombPotion.EnsureLocalized();

        // 分离模式开局遗物「苹果」的名字/描述（这里只是抢个早，真正生效靠 LateLocalization 的覆盖式注入）。
        AppleRelic.EnsureLocalized();

        // 烟雾弹「脱战但不死」：Creature.get_IsAlive 后置补丁 + 脱战集合（详见 SmokeBombEscape.cs）。
        SmokeBombEscapePatches.Apply();
        BlessedSpringShopPatch.Apply();

        // 第三方 mod 兼容：薄荷尖塔（Minty Spire 2）在背叛 PvP 里会把队友当怪物去读
        // `Creature.Monster`（玩家为 null）→ 血条回调里 NRE。这里在背叛战斗期间把它的
        // 「受到的伤害」统计按死（设置项：建筑师的挑拨 → 屏蔽伤害统计）。没装就什么都不做。
        // 详见 Scripts\compat\MintySpire2Compat.cs。
        MintySpire2Compat.Apply();

        // 多人专属卡 ×10 + 自写能力的文本：挂 LocManager 的后置补丁来注入（覆盖式）。
        // ⚠ 不能在 Init 里直接 merge —— 这时 LocManager 还没建好，是空转（详见 LateLocalization.cs 顶部注释）。
        LateLocalizationPatches.Apply();

        // 新玩法：多人局里所有玩家共用一条血池、一个钱包。
        SharedVitalsPatches.Apply();
        SharedEnergyPatches.Apply();

        // 新玩法：拖动队友（阶段 1 · 纯本地 + 阶段 2 · 甩飞事件广播）。
        // 窗口 = 我结束回合之后、队友还没结束的那段；按住左键抓立绘，
        // 松手甩出去、撞屏幕边反弹、3 秒后瞬移回原位。
        // 同步走 RitsuLib 的 Sidecar（Unreliable/BestEffort/不缓冲）——
        // 丢一条只是对面少看一次，不影响任何状态。
        TeammateDrag.Init();
        TeammateDragSync.Init();

        // 右键互动（Q弹 + 右键攻击特效）已剥离成独立 mod「弹弹弹」
        // （id = boingboing，源码在 D:\RightClickParty）。
        // 这里不再初始化它们 —— 两份都开着的话，同一次右键会被两边各播一遍。

        // 新玩法：任务模式（每人每层 3 个任务，关底随 Boss 奖励结算）。
        // 阶段 1 只做生成 + 显示；进度钩子与奖励挂载在后续阶段接。
        QuestService.Init();
        QuestRewards.Init();
        QuestLifecycle.Init();
        QuestUi.Init();

        // 修佩尔之眼被 Watcher mod 的闸门挡住导致"每回合都送额外回合"的问题。
        PaelsEyeFixPatches.Apply();

        // 手速模式第一轮保护：所有玩家都还没结束第二个回合时，双方都不掉血。
        SpeedFirstTurnGuardPatches.Apply();

        // 建筑师最后那句台词的兜底文本。
        // 注意它走的是 "ancients" 表（建筑师自己的表），不是事件用的 "events" 表。
        ArchitectBetrayalEvent.EnsureArchitectLines();

        // 「PVP 决斗」（原独立 mod pvp_duel 0.15.1）已整棵源码并进本程序集，
        // 装配点改在这里显式调用：设置页 / 全部 Harmony 补丁 / 决斗幕 / 入口事件。
        // 它自己的 [ModInitializer] 特性已去掉，原因见 Scripts\PvpDuel\PvpDuelMain.cs 头注释。
        try
        {
            PvpDuel.Main.Initialize();
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"PVP 决斗：装配失败 {ex}");
        }
    }
}
