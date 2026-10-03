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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
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

    /// <summary>
    /// 场上有我们的木桩时：标记"这场是背叛战斗"，并把木桩的血拉满
    /// （199999 是设计值，通常已经够大，这里是兜底）。
    ///
    /// ⚠ 这里**只认我们自己的木桩类型**（BetrayalEggMonster）。
    /// 早期版本是"只要是敌方单位就加血"，一旦 `BetrayalCombatActive` 因为
    /// 黑屏/中途退出残留在 true，普通怪物也会被加到 99999 血 —— 现在不会了。
    /// </summary>
    public override async Task AfterCreatureAddedToCombat(Creature creature)
    {
        try
        {
            if (creature?.Monster is not BetrayalEggMonster)
                return;

            // 木桩在场 = 这场就是我们的战斗。顺手把辅助开关点亮，并记住这场战斗。
            _betrayalCombat = CurrentCombatState;
            _betrayalCombatConfirmed = true;
            ArchitectBetrayalEvent.BetrayalCombatActive = true;

            await PrepareDummyAsync(creature);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：木桩加血失败 {ex}");
        }
    }

    /// <summary>本场战斗里出现过我们的木桩吗（= 这场真的是「建筑师的挑拨」那场）。</summary>
    private static MegaCrit.Sts2.Core.Combat.ICombatState? _betrayalCombat;

    /// <summary>
    /// "这场是背叛战斗"的**缓存结果**（不是每次现算）。
    ///
    /// ⚠ 这个缓存是必须的，不是优化 —— 起因是一次真实的栈溢出崩溃（0xc00000fd）：
    ///   `PvpEnemyListPatch` 挂在 `CombatState.Enemies` 的 Postfix 上，它第一句就是读
    ///   `BetrayalCombatActive`；如果那个属性每次现算"场上有沒有我们的木桩"，
    ///   就会去遍历 `combat.Enemies` —— 又走回同一个 Postfix → 无限递归 → 爆栈闪退。
    ///   所以：判定只在"看到木桩/开战确认/清标记"这几个明确时机做一次，结果缓存起来，
    ///   属性只读缓存，绝不回头碰 `Enemies`。
    /// </summary>
    private static bool _betrayalCombatConfirmed;

    /// <summary>`CombatState._enemies` 原始字段（绕开被我们挂过后置的 `Enemies` 属性）。</summary>
    private static readonly FieldInfo? RawEnemiesField =
        AccessTools.Field(typeof(MegaCrit.Sts2.Core.Combat.CombatState), "_enemies");

    /// <summary>
    /// 直接读 `_enemies` 原始字段，**故意绕开被补丁挂过的 `Enemies` 属性**，
    /// 免得"属性 → 开关 → 属性"这种自环。
    /// </summary>
    private static IEnumerable<Creature> RawEnemies(MegaCrit.Sts2.Core.Combat.ICombatState combat)
    {
        try
        {
            if (combat is MegaCrit.Sts2.Core.Combat.CombatState state
                && RawEnemiesField?.GetValue(state) is IEnumerable<Creature> raw)
                return raw;
        }
        catch
        {
            // 拿不到字段就走下面的兜底
        }

        // 兜底：只有在"判定时机"才会走到这里（那时 `BetrayalCombatActive` 的缓存是 false，
        // PvpEnemyListPatch 会立刻 return，不会成环）。
        return combat.Enemies;
    }

    /// <summary>这一场上有没有我们的木桩（纯查询，不改任何状态）。</summary>
    public static bool IsBetrayalCombat(MegaCrit.Sts2.Core.Combat.ICombatState? combat)
        => combat is not null
           && RawEnemies(combat).Any(enemy => enemy?.Monster is BetrayalEggMonster);

    /// <summary>
    /// "现在这个房间就是我们那只卵的遭遇战吗" —— 作为木桩判据的补充。
    /// 用来兜住一种时序：万一 `BeforeCombatStart` 比"木桩被加进战斗"更早跑，
    /// 场上还没有木桩，但 `RunState.CurrentRoom` 已经是我们的 EventCombat 了
    /// （`CombatRoom.ParentEventId` / `Encounter` 就是那只卵）。
    /// 普通怪物房间里这两条都不成立 → 一定判成"不是我们的战斗"。
    /// </summary>
    private static bool CurrentRoomIsOurEncounter(MegaCrit.Sts2.Core.Combat.ICombatState? combat)
    {
        try
        {
            var player = combat?.Players?.FirstOrDefault();
            if (player?.RunState?.CurrentRoom is not CombatRoom room)
                return false;

            return room.Encounter is BetrayalEggEncounter;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 清掉"这场是背叛战斗"的标记 + 开关（自愈）。
    /// </summary>
    public static void ClearBetrayalMarkers(string reason)
    {
        if (_betrayalCombat is null
            && !_betrayalCombatConfirmed
            && !ArchitectBetrayalEvent.BetrayalCombatActive)
            return;

        _betrayalCombat = null;
        _betrayalCombatConfirmed = false;
        ArchitectBetrayalEvent.BetrayalCombatActive = false;
        Entry.Logger.Info($"建筑师的挑拨：清掉残留的战斗标记（{reason}）");
    }

    /// <summary>
    /// 进任何房间时先把"这场是背叛战斗"清掉 —— 真的那场会在战斗布场时
    /// （AfterCreatureAddedToCombat 看到木桩）重新点亮。
    /// 这一步很关键：黑屏/中途退出没跑 AfterCombatEnd 的时候，就靠它在下一间房自愈。
    /// </summary>
    public override Task BeforeRoomEntered(AbstractRoom room)
    {
        try
        {
            // ⚠ 只清"非战斗房间"：进战斗房间时**不能清** ——
            //   因为"队友搬到对面/木桩挪最右"这些补丁跑在这场战斗布场的最开始，
            //   开关必须在那一刻还是 true。（战斗房间的校验交给 BeforeCombatStart，
            //   它会在"不是我们这场"时清掉。）
            if (room is not CombatRoom)
                ClearBetrayalMarkers(room is null ? "进房间" : $"进入 {room.GetType().Name}");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：进房间清理标记失败 {ex}");
        }

        return Task.CompletedTask;
    }

    /// <summary>再多兜一层：战斗正式开始时确认木桩血量。</summary>
    public override async Task BeforeCombatStart()
    {
        try
        {
            var combat = CurrentCombatState;
            if (combat is null)
                return;

            // 唯一判据：场上有没有我们那只木桩（BetrayalEggMonster）。
            // —— 这样无论事件流程有没有跑、开关有没有残留，都不会误判：
            //      · 事件正常开打 → 有木桩 → 走下面的加成 ✓
            //      · 自定义/调试房间直接放这场遭遇 → 也有木桩 ✓
            //      · 普通怪物房间（开关残留）→ 没木桩 → 清掉残留、什么都不做 ✓
            if (!IsBetrayalCombat(combat) && !CurrentRoomIsOurEncounter(combat))
            {
                ClearBetrayalMarkers("开战了但场上没有我们的木桩");
                return;
            }

            _betrayalCombat = combat;
            _betrayalCombatConfirmed = true;
            ArchitectBetrayalEvent.BetrayalCombatActive = true;

            // ★ 这一句必须最先做，且不能被任何会抛异常的步骤挡在前面。
            //   多人局里少了它，第一回合 StartTurn → RecordChecksum 会抛
            //   "RecordInitialState must be called first"，回放/回合循环当场死掉，
            //   表现就是黑屏（详见 EnsureReplayInitialState 的注释）。
            //   之前把它放在最后，而前面新加的"发遗物"里 RelicCmd.Obtain 会碰界面
            //   （NRun.Instance.GlobalUi.RelicInventory.AnimateRelic），
            //   一旦那里抛异常就会把它一起跳过 —— 这就是黑屏的来源。
            EnsureReplayInitialState();

            // 玩家对决开始 —— 先把这场战斗的曲子放起来。
            StartPvpMusic();

            // 开打之前所有人回满血，并且生命上限翻三倍 —— 这样互相打起来才够久。
            // （这场战斗之后整局就结束了，所以不用恢复原值。）
            // 标记改成"这场战斗"（原来是一个静态 bool）：万一上一场的 AfterCombatEnd 没跑到
            // （自定义房间、中途退出等），旧的 true 会把这一场的加成吞掉。
            if (!ReferenceEquals(_buffedCombat, combat))
            {
                _buffedCombat = combat;

                // ⛔ 回退：不再发【死斗】遗物，改回"直接改血"（原版那条路）。
                //   原因（线上实测）：
                //     ① `RelicCmd.Obtain` 内部会碰界面节点（动画/音效/图鉴标记），
                //        在"开战那一瞬间"调用很容易抛异常，进而把后面的步骤一起跳过；
                //     ② 遗物是整局对象，作为"这场战斗"的判据容易牵连后面的战斗；
                //     ③ 直接改血在客户端/主机上都是纯数值操作，最稳。
                foreach (var playerCreature in combat.PlayerCreatures)
                {
                    if (playerCreature.Player is not { } owner || owner.Creature is not { } creature)
                        continue;

                    var tripled = creature.MaxHp * 3;
                    await CreatureCmd.SetMaxHp(creature, tripled);
                    await CreatureCmd.SetCurrentHp(creature, tripled);

                    Entry.Logger.Info(
                        $"建筑师的背叛：NetId={owner.NetId} 回满血，生命上限 ×3 → {tripled}");
                }
            }

            foreach (var enemy in combat.Enemies.Where(e => e.Player is null).ToList())
                await PrepareDummyAsync(enemy);
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

    /// <summary>已经加过血的那场战斗（同一个战斗里 BeforeCombatStart 可能不只来一次）。</summary>
    private static object? _buffedCombat;

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
        ClearBetrayalMarkers("战斗结束");
        _buffedCombat = null;
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
