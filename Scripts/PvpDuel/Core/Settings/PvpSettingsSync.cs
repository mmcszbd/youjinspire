using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using YouJin.Scripts;

namespace PvpDuel.Core.Settings;

/// <summary>
/// 「设置只由房主决定、其他人自动跟随」的两端。
/// </summary>
/// <remarks>
/// <para>
/// 手法照 HextechRunes（workshop <c>3747501308</c>）：房主把整份设置抄进一个
/// <see cref="PvpSettingsModifier" />，再挂进 <c>RunState.Modifiers</c>；原版的开局 / 存档同步会把这个
/// 列表带到每个玩家，客户端从 <c>RunState.Modifiers</c> 读回房主的值。
/// </para>
/// <para>
/// <b>读取端不改本机配置</b>：<see cref="PvpSettingsStore.Current" /> 与
/// <see cref="YouJinSettings.PvpStyleSetting" /> / <see cref="YouJinSettings.PartyModeSetting" />
/// 在本局有快照时优先返回快照，本机设置（以及设置页显示的值）保持原样。
/// 早先的版本是把房主的值「原地写回」本机设置对象，那会把房主的开关落到客户端磁盘上，
/// 下一局单机也跟着变 —— 这里刻意不再那么做。
/// </para>
/// </remarks>
internal static class PvpSettingsSync
{
    private static RunState? _snapshotState;

    private static PvpSettings? _snapshot;

    private static int _loggedCount;

    /// <summary>房主侧：把本机设置塞进开局用的 modifiers 列表（多人 lobby 开局这条路径）。</summary>
    internal static void StampHostSettings(StartRunLobby lobby, List<ModifierModel>? modifiers)
    {
        if (modifiers is null)
        {
            return;
        }

        try
        {
            if (!NetGameTypeExtensions.IsMultiplayer(lobby.NetService.Type))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[PvP 设置同步] 判断联机类型失败，按单机处理：{ex.Message}");
            return;
        }

        if (modifiers.Any(m => m is PvpSettingsModifier { Specified: true }))
        {
            return;
        }

        var mutable = BuildFromLocal();
        modifiers.Add(mutable);
        LogStamp(mutable, "开局 modifiers");
    }

    /// <summary>
    /// 房主侧：本局还没有同步载体时，按本机设置造一个挂进 <c>RunState</c>。
    /// </summary>
    /// <remarks>
    /// 分离模式不经过 <c>StartRunLobby.BeginRunForAllPlayers</c>（各自开单机局、再合流），
    /// 所以光靠 <see cref="StampHostSettings" /> 那条路，房主的设置永远进不了
    /// <c>RunState.Modifiers</c>，合流存档自然也带不过去。这里补的就是「单机开局也挂上」。
    /// </remarks>
    internal static void EnsureRunModifier(RunState? state)
    {
        if (state is null || FindSynced(state) is not null || !ShouldCarrySettings())
        {
            return;
        }

        try
        {
            var mutable = BuildFromLocal();
            state.AddModifierDebug(mutable);

            // 刚挂上去，之前缓存下来的「本局没有快照」已经过时。
            _snapshotState = null;
            _snapshot = null;

            LogStamp(mutable, "单机开局");
        }
        catch (Exception ex)
        {
            Log.Warn($"[PvP 设置同步] 挂载本局设置失败（继续用本机配置）：{ex}");
        }
    }

    /// <summary>本局快照（房主写进 <see cref="PvpSettingsModifier" /> 的那份）；没有就返回 null。</summary>
    internal static PvpSettings? SnapshotFor(RunState? state)
    {
        if (state is null)
        {
            return null;
        }

        if (ReferenceEquals(state, _snapshotState))
        {
            return _snapshot;
        }

        var synced = FindSynced(state);
        if (synced is null)
        {
            _snapshotState = state;
            _snapshot = null;
            return null;
        }

        try
        {
            var settings = new PvpSettings();
            synced.ApplyTo(settings);
            _snapshotState = state;
            _snapshot = settings;

            Log.Info($"[PvP 设置同步] 已跟随房主（{synced.HostId}）：PvP 方式={(PvpStyle)synced.StyleValue}"
                     + $"｜玩法={(PartyMode)synced.PartyModeValue}"
                     + $"｜主开关={synced.Enabled} 入口={synced.ModuleInvitation} 额外幕={synced.ModuleExtraAct}"
                     + $"｜分离={synced.ModuleSplitMode} 合流=随分离 擂台赛={synced.ModuleContest}"
                     + "（本局这些开关由房主决定，本机设置页改动不生效）");
            return settings;
        }
        catch (Exception ex)
        {
            Log.Warn($"[PvP 设置同步] 读取房主设置失败（继续用本机配置）：{ex}");
            _snapshotState = state;
            _snapshot = null;
            return null;
        }
    }

    /// <summary>本局生效的 PvP 方式；本局没有快照时返回 null（让调用方回落到本机设置）。</summary>
    internal static PvpStyle? StyleFor()
    {
        var synced = SyncedModifier();
        if (synced is null)
        {
            return null;
        }

        return synced.StyleValue is >= 0 and <= 2 ? (PvpStyle)synced.StyleValue : null;
    }

    /// <summary>本局生效的玩法；本局没有快照时返回 null（让调用方回落到本机设置）。</summary>
    internal static PartyMode? PartyModeFor()
    {
        var synced = SyncedModifier();
        if (synced is null)
        {
            return null;
        }

        return synced.PartyModeValue is >= 0 and <= 4 ? (PartyMode)synced.PartyModeValue : null;
    }

    /// <summary>新的一局：忘掉上一局的 state，让下一次读取重新算。</summary>
    internal static void Reset()
    {
        _snapshotState = null;
        _snapshot = null;
    }

    private static PvpSettingsModifier BuildFromLocal()
    {
        var mutable = (PvpSettingsModifier)ModelDb.Modifier<PvpSettingsModifier>().ToMutable();

        // 这里读的是本机（房主）的本地配置；EnsureRunModifier 刚清过缓存，不会被本局快照绕回去。
        mutable.CaptureFrom(PvpSettingsStore.Current, YouJinSettings.PvpStyleSetting, Environment.MachineName);

        // 玩法四选一也是「影响玩法」的一项，而且建筑师挑拨的计分只认贪婪模式：
        // 不同步它，房主和客户端会各算一套 boss。
        mutable.PartyModeValue = (int)YouJinSettings.PartyModeSetting;
        return mutable;
    }

    private static void LogStamp(PvpSettingsModifier mutable, string where)
    {
        Interlocked.Increment(ref _loggedCount);
        Log.Info($"[PvP 设置同步] 房主写入本局设置（{where}）：PvP 方式={YouJinSettings.PvpStyleSetting}"
                 + $"｜玩法={YouJinSettings.PartyModeSetting}"
                 + $"｜主开关={mutable.Enabled} 入口={mutable.ModuleInvitation} 额外幕={mutable.ModuleExtraAct}"
                 + $"｜分离={mutable.ModuleSplitMode} 合流=随分离 擂台赛={mutable.ModuleContest}"
                 + $"｜写入第 {_loggedCount} 次");
    }

    private static PvpSettingsModifier? SyncedModifier()
    {
        try
        {
            return FindSynced(RunManager.Instance?.DebugOnlyGetState());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PvpSettingsModifier? FindSynced(RunState? state)
        => state?.Modifiers?.OfType<PvpSettingsModifier>().FirstOrDefault(m => m.Specified);

    /// <summary>
    /// 这一局要不要背设置：多人局由房主背；分离模式虽然开局是单机，但随后会合流成同一局，也要背。
    /// </summary>
    /// <remarks>
    /// 纯单机局刻意<b>不</b>背 —— 挂上之后本局生效值就成了开局的快照，
    /// 局内再改设置（比如临时开「测试工具」）会被冻住，那样很难用。
    /// </remarks>
    private static bool ShouldCarrySettings()
    {
        try
        {
            var service = RunManager.Instance?.NetService;
            var type = service?.Type ?? NetGameType.None;
            if (NetGameTypeExtensions.IsMultiplayer(type))
            {
                return type == NetGameType.Host;
            }
        }
        catch (Exception)
        {
            // 判断不了就按单机走，下面再看分离模式。
        }

        return YouJinSettings.PartyModeSetting == PartyMode.Split;
    }
}

/// <summary>房主在多人开局前写入设置。</summary>
[HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayers")]
internal static class PvpSettingsStampPatch
{
    private static void Prefix(StartRunLobby __instance, List<ModifierModel>? modifiers)
    {
        PvpSettingsSync.StampHostSettings(__instance, modifiers);
    }
}

/// <summary>房主开单机局（分离模式的第一段）：把设置挂进本局，合流时随存档带过去。</summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewSingleplayer))]
internal static class PvpSettingsSingleplayerPatch
{
    private static void Postfix(RunManager __instance)
    {
        PvpSettingsSync.EnsureRunModifier(__instance.DebugOnlyGetState());
    }
}

/// <summary>房主开多人局：同上（<c>BeginRunForAllPlayers</c> 没覆盖到的入口在这里兜底）。</summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewMultiplayer))]
internal static class PvpSettingsMultiplayerPatch
{
    private static void Postfix(RunManager __instance)
    {
        PvpSettingsSync.EnsureRunModifier(__instance.DebugOnlyGetState());
    }
}
