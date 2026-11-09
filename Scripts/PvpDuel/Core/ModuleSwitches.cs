using PvpDuel.Core.Settings;

namespace PvpDuel.Core;

/// <summary>模块级开关：每个模块的补丁在入口处先问这里，关掉的模块连补丁体都不会执行。</summary>
/// <remarks>
/// <para>
/// 两类开关别混：① 会改变对局内容的（入口 / 额外幕 / 解绑）——两端必须一致，
/// 否则章节列表或共享状态会分叉；② 纯本机可见的（横幅 / 诊断）——各机可以不同。
/// </para>
/// <para>
/// 依赖关系：额外幕建立在「入口」模块之上（要靠那个事件进决斗），
/// 所以关掉入口 = 这两个跟着失效；所有模块（诊断除外）都受主开关 <see cref="Core" /> 约束。
/// 诊断独立于主开关：开关关着也要能看见「为什么不生效」。
/// </para>
/// <para>「模块」在本工程里是一等公民：一个模块 = 一个目录 + 这里的一个开关 + 它的补丁与设置项。</para>
/// </remarks>
internal static class ModuleSwitches
{
    private static PvpSettings Settings => PvpSettingsStore.Current;

    /// <summary>主开关：决斗本体（战斗改造、轮流行动、目标判定、回合上限、结算）。</summary>
    public static bool Core => Settings.Enabled;

    /// <summary>入口模块：「对决邀请」事件。（⚠ 两端一致）</summary>
    public static bool Invitation => Core && Settings.ModuleInvitation;

    /// <summary>额外幕模块：三幕打完进入「PVP 决斗」幕。（⚠ 两端一致）</summary>
    /// <remarks>
    /// <para>
    /// 两条路都能开：
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="Invitation" />（普通联机局：事件入口 + 主开关）；</item>
    /// <item><see cref="SplitMode" />（分离模式：各爬三幕后合流进决斗局）。</item>
    /// </list>
    /// <para>
    /// 2026-11-06 修：早先是 <c>Invitation &amp;&amp; ModuleExtraAct</c>，而 <c>Invitation</c> 压在
    /// <see cref="Core" /> 下面、<see cref="Core" /> 又只是「PvP 方式选回合制」的投影
    /// （<c>PvpSettings.Enabled</c>）—— 手速模式 + 分离模式时 <c>Enabled == false</c>，
    /// 于是决斗幕既不换地图也不把房间换成「战战战」，进了决斗局却只有普通房间
    /// （日志 <c>godot.log</c> 1249 行 <c>主开关=False 分离=True 合流=True</c>，
    /// 之后 9 分钟没有一条「决斗幕：把这一间房换成「对决邀请」」）。
    /// 手速形态本来就不需要 <c>Core</c>（<see cref="PvpCombatKit.CanRun" /> 的 Speed 分支只看人数），
    /// 所以这里让分离模式单独放行。
    /// </para>
    /// </remarks>
    public static bool ExtraAct => Invitation || SplitMode;

    /// <summary>互操作模块：进决斗前解绑 together 的共享。（⚠ 会影响共享状态）</summary>
    public static bool TogetherUnbind => Core && Settings.ModuleTogetherUnbind;

    /// <summary>表现模块：决斗里的回合横幅文案。</summary>
    public static bool Banners => Core && Settings.ModuleBanners;

    /// <summary>表现模块：决斗限制小抄（右上角）。</summary>
    public static bool LimitHud => Core && Settings.ModuleLimitHud;

    /// <summary>诊断模块：只打一次的详细日志 + 战斗启动异常打印。</summary>
    public static bool Diagnostics => Settings.ModuleDiagnostics;

    /// <summary>平衡模块：单回合出牌上限、同卡上限、额外回合上限、后手补偿。（⚠ 两端一致）</summary>
    public static bool Balance => Core;

    /// <summary>
    /// <b>擂台赛</b>：「对决邀请」事件的多选项形态（上台 / 观战 / 结束），循环打、按胜场收场。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它建立在 <see cref="Invitation" /> 之上（要先有那个事件才有投票可谈），但<b>不</b>受
    /// 「额外幕」约束 —— 走普通联机局时也能在事件里开擂台。
    /// </para>
    /// <para>关掉＝老行为：一个「接受对决」选项，两人直接开打。（⚠ 两端一致）</para>
    /// <para>
    /// 2026-11-06 修：和 <see cref="ExtraAct" /> 同一个病 —— <see cref="Invitation" /> 压在
    /// <see cref="Core" /> 下面，而 <see cref="Core" /> 只代表「PvP 方式选回合制」。
    /// 手速模式 + 分离模式时它是假，于是决斗幕里的「战战战」事件只给一个「接受对决」，
    /// 擂台赛三选项消失。分离模式本来就该有完整的擂台赛，所以让它单独放行。
    /// </para>
    /// </remarks>
    public static bool Contest => Invitation || SplitMode;

    /// <summary>
    /// 分离模式（C2 实验）：把联机链路从「一局」里剥出来，为「两人各跑各的单机局」打地基。
    /// </summary>
    /// <remarks>
    /// <para>设计见 <c>docs/SPLIT_MODE.md</c>。</para>
    /// <para>
    /// ⚠ <b>不再受 <see cref="Core" /> 约束</b>。早先是 <c>Core &amp;&amp; Settings.ModuleSplitMode</c>，
    /// 意思是"PvP 方式没选就不许玩分离模式"——但分离模式本来就是"各自开单机局"，
    /// 和回合制 PvP 模块没有依赖关系。被压在 Core 下面导致：PvP 方式选「关闭」时
    /// 整个分离模式连链路都不起来。
    /// </para>
    /// <para>
    /// 现在分离模式和 PvP 模块是<b>并列</b>的两套：PvP 方式只决定"三幕打完进不进决斗幕"，
    /// 分离模式只决定"这局长什么样"。
    /// </para>
    /// </remarks>
    public static bool SplitMode => Settings.ModuleSplitMode;

    /// <summary>
    /// 分离模式的「保活」：退出对局时不主动断开联机连接。
    /// </summary>
    /// <remarks>
    /// ⚠ 不再受 <c>ModuleSplitKeepAlive</c> 子开关约束 —— 分离模式整条链路都建立在「连接不能断」上：
    /// 合流时要先 <c>RunManager.CleanUp()</c>（它内部会 <c>NetService.Disconnect(NetError.Quit)</c>），
    /// 全靠 <c>SplitLink.ShouldSuppressDisconnect</c> 拦下那一次断开。
    /// 一旦 <c>Armed == false</c>（保活没开），那一下会真的把连接断掉 → 客户端掉线、主机重开一局
    /// （2026-11-02 实测日志：<c>合流：已经进到决斗局</c> 之后紧跟 <c>联机传输断开：Quit</c>）。
    /// 所以这里只要有分离模式就必须保活，不给"关掉但还能用"的配置。
    /// </remarks>
    public static bool SplitKeepAlive => SplitMode;

    /// <summary>分离模式的坐标同步（Presence 通道）：每隔 250ms 把「我在哪、在干什么」发给对端。（⚠ 两端一致）</summary>
    public static bool SplitPresence => SplitMode && Settings.ModuleSplitPresence;

    /// <summary>分离模式的地图标记：在地图上把对端画出来。（纯本机表现）</summary>
    public static bool SplitIcon => SplitMode && Settings.ModuleSplitIcon;

    /// <summary>
    /// <b>分离模式</b>：大厅按下开始时各自开自己的单机局（同种子），连接保留。
    /// </summary>
    /// <remarks>
    /// 关掉＝老玩法：照常开联机共享局，三幕打完进「PVP 决斗」额外幕、在事件里开打。
    /// ⚠ 实验功能：开着时两人各自独立，没有合流之前回不去同一局。（⚠ 两端一致）
    /// <para>2026-11-06：设置页那一行删了，这里恒 <c>true</c> —— 分离模式就等于各开单机局。</para>
    /// </remarks>
    public static bool SplitSoloRun => SplitMode;

    /// <summary>
    /// <b>三层合流</b>：各自的单机局打完三幕之后，把双方快照合成一局 PvP 决斗局。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2026-11-06 起<b>没有单独的开关</b>：分离模式（+「各自开单机局」）开着就一定会合流。
    /// 早先那个 <c>ModuleSplitMeeting</c> 开关只会制造「分离模式开了但打不出 PvP」这种半截状态
    /// —— 用户要的就是「分离 → 三幕 → 合流 → PvP」这一条完整的路。
    /// </para>
    /// <para>
    /// 字段 <c>PvpSettings.ModuleSplitMeeting</c> 仍留在存档里（旧配置兼容），但不再参与判定。
    /// </para>
    /// </remarks>
    public static bool SplitMeeting => SplitSoloRun;

    /// <summary>测试用：打赢第一场战斗就当作「三幕打完」，省掉爬三幕。（⚠ 两端一致）</summary>
    public static bool SplitMeetingFastTest => SplitMeeting && Settings.ModuleSplitMeetingFastTest;

    /// <summary>分离模式的状态行（纯本机观测，和诊断一样不受主开关约束）。</summary>
    public static bool SplitStatusHud => Settings.ModuleSplitStatusHud;

    /// <summary>给启动日志用的一行摘要。</summary>
    public static string Describe()
    {
        return $"主={(Core ? "开" : "关")} 入口={(Invitation ? "开" : "关")} "
               + $"额外幕={(ExtraAct ? "开" : "关")} "
               + $"解绑={(TogetherUnbind ? "开" : "关")} 平衡={(Balance ? "开" : "关")} "
               + $"横幅={(Banners ? "开" : "关")} 分离模式={(SplitMode ? "开" : "关")} "
               + $"诊断={(Diagnostics ? "开" : "关")}";
    }
}
