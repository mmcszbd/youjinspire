using System.Reflection;
using System.Runtime.CompilerServices;

using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.Diagnostics;

/// <summary>诊断：把「非共享事件（如 EVENT.NEOW）的选项同步」每一步都打出来。</summary>
/// <remarks>
/// 现场：房主收到队友的 <c>OptionIndexChosenMessage</c> 时，自己那份该玩家的事件已经 IsFinished，
/// <c>EventSynchronizer.ChooseOptionForEvent</c> 直接抛
/// <c>InvalidOperationException: Option chosen for player ... but it is already finished!</c>，
/// <c>eventOption.Chosen()</c> 根本没跑 → 队友的祝福只存在于队友机器上 → 离场 checksum 必然分叉。
///
/// 本补丁只加日志，不改任何判定，用来分开两种可能：
///   (a) 房主那份事件被别的路径提前收尾（<c>BeginEvent</c> 的 EnsureCleanup / SetEventFinished / 页推进）；
///   (b) 玩家槽位映射错，打到了另一个已完成的实例上。
/// 每次 <c>set_IsFinished=true</c> 都会带一小段栈，答案就在那几行里。
///
/// 只在 <see cref="ModuleSwitches.Diagnostics"/> 打开时说话；定位完可以整体删掉。
/// </remarks>
[HarmonyPatch]
internal static class EventOptionSyncDiagPatch
{
    private static readonly FieldInfo? EventsField =
        AccessTools.Field(typeof(EventSynchronizer), "_events");

    private static readonly FieldInfo? PageIndexField =
        AccessTools.Field(typeof(EventSynchronizer), "_pageIndex");

    private static readonly FieldInfo? CanonicalEventField =
        AccessTools.Field(typeof(EventSynchronizer), "_canonicalEvent");

    /// <summary>每次有人执行事件选项之前，把「要打到哪一份、那份是不是已经结束」摊开。</summary>
    [HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent")]
    [HarmonyPrefix]
    private static void BeforeChooseOption(EventSynchronizer __instance, Player player, int optionIndex)
    {
        if (!ModuleSwitches.Diagnostics)
        {
            return;
        }

        try
        {
            var target = __instance.GetEventForPlayer(player);
            ModInfo.Info(
                $"[事件同步] ChooseOptionForEvent player={player.NetId} option={optionIndex} "
                + $"target={Describe(target)} hash={RuntimeHelpers.GetHashCode(target)} "
                + $"finished={target.IsFinished} options={target.CurrentOptions.Count} "
                + $"page={PageIndexField?.GetValue(__instance) ?? "?"}");
            DumpAllEvents(__instance, "choose");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[事件同步] 诊断打印失败：{ex.Message}");
        }
    }

    /// <summary>谁把事件标成结束的 —— 带栈，用来区分「提前收尾」和「槽位打错」。</summary>
    [HarmonyPatch(typeof(EventModel), "set_IsFinished")]
    [HarmonyPrefix]
    private static void BeforeSetFinished(EventModel __instance, bool value)
    {
        if (!ModuleSwitches.Diagnostics || !value)
        {
            return;
        }

        try
        {
            ModInfo.Info(
                $"[事件同步] IsFinished=true {Describe(__instance)} "
                + $"owner={__instance.Owner?.NetId.ToString() ?? "?"} "
                + $"hash={RuntimeHelpers.GetHashCode(__instance)}\n{ShortStack()}");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[事件同步] 诊断打印失败：{ex.Message}");
        }
    }

    /// <summary>BeginEvent 会把未完成的旧事件 EnsureCleanup 掉：那是嫌疑人之一，单独记一笔。</summary>
    [HarmonyPatch(typeof(EventModel), "EnsureCleanup")]
    [HarmonyPrefix]
    private static void BeforeEnsureCleanup(EventModel __instance)
    {
        if (!ModuleSwitches.Diagnostics)
        {
            return;
        }

        try
        {
            if (__instance.IsFinished)
            {
                return;
            }

            ModInfo.Warn(
                $"[事件同步] EnsureCleanup 清理了未完成的事件 {Describe(__instance)} "
                + $"owner={__instance.Owner?.NetId.ToString() ?? "?"} "
                + $"hash={RuntimeHelpers.GetHashCode(__instance)}\n{ShortStack()}");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[事件同步] 诊断打印失败：{ex.Message}");
        }
    }

    /// <summary>离场对账那一发：把四份事件的收尾状态一次性打出来（护栏的观测点）。</summary>
    // 0.113 的 ChecksumTracker 有两个同名重载：
    //   GenerateChecksum(string context, GameAction? action)
    //   GenerateChecksum(NetFullCombatState state)
    // 只写方法名时 Harmony 判不出目标（日志里的 “Ambiguous match”），
    // 所以这里显式给出参数类型，钉死带 context 的那一个。
    [HarmonyPatch(
        typeof(ChecksumTracker),
        nameof(ChecksumTracker.GenerateChecksum),
        new[] { typeof(string), typeof(GameAction) })]
    [HarmonyPostfix]
    private static void AfterGenerateChecksum(string context)
    {
        if (!ModuleSwitches.Diagnostics)
        {
            return;
        }

        try
        {
            if (context?.StartsWith("Exiting event room", StringComparison.Ordinal) != true)
            {
                return;
            }

            var synchronizer = RunManager.Instance?.EventSynchronizer;
            if (synchronizer is null)
            {
                return;
            }

            var unfinished = DumpAllEvents(synchronizer, "exit");
            if (unfinished > 0)
            {
                ModInfo.Error(
                    $"[事件同步护栏] 离场时还有 {unfinished} 份事件没结束（上面 dump 里的 finished=False），"
                    + "这一发的 checksum 很可能因为漏结算而对不上");
            }
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[事件同步] 离场诊断打印失败：{ex.Message}");
        }
    }

    /// <summary>打印当前事件槽位表，返回「没结束」的份数。</summary>
    private static int DumpAllEvents(EventSynchronizer synchronizer, string tag)
    {
        if (EventsField?.GetValue(synchronizer) is not IList<EventModel> events)
        {
            ModInfo.Warn($"[事件同步] {tag}: 拿不到 _events");
            return 0;
        }

        var canonical = CanonicalEventField?.GetValue(synchronizer) as EventModel;
        ModInfo.Info(
            $"[事件同步] {tag}: canonical={canonical?.Id.Entry ?? "?"} "
            + $"shared={synchronizer.IsShared} page={PageIndexField?.GetValue(synchronizer) ?? "?"} "
            + $"events={events.Count}");

        var unfinished = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var @event = events[i];
            if (@event is null)
            {
                ModInfo.Info($"[事件同步] {tag}: [{i}] <null>");
                continue;
            }

            if (!@event.IsFinished)
            {
                unfinished++;
            }

            ModInfo.Info(
                $"[事件同步] {tag}: [{i}] {Describe(@event)} "
                + $"owner={@event.Owner?.NetId.ToString() ?? "?"} "
                + $"finished={@event.IsFinished} options={@event.CurrentOptions.Count} "
                + $"hash={RuntimeHelpers.GetHashCode(@event)}");
        }

        return unfinished;
    }

    private static string Describe(EventModel? @event)
        => @event is null ? "<null>" : $"{@event.Id.Entry}";

    /// <summary>只取最靠外的几帧，够看出是谁调的就收手（整栈太长会淹没日志）。</summary>
    private static string ShortStack()
    {
        var trace = new System.Diagnostics.StackTrace(2, fNeedFileInfo: false);
        var frames = trace.GetFrames();
        if (frames is null || frames.Length == 0)
        {
            return "    <no frames>";
        }

        var lines = new List<string>();
        for (var i = 0; i < frames.Length && lines.Count < 6; i++)
        {
            var method = frames[i].GetMethod();
            if (method is null)
            {
                continue;
            }

            lines.Add($"    ← {method.DeclaringType?.Name}.{method.Name}");
        }

        return string.Join("\n", lines);
    }
}
