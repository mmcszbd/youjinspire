using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.SplitMode;

/// <summary>捕获那条联机传输（每开一局都会走 <c>InitializeShared</c>，单机局传进来的是单机服务，会被 <see cref="SplitLink.Capture" /> 忽略）。</summary>
[HarmonyPatch(typeof(RunManager), "InitializeShared")]
internal static class SplitLinkCapturePatch
{
    [HarmonyPostfix]
    private static void Postfix(object __0)
    {
        if (!ModuleSwitches.SplitMode)
        {
            return;
        }

        SplitLink.Capture(__0 as INetGameService);
        SplitStatusHud.EnsureRunning();
        SplitLink.EnsurePump();

        if (ModuleSwitches.SplitKeepAlive)
        {
            SplitLink.Arm();
        }
    }
}

/// <summary>把「退出对局」这一段包起来：只在这段窗口里允许拦断连。</summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class SplitCleanUpWindowPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        if (ModuleSwitches.SplitKeepAlive)
        {
            SplitLink.BeginSuppressWindow();
        }
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        SplitLink.EndSuppressWindow();
    }
}

/// <summary>客户端：拦下退出对局时的主动断开（返回 false = 不执行原方法）。</summary>
[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Disconnect))]
internal static class SplitClientDisconnectGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NetError reason)
    {
        return !SplitLink.ShouldSuppressDisconnect(reason);
    }
}

/// <summary>主机：同上。</summary>
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.Disconnect))]
internal static class SplitHostDisconnectGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NetError reason)
    {
        return !SplitLink.ShouldSuppressDisconnect(reason);
    }
}
