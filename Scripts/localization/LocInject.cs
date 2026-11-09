// ============================================================================
// 友尽尖塔（youjin）：把自建内容的文案注入原版表
// ----------------------------------------------------------------------------
// 全仓有九处「拿到表 → 判断是不是已经注入过 → 按当前语言选中文/英文 → MergeWith」
// 的同构代码，只有表名、探针键、日志前缀和是否覆盖式不同。统一到这里。
//
// 两条约定：
//   · 表还没建好（LocManager 尚未初始化 / 表还没加载）就静默跳过 —— 调用方会在
//     Entry 里先试一次，LateLocalization 在 SetLanguage 之后再补一次；
//   · 探针键已存在就整批跳过，所以多处重复调用是幂等的。
// ============================================================================

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace YouJin.Scripts;

internal static class LocInject
{
    /// <summary>
    /// 把一批文案并入 <paramref name="tableName" />。
    /// <paramref name="probeKey" /> 已存在就跳过（幂等）。
    /// </summary>
    public static void Merge(
        string tableName,
        string probeKey,
        Func<bool, Dictionary<string, string>> build,
        string tag)
        => Merge(tableName, probeKey, false, build, tag);

    /// <summary>
    /// 同上，<paramref name="force" /> = true 时覆盖已有条目
    /// （LateLocalization 在语言表加载完之后用它改文案，不用重导 PCK）。
    /// </summary>
    public static void Merge(
        string tableName,
        string probeKey,
        bool force,
        Func<bool, Dictionary<string, string>> build,
        string tag)
    {
        try
        {
            var loc = LocManager.Instance;
            var table = loc?.GetTable(tableName);
            if (loc is null || table is null)
                return;

            if (!force && table.HasEntry(probeKey))
                return;

            var chinese = (loc.Language ?? string.Empty)
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            table.MergeWith(build(chinese));
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"{tag}：本地化注入失败 {ex}");
        }
    }
}
