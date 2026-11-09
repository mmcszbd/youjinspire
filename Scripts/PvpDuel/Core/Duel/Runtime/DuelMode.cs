using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using YouJin.Scripts;

namespace PvpDuel.Core.Duel;

/// <summary>把战斗改造成决斗：清掉怪物、补齐战斗录像快照。</summary>
/// <remarks>
/// <para>
/// <b>两人都留在我方侧，对手只是看起来在对面</b>：早先试过把 <c>Players[1]</c> 的
/// <c>Creature.Side</c> 改成敌方，代价是同时踩中本体五处「怪物专属」路径
/// （<c>TakeTurn</c> / <c>AfterAddedToRoom</c> / <c>RollMove</c> / <c>PrepareForNextTurn</c> / <c>PerformIntent</c>）
/// 并且要接管整个敌方回合。现在只改两处「看法」——立绘位置与朝向（<c>Patches/LayoutPatches.cs</c>）、
/// 敌我视角（<c>Patches/CombatPatches.cs</c> 与 <c>Patches/TargetPatches.cs</c>）。
/// </para>
/// <para>
/// <b>对手是谁必须两端算出同一个答案</b>：固定用 <c>Players[1]</c>（大厅顺序靠后的那位），
/// 不能用本机视角判断，否则主机和客户端会当场分叉。
/// </para>
/// </remarks>
internal static class DuelMode
{
    /// <summary>幂等地对齐一场战斗的决斗布置（由 AddPlayer 补丁在每位玩家加入后调用）。</summary>
    public static void EnsureApplied(CombatState? state)
    {
        if (state is null || !DuelRules.CanRunDuel(state))
        {
            return;
        }

        DuelSession.Begin(state);

        if (!DuelSession.InDuel)
        {
            return;
        }

        EnsureReplaySnapshot();
        RemoveMonsters(state);
        ApplyPlayerBuffs(state);
    }

    /// <summary>这一场已经施加过血量/壁垒的战斗（用来识别「换了一场」，换场才清账）。</summary>
    private static CombatState? _buffedCombat;

    /// <summary>这一场里已经处理过的玩家角色。</summary>
    /// <remarks>
    /// 为什么按角色记、不按战斗记：事件开战（EnterCombatWithoutExitingEvent）的 CombatState 是空建的，
    /// 玩家由 CombatRoom 逐个 AddPlayer 塞进来。第一次 EnsureApplied 时战斗里只有 1 位玩家
    /// （日志：`新的战斗：allies=1 enemies=0 players=1`），按战斗去重会把整场标成「已施加」，
    /// 第 2 位玩家永远翻不了倍 —— 表现就是主机血量 ×2、客户端血量原样。
    /// </remarks>
    private static readonly HashSet<Creature> BuffedCreatures = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 0.10.8 新增：把「血量倍数」与「群体壁垒」施加到两位玩家身上。
    /// </summary>
    /// <remarks>
    /// 这两项**和手速模式共用同一份设置**（`YouJinSettings.ArchitectHpMultiplier` /
    /// `ArchitectFortitudeEnabled`），在设置页里两处都能改，改的是同一个值。
    /// 回合制原本的「回合上限」已在 0.10.8 整项删掉，现在只能打到有人倒下 ——
    /// 想让决斗打得久一点，就是靠这里的血量倍数。
    /// 施加动作是异步的（<c>CreatureCmd</c>/<c>PowerCmd</c> 返回 Task），
    /// 而 <see cref="EnsureApplied" /> 是同步补丁入口，所以用 <c>TaskHelper.RunSafely</c> 甩出去。
    /// </remarks>
    private static void ApplyPlayerBuffs(CombatState state)
    {
        if (!ReferenceEquals(_buffedCombat, state))
        {
            _buffedCombat = state;
            BuffedCreatures.Clear();
        }

        var multiplier = YouJinSettings.ArchitectHpMultiplier;
        var fortitude = YouJinSettings.ArchitectFortitudeEnabled;

        // boss（建筑师挑拨选出来的那位）的倍率由 BossHpPatch 负责，这里不看它。
        if (multiplier <= 1 && !fortitude)
        {
            return;
        }

        // 谁还没处理就补谁：事件战斗里的玩家是逐个 AddPlayer 进来的，
        // 每一轮 EnsureApplied 都要把新到的落单玩家补上。
        var pending = new List<Creature>();

        foreach (var playerCreature in state.PlayerCreatures.ToList())
        {
            if (BuffedCreatures.Add(playerCreature))
            {
                pending.Add(playerCreature);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        TaskHelper.RunSafely(ApplyPlayerBuffsAsync(pending, multiplier, fortitude));
    }

    private static async Task ApplyPlayerBuffsAsync(List<Creature> targets, int multiplier, bool fortitude)
    {
        foreach (var playerCreature in targets)
        {
            if (playerCreature.Player is not { } owner)
            {
                continue;
            }

            if (multiplier > 1)
            {
                try
                {
                    var boosted = playerCreature.MaxHp * multiplier;
                    await CreatureCmd.SetMaxHp(playerCreature, boosted);
                    await CreatureCmd.SetCurrentHp(playerCreature, boosted);
                    ModInfo.Info($"决斗：NetId={owner.NetId} 生命上限 ×{multiplier} → {boosted}");
                }
                catch (Exception ex)
                {
                    ModInfo.Warn($"决斗：给 NetId={owner.NetId} 加血失败（不影响战斗）：{ex.Message}");
                }
            }

            if (!fortitude)
            {
                continue;
            }

            try
            {
                // ctx 传 null —— 和手速模式挂壁垒用的是同一个调用形状。
                await PowerCmd.Apply<BarricadePower>(null!, playerCreature, 1m, null!, null!, false);
                ModInfo.Info($"决斗：NetId={owner.NetId} 已挂壁垒");
            }
            catch (Exception ex)
            {
                ModInfo.Warn($"决斗：给 NetId={owner.NetId} 上壁垒失败（不影响战斗）：{ex.Message}");
            }
        }

        // boss（建筑师挑拨选出来的那位）的血量不在这里加：
        // 挑拨那场是固定手速混战，3 人以上时 CanRunDuel 为假、本方法整场早退，
        // 所以它走独立的 BossHpPatch（只认「谁是 boss」，不认决斗参战人数）。

        ModInfo.Info(
            $"决斗：已按设置施加「血量 ×{multiplier}」与「群体壁垒={fortitude}」（本次补 {targets.Count} 位）");
    }

    /// <summary>补上战斗录像的初始快照。</summary>
    /// <remarks>
    /// 本体只在「走地图点」和「按房间类型直接进房」两条路上记它，事件里开打（EnterCombatWithoutExitingEvent）不记。
    /// 快照为空时 CombatReplayWriter 的每个钩子都会抛 RecordInitialState must be called first，
    /// 回合开始的第一个 checksum 正好踩中它，整条回合链当场死掉（表现：战斗卡住不动）。
    /// </remarks>
    private static void EnsureReplaySnapshot()
    {
        try
        {
            var run = RunManager.Instance;
            var writer = run?.CombatReplayWriter;

            if (run is null || writer is null || !writer.IsEnabled || writer.IsRecordingReplay)
            {
                return;
            }

            writer.RecordInitialState(run.ToSave(null));

            ModInfo.LogOnce("决斗：补上战斗录像的初始快照");
        }
        catch (Exception ex)
        {
            ModInfo.Warn($"补战斗录像快照失败（不影响战斗）：{ex.Message}");
        }
    }

    /// <summary>纯玩家对决：把敌方侧的怪物全部摘掉。</summary>
    private static void RemoveMonsters(CombatState state)
    {
        foreach (var creature in state.Enemies.ToList())
        {
            if (creature.IsPlayer)
            {
                continue;
            }

            try
            {
                state.RemoveCreature(creature, unattach: true);
                ModInfo.Info($"移除怪物：{creature.Monster?.Id.Entry ?? "?"}");
            }
            catch (Exception ex)
            {
                ModInfo.Warn($"移除怪物失败：{ex.Message}");
            }
        }
    }
}
