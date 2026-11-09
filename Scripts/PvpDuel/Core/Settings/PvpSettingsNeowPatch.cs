using System.Reflection;

using HarmonyLib;

using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.Settings;

/// <summary>涅奥：生成祝福时把「设置快照」这个 modifier 暂时藏起来，别让它把原版祝福挤掉。</summary>
/// <remarks>
/// 原版 <c>Neow.GenerateInitialOptions()</c>（反编译见 <c>.tmp_ref/neow.cs:217</c>）的分支判据是
/// <c>Owner.RunState.Modifiers.Count &lt;= 0</c>：
/// <list type="bullet">
/// <item>没有 modifier → 走原版四项祝福（抽签、允许性过滤都在这里）；</item>
/// <item>有 modifier → 只问每个 modifier 要 <c>GenerateNeowOption</c>，谁都不给就返回空列表。</item>
/// </list>
/// 我们为了跨机同步把 <see cref="PvpSettingsModifier"/> 挂进了 <c>RunState.Modifiers</c>，
/// 于是涅奥走进了第二条路 —— 而它本来就不该有涅奥祝福，结果 <c>Modifiers.Count</c> 非零、选项却是空的
/// （2026-11-07 21:26 日志：<c>Event EVENT.NEOW began for player 1 with options: </c> 后面什么都没有，
/// 客户端随后点了个不存在的选项，服务端抛
/// <c>Option chosen for player ... but it is already finished!</c>）。
///
/// 这里在生成选项期间把它从列表里摘掉，生成完立刻放回：本局的设置快照还在，存档/同步都不受影响。
/// 别的 modifier（原版的每日挑战之类）照旧保留 —— 它们本来就该决定涅奥给什么。
/// </remarks>
[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
internal static class PvpSettingsNeowPatch
{
    private static readonly FieldInfo? ModifiersField =
        AccessTools.Field(typeof(RunState), "<Modifiers>k__BackingField");

    private static bool _reportedMissingField;

    [HarmonyPrefix]
    private static void Prefix(Neow __instance, out IReadOnlyList<ModifierModel>? __state)
    {
        __state = null;

        try
        {
            if (ModifiersField is null)
            {
                if (!_reportedMissingField)
                {
                    _reportedMissingField = true;
                    ModInfo.Warn("[PvP 设置同步] 涅奥：找不到 RunState.Modifiers 的字段，跳过摘除（涅奥祝福可能为空）");
                }

                return;
            }

            var run = __instance.Owner?.RunState;
            if (run is null)
            {
                return;
            }

            if (run.Modifiers is not { } modifiers || !modifiers.Any(m => m is PvpSettingsModifier))
            {
                return;
            }

            var kept = modifiers.Where(m => m is not PvpSettingsModifier).ToList();
            ModifiersField.SetValue(run, kept);
            __state = modifiers;

            ModInfo.Info(
                $"[PvP 设置同步] 涅奥：生成祝福时暂时摘掉设置快照（{modifiers.Count} → {kept.Count} 个 modifier）");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[PvP 设置同步] 涅奥：摘除设置快照失败（不影响开局）：{ex.Message}");
        }
    }

    [HarmonyPostfix]
    private static void Postfix(Neow __instance, IReadOnlyList<ModifierModel>? __state)
    {
        if (__state is null || ModifiersField is null)
        {
            return;
        }

        try
        {
            var run = __instance.Owner?.RunState;
            if (run is not null)
            {
                ModifiersField.SetValue(run, __state);
            }
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"[PvP 设置同步] 涅奥：放回设置快照失败：{ex.Message}");
        }
    }
}
