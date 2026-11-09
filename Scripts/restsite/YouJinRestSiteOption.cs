// ============================================================================
// 友尽尖塔（youjin）：火堆选项的公共壳
// ----------------------------------------------------------------------------
// 四个自建火堆选项（假寐 / 偷牌 / 偷遗物 / 酣睡）结构完全一样：
//   常亮 → 点一下 → 拿模型 Instance → 结算 → 报告成败。
// 之前四份逐行相同的 IsEnabled / OnSelect / Instance 判空，统一到这里。
//
// 加入逻辑也一样：多人局才加、同一火堆只加一次、出错只记日志不外抛。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;

namespace YouJin.Scripts;

/// <summary>友尽尖塔的四个火堆选项共用的壳：一律常亮，点一下就把活干完。</summary>
public abstract class YouJinRestSiteOption : RestSiteOption
{
    protected YouJinRestSiteOption(Player owner) : base(owner)
    {
    }

    /// <summary>自建选项不设灰态：进得来就点得动。</summary>
    public sealed override bool IsEnabled => true;

    public sealed override Task<bool> OnSelect() => ActivateAsync();

    /// <summary>真正的结算；返回 false 表示这次没做成（别算作已选）。</summary>
    protected abstract Task<bool> ActivateAsync();
}

/// <summary>火堆选项的加入逻辑。</summary>
internal static class YouJinRestSiteOptions
{
    /// <summary>
    /// 多人局才加、同一火堆只加一次；<paramref name="add" /> 抛异常就记日志并当作没加。
    /// </summary>
    public static bool TryAdd(
        Player player,
        ICollection<RestSiteOption> options,
        string optionId,
        string tag,
        Action add)
    {
        try
        {
            if (!YouJinSettings.GameplayEnabled)
                return false;

            var run = player.RunState;
            if (run is null || run.Players.Count <= 1)
                return false;

            if (options.Any(option => option.OptionId == optionId))
                return false;

            add();
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"{tag}：添加选项失败 {ex}");
            return false;
        }
    }
}
