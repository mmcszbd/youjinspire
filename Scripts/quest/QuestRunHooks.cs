// ============================================================================
// 任务模式（Quest）：生命周期钩子
// ----------------------------------------------------------------------------
// 阶段 1 只需要"每层开始 → 生成 3 个任务"。
// 用 RitsuLib 的 HookedSingletonModel(HookType.Run)，和原版 Modifier 的
// AfterActEntered 走同一条钩子流；[RegisterSingleton] 由 RitsuLib 自动登记，
// 不要手动 ModelDb.Inject（见交接文档 §4.4）。
//
// 阶段 2 的进度统计会另开一个 HookType.Combat 的单例（伤害/出牌/击杀…），
// 这里保持只做"层"这一级的事。
// ============================================================================

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts.Quest;

/// <summary>任务模式的层（Act）级钩子。</summary>
[RegisterSingleton]
public class QuestRunHooks : HookedSingletonModel
{
    public QuestRunHooks() : base(HookType.Run)
    {
    }

    /// <summary>进入新的一层：确保每个玩家都有本层的 3 个任务。</summary>
    public override Task AfterActEntered()
    {
        try
        {
            if (YouJinSettings.QuestModeEnabled && CurrentRunState is RunState run)
                QuestService.EnsureGeneratedForAct(run);
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Info($"任务模式：AfterActEntered 处理失败 {ex}");
        }

        return Task.CompletedTask;
    }
}

/// <summary>读档等场景下的兜底：补生成当前层的任务。</summary>
public static class QuestLifecycle
{
    private static bool _hooked;

    /// <summary>在 Entry.Init 里调用一次。</summary>
    public static void Init()
    {
        if (_hooked)
            return;

        _hooked = true;

        try
        {
            // 开局兜底：万一这一局没有触发 AfterActEntered（或钩子流还没接上），
            // 至少保证第 1 层有任务。幂等，重复调用无害。
            RitsuLibFramework.SubscribeLifecycle<RunStartedEvent>(
                e => QuestService.EnsureGeneratedForAct(e.RunState),
                true);

            RitsuLibFramework.SubscribeLifecycle<RunLoadedEvent>(
                e => QuestService.EnsureGeneratedForAct(e.RunState),
                true);
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Info($"任务模式：注册读档兜底钩子失败 {ex}");
        }
    }
}
