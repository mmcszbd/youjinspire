// ============================================================================
// 友尽尖塔（youjin）：存档保护补丁（结构拆分，零行为变化）
// ----------------------------------------------------------------------------
// 这四个补丁原来住在 ArchitectBetrayalPatches.cs 里，跟「建筑师的背叛」共用同一个宿主。
// 它们其实和服务端数据健壮性有关，和 PvP 一点关系都没有，放在一起的后果是：
//   · 想改 PvP 的人要在一千四百行里翻；
//   · 出存档问题的人第一眼根本不会点开"建筑师背叛"那个文件。
// 现在单独一个文件，入口是下面的 ArchitectSaveGuards.Apply（由 ArchitectBetrayalPatches.Apply
// 的第 C 组调用，安装时机与顺序和拆分前完全一致）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Ancients;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace YouJin.Scripts;


/// <summary>
/// ⑬ 存档保护：**不是事件的东西，不许写进"已发现事件"**。
/// ----------------------------------------------------------------------------
/// 背景（一次真实的"游戏打不开"事故）：
///   我们的木桩遭遇声明成 `RoomType.Event`（这样事件战斗的背景/流程才正常，见
///   BetrayalEggEncounter.RoomType 的注释），而原版会按 RoomType 把"事件房"的 id
///   记进进度存档 `discovered_events`：
///       ProgressSaveManager.UpdateWithRunData → ProgressState.MarkEventAsSeen(id)
///   于是存档里出现了 `"ENCOUNTER.YOUJIN_ENCOUNTER_BETRAYAL_EGG_ENCOUNTER"`。
///   而启动读档时那份列表是**当 `EventModel` 用的**：
///       InvalidCastException: Unable to cast 'BetrayalEggEncounter' to 'EventModel'
///   异常发生在 NGame.GameStartup() → 游戏直接起不来。
///
/// 修法分**读、写两道**（⚠ 只堵写是不够的 —— 实测踩过）：
///   · 写侧（本补丁）：MarkEventAsSeen 只放行真事件；
///   · 读侧（DiscoveredEventReadGuardPatch）：FromSerializable 解析 discovered_events 之前，
///     先把"不是事件"的脏条目剔掉 —— 这样**已经被旧版本写脏的存档**也能自己恢复，
///     否则那种存档每次启动都会在 NGame.GameStartup() 崩掉。
///
/// ⚠⚠ 探针千万别写 `ModelDb.GetByIdOrNull&lt;EventModel&gt;(id)`：
///   它的实现是 `_contentById.TryGetValue(id, out var m); return (T)m;` ——
///   "id 存在但不是这个类型"时**直接抛 InvalidCastException**，不是返回 null。
///   （原来的写侧保护就是因为这个，一探就抛 → catch 里"按原版放行" → 保护静默失效。）
///   正确写法：先取 `AbstractModel`（任何模型都能转成功），再用 `is EventModel` 判断。
/// </summary>
[HarmonyPatch(typeof(ProgressState), nameof(ProgressState.MarkEventAsSeen))]
internal static class EventSeenGuardPatch
{
    // ⚠ 参数一律用 __0（位置参数）：原方法的参数名是 `eventId` 而不是 `id`，
    //    写名字会让 Harmony 直接报 "Parameter ... not found" 而**整个补丁装不上**
    //    （踩过：日志里显示"存档保护补丁安装失败"，保护静默失效）。
    private static bool Prefix(ModelId __0, ref bool __result)
    {
        try
        {
            if (IsRealEvent(__0))
                return true; // 真的是事件 —— 走原版

            __result = false;

            Entry.Logger.Info(
                $"存档保护：拦下一条不是事件的 id（{__0}）—— 不写进 discovered_events，"
                + "否则下次启动会因 InvalidCastException 打不开游戏");

            return false; // 跳过原方法
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"存档保护：判定失败（按原版放行）{ex}");
            return true;
        }
    }

    /// <summary>
    /// 不抛异常的判定：这个 id 解析出来是不是一个真正的事件模型。
    /// （先取 AbstractModel —— 它是所有模型的基类，转换不会失败；再去比对类型。）
    /// </summary>
    internal static bool IsRealEvent(ModelId id)
        => ModelDb.GetByIdOrNull<AbstractModel>(id) is EventModel;
}

/// <summary>
/// ⑭ 存档保护（读侧）：读进度存档时，先把 `discovered_events` 里"不是事件"的脏条目剔掉。
/// ----------------------------------------------------------------------------
/// 崩溃链（队友日志实测）：
///   NGame.GameStartup → SaveManager.InitProgressData → ProgressSaveManager.LoadProgress
///     → ProgressState.FromSerializable → ParseDiscoveredSet&lt;EventModel&gt;
///     → ModelDb.GetByIdOrNull&lt;EventModel&gt; → **InvalidCastException**
///       "Unable to cast object of type 'YouJin.Scripts.BetrayalEggEncounter'
///        to type 'MegaCrit.Sts2.Core.Models.EventModel'."
///   → 游戏弹错误框、起不来。
///
/// 因为写侧保护曾经失效（见上），线上已经有存档被写进了
/// `ENCOUNTER.YOUJIN_ENCOUNTER_BETRAYAL_EGG_ENCOUNTER`。这条脏数据在"读"的时候炸，
/// 所以必须在读之前清掉：清完原方法就看不见它了，下次存档写回时也随之消失（自愈）。
/// </summary>
[HarmonyPatch(typeof(ProgressState), nameof(ProgressState.FromSerializable))]
internal static class DiscoveredEventReadGuardPatch
{
    // FromSerializable(SerializableProgress save, DeserializationContext ctx) —— 静态方法，
    // 参数用位置写法 __0（和上面同样的理由：不依赖参数名）。
    private static bool _loggedOnce;

    private static void Prefix(SerializableProgress __0)
    {
        try
        {
            var ids = __0?.DiscoveredEvents;
            if (ids is null)
                return;

            if (!_loggedOnce)
            {
                _loggedOnce = true;
                Entry.Logger.Info($"存档保护：FromSerializable 读档保护已生效（discovered_events 共 {ids.Count} 条）");
            }

            if (ids.Count == 0)
                return;

            var removed = ids.RemoveAll(id => !EventSeenGuardPatch.IsRealEvent(id));
            if (removed > 0)
            {
                Entry.Logger.Info(
                    $"存档保护：读档时清掉 {removed} 条「不是事件、却被写进 discovered_events」的脏数据"
                    + "（否则 NGame.GameStartup 会因 InvalidCastException 起不来）");
            }
        }
        catch (Exception ex)
        {
            // 兜底：清理失败也绝不能让读档流程挂掉。
            Entry.Logger.Info($"存档保护：读档清理失败（按原版放行）{ex}");
        }
    }
}

/// <summary>
/// ⑮ 存档保护（读档前修文件）—— 上面两道保护的兜底。
/// ----------------------------------------------------------------------------
/// 为什么还要这一道：队友更新到 0.6.2 之后，补丁⑭ 虽然显示"安装完成"，
/// 但日志里没有"读档时清掉 N 条"那行、崩溃也依旧 —— 说明**内存里那道没赶上**
/// （不是没跑到，就是那次解析走的不是这个对象）。
/// 所以这里换个更靠前、也更硬的落点：`SaveManager.InitProgressData`（崩溃堆栈里
/// LoadProgress 的上一帧，必然在所有解析之前执行），在游戏读文件**之前**，
/// 把 `progress.save` 里 `discovered_events` 中"不是 EVENT.* 的条目"从原文里删掉。
///
/// 做法是**字符串手术**：只替换 `discovered_events` 那个数组的内容，
/// 文件其余部分一个字节都不动（不整体重新序列化，避免动坏其它字段）。
/// </summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.InitProgressData))]
internal static class ProgressSaveFileRepairPatch
{
    private static void Prefix()
    {
        try
        {
            var saveManager = SaveManager.Instance;
            if (saveManager is null)
            {
                Entry.Logger.Info("存档保护：读档前修复跳过（SaveManager.Instance 还没就绪）");
                return;
            }

            // ⚠ 不能只信 GetProgressPathForProfile：它返回的是**档案相对路径**
            //    （形如 `modded/profile1/saves/progress.save`），真实文件却在
            //    `user://steam/<steamid>/modded/profile1/saves/progress.save`。
            //    拿相对路径去问 FileAccess.FileExists 永远 false → "跳过" → LoadProgress
            //    照样读那份脏存档 → NGame.GameStartup 抛 InvalidCastException → 打不开游戏。
            //    （0.9.5 线上事故实证：日志里正是"读档前修复跳过（没有进度存档：
            //      modded/profile1/saves/progress.save）"，下一行就是那个 InvalidCast。）
            var rel = MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager
                .GetProgressPathForProfile(saveManager.CurrentProfileId, null);
            var candidates = BuildCandidatePaths(rel);
            if (candidates.Count == 0)
            {
                Entry.Logger.Info("存档保护：读档前修复跳过（拿不到进度存档路径）");
                return;
            }

            var hit = 0;
            var totalRemoved = 0;
            foreach (var path in candidates)
            {
                try
                {
                    if (!Godot.FileAccess.FileExists(path))
                        continue;

                    hit++;
                    var text = Godot.FileAccess.GetFileAsString(path);
                    var cleaned = RemoveNonEventEntries(text, out var removed);
                    if (removed <= 0 || cleaned is null)
                        continue;

                    using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
                    if (file is null)
                    {
                        Entry.Logger.Info($"存档保护：发现 {removed} 条脏数据但文件打不开（{path}）");
                        continue;
                    }

                    file.StoreString(cleaned);
                    totalRemoved += removed;
                    Entry.Logger.Info(
                        $"存档保护：读档前从进度存档里删掉 {removed} 条「不是事件」的 discovered_events 脏条目"
                        + $"（{path}）");
                }
                catch (Exception ex)
                {
                    Entry.Logger.Info($"存档保护：修复 {path} 失败（跳过）{ex.Message}");
                }
            }

            if (hit == 0)
            {
                Entry.Logger.Info(
                    "存档保护：读档前修复跳过（所有候选路径都不存在：" + string.Join(" | ", candidates) + "）");
            }
            else if (totalRemoved == 0)
            {
                Entry.Logger.Info($"存档保护：读档前检查过 {hit} 份进度存档，discovered_events 干净");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"存档保护：读档前修复失败（按原版放行）{ex}");
        }
    }

    /// <summary>
    /// 把 `GetProgressPathForProfile` 给出的路径展开成**真实可用的 Godot 路径候选**。
    /// 它本身只给档案相对路径（`modded/profile1/saves/progress.save`），完整路径要补
    /// `user://steam/&lt;steamid&gt;/` 这一段；不同账号/正式存档与 modded 存档都要试一遍。
    /// </summary>
    private static List<string> BuildCandidatePaths(string? rel)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(rel))
            return list;

        if (rel.Contains("://", StringComparison.Ordinal))
        {
            list.Add(rel);
            return list;
        }

        list.Add("user://" + rel);

        try
        {
            var accounts = Godot.DirAccess.GetDirectoriesAt("user://steam");
            if (accounts is not null)
            {
                foreach (var account in accounts)
                {
                    var path = $"user://steam/{account}/{rel}";
                    if (!list.Contains(path))
                        list.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"存档保护：枚举 user://steam 失败（{ex.Message}）");
        }

        // 3) 兜底大扫：RitsuLib 的进度镜像（progress_records_mirror.save）等"第二份
        //    带 discovered_events 的文件"—— 它们不在 GetProgressPathForProfile 的路径里，
        //    但会被合并回进度（LoadProgress 上有 RitsuLib 的 N 个补丁），脏数据一样炸。
        foreach (var root in new[] { "user://steam", "user://default" })
            AddScannedSaves(root, 0, list);

        return list;
    }

    private const int ScanMaxDepth = 7;
    private const long ScanMaxBytes = 8L * 1024 * 1024;

    private static void AddScannedSaves(string dir, int depth, List<string> list)
    {
        if (depth > ScanMaxDepth)
            return;

        try
        {
            if (!Godot.DirAccess.DirExistsAbsolute(dir))
                return;

            var files = Godot.DirAccess.GetFilesAt(dir);
            if (files is not null)
            {
                foreach (var file in files)
                {
                    var lower = file.ToLowerInvariant();
                    if (!lower.EndsWith(".save", StringComparison.Ordinal))
                        continue;
                    if (!lower.Contains("progress", StringComparison.Ordinal)
                        && !lower.Contains("mirror", StringComparison.Ordinal))
                        continue;

                    var full = dir + "/" + file;
                    if (list.Contains(full))
                        continue;

                    try
                    {
                        using var probe = Godot.FileAccess.Open(full, Godot.FileAccess.ModeFlags.Read);
                        if (probe is null || probe.GetLength() > ScanMaxBytes)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    list.Add(full);
                }
            }

            var dirs = Godot.DirAccess.GetDirectoriesAt(dir);
            if (dirs is null)
                return;

            foreach (var sub in dirs)
                AddScannedSaves(dir + "/" + sub, depth + 1, list);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"存档保护：扫描 {dir} 失败（{ex.Message}）");
        }
    }

    /// <summary>
    /// 只动 `"discovered_events": [ ... ]` 里的内容：保留以 `EVENT.` 开头的条目，
    /// 其余（例如我们那个 `ENCOUNTER.YOUJIN_...`）删掉。返回 null = 不需要改。
    /// </summary>
    private static string? RemoveNonEventEntries(string text, out int removed)
    {
        removed = 0;

        var key = text.IndexOf("\"discovered_events\"", StringComparison.Ordinal);
        if (key < 0)
            return null;

        var open = text.IndexOf('[', key);
        if (open < 0)
            return null;

        var close = text.IndexOf(']', open);
        if (close < 0)
            return null;

        var body = text.Substring(open + 1, close - open - 1);
        var matches = Regex.Matches(body, "\"([^\"]*)\"");
        if (matches.Count == 0)
            return null;

        var kept = new List<string>();
        foreach (Match match in matches)
        {
            var id = match.Groups[1].Value;
            if (id.StartsWith("EVENT.", StringComparison.Ordinal))
                kept.Add(id);
            else
                removed++;
        }

        if (removed == 0)
            return null;

        var rebuilt = string.Join(",", kept.Select(id => "\"" + id + "\""));
        return text[..(open + 1)] + rebuilt + text[close..];
    }
}

/// <summary>
/// ⑯ 存档保护（读侧 · ancient_stats）：`ancient_stats[].ancient_id` 也会被当 `EventModel` 用。
/// ----------------------------------------------------------------------------
/// 崩溃链（队友日志实测，0.9.63 已清掉 discovered_events 之后仍然炸）：
///   ProgressSaveManager.LoadProgress → ProgressState.ParseAncientStats
///     → ModelDb.GetByIdOrNull&lt;EventModel&gt;(ancient_id) → **InvalidCastException**
///       "Unable to cast 'BetrayalEggEncounter' to 'EventModel'"
///
/// 为什么会有：木桩遭遇是 `RoomType.Event` 的遭遇，原版把"事件房里碰到的东西"
/// 同时记进了 discovered_events 和 ancient_stats —— 两处都是 `EventModel` 类型，
/// 两处都会炸。⑮ 的文件手术只清了 discovered_events，所以这次死在这一边。
///
/// 这里直接过滤 `ParseAncientStats(List&lt;AncientStats&gt; …)` 的**入参**：
/// 入口处就把"不是事件"的条目删掉，后面的转换自然看不见它们（并被原版当作未知 id
/// 记进 _unknownAncientStats，不影响启动）。
/// </summary>
[HarmonyPatch(typeof(ProgressState), "ParseAncientStats")]
internal static class AncientStatsReadGuardPatch
{
    // 参数用位置写法 __0（不依赖参数名）：原签名是
    //   ParseAncientStats(List<AncientStats> src, Dictionary<ModelId,AncientStats> d1,
    //                     Dictionary<ModelId,AncientStats> d2, DeserializationContext ctx)
    private static void Prefix(List<AncientStats> __0)
    {
        try
        {
            if (__0 is null || __0.Count == 0)
                return;

            var removed = __0.RemoveAll(stats => !EventSeenGuardPatch.IsRealEvent(stats.Id));
            if (removed > 0)
            {
                Entry.Logger.Info(
                    $"存档保护：读档时剔掉 {removed} 条「不是事件」的 ancient_stats 条目"
                    + "（否则 ParseAncientStats 里 GetByIdOrNull<EventModel> 会抛 InvalidCastException）");
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"存档保护：ancient_stats 清理失败（按原版放行）{ex.Message}");
        }
    }
}

/// <summary>安装「存档保护」这一组补丁。</summary>
/// <remarks>
/// 只管"别人的存档里带着我们不认识的脏数据"这一类问题，全部是读取侧净化 + 写入侧拦截，
/// 不改任何游戏规则。注册顺序与拆分前一致。
/// </remarks>
internal static class ArchitectSaveGuards
{
    public static void Apply(Harmony harmony)
    {
        ArchitectBetrayalPatches.PatchOne(harmony, typeof(EventSeenGuardPatch), "存档保护：非事件不写进已发现事件");
        ArchitectBetrayalPatches.PatchOne(harmony, typeof(DiscoveredEventReadGuardPatch), "存档保护：读档时清掉脏的已发现事件");
        ArchitectBetrayalPatches.PatchOne(harmony, typeof(ProgressSaveFileRepairPatch), "存档保护：读档前修进度存档文件");
        ArchitectBetrayalPatches.PatchOne(harmony, typeof(AncientStatsReadGuardPatch), "存档保护：读档时清掉脏的 ancient_stats");
    }
}

