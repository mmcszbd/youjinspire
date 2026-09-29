using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib;
using STS2RitsuLib.Interop;

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

        // 把本 mod 的玩法总开关注册进游戏设置的「模组配置」分区
        // （BonModConfig 那套；没装 BonModConfig 时会自动跳过，不影响玩）。
        YouJinSettings.Register();

        // 商店药水「被祝福的泉水」：注入名字/描述 + 把它固定塞进每间商店的药水位。
        BlessedSpringPotion.EnsureLocalized();
        SmokeBombPotion.EnsureLocalized();
        BlessedSpringShopPatch.Apply();

        // 建筑师最后那句台词的兜底文本。
        // 注意它走的是 "ancients" 表（建筑师自己的表），不是事件用的 "events" 表。
        ArchitectBetrayalEvent.EnsureArchitectLines();
    }
}
