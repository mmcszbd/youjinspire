// ============================================================================
// 友尽尖塔（youjin）：「建筑师的背叛」的战斗监听
// ----------------------------------------------------------------------------
// 这个单例只在「背叛战斗」里干活（ArchitectBetrayalEvent.BetrayalCombatActive）：
//
//   1. 木桩是原版 BattlewornDummyEventV1Encounter 里的 BattleFriendV1（血很少、
//      而且不产奖励）。开战时把它改成 99999 血，让它打不死 —— 它只是让「战斗」
//      这个玩法成立的最低条件（Creature.Side 是 private，没法把队友真的变成敌人）。
//   2. 只剩最后一个人活着时，把木桩打飞 → 战斗按「胜利」结束 →
//      因为进战斗时带了 resumeAfterCombat = true，游戏会回到事件本身，
//      接着走 ArchitectBetrayalEvent.Resume 里的终章。
//
// 反编译踩点：
//   · Creature.MaxHp / CurrentHp 的 setter 是 private，公开的入口是
//     CreatureCmd.SetMaxHp(Creature, decimal) 和 CreatureCmd.SetCurrentHp(Creature, decimal)
//     —— 两个都不需要 PlayerChoiceContext。
//   · 死亡后触发的钩子是 AfterDeath(PlayerChoiceContext, Creature, bool wasRemovalPrevented, float deathAnimLength)
//     （和 DeathGoldSplit 用的是同一个）。
//   · CreatureCmd.Kill(creature, force: true) 才会绕过「免死」类效果，保证木桩真的退场。
// ============================================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Godot;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>「建筑师的背叛」战斗：给木桩加血、只剩一人时收场。</summary>
[RegisterSingleton]
public sealed class ArchitectBetrayalCombatModel : HookedSingletonModel
{
    public ArchitectBetrayalCombatModel() : base(HookType.Combat)
    {
    }

    /// <summary>玩家对决时循环播放那首曲子的播放器。</summary>
    private static AudioStreamPlayer? _pvpMusicPlayer;

    /// <summary>曲子放在 mod 的 pck 里，路径由导出预设的 include_filter 带进去。</summary>
    private const string PvpMusicPath = "res://fmod_audio/yuai.mp3";

    /// <summary>
    /// 玩家对决开始 → 放这首曲子。
    ///
    /// 这里用 Godot 自己的 AudioStreamPlayer + AudioStreamMP3，不走 RitsuLib 的音频服务，
    /// 原因有三个（都是这次踩出来的）：
    ///   1. 我们的 mp3 是靠导出预设的 include_filter 作为**原始文件**进 pck 的
    ///      ——实测 FileAccess.file_exists = true、ResourceLoader.exists = false、
    ///      包里也没有 .godot/imported 的 .mp3str。
    ///      所以"按字节读出来塞进 AudioStreamMP3.Data"才是它的正确打开方式
    ///      （实测能读出 2640089 字节、长度 110 秒）。
    ///   2. 少碰一层 FMOD：队友那次闪退伴随着
    ///      "System.Object 不能转换成 Variant" 的报错，而 RitsuLib 的音频通道是要
    ///      穿过 FmodServer（GDExtension 单例）的，正好会做 Variant 转换。
    ///   3. 播放器挂到战斗房间节点下面，房间一销毁声音就没了；
    ///      同时我们还能自己 Stop —— 之前"事件结束了音乐没停"就是收尾没走到。
    /// </summary>
    private static void StartPvpMusic()
    {
        // 上一个播放器可能已经随房间销毁了（那样引用会失效），先判一下。
        if (_pvpMusicPlayer is not null)
        {
            if (GodotObject.IsInstanceValid(_pvpMusicPlayer))
                return;

            _pvpMusicPlayer = null;
        }

        try
        {
            if (!Godot.FileAccess.FileExists(PvpMusicPath))
            {
                Entry.Logger.Info($"建筑师的背叛：pck 里没有 {PvpMusicPath}，跳过 BGM");
                return;
            }

            var bytes = Godot.FileAccess.GetFileAsBytes(PvpMusicPath);
            if (bytes is null || bytes.Length == 0)
            {
                Entry.Logger.Info($"建筑师的背叛：{PvpMusicPath} 读出来是空的，跳过 BGM");
                return;
            }

            var stream = new AudioStreamMP3
            {
                Data = bytes,
                Loop = true,
            };

            var player = new AudioStreamPlayer
            {
                Stream = stream,
                VolumeDb = 0f,
            };

            // 挂在战斗房间节点下面：房间被销毁时播放器跟着一起没，声音自然停。
            Node? host = NCombatRoom.Instance;
            if (host is null && Engine.GetMainLoop() is SceneTree tree)
                host = tree.Root;

            if (host is null)
            {
                Entry.Logger.Info("建筑师的背叛：找不到能挂播放器的节点，跳过 BGM");
                return;
            }

            host.AddChild(player);
            player.Play();

            _pvpMusicPlayer = player;
            Entry.Logger.Info(
                $"建筑师的背叛：开始播放 BGM {PvpMusicPath}（{stream.GetLength():F0} 秒，循环）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：播放 BGM 失败 {ex}");
        }
    }

    /// <summary>战斗结束 → 停掉曲子。</summary>
    private static void StopPvpMusic()
    {
        var player = _pvpMusicPlayer;
        _pvpMusicPlayer = null;

        if (player is null || !GodotObject.IsInstanceValid(player))
            return;

        try
        {
            player.Stop();
            player.QueueFree();
            Entry.Logger.Info("建筑师的背叛：停止 BGM");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：停止 BGM 失败 {ex}");
        }
    }

    /// <summary>敌人被放进战斗时，把木桩的血拉满（幂等：已经够大就跳过）。</summary>
    public override async Task AfterCreatureAddedToCombat(Creature creature)
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return;

            if (creature.Side != CombatSide.Enemy || creature.Player is not null)
                return;

            await PrepareDummyAsync(creature);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：木桩加血失败 {ex}");
        }
    }

    /// <summary>再多兜一层：战斗正式开始时确认木桩血量。</summary>
    public override async Task BeforeCombatStart()
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return;

            var combat = CurrentCombatState;
            if (combat is null)
                return;

            // 玩家对决开始 —— 先把这场战斗的曲子放起来。
            StartPvpMusic();

            // 开打之前所有人回满血，并且生命上限翻三倍 —— 这样互相打起来才够久。
            // （这场战斗之后整局就结束了，所以不用恢复原值。）
            if (!_playersBuffed)
            {
                _playersBuffed = true;

                foreach (var playerCreature in combat.PlayerCreatures)
                {
                    var tripled = playerCreature.MaxHp * 3;
                    await CreatureCmd.SetMaxHp(playerCreature, tripled);
                    await CreatureCmd.SetCurrentHp(playerCreature, tripled);
                }

                Entry.Logger.Info("建筑师的背叛：所有玩家回满血，生命上限 ×3");
            }

            foreach (var enemy in combat.Enemies.Where(e => e.Player is null).ToList())
                await PrepareDummyAsync(enemy);

            // 关键：替原版补上「战斗初始状态记录」，否则多人局第一回合就会卡死。
            EnsureReplayInitialState();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：开战时确认木桩血量失败 {ex}");
        }
    }

    /// <summary>
    /// 替原版补上「战斗初始状态记录」。
    ///
    /// 原版只有 RunManager.EnterMapPointInternal（从地图进战斗）和 EnterRoomDebug
    /// 会调 CombatReplayWriter.RecordInitialState；而事件战斗走的是
    /// EventCombatSynchronizer.EnterCombat → EnterRoomWithoutExitingCurrentRoom，
    /// 这条路径没人补这一步。
    ///
    /// CombatReplayWriter.IsEnabled 只在 RunManager.InitializeShared 里被打开（也就是多人局），所以：
    ///   · 单人局 IsEnabled = false，RecordChecksum 第一行就 return —— 没事；
    ///   · 多人局战斗第一回合 StartTurn → RecordChecksum 会抛
    ///     "RecordInitialState must be called first"，回合循环当场死掉，
    ///     日志里是 "turn loop died while its combat is in progress; the combat is stuck"
    ///     —— 表现就是「那场战斗里不能出牌」。
    ///
    /// 这里补记；万一记不上，就把本场回放记录关掉。绝不能让回合循环被拖死。
    /// </summary>
    private static void EnsureReplayInitialState()
    {
        try
        {
            if (RunManager.Instance is not { } manager)
                return;

            var writer = manager.CombatReplayWriter;
            if (writer is null || !writer.IsEnabled)
                return;

            try
            {
                writer.RecordInitialState(manager.ToSave(null));

                if (!writer.IsRecordingReplay)
                {
                    Entry.Logger.Info("建筑师的背叛：回放初始状态没记上，关闭本场回放记录");
                    writer.IsEnabled = false;
                }

                Entry.Logger.Info("建筑师的背叛：已补记战斗初始状态（回放记录）");
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"建筑师的背叛：补记战斗初始状态失败，改为关闭本场回放记录 {ex}");
                writer.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：处理回放记录时出错 {ex}");
        }
    }

    /// <summary>玩家加血只做一次（同一个战斗里 BeforeCombatStart 可能不只来一次）。</summary>
    private static bool _playersBuffed;

    /// <summary>只剩一个人活着 → 把木桩打飞，让战斗正常结算结束。</summary>
    public override async Task AfterDeath(
        PlayerChoiceContext choiceContext,
        Creature creature,
        bool wasRemovalPrevented,
        float deathAnimLength)
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return;

            // 只关心玩家死亡（木桩自己被打死不算）。
            if (creature.Player is null)
                return;

            var combat = CurrentCombatState;
            if (combat is null)
                return;

            var alivePlayers = combat.PlayerCreatures.Count(playerCreature => playerCreature.IsAlive);
            if (alivePlayers > 1)
                return;

            Entry.Logger.Info($"建筑师的背叛：只剩 {alivePlayers} 名玩家，结束背叛战斗");

            // 只清掉真正的怪物（那只卵）。
            // 注意：出牌期间 Enemies 里可能混着队友（见 PvpEnemyListPatch），
            // 无脑"全杀"会把唯一活着的那个人也干掉。
            foreach (var enemy in combat.Enemies
                         .Where(e => e.IsAlive && e.Player is null && e.PetOwner is null)
                         .ToList())
                await CreatureCmd.Kill(enemy, true);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：结束战斗失败 {ex}");
        }
    }

    /// <summary>战斗彻底结束时关掉标记，别影响后面别的战斗。</summary>
    public override Task AfterCombatEnd(CombatRoom room)
    {
        StopPvpMusic();
        ArchitectBetrayalEvent.BetrayalCombatActive = false;
        _playersBuffed = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 本场战斗里让「木偶」不可被打击 —— 也就是实验体转阶段那种"锁定不了"的状态。
    ///
    /// 原版机制（反编译确认）：
    ///     Creature.IsHittable         = !IsDead &amp;&amp; Hook.ShouldAllowHitting(CombatState, this)
    ///     CombatState.HittableEnemies = Enemies.Where(c =&gt; c.IsHittable)
    /// 而 Hook.ShouldAllowHitting 会遍历战斗里的 hook 监听者，
    /// **只要有一个返回 false，这个单位就不在 HittableEnemies 里**。
    ///
    /// 原版没有任何模型实现 ShouldAllowHitting（只有 AbstractModel 的声明 + Hook 的分发器），
    /// 这是官方留的开放插槽。所有"自动挑敌人"的逻辑都吃这份列表：
    ///   · 地狱狂徒 HellraiserPower.AfterCardDrawnEarly
    ///   · 对全体敌人 / 随机敌人的卡、遗物、药水
    ///   · Hook.ModifyDamage 里的敌人判定
    /// 所以木偶一旦不可打击，这些逻辑都会自动跳过它，不用改任何卡。
    ///
    /// 注意：这里只做"否决"。木偶仍然留在 CombatState.Enemies 里，
    /// 所以 CombatManager.IsCombatEnding 的「敌人全灭」判定不会被误触发，战斗照常继续。
    /// 玩家（creature.Player 非空）一律返回 true，不受影响。
    /// </summary>
    public override bool ShouldAllowHitting(Creature creature)
    {
        if (!ArchitectBetrayalEvent.BetrayalCombatActive)
            return true;

        if (creature is null || creature.Player is not null)
            return true;

        // 背叛战斗里唯一的非玩家单位就是木偶。
        return false;
    }

    /// <summary>
    /// 把训练木偶改造成「一直站在那里、而且打不死」。
    ///
    /// 原版 BattleFriendV1 自带 BattlewornDummyTimeLimitPower —— 就是原版训练木偶那个
    /// 回合倒计时，层数扣完会 set_RanOutOfTime(true)，然后事件收场、木偶消失。
    /// 这就是「木偶只能存在三回合」的原因，所以第一件事是把它摘掉。
    ///
    /// 然后才是把血改成 99999，免得几刀就被打死、战斗提前结束。
    /// </summary>
    private static async Task PrepareDummyAsync(Creature dummy)
    {
        // 1) 摘掉原版的回合倒计时，让它一直留在场上。
        if (dummy.GetPower<BattlewornDummyTimeLimitPower>() is { } timeLimit)
        {
            await PowerCmd.Remove(timeLimit);
            Entry.Logger.Info("建筑师的背叛：已移除木桩的回合倒计时");
        }

        // 2) 血量拉满（已经改过就跳过）。
        if (dummy.MaxHp >= ArchitectBetrayalEvent.DummyHp)
            return;

        await CreatureCmd.SetMaxHp(dummy, ArchitectBetrayalEvent.DummyHp);
        await CreatureCmd.SetCurrentHp(dummy, ArchitectBetrayalEvent.DummyHp);
        dummy.SetMaxHpInternal(ArchitectBetrayalEvent.DummyHp);
        dummy.SetCurrentHpInternal(ArchitectBetrayalEvent.DummyHp);

        Entry.Logger.Info($"建筑师的背叛：木桩血量设为 {ArchitectBetrayalEvent.DummyHp}");
    }
}
