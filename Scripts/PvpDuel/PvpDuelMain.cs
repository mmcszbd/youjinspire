using System.Reflection;

using HarmonyLib;

using MegaCrit.Sts2.Core.Modding;
using PvpDuel.Core;
using PvpDuel.Core.Settings;
using PvpDuel.Core.SplitMode;

namespace PvpDuel;

/// <summary>「PVP 决斗」入口：只负责装配（初始化设置、逐类装补丁）。</summary>
/// <remarks>
/// 本文件<b>不含任何业务逻辑</b>。模块划分与依赖方向（自上而下单向，下层不引用上层）：
/// <list type="number">
/// <item><description>Api/ —— 对外接口（别的 mod 用，不参与本 mod 自身逻辑）。</description></item>
/// <item><description>各模块 <c>Patches/</c> —— 薄适配层：只判断「本体走到这一步该通知谁」，不含业务逻辑。</description></item>
/// <item><description>Duel/ Act/ Entry/ —— 功能模块：决斗本体、额外幕、入口事件（各自一个模块开关）。</description></item>
/// <item><description>Settings/ Interop/ Diagnostics/ —— 基础模块：设置、together 接口存根、日志与诊断。</description></item>
/// </list>
/// 命名空间跟着模块走：<c>PvpDuel</c>（入口）/ <c>PvpDuel.Core</c>（基础设施）/ <c>PvpDuel.Core.Duel</c>、
/// <c>.Act</c>、<c>.Entry</c>、<c>.Settings</c> 等，模块内的子目录只做文件分组。详见 <c>docs/ARCHITECTURE.md</c>。
/// </remarks>
// 二合一说明：本文件原来靠 [ModInitializer] 自己启动（那时它是独立 mod）。
// 现在整棵源码并进了 youjin.dll，装配点改成由 Entry.Init() 显式调用 Initialize()，
// 特性必须去掉 —— 一个程序集里只能有一个 ModInitializer，否则先发现的赢、另一个的补丁永远不装。
public static class Main
{
    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var harmony = new Harmony(ModInfo.ModId);

        // 设置要在任何「读设置」的代码之前就位。
        // 二合一之后这一段尤其要各自兜住：设置页注册失败（例如 RitsuLib 认不出 pvp_duel
        // 这个"已不存在的 mod id"）绝不能连带让下面几十个补丁一个都不装 ——
        // 那样症状是"整块功能凭空消失"，日志里只有一条，极难查。
        TryStep("加载设置", PvpSettingsStore.Initialize);

        // 设置存储刚就位，立刻把友尽尖塔那边的「PvP 方式」单选投影过来。
        // 为什么非在这里再来一发：YouJinSettings 的读档可能比这一步更早
        // （任何补丁读到 GreedyEnabled 之类都会触发），那一次投影会因为存储没起来而落空，
        // 于是 PvpSettings.Enabled 停在默认 true —— 玩家明明选了手速模式，回合制却照样生效。
        TryStep("同步 PvP 方式", YouJin.Scripts.YouJinSettings.SyncPvpStore);

        // 设置页不在这里注册：二合一之后整块 UI 并进了友尽尖塔自己的设置页
        // （YouJinSettings.Register 链尾的 .AddDuelModeSections()，实现在 Scripts/PvpDuelUi.cs）。
        // PvpModSettingsPage 那个独立页保留文件但不再调用 —— 否则设置界面里会多出一个
        // 挂着「PVP 决斗」名字的独立 tab，等于同一批开关画两遍。
        TryStep("挂状态 HUD", SplitStatusHud.EnsureRunningDeferred);

        // 分离模式的坐标同步：模块开着就先把通道注册好（订阅要早，免得对面先发我们收不到）。
        if (ModuleSwitches.SplitPresence)
        {
            TryStep("注册坐标通道", SplitPresenceChannel.Initialize);
        }

        var applied = 0;
        var failed = 0;

        foreach (var type in assembly.GetTypes())
        {
            // ★ 二合一的关键一行：现在 GetExecutingAssembly() 是 youjin.dll，
            //   里面还有友尽尖塔自己那几十个 [HarmonyPatch] 类。不按命名空间筛，
            //   这里会把它们**再装一遍**（Harmony 允许同一个类挂两次 → 前缀跑两遍，
            //   "队友搬到对面/木桩挪最右"这类一次性动作会重复执行，症状极难查）。
            if (type.Namespace is null || !type.Namespace.StartsWith("PvpDuel", StringComparison.Ordinal))
            {
                continue;
            }

            if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
            {
                continue;
            }

            try
            {
                harmony.CreateClassProcessor(type).Patch();
                applied++;
            }
            catch (Exception ex)
            {
                failed++;
                ModInfo.Error($"补丁类 {type.FullName} 应用失败：{Describe(ex)}");
            }
        }

        ModInfo.Info(
            $"initialized v{ModInfo.Version}; 模块：{ModuleSwitches.Describe()}; "
            + $"patch classes applied={applied} failed={failed}; "
            + $"Harmony patched {harmony.GetPatchedMethods().Count()} method(s).");
    }

    /// <summary>装配期的一步：出错只报一条，绝不中断后面的步骤。</summary>
    private static void TryStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            ModInfo.Error($"装配步骤「{name}」失败（其余步骤照常继续）：{Describe(ex)}");
        }
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join("  ←  ", parts);
    }
}
