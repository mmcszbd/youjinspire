using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using YouJin.Scripts;

namespace PvpDuel.Core.SplitMode;

/// <summary>
/// 分离期入口：在<b>大厅按下「开始」的那一刻</b>拦下来，各自开始自己的单机局（同种子）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么拦在大厅而不是进局之后再切：先切要等联机局真的开起来（两人已经共享跑了几步、可能进了战斗），
/// 再 <c>CleanUp</c> 会把这一段丢掉，还要和一个"活着的对局"抢状态；拦在大厅则从头就是干净的——
/// 联机局压根没被创建，<c>RunManager.State</c> 还是 null，起单机局不需要任何清场。
/// </para>
/// <para>
/// 连接不用重连：调用方紧接着就会 <c>CleanUpLobby(disconnectSession: false)</c>（本体自己的行为），
/// 只解注册大厅、不断开。我们要做的是把那条传输<b>提前抓进 <see cref="SplitLink" /></b>——
/// 因为接下来本机跑的是单机局，本体不会再有人泵它了（详见 SplitLink 的注释）。
/// </para>
/// <para>已知取舍：单机局用的是<b>新玩家</b>（角色/进阶照搬，卡组与遗物是初始的），不继承联机局的进度。</para>
/// </remarks>
internal static class SplitSoloRun
{
    /// <summary>能不能拦（开关开着 + 大厅信息齐全）。不满足就让本体照常开联机局。</summary>
    public static bool CanIntercept(StartRunLobby? lobby)
    {
        // 分离模式独立于「相遇决斗」：这里只问分离模式自己。
        // 分离模式关掉 → 不拦 → 本体照常开联机共享局 → 老玩法（额外幕 + 「对决邀请」事件 → PVP）。
        return ModuleSwitches.SplitSoloRun
               && NGame.Instance is not null
               && lobby is not null
               && lobby.LocalPlayer.character is not null;
    }

    /// <summary>代替本体开始联机局：本机开一局自己的单机局，并把大厅那条传输接管下来。</summary>
    public static async Task<RunState> StartInsteadOfMultiplayer(
        StartRunLobby lobby,
        IReadOnlyList<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers,
        string seed,
        int ascensionLevel)
    {
        var game = NGame.Instance!;
        var character = lobby.LocalPlayer.character;

        // 分离期本机跑的是单机局，RunState 里只有自己一个人 —— 整局几个人只能现在从大厅记下来。
        // 「相遇 → 镜像 PvP」按两人配对设计，4 人时必须靠这个数字把自己关掉（见 SplitMirrorTrigger）。
        SplitRuntime.NotePartySize(lobby.Players.Count);

        // 新一局：把上一局残留的分离状态清干净。
        // 这些标记都是静态的、不会随局结束复位：合流阶段的 Stage 停在 Meeting、
        // 合流的 _merged / _fastTestDone 还是真 → 第二局 ShouldTriggerMeeting 与
        // TickFastTest 第一句就早退，怎么点都不合流（2026-11-04 19:12 日志：三次
        // 「本局人数：2」，只有第一次后面跟着「合流测试：开关已生效」）。
        SplitRuntime.SetStage(SplitStage.Off);
        SplitRuntime.ResetProgress();
        SplitMeeting.ResetForNewRun();

        // 分离 = 各自为战：先把 together 的共生体解掉。
        // 否则它还认为两人"共用身体"，会把金币/能力/卡组往对面推（日志里那些"金币同步"就是它干的），
        // 而我们马上要跑的是两台各自独立的单机局——两边的镜像互相打架。
        if (ModuleSwitches.TogetherUnbind)
        {
            Interop.TogetherInterop.Unbind("split_solo");
        }

        // 大厅这条传输就是分离期的生命线：本体马上不会再泵它了，先抓住、守住。
        SplitLink.Capture(lobby.NetService);
        SplitLink.EnsurePump();
        SplitPresenceChannel.Initialize();
        SplitLink.Arm();

        ModInfo.Info(
            $"[SplitMode] 大厅拦截：不开始联机局，各自开单机局"
            + $"（角色={character.Id.Entry} 种子={seed} 进阶={ascensionLevel} 幕数={acts.Count}）");

        // 0.113 的 StartNewSingleplayerRun 收的是 Player（0.111 收的是 CharacterModel），
        // 并且多了一个 isCustomGame 参数；Player 要自己按「角色 + 解锁状态 + 本机 NetId」造。
        var player = Player.CreateForNewRun(
            character,
            SaveManager.Instance.GenerateUnlockStateFromProgress(),
            lobby.LocalPlayer.id);

        var runState = await game.StartNewSingleplayerRun(
            player,
            shouldSave: false,
            acts,
            modifiers,
            seed,
            GameMode.Standard,
            isCustomGame: false,
            ascensionLevel: ascensionLevel);

        ModInfo.Info("[SplitMode] 大厅拦截：自己的单机局已开始（连接留着，坐标照发）");

        // 开局遗物「苹果」：分离模式专属，每人一件（详见 Scripts\relics\AppleRelic.cs）。
        await GrantAppleRelicAsync(runState);

        return runState;
    }

    /// <summary>
    /// 给局里每个玩家发「苹果」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 走 <see cref="RelicCmd.Obtain(RelicModel, MegaCrit.Sts2.Core.Entities.Players.Player, int)" /> 是为了让遗物栏
    /// 有正常的"得到遗物"动画和音效；但它内部会碰界面节点（<c>NRun.GlobalUi.RelicInventory</c>），
    /// 仓库里有先例是它抛异常把后面的步骤一起带崩（见 ArchitectBetrayalCombatModel 的注释）。
    /// </para>
    /// <para>
    /// 所以这里兜一层：抛了就退回 <c>AddRelicInternal(silent: true)</c> —— 纯数值挂上去，
    /// 不碰任何界面，效果一样（只是没有那一下闪光）。玩家测出来动画不对再回头调。
    /// </para>
    /// </remarks>
    private static async Task GrantAppleRelicAsync(RunState runState)
    {
        foreach (var player in runState.Players)
        {
            if (player.Relics.Any(relic => relic is AppleRelic))
                continue;

            try
            {
                await RelicCmd.Obtain(ModelDb.Relic<AppleRelic>().ToMutable(), player);
            }
            catch (Exception ex)
            {
                ModInfo.Info($"[SplitMode] 苹果：RelicCmd.Obtain 抛异常，退回静默发放 {ex}");

                if (!player.Relics.Any(relic => relic is AppleRelic))
                {
                    var fallback = ModelDb.Relic<AppleRelic>().ToMutable();
                    player.AddRelicInternal(fallback, -1, silent: true);
                    await fallback.AfterObtained();
                }
            }

            ModInfo.Info(
                $"[SplitMode] 苹果：NetId={player.NetId} 已发放"
                + $"（遗物数={player.Relics.Count}、生命={player.Creature?.CurrentHp}/{player.Creature?.MaxHp}）");
        }
    }
}
