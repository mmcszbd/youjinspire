using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;
using YouJin.Scripts;

namespace PvpDuel.Core.Settings;

/// <summary>
/// 把「房主的那份 PvP 设置」塞进本局的 modifiers 列表，让原版的开局同步把它发给所有人。
/// </summary>
/// <remarks>
/// <para>
/// 手法借自 Auto-Anthonyology（<c>AutoAnthony.ChaosPoolSnapshotModifier</c>）：不自己开网络通道，
/// 而是在 <c>StartRunLobby.BeginRunForAllPlayers(seed, modifiers)</c> 之前往 <paramref name="modifiers"/>
/// 里塞一个带 <c>[SavedProperty]</c> 字段的 mod 模型 —— 原版会把整个 modifiers 列表随开局发给每个玩家，
/// 客户端用 <c>RunState.Modifiers</c> 就能读回房主的值。
/// </para>
/// <para>
/// 注册不用自己动手：<c>ModelDb.AllAbstractModelSubtypes</c> 会通过
/// <c>ReflectionHelper.GetSubtypesInMods&lt;AbstractModel&gt;()</c> 把 mod 程序集里的子类全扫进去。
/// </para>
/// <para>
/// 只放「会影响对局内容」的字段。横幅 / 小抄 / 诊断 / 状态行这些纯本机表现各机自选，
/// 否则客户端连自己的日志开关都被房主覆盖掉，排查问题会很别扭。
/// </para>
/// </remarks>
public sealed class PvpSettingsModifier : ModifierModel
{
    /// <summary>是否由房主写过（false = 本局没有同步设置，各机继续用本地配置）。</summary>
    [SavedProperty]
    public bool Specified { get; set; }

    /// <summary>写入者（房主的 netId，纯日志用）。</summary>
    [SavedProperty]
    public string HostId { get; set; } = string.Empty;

    /// <summary>PvP 方式（<c>0=Off / 1=Speed / 2=TurnBased</c>，对应 <see cref="PvpStyle" />）。</summary>
    [SavedProperty]
    public int StyleValue { get; set; }

    /// <summary>
    /// 玩法四选一（<c>0=Off / 1=Greedy / 2=Shared / 3=Quest / 4=Split</c>，对应 <c>YouJinSettings.PartyMode</c>）。
    /// </summary>
    /// <remarks>
    /// 这一项必须同步，否则「建筑师挑拨」的计分会在两端算成两套：
    /// 计分只在贪婪模式下进行（<c>YouJinSettings.GreedyEnabled</c>），
    /// 房主是贪婪、客户端是别的玩法时，客户端会算出「没有 boss」。
    /// </remarks>
    [SavedProperty]
    public int PartyModeValue { get; set; }

    [SavedProperty]
    public bool Enabled { get; set; }

    [SavedProperty]
    public bool ModuleInvitation { get; set; }

    [SavedProperty]
    public bool ModuleExtraAct { get; set; }

    [SavedProperty]
    public bool ModuleTogetherUnbind { get; set; }

    [SavedProperty]
    public bool ModuleSplitMode { get; set; }

    [SavedProperty]
    public bool ModuleSplitKeepAlive { get; set; }

    [SavedProperty]
    public bool ModuleSplitPresence { get; set; }

    [SavedProperty]
    public bool ModuleSplitIcon { get; set; }

    [SavedProperty]
    public bool ModuleSplitSoloRun { get; set; }

    [SavedProperty]
    public bool ModuleMirrorDuel { get; set; }

    [SavedProperty]
    public bool ModuleSplitMeeting { get; set; }

    [SavedProperty]
    public bool ModuleSplitMeetingFastTest { get; set; }

    [SavedProperty]
    public bool ModuleContest { get; set; }

    [SavedProperty]
    public bool ModuleTestTools { get; set; }

    [SavedProperty]
    public bool ModuleBalance { get; set; }

    [SavedProperty]
    public int MaxCardsPerTurn { get; set; }

    [SavedProperty]
    public int MaxSameCardPerTurn { get; set; }

    [SavedProperty]
    public int ExtraTurnLimit { get; set; }

    [SavedProperty]
    public int SecondPlayerEnergy { get; set; }

    [SavedProperty]
    public int SecondPlayerBlock { get; set; }

    [SavedProperty]
    public int SecondPlayerCards { get; set; }

    /// <summary>纯同步载体，不参与战斗钩子。</summary>
    public override bool ShouldReceiveCombatHooks => false;

    // 这个 modifier 是程序化塞进去的，不会出现在任何选项列表里；标题借原版现成的键，
    // 避免在 modifiers 表里缺键时打出 "Key not found" 警告。
    public override LocString Title => new LocString("modifiers", "draft.title");

    public override LocString Description => new LocString("modifiers", "draft.description");

    /// <summary>把本机（房主）的设置抄进这个模型。</summary>
    internal void CaptureFrom(PvpSettings settings, PvpStyle style, string hostId)
    {
        Specified = true;
        HostId = hostId ?? string.Empty;
        StyleValue = (int)style;
        Enabled = settings.Enabled;
        ModuleInvitation = settings.ModuleInvitation;
        ModuleExtraAct = settings.ModuleExtraAct;
        ModuleTogetherUnbind = settings.ModuleTogetherUnbind;
        ModuleSplitMode = settings.ModuleSplitMode;
        ModuleSplitKeepAlive = settings.ModuleSplitKeepAlive;
        ModuleSplitPresence = settings.ModuleSplitPresence;
        ModuleSplitIcon = settings.ModuleSplitIcon;
        ModuleSplitSoloRun = settings.ModuleSplitSoloRun;
        ModuleMirrorDuel = settings.ModuleMirrorDuel;
        ModuleSplitMeeting = settings.ModuleSplitMeeting;
        ModuleSplitMeetingFastTest = settings.ModuleSplitMeetingFastTest;
        ModuleContest = settings.ModuleContest;
        ModuleTestTools = settings.ModuleTestTools;
        ModuleBalance = settings.ModuleBalance;
        MaxCardsPerTurn = settings.MaxCardsPerTurn;
        MaxSameCardPerTurn = settings.MaxSameCardPerTurn;
        ExtraTurnLimit = settings.ExtraTurnLimit;
        SecondPlayerEnergy = settings.SecondPlayerEnergy;
        SecondPlayerBlock = settings.SecondPlayerBlock;
        SecondPlayerCards = settings.SecondPlayerCards;
    }

    /// <summary>把这台机器上的设置改写成房主的值（原地改，保住 <c>PvpSettingsStore.Current</c> 的引用身份）。</summary>
    internal void ApplyTo(PvpSettings settings)
    {
        settings.Enabled = Enabled;
        settings.ModuleInvitation = ModuleInvitation;
        settings.ModuleExtraAct = ModuleExtraAct;
        settings.ModuleTogetherUnbind = ModuleTogetherUnbind;
        settings.ModuleSplitMode = ModuleSplitMode;
        settings.ModuleSplitKeepAlive = ModuleSplitKeepAlive;
        settings.ModuleSplitPresence = ModuleSplitPresence;
        settings.ModuleSplitIcon = ModuleSplitIcon;
        settings.ModuleSplitSoloRun = ModuleSplitSoloRun;
        settings.ModuleMirrorDuel = ModuleMirrorDuel;
        settings.ModuleSplitMeeting = ModuleSplitMeeting;
        settings.ModuleSplitMeetingFastTest = ModuleSplitMeetingFastTest;
        settings.ModuleContest = ModuleContest;
        settings.ModuleTestTools = ModuleTestTools;
        settings.ModuleBalance = ModuleBalance;
        settings.MaxCardsPerTurn = MaxCardsPerTurn;
        settings.MaxSameCardPerTurn = MaxSameCardPerTurn;
        settings.ExtraTurnLimit = ExtraTurnLimit;
        settings.SecondPlayerEnergy = SecondPlayerEnergy;
        settings.SecondPlayerBlock = SecondPlayerBlock;
        settings.SecondPlayerCards = SecondPlayerCards;
    }
}
