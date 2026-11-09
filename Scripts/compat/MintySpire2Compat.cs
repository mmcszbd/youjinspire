// ============================================================================
// 友尽尖塔（youjin）× 薄荷尖塔（Minty Spire 2）：「建筑师的挑拨」PvP 兼容层
// ----------------------------------------------------------------------------
// 症状（用户 2026-10-28 报）：最后「建筑师的挑拨」里队友互殴 → 薄荷尖塔出错。
//
// 根因（反编译 MintySpire2.dll v1.2.0 确认，只有一个爆点）：
//   MintySpire2.MintySpire2Code.combat.SummedIncomingDamageRender.CalculateIncomingDamage
//     foreach (var enemy in creature.CombatState.Enemies.Where(...))
//         foreach (var intent in enemy.Monster.NextMove.Intents)   // ★ 这里
//             if (intent 是攻击) total += ((AttackIntent)intent).GetTotalDamage(new[]{creature}, enemy);
//   · 它只在「玩家的血条 + Config.ShowIncomingDamage 开着 + CombatManager.IsEnemyTurnStarted」时跑
//     （RefreshVisibilityAndText 里先判 c.Player != null 和 c.CombatState != null，怪物血条根本不进来）；
//   · vanilla 里玩家的 Enemies 全是怪物，Monster 一定非空 —— 所以他们没做判空；
//   · 但在「建筑师的挑拨」里，BetrayalCombatView 会把队友算成对手，于是 Enemies 里混进了
//     **Player 生物**；Creature 的玩家构造函数不会给 <Monster> 赋值（怪物构造函数才会），
//     所以 enemy.Monster == null → 在血条刷新回调里抛 NullReferenceException →
//     整个战斗 UI 的搭建被中断（血条/界面坏掉，严重时黑屏）。
//   · 全 dll 里 `get_Monster` / `get_NextMove` 各只有这 1 处调用，所以只要挡住这里就够了。
//
// 处理方式（用户要求：在事件里加一个「屏蔽伤害统计」的开关）：
//   1. 主入口：给 Config.get_ShowIncomingDamage 挂后置补丁 —— 背叛战斗期间一律返回 false，
//      等于"这一场把他们的伤害统计关掉"。这样薄荷连标签都不会建、计算也不会跑，
//      而且**只影响这一场**，不改他们的设置文件、战斗结束自动恢复。
//   2. 兜底：再给 CalculateIncomingDamage 挂一个前缀，万一他们以后改了上面的开关判定、
//      又跑到这里，就直接返回 0（不显示数字）跳过原版，不让它抛异常。
//   3. 两者都受设置项「建筑师的挑拨 → 屏蔽伤害统计」控制（YouJinSettings.ArchitectMintyCompatEnabled，
//      默认开）。关掉 = 放任它报错，用来复现问题。
//
// 薄荷尖塔没装 → 找不到类型 → 什么都不做（不报错、不打日志刷屏）。
// 加载顺序：可能我们比它先初始化，所以除了启动时试一次，还挂了 AppDomain.AssemblyLoad，
// 并在背叛战斗开始时（BetrayalCombatView.Install 附近）再懒重试一次。
// ============================================================================

using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace YouJin.Scripts;

/// <summary>薄荷尖塔（Minty Spire 2）在 PvP 战斗里的兼容层。</summary>
internal static class MintySpire2Compat
{
    private const string HarmonyId = "youjin.compat.mintyspire2";

    /// <summary>薄荷的设置类（ShowIncomingDamage 就在它身上）。</summary>
    private const string ConfigTypeName = "MintySpire2.MintySpire2Code.Config";

    /// <summary>薄荷算「受到的伤害总和」的类。</summary>
    private const string DamageTypeName = "MintySpire2.MintySpire2Code.combat.SummedIncomingDamageRender";

    private static Harmony? _harmony;
    private static bool _getterPatched;
    private static bool _damagePatched;
    private static bool _assemblyHooked;
    private static bool _missingLogged;
    private static bool _skippedLogged;

    /// <summary>Entry.Init 里调一次。</summary>
    public static void Apply()
    {
        try
        {
            HookAssemblyLoad();
            TryPatch("启动时");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"薄荷尖塔兼容：初始化失败 {ex}");
        }
    }

    /// <summary>懒重试（幂等；没装薄荷时是几次字符串/类型查找，基本零开销）。</summary>
    public static void Retry()
    {
        try
        {
            TryPatch("懒重试");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"薄荷尖塔兼容：懒重试失败 {ex}");
        }
    }

    /// <summary>这场战斗里要不要屏蔽他们的伤害统计。</summary>
    private static bool ShouldSuppress()
    {
        if (!YouJinSettings.ArchitectMintyCompatEnabled)
            return false;

        return ArchitectBetrayalEvent.BetrayalCombatActive;
    }

    // ------------------------------------------------------------------
    // 补丁安装
    // ------------------------------------------------------------------

    private static void TryPatch(string reason)
    {
        if (_getterPatched && _damagePatched)
            return;

        var configType = FindType(ConfigTypeName);
        var damageType = FindType(DamageTypeName);

        if (configType is null && damageType is null)
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                Entry.Logger.Info($"薄荷尖塔兼容：没找到 MintySpire2（{reason}），不做任何事");
            }

            return;
        }

        _harmony ??= new Harmony(HarmonyId);

        // ① 主入口：Config.ShowIncomingDamage 的 getter → 背叛战斗期间返回 false
        if (!_getterPatched && configType is not null)
        {
            try
            {
                var getter = configType
                    .GetProperty("ShowIncomingDamage", BindingFlags.Public | BindingFlags.Static)
                    ?.GetGetMethod();
                var postfix = typeof(MintySpire2Compat).GetMethod(
                    nameof(SuppressIncomingDamageGetter), BindingFlags.NonPublic | BindingFlags.Static);

                if (getter is not null && postfix is not null)
                {
                    _harmony.Patch(getter, postfix: new HarmonyMethod(postfix));
                    _getterPatched = true;
                    Entry.Logger.Info($"薄荷尖塔兼容：已接管 Config.ShowIncomingDamage（{reason}）");
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"薄荷尖塔兼容：接管 ShowIncomingDamage 失败（{reason}）{ex}");
            }
        }

        // ② 兜底：万一他们以后改了开关判定又跑到计算方法里，这里不让它碰 Monster
        if (!_damagePatched && damageType is not null)
        {
            try
            {
                var method = damageType.GetMethod(
                    "CalculateIncomingDamage",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(Creature) },
                    null);
                var prefix = typeof(MintySpire2Compat).GetMethod(
                    nameof(SuppressIncomingDamageCalculation), BindingFlags.NonPublic | BindingFlags.Static);

                if (method is not null && prefix is not null)
                {
                    _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                    _damagePatched = true;
                    Entry.Logger.Info($"薄荷尖塔兼容：已接管 SummedIncomingDamageRender.CalculateIncomingDamage（{reason}）");
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"薄荷尖塔兼容：接管 CalculateIncomingDamage 失败（{reason}）{ex}");
            }
        }
    }

    private static void HookAssemblyLoad()
    {
        if (_assemblyHooked)
            return;

        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        _assemblyHooked = true;
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e)
    {
        try
        {
            var name = e.LoadedAssembly.GetName().Name;
            if (string.IsNullOrEmpty(name)
                || name.IndexOf("MintySpire", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            TryPatch($"程序集加载 {name}");
        }
        catch
        {
            // 程序集加载回调里绝不能抛：失败就算了，后面还有懒重试。
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type is not null)
                    return type;
            }
            catch
            {
                // 某些动态程序集 GetType 会抛，忽略。
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 补丁体
    // ------------------------------------------------------------------

    /// <summary>① 背叛战斗期间：把「显示受到的伤害」这门开关按死（只影响这场战斗）。</summary>
    private static void SuppressIncomingDamageGetter(ref bool __result)
    {
        try
        {
            if (!__result || !ShouldSuppress())
                return;

            __result = false;
            LogSkippedOnce();
        }
        catch
        {
            // getter 里抛异常会把薄荷的 UI 打断，宁可不生效。
        }
    }

    /// <summary>② 兜底：这场战斗里直接跳过他们的计算（原版会去读 player.Monster == null）。</summary>
    private static bool SuppressIncomingDamageCalculation(Creature creature, ref int __result)
    {
        try
        {
            if (!ShouldSuppress())
                return true; // 放行原版

            // 只在「PvP 视角」下接管：普通战斗里 creature.CombatState 是真 CombatState，不受影响。
            if (creature?.CombatState is not BetrayalCombatView)
                return true;

            __result = 0;
            LogSkippedOnce();
            return false; // 跳过原版
        }
        catch
        {
            return true;
        }
    }

    private static void LogSkippedOnce()
    {
        if (_skippedLogged)
            return;

        _skippedLogged = true;
        Entry.Logger.Info("薄荷尖塔兼容：这场 PvP 里对手是玩家，已屏蔽第三方「受到的伤害」统计（设置项：建筑师的挑拨 → 屏蔽伤害统计）");
    }
}
