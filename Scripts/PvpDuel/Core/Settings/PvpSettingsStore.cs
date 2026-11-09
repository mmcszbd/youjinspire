using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Utils.Persistence;

namespace PvpDuel.Core.Settings;

/// <summary>设置的持久化入口（RitsuLib 数据存储；注册必须在 BeginModDataRegistration 作用域里且只做一次）。</summary>
internal static class PvpSettingsStore
{
    internal const string DataKey = "settings";

    /// <summary>
    /// 数据挂在哪个 mod id 下（★ 二合一之后是 youjin）。
    /// </summary>
    /// <remarks>
    /// RitsuLib 的数据存储是「按 mod id 注册」的，而合并后 “pvp_duel” 已经不是游戏认得的 mod 了。
    /// 继续用 pvp_duel 注册会走「未注册 mod」的兜底路径（注册可能被拒 → 每次 <see cref="Local" /> 都返回
    /// 新对象、设置根本存不下来）。所以数据改归 youjin 管，文件名仍是 pvp_duel_settings.json：
    /// 老配置读不回来也只是回落默认值，不会崩。
    /// </remarks>
    public const string StoreModId = "youjin";

    private const string FileName = "pvp_duel_settings.json";

    private static bool _initialized;

    /// <summary>本机持久化的那一份（永远不跟随房主）。设置页读写、以及写回本地时用它。</summary>
    public static PvpSettings Local
    {
        get
        {
            Initialize();

            return RitsuLibFramework.GetDataStore(StoreModId).Get<PvpSettings>(DataKey)
                   ?? new PvpSettings();
        }
    }

    /// <summary>本局实际生效的设置：房主同步过就用房主那份，否则用本机那份。</summary>
    /// <remarks>
    /// 客户端本机的配置文件一个字都不改 —— 把房主的值写回本地，会落到磁盘上，下一局单机也跟着变。
    /// 设置页要显示/编辑本机值时请用 <see cref="Local" />。
    /// </remarks>
    public static PvpSettings Current
        => PvpSettingsSync.SnapshotFor(RunManager.Instance?.DebugOnlyGetState()) ?? Local;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        using (RitsuLibFramework.BeginModDataRegistration(StoreModId, false))
        {
            RitsuLibFramework.GetDataStore(StoreModId).Register(
                DataKey,
                FileName,
                SaveScope.Global,
                defaultFactory: () => new PvpSettings(),
                autoCreateIfMissing: true);
        }

        RitsuLibFramework.GetDataStore(StoreModId).InitializeGlobal();
        _initialized = true;
    }
}
