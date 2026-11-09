using MegaCrit.Sts2.Core.Logging;

namespace PvpDuel.Core;

/// <summary>本 mod 的身份与日志出口：ModId / Version 的唯一副本，日志前缀的唯一来源。</summary>
internal static class ModInfo
{
    /// <summary>必须与清单 <c>pvp_duel.json</c> 的 id、DLL 名一致。</summary>
    public const string ModId = "pvp_duel";

    /// <summary>必须与清单 <c>pvp_duel.json</c> 的 version 一致（编译前有 CheckVersionConsistency 把关）。</summary>
    public const string Version = "0.15.1";

    private const string Prefix = "[pvp_duel] ";

    private static readonly HashSet<string> OnceMessages = [];

    public static void Info(string message)
    {
        Log.Info(Prefix + message);
    }

    public static void Warn(string message)
    {
        Log.Warn(Prefix + message);
    }

    public static void Error(string message)
    {
        Log.Error(Prefix + message);
    }

    /// <summary>只打一次（同一句话每次进程只出一条，避免每回合刷屏）。</summary>
    /// <remarks>这类"过程日志"归诊断模块管：诊断关掉时一条都不打，其余 Info/Warn/Error 照常。</remarks>
    public static void LogOnce(string message)
    {
        if (!ModuleSwitches.Diagnostics)
        {
            return;
        }

        lock (OnceMessages)
        {
            if (!OnceMessages.Add(message))
            {
                return;
            }
        }

        Log.Info(Prefix + message);
    }
}
