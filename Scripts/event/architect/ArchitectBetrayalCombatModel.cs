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
using MegaCrit.Sts2.Core.Models;
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
    /// <summary>背叛战斗是否已经在收尾（防止两名玩家同帧死亡时跑两遍）。</summary>
    private static bool _betrayalEnding;

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

            // 回合制 PvP 时这场由「PVP 决斗」接管：木桩不该加血，更不该点亮手速模式的开关。
            // （正常流程里回合制分支根本不摆木桩，这里是兜底：调试/自定义房间直接放这场遭遇时也不能叠两套逻辑。）
            if (!YouJinSettings.PvpSpeedModeEnabled)
            {
                Entry.Logger.Info("建筑师的挑拨：PvP 方式不是手速模式 → 木桩不加血、也不点亮开关");
                return;
            }

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

    /// <summary>只读出口：这场 PvP 的战斗状态（给诊断快照用；不在战斗里就是 null）。</summary>
    public static MegaCrit.Sts2.Core.Combat.ICombatState? CurrentStateForLog => _betrayalCombat;

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

        // 把有主单位的 CombatState 还原成真实状态。
        BetrayalCombatView.Uninstall();

        // 顺带把「每回合出牌上限」的计数清掉。
        ArchitectDuelRules.ResetAll();

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

    /// <summary>战斗正式开始时：确认这场是我们的 → 布好战斗环境 → 加满玩家 → 拉满木桩血量。</summary>
    /// <remarks>
    /// 这里只负责编排。改造前这一整段塞在同一个方法里，四种职责混在一起，
    /// 而"顺序有硬约束"这件事只写在注释里 —— 具体是：
    ///   ① 认不出这场就必须立刻清标记退出（否则后面全在给别人的战斗加血）；
    ///   ② 回放初始状态必须排在所有会抛异常的步骤**前面**（详见 EnsureReplayInitialState）；
    ///   ③ 队伍视图必须在 ② 之后装（② 一抛，视图就装不上，出牌判定全失效）；
    ///   ④ 加血/挂壁垒只做一次（同一场里 BeforeCombatStart 可能不只来一次）。
    /// 拆成四个具名步骤后，顺序一眼可见，任何一步都能单独读、单独改。
    /// </remarks>
    public override async Task BeforeCombatStart()
    {
        try
        {
            var combat = CurrentCombatState;
            if (combat is null)
                return;

            // 回合制 PvP：这场是「PVP 决斗」（一人一回合轮流动），
            // 木桩 / 队友搬到对面 / 血量倍数这些手速模式的布场一样都不做。
            if (!YouJinSettings.PvpSpeedModeEnabled)
            {
                Entry.Logger.Info("建筑师的挑拨：PvP 方式=回合制 → 本场跳过手速模式布场（交给「PVP 决斗」）");
                return;
            }

            if (!ConfirmThisCombat(combat))
                return;

            PrepareCombatInfrastructure(combat);

            await BuffPlayersIfNeededAsync(combat);

            // 无木桩形态：场上压根没有非玩家单位，这一步没有对象可处理。
            if (PvpBareCombat.Active(combat))
                Entry.Logger.Info("建筑师的挑拨：无木桩形态（场上只有你们两个）→ 跳过木桩准备");
            else
                await PrepareDummiesAsync(combat);

            PvpCombat.Report("开战布场完成", combat);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：开战时确认木桩血量失败 {ex}");
        }
    }

    /// <summary>
    /// ① 确认这场就是「建筑师的挑拨」，认不出就清掉残留标记并返回 false。
    /// </summary>
    /// <remarks>
    /// 判据：场上有没有我们那只木桩（BetrayalEggMonster），或当前房间是不是我们那只卵的遭遇。
    /// —— 这样无论事件流程有没有跑、开关有没有残留，都不会误判：
    ///      · 事件正常开打 → 有木桩 → 继续 ✓
    ///      · 自定义/调试房间直接放这场遭遇 → 也有木桩 ✓
    ///      · 普通怪物房间（开关残留）→ 没木桩 → 清掉残留、什么都不做 ✓
    /// </remarks>
    private bool ConfirmThisCombat(MegaCrit.Sts2.Core.Combat.ICombatState combat)
    {
        if (!IsBetrayalCombat(combat) && !CurrentRoomIsOurEncounter(combat) && !PvpBareCombat.Active(combat))
        {
            ClearBetrayalMarkers("开战了但场上没有我们的木桩");
            return false;
        }

        _betrayalCombat = combat;
        _betrayalCombatConfirmed = true;
        ArchitectBetrayalEvent.BetrayalCombatActive = true;

        return true;
    }

    /// <summary>
    /// ② 布场基础设施：回放补记 → 队伍视图 → 第三方兼容 → BGM。
    /// </summary>
    /// <remarks>
    /// ★ <see cref="EnsureReplayInitialState" /> 必须最先做，且不能被任何会抛异常的步骤挡在前面。
    ///   多人局里少了它，第一回合 StartTurn → RecordChecksum 会抛
    ///   "RecordInitialState must be called first"，回放/回合循环当场死掉，表现就是黑屏。
    ///   之前把它放在最后，而前面"发遗物"里 RelicCmd.Obtain 会碰界面节点，
    ///   一旦那里抛异常就会把它一起跳过 —— 这就是黑屏的来源。
    /// </remarks>
    private static void PrepareCombatInfrastructure(MegaCrit.Sts2.Core.Combat.ICombatState combat)
    {
        EnsureReplayInitialState();

        // 队伍视图：给"有主单位"装上按队伍看的 CombatState。
        BetrayalCombatView.Install(combat);

        // 第三方 mod 兼容：薄荷尖塔这类"受到的伤害"统计会把队友当怪物读 Monster（null）→ 崩。
        // 正常在 Entry.Init 就装好了；这里再懒重试一次，防它比我们晚加载。
        MintySpire2Compat.Retry();

        // 玩家对决开始 —— 先把这场战斗的曲子放起来。
        StartPvpMusic();
    }

    /// <summary>
    /// ③ 开打之前所有人回满血、生命上限按设置翻倍；顺手挂「群体壁垒」。
    /// </summary>
    /// <remarks>
    /// 这场战斗之后整局就结束了，所以不用恢复原值。
    /// 判据是"这场战斗"（`_buffedCombat`）而不是静态 bool：万一上一场的 AfterCombatEnd 没跑到
    /// （自定义房间、中途退出等），旧的 true 会把这一场的加成吞掉。
    ///
    /// ⛔ 不再发【死斗】遗物，改回"直接改血"，原因（线上实测）：
    ///   ① RelicCmd.Obtain 内部会碰界面节点（动画/音效/图鉴标记），开战那一瞬间调用很容易抛，
    ///      进而把后面的步骤一起跳过；② 遗物是整局对象，用作"这场战斗"的判据容易牵连后面的战斗；
    ///   ③ 直接改血在客户端/主机上都是纯数值操作，最稳。
    /// </remarks>
    private static async Task BuffPlayersIfNeededAsync(MegaCrit.Sts2.Core.Combat.ICombatState combat)
    {
        if (ReferenceEquals(_buffedCombat, combat))
            return;

        _buffedCombat = combat;

        foreach (var playerCreature in combat.PlayerCreatures)
        {
            if (playerCreature.Player is not { } owner || owner.Creature is not { } creature)
                continue;

            // 倍数来自设置（1-5，默认 3）。
            var multiplier = YouJinSettings.ArchitectHpMultiplier;
            var boosted = creature.MaxHp * multiplier;
            await CreatureCmd.SetMaxHp(creature, boosted);
            await CreatureCmd.SetCurrentHp(creature, boosted);

            Entry.Logger.Info(
                $"建筑师的背叛：NetId={owner.NetId} 回满血，生命上限 ×{multiplier} → {boosted}");
        }

        // 【群体壁垒】开关：开战给所有玩家挂上「壁垒」（BarricadePower）。
        if (!YouJinSettings.ArchitectFortitudeEnabled)
            return;

        foreach (var player in combat.Players)
        {
            if (player.Creature is not { } target)
                continue;

            try
            {
                // ctx 传 null —— 和 SpirePvp 在开战时挂能力用的是同一个调用形状。
                await PowerCmd.Apply<BarricadePower>(null!, target, 1m, null!, null!, false);
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"建筑师的背叛：给 NetId={player.NetId} 上壁垒失败 {ex}");
            }
        }

        Entry.Logger.Info("建筑师的背叛：已给所有玩家挂上「壁垒」（群体壁垒开关）");
    }

    /// <summary>④ 把场上所有"没有主人的敌方单位"（= 我们的木桩）改成打不死的活靶子。</summary>
    private static async Task PrepareDummiesAsync(MegaCrit.Sts2.Core.Combat.ICombatState combat)
    {
        foreach (var enemy in combat.Enemies.Where(e => e.Player is null).ToList())
            await PrepareDummyAsync(enemy);
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
        CardModel? source,
        bool wasRemovalPrevented,
        float deathAnimLength)
    {
        try
        {
            // 判据走门面（PvpCombat）：静态开关 或「按遭遇类型判定」任一成立就算这场
            // —— 后者更稳，不靠状态清理时序。
            if (!PvpCombat.IsActive(creature?.CombatState))
                return;

            // 只关心玩家死亡（木桩自己被打死不算）。
            if (creature.Player is null)
                return;

            var combat = CurrentCombatState;
            if (combat is null)
                return;

            var alivePlayers = combat.PlayerCreatures.Count(playerCreature => playerCreature.IsAlive);
            if (_betrayalEnding)
            {
                Entry.Logger.Info("建筑师的背叛：已在收尾，跳过重复的结束流程");
                return;
            }
            if (alivePlayers > 1)
                return;

            _betrayalEnding = true;
            Entry.Logger.Info($"建筑师的背叛：只剩 {alivePlayers} 名玩家，结束背叛战斗（已加闸，防重复）");

            // 只清掉真正的怪物（那只卵）。
            // 注意：出牌期间 Enemies 里可能混着队友（见 PvpEnemyListPatch），
            // 无脑"全杀"会把唯一活着的那个人也干掉。
            //
            // 无木桩形态下场上本来就没有怪物，这一圈是空转 —— 收场交给
            // BareCombatKeepAlivePatch（它看到「只剩 1 人」就把结束判定放开）。
            if (PvpBareCombat.Active(combat))
            {
                Entry.Logger.Info("建筑师的挑拨：无木桩形态 → 交给胜负判定收场（没有要清的单位）");
                return;
            }

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
        _betrayalEnding = false;
        StopPvpMusic();
        ClearBetrayalMarkers("战斗结束");
        _buffedCombat = null;
        return Task.CompletedTask;
    }

    /// <summary>本回合这个玩家打出的牌数 +1（用于「每回合出牌上限」）。</summary>
    public override Task AfterCardPlayed(
        PlayerChoiceContext choiceContext,
        MegaCrit.Sts2.Core.Entities.Cards.CardPlay play)
    {
        try
        {
            if (ArchitectBetrayalEvent.BetrayalCombatActive)
                ArchitectDuelRules.OnCardPlayed(play.Player);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：出牌计数失败 {ex}");
        }

        return Task.CompletedTask;
    }

    /// <summary>回合开始：清掉这个玩家本回合的出牌计数。</summary>
    public override Task AfterPlayerTurnStart(
        PlayerChoiceContext choiceContext,
        MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        try
        {
            if (ArchitectBetrayalEvent.BetrayalCombatActive)
                ArchitectDuelRules.ResetForPlayer(player);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的挑拨：出牌计数重置失败 {ex}");
        }

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
        // 同上：判据统一走门面（开关 或 遭遇类型，任一成立）。
        if (!PvpCombat.IsActiveFor(creature))
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
