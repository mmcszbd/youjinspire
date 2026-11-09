namespace PvpDuel.Core.SplitMode;

/// <summary>分离模式（C2）的阶段。</summary>
internal enum SplitStage
{
    /// <summary>没启用（或已经回到普通玩法）。</summary>
    Off,

    /// <summary>分离期：本机跑自己的单机局，跨机只传坐标与信令。</summary>
    SoloRun,

    /// <summary>合流：双方都打完第三幕 → 换快照 → 合成决斗局存档 → 切会话。</summary>
    Meeting,

    /// <summary>决斗期：临时的联机局，只装「PVP 幕」。</summary>
    DuelRun,

    /// <summary>赛后：决斗打完，两人都结束本局。</summary>
    AfterRun,
}

/// <summary>分离模式的阶段状态（唯一一份，只在日志与 HUD 里读）。</summary>
internal static class SplitRuntime
{
    public static SplitStage Stage { get; private set; } = SplitStage.Off;

    /// <summary>
    /// 这一局大厅里有几个人。分离期本机跑的是单机局（<c>RunState.Players</c> 只有自己），
    /// 想判断「整局几个人」只能靠开跑那一刻从大厅记下来。
    /// </summary>
    public static int PartySize { get; private set; }

    /// <summary>
    /// 本机已经打完三幕（最后一幕的 BOSS 结算完、<c>RunManager.EnterNextAct</c> 被调起）。
    /// 由 <see cref="SplitLastActPatch" /> 置真，<see cref="PresencePublisher" /> 把它当
    /// <c>Activity = 3</c> 广播出去，两边都置真才合流。
    /// </summary>
    public static bool FinishedLastAct { get; private set; }

    /// <summary>记下大厅人数（<see cref="SplitSoloRun" /> 开跑时调一次）。</summary>
    public static void NotePartySize(int size)
    {
        PartySize = size;
        ModInfo.Info($"[SplitMode] 本局人数：{size}");
    }

    /// <summary>本机打完三幕了。</summary>
    public static void NoteLastActFinished()
    {
        if (FinishedLastAct)
        {
            return;
        }

        FinishedLastAct = true;
        ModInfo.Info("[SplitMode] 本机已打完三幕，等着对方也打完就合流");
    }

    /// <summary>新一局开始时清掉。</summary>
    public static void ResetProgress()
    {
        FinishedLastAct = false;
    }

    public static void SetStage(SplitStage stage)
    {
        if (Stage == stage)
        {
            return;
        }

        ModInfo.Info($"[SplitMode] 阶段 {Stage} → {stage}");
        Stage = stage;
    }

    public static string Describe()
    {
        return Stage switch
        {
            SplitStage.Off => "未启用",
            SplitStage.SoloRun => "分离期（各自单机局）",
            SplitStage.Meeting => "合流（换快照 / 合成存档）",
            SplitStage.DuelRun => "决斗期（临时联机局）",
            SplitStage.AfterRun => "赛后（结束本局）",
            _ => Stage.ToString(),
        };
    }
}
