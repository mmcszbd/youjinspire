namespace PvpDuel.Core.Settings;

/// <summary>本 mod 的持久化设置（加字段给个合理默认值即可，老配置缺字段会取默认值）。</summary>
/// <remarks>
/// 带「⚠ 两端一致」的开关会改变对局内容（章节列表、房间、共享状态），两端不一致会分叉；
/// 其余是纯本机表现 / 日志，各机可以不同。所有模块都受 <see cref="Enabled" /> 这个主开关约束。
/// </remarks>
public sealed class PvpSettings
{
    /// <summary>主开关：关掉后本 mod 所有模块都不工作。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>入口模块：生成「对决邀请」。（⚠ 两端一致）</summary>
    public bool ModuleInvitation { get; set; } = true;

    /// <summary>额外幕模块：三幕打完进入「PVP 决斗」幕。（⚠ 两端一致）</summary>
    public bool ModuleExtraAct { get; set; } = true;


    /// <summary>互操作模块：进决斗前解绑 together 的共享。（⚠ 会影响共享状态）</summary>
    public bool ModuleTogetherUnbind { get; set; } = true;

    /// <summary>表现模块：决斗里的回合横幅文案（纯本机）。</summary>
    public bool ModuleBanners { get; set; } = true;

    /// <summary>表现模块：决斗限制小抄（右上角显示用量 / 上限 / 血量差，纯本机）。</summary>
    public bool ModuleLimitHud { get; set; } = true;

    /// <summary>诊断模块：只打一次的详细日志 + 战斗启动异常打印（纯本机）。</summary>
    public bool ModuleDiagnostics { get; set; } = true;

    // ==================== 分离模式（C2，实验）====================

    /// <summary>分离模式（C2 实验）：把联机链路从「一局」里剥出来。（⚠ 实验功能）</summary>
    public bool ModuleSplitMode { get; set; }

    /// <summary>分离模式下的保活：退出对局时不主动断开联机连接。（⚠ 实验功能，两端都要开）</summary>
    public bool ModuleSplitKeepAlive { get; set; }

    /// <summary>
    /// 分离模式的坐标同步（Presence 通道）。（⚠ 两端一致）
    /// </summary>
    /// <remarks>
    /// 默认开：它是分离模式的核心内容，且整条链路受 <see cref="ModuleSplitMode" /> 约束（那个默认关），
    /// 老配置文件里没有这个键时也能直接生效，省得"总开关开了却什么都没发生"。
    /// </remarks>
    public bool ModuleSplitPresence { get; set; } = true;

    /// <summary>分离模式的地图标记（在地图上画出对端）。</summary>
    public bool ModuleSplitIcon { get; set; } = true;

    /// <summary>
    /// 分离期入口：进联机局后各自切到自己的单机局（同种子）。（⚠ 实验功能，两端都要开）
    /// </summary>
    public bool ModuleSplitSoloRun { get; set; }


    /// <summary>
    /// <b>镜像决斗</b>：分离模式专属的「狭路相逢」事件 —— 两人走到同一节点时，各自打对方的镜像。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是<b>分离模式</b>的玩法，不是回合制 PvP 模块的一部分：它不碰 <c>DuelSession</c>、
    /// 不碰 <c>PvpCombatKit</c>、不需要 PvP 方式选「回合制」。只要分离模式开着就能用。
    /// </para>
    /// <para>（⚠ 两端一致：只有一边开着的话，相遇了另一边不会进事件。）</para>
    /// </remarks>
    public bool ModuleMirrorDuel { get; set; }

    /// <summary>
    /// <b>已废弃</b>：合流不再有单独开关 —— 分离模式（+「各自开单机局」）开着就一定合流。
    /// </summary>
    /// <remarks>
    /// 2026-11-06 起判定只看 <see cref="ModuleSplitSoloRun" />，见
    /// <c>ModuleSwitches.SplitMeeting</c>。字段留着只为读回旧存档（旧配置里通常是 <c>true</c>），
    /// 设置页不再显示，也不再参与任何判断。
    /// </remarks>
    public bool ModuleSplitMeeting { get; set; } = true;

    /// <summary>
    /// <b>测试用</b>：不爬三幕，<b>打赢第一场战斗</b>就当作「三幕打完」，直接进合流。（⚠ 两端一致）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 正常流程要爬满三幕才会触发合流，验一次要几十分钟。开着这个开关时，
    /// <see cref="PvpDuel.Core.SplitMode.SplitMeeting" /> 会在进第一场战斗那一刻先把本机快照抓下来
    /// （战死 / 放弃也留得下），然后在<b>这场战斗打赢</b>的那一帧置「三幕打完」。
    /// </para>
    /// <para>
    /// 判定用的是「敌人全部倒下」而不是「离开战斗房」：后者在玩家点「放弃本局」时也会成立
    /// （<c>CurrentRoom</c> 变 null），那时候 <c>ToSave</c> 已经拿不到东西了。
    /// </para>
    /// <para>⚠ 只在测试时开。正式玩请关掉，否则第一场小怪打完就直接进决斗局。</para>
    /// </remarks>
    public bool ModuleSplitMeetingFastTest { get; set; }

    /// <summary>
    /// <b>擂台赛</b>：三人以上合流后，「对决邀请」事件改成「上台 / 观战 / 结束」三选项 —— 每轮在
    /// 点了上台的人里抽两位开打，其余人留在场上观战，所有人都选「结束」才收场。（⚠ 两端一致）
    /// </summary>
    /// <remarks>
    /// 关掉时行为不变（仍是「接受对决」一项，两人直接开打）。开不开关都要求合流后正好两人
    /// 才走老路径；三人以上且这个开关关着，事件会就地收掉。
    /// </remarks>
    public bool ModuleContest { get; set; }

    /// <summary>
    /// <b>测试工具</b>：设置页里显示一组「点一下即执行」的开关 —— 跳房间 + 秒赢当前战斗。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 等价于游戏自带控制台的 <c>room &lt;ID&gt;</c> 与 <c>win</c>（见 <c>Scripts/TestTools.cs</c>）。
    /// 开关本身不改对局状态，点了哪一行才做哪一件事。
    /// </para>
    /// <para>秒赢一律跳过 PvP 战斗，免得把 <c>DuelSession</c> 的胜负判据打穿。</para>
    /// </remarks>
    public bool ModuleTestTools { get; set; }



    // ==================== 岔路（ForkedRoad）====================

    /// <summary>
    /// 旧「回合上限」（0.10.8 起不再生效，为存档兼容只留着这个字段）。
    /// 决斗现在只打到有人倒下；血厚不厚由「血量倍数」决定（见 §4.1.3）。
    /// </summary>
    public int MaxRounds { get; set; } = 30;

    // ==================== 平衡（决斗专用规则，都在平衡模块下）====================

    /// <summary>平衡模块：单回合出牌上限、同卡上限、额外回合上限、后手补偿。（⚠ 两端一致）</summary>
    public bool ModuleBalance { get; set; } = true;

    /// <summary>单回合出牌上限：达到就自动结束你的回合。0 = 不限制。</summary>
    public int MaxCardsPerTurn { get; set; } = 15;

    /// <summary>同一张牌每回合最多打出几次，超过就打不出去。0 = 不限制。</summary>
    public int MaxSameCardPerTurn { get; set; } = 3;

    /// <summary>同一玩家连续额外回合上限，超过就不再给额外回合。0 = 不限制。</summary>
    public int ExtraTurnLimit { get; set; } = 1;

    /// <summary>后手第一次回合的额外能量。</summary>
    public int SecondPlayerEnergy { get; set; } = 1;

    /// <summary>后手第一次回合的开局格挡。</summary>
    public int SecondPlayerBlock { get; set; } = 5;

    /// <summary>后手第一次回合的额外抽牌。</summary>
    public int SecondPlayerCards { get; set; }
}
