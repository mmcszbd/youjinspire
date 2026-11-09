using System.Reflection;

using HarmonyLib;

using MegaCrit.Sts2.Core.Achievements;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using PvpDuel.Core.Entry;
using YouJin.Scripts;

namespace PvpDuel.Core.Act;

/// <summary>把「PVP 决斗」额外幕追加到本局末尾。</summary>
/// <remarks>
/// 挂点选在 <c>RunState.CreateForNewRun</c>：章节列表在这里定下来，而调用方紧接着会走
/// <c>RunManager.GenerateRooms</c>（它是 <c>for (i in 0..Acts.Count)</c> 全覆盖），
/// 所以额外幕的房间会被正常生成，不需要自己调 GenerateRooms。
/// 判据只认「正好两人」这种两端一致的事实；本机开关也读一下 —— 两端开关不一致会分叉，日志里会提示。
/// </remarks>
internal static class DuelActPlan
{
    /// <summary>RunState.Acts 的 setter 是 private，只能反射拿（RitsuLib 内部也是这么做的）。</summary>
    private static readonly Action<RunState, IReadOnlyList<ActModel>>? ActsSetter = CreateActsSetter();

    private static Action<RunState, IReadOnlyList<ActModel>>? CreateActsSetter()
    {
        var property = typeof(RunState).GetProperty(nameof(RunState.Acts), BindingFlags.Public | BindingFlags.Instance);
        var setter = property?.GetSetMethod(nonPublic: true);

        return setter is null ? null : (runState, acts) => setter.Invoke(runState, [acts]);
    }

    /// <summary>幂等地把额外幕追加到末尾。</summary>
    public static void TryAppend(RunState runState, IReadOnlyList<Player> players)
    {
        if (ActsSetter is null)
        {
            ModInfo.Error("拿不到 RunState.Acts 的 setter，无法追加决斗幕（本局按普通流程走）");
            return;
        }

        if (players.Count < 2)
        {
            return;
        }

        if (!ModuleSwitches.ExtraAct)
        {
            ModInfo.Warn(
                $"本局 {players.Count} 人，但本机把「额外幕」模块关掉了，因此不追加决斗幕"
                + "（两端开关必须一致，否则章节列表会不一样）");
            return;
        }

        if (runState.Acts.Any(act => act is PvpDuelAct))
        {
            return;
        }

        try
        {
            var acts = runState.Acts.ToList();
            acts.Add(ModelDb.Act<PvpDuelAct>().ToMutable());

            ActsSetter(runState, acts);

            ModInfo.Info($"已追加「PVP 决斗」额外幕：本局共 {acts.Count} 幕，最后一场是决斗");
        }
        catch (Exception ex)
        {
            ModInfo.Error($"追加决斗幕失败（本局按普通流程走）：{ex}");
        }
    }
}

/// <summary>本局章节列表成形时追加额外幕。</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
internal static class DuelActAppendPatch
{
    [HarmonyPostfix]
    private static void Postfix(IReadOnlyList<Player> players, RunState __result)
    {
        DuelActPlan.TryAppend(__result, players);
    }
}

/// <summary>决斗幕里「每一个房间」都是「对决邀请」。</summary>
/// <remarks>
/// 本体建房间的唯一入口是 <c>RunManager.CreateRoom</c>（<c>EnterMapPointInternal</c> 里 roll 完类型就调它）。
/// 这里用 <b>Prefix 直接换掉结果</b>、根本不去跑本体那段建房逻辑：那段逻辑会按节点类型回幕里取内容
/// （先古房 <c>PullAncient</c>、怪房 <c>PullNextEncounter</c>…），幕里对应池一旦为空就会抛
/// （典型是 <c>"RoomSet.Ancient not set!"</c>）。决斗幕的内容虽然借的是第三幕的，
/// 但没必要依赖它 —— 不跑那一步，先古 / 遭遇 / boss 池有没有东西都无所谓。
/// </remarks>
[HarmonyPatch(typeof(RunManager), "CreateRoom")]
internal static class DuelActCreateRoomPatch
{
    [HarmonyPrefix]
    private static bool Prefix(RunManager __instance, ref AbstractRoom __result)
    {
        if (!ModuleSwitches.ExtraAct || __instance.DebugOnlyGetState()?.Act is not PvpDuelAct)
        {
            return true;
        }

        ModInfo.LogOnce("决斗幕：把这一间房换成「对决邀请」");
        __result = new EventRoom(ModelDb.Event<BattleBattleBattleEvent>());
        return false;
    }
}

/// <summary>决斗幕换用自己的地图（<see cref="DuelActMap" />：只有初始节点和 BOSS 节点）。</summary>
/// <remarks><c>ActModel.CreateMap</c> 不是 virtual，只能在这里替换返回值；其它幕一律放行。</remarks>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.CreateMap))]
internal static class DuelActCreateMapPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ActModel __instance, RunState runState, ref ActMap __result)
    {
        if (__instance is not PvpDuelAct || !ModuleSwitches.ExtraAct)
        {
            return true;
        }

        __result = new DuelActMap(runState.Act.HasSecondBoss);
        return false;
    }
}

/// <summary>把本体的「双 boss」还给原本的最后一幕。</summary>
/// <remarks>
/// 本体的双 boss（进阶 10+）在 <c>RunManager.GenerateRooms</c> 里发给 <c>Acts.Count - 1</c> 那一幕；
/// 追加决斗幕后那就是决斗幕，于是第三幕的双 boss 会消失、双 boss 反而落到只有一个 BOSS 节点的决斗幕上。
/// 这里在房间生成后纠正：决斗幕不要第二个 BOSS，双 boss 补给追加之前本来就是最后一幕的那一幕。
/// 挑 BOSS 用独立 Rng（种子取自本局种子），两端结果一致，也不扰动本体 RNG 流。
/// </remarks>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
internal static class DuelActDoubleBossPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunManager __instance)
    {
        var state = __instance.DebugOnlyGetState();
        if (!ModuleSwitches.ExtraAct
            || state is null
            || state.Acts.Count < 2
            || state.Acts[^1] is not PvpDuelAct duelAct)
        {
            return;
        }

        if (duelAct.HasSecondBoss)
        {
            duelAct.SetSecondBossEncounter(null);
        }

        if (!__instance.AscensionManager.HasLevel(AscensionLevel.DoubleBoss))
        {
            return;
        }

        var lastRealAct = state.Acts[^2];
        if (lastRealAct is PvpDuelAct || lastRealAct.HasSecondBoss)
        {
            return;
        }

        try
        {
            var second = new Rng(state.Rng.Seed, "pvp_duel_last_real_act_second_boss")
                .NextItem(lastRealAct.AllBossEncounters.Where(e => e.Id != lastRealAct.BossEncounter.Id));

            lastRealAct.SetSecondBossEncounter(second);

            ModInfo.Info($"双 boss 已还给最后一幕（{lastRealAct.Id.Entry}）");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"恢复最后一幕的双 boss 失败（不影响本局其它内容）：{ex.Message}");
        }
    }
}

/// <summary>兜住本体「按幕 id 拼成就名」的写法。</summary>
/// <remarks>
/// <c>ActModel.DefeatedAllEnemiesAchievement</c> 是
/// <c>Enum.Parse&lt;Achievement&gt;("Defeat" + Id.Entry.Capitalize() + "Enemies")</c>，
/// 自定义幕的 id 不在本体的 Achievement 枚举里，谁读这个属性谁抛。
/// 今天 AchievementsHelper 里那几个方法是空实现（潜在雷），成就系统一旦接回去就是必炸点。
/// </remarks>
[HarmonyPatch(typeof(ActModel), "get_DefeatedAllEnemiesAchievement")]
internal static class DuelActAchievementGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ActModel __instance, ref Achievement __result)
    {
        if (__instance is not PvpDuelAct)
        {
            return true;
        }

        __result = Achievement.DefeatGloryEnemies;
        return false;
    }
}

/// <summary>第 4 幕没有「多人血量缩放」档位：把越界的幕号折回最后一档。</summary>
/// <remarks>
/// 本体 <c>MultiplayerScalingModel.GetMultiplayerScaling</c> 是
/// <c>switch (actIndex) { 0/1/2 …; default: throw }</c>，而 <c>CombatState.CreateCreature</c> 建怪时就会问它。
/// 决斗幕是第 4 幕（actIndex=3），于是「接受对决」那一刻建战斗直接抛
/// <c>ArgumentOutOfRangeException: Invalid act index for HP scaling</c>。
/// 这一场本来就没有怪物（拦在 AddCreature），缩放取哪一档都无所谓，折回第 3 幕那档即可。
/// </remarks>
[HarmonyPatch(typeof(MultiplayerScalingModel), nameof(MultiplayerScalingModel.GetMultiplayerScaling))]
internal static class DuelActScalingIndexPatch
{
    /// <summary>本体表格的最后一档（actIndex 2 = 第三幕）。</summary>
    private const int LastScaledActIndex = 2;

    [HarmonyPrefix]
    private static void Prefix(ref int actIndex)
    {
        if (actIndex > LastScaledActIndex)
        {
            actIndex = LastScaledActIndex;
        }
    }
}
