// ============================================================================
// 任务模式（Quest）：一局级（Run）钩子
// ----------------------------------------------------------------------------
// 这里只放**一局级**的事件：这类 Hook.AfterXxx 内部走的是
//     IRunState.IterateHookListeners(...)
// 一局级订阅者**永远**会被遍历（战斗内、战斗外都是），所以这类事件必须挂在这一侧。
//
// ⚠ 踩过的坑：死亡 / 火堆休息 / 火堆锻造都是一局级事件。
//   挂在 HookType.Combat 上时，战斗里能收到（调用方带了 CombatState，
//   一局级遍历会顺带把战斗级订阅者也发一遍），但战斗外只有一局级订阅者能收到。
//
// 战斗专属的钩子（伤害/出牌/格挡/Power/球/召唤/星辰/锻造）在 QuestCombatHooks.cs。
// ============================================================================

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts.Quest;

/// <summary>任务模式的一局级钩子：进层生成 + 金币/回复/药水/击杀。</summary>
[RegisterSingleton]
public class QuestRunHooks : HookedSingletonModel
{
    public QuestRunHooks() : base(HookType.Run)
    {
    }

    private static bool Active => QuestService.Enabled;

    /// <summary>进入新的一层：确保每个玩家都有本层的 3 个任务。</summary>
    public override Task AfterActEntered()
    {
        try
        {
            if (Active && CurrentRunState is RunState run)
                QuestService.EnsureGeneratedForAct(run);
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Info($"任务模式：AfterActEntered 处理失败 {ex}");
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 一局级：火堆 / 击杀
    // ------------------------------------------------------------------

    /// <summary>在火堆选择「休息」。</summary>
    public override Task AfterRestSiteHeal(Player player, bool someFlag)
    {
        if (Active)
            QuestService.Report(player, QuestGoal.RestAtCampfire, 1);

        return Task.CompletedTask;
    }

    /// <summary>在火堆选择「锻造」（升级一张牌）。</summary>
    public override Task AfterRestSiteSmith(Player player)
    {
        if (Active)
            QuestService.Report(player, QuestGoal.SmithCard, 1);

        return Task.CompletedTask;
    }

    /// <summary>敌人死亡：算击杀（精英战里额外算一次精英击杀）。</summary>
    public override Task AfterDeath(
        PlayerChoiceContext choiceContext,
        Creature creature,
        bool wasRemovalPrevented,
        float deathAnimLength)
    {
        if (!Active)
            return Task.CompletedTask;

        if (!creature.IsEnemy)
            return Task.CompletedTask;

        // 玩家本体 / 宠物不算"敌人"（宠物 IsEnemy 可能为 true）。
        if (creature.Player is not null || creature.PetOwner is not null)
            return Task.CompletedTask;

        // 击杀归属：直接套用贪婪模式的"补尾刀"结果
        // （CombatLootTracker 在 BeforeDeath 里已经把归属算好了：最后一击来源 →
        //   上灾厄最多 → 上毒最多 → 随机存活玩家）。
        Player? killer = null;
        if (LootLedger.KillerOf.TryGetValue(creature, out var resolved) && resolved is not null)
            killer = resolved;

        if (killer is null
            && LootLedger.LastDamageDealer.TryGetValue(creature, out var dealer)
            && dealer is not null)
        {
            killer = dealer;
        }

        killer ??= QuestService.ActionOwner();

        if (killer is null)
            return Task.CompletedTask;

        QuestService.Report(killer, QuestGoal.KillEnemies, 1);

        var run = RunManager.Instance?.DebugOnlyGetState();
        if (run?.CurrentRoom is CombatRoom room && room.RoomType == RoomType.Elite)
            QuestService.Report(killer, QuestGoal.KillElites, 1);

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
