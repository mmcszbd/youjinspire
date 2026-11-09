// ============================================================================
// 友尽尖塔（youjin）：PvP 战斗「形态」模块
// ----------------------------------------------------------------------------
// 解决的问题：回合制 PvP 原来只活在两个写死的地方（「PVP 决斗」第 4 幕的对决邀请、
// 「建筑师的挑拨」的 goFight 分支），谁想再来一处 PvP 就得把那段分支再抄一遍，
// 而且抄的时候极容易把两套逻辑叠在一起。
//
// 现在把「这场 PvP 怎么打」抽成**可替换的形态**（和拼手速完全对等）：
//
//     PvpCombatKind.Speed      —— 手速：木桩站场 + 两边同时出牌、同时结束回合
//     PvpCombatKind.TurnBased  —— 回合制：一人一回合轮流（内置「PVP 决斗」的机制）
//
// 调用方只有三步（见 ArchitectBetrayalEvent.ResolveAsync / BattleBattleBattleEvent.BeginDuel）：
//
//     var plan = PvpCombatKit.Plan(kind, run, speedEncounter, resumeAfterCombat);
//     if (!plan.Ready) return;              // 形态不可用（人数/开关不满足），已经打过日志
//     plan.Apply();                          // 设静态开关 或 登记下一场为决斗
//     EnterCombatWithoutExitingEvent(plan.Encounter, Array.Empty<Reward>(), plan.ResumeAfterCombat);
//
// ⚠ 为什么遭遇要跟着形态走（★ 最容易踩的一条）：
//   手速那套的判据 PvpCombat.IsActive 是「静态开关 **或** 遭遇是 BetrayalEggEncounter」。
//   回合制如果还用卵，遭遇判据成立 → 手速的布场（队友搬对面/木桩挪最右/出牌上限/AfterDeath 收场）
//   会和决斗同时上手。所以回合制必须换成决斗那边的**占位遭遇**（怪物会在 CombatState.AddCreature
//   处被决斗逻辑全部拦下），这样两条判据天然为假 = 本 mod 完全放手。
//
// ⚠ 为什么回合制要求「正好两人」：
//   DuelRules.CanRunDuel 只认两人。三人以上它既不拦怪也不布场，但 DuelSession.Armed 已经登记，
//   这个标记会漏到**下一场普通战斗**（那场会被误当成决斗）。所以按人数降级成手速。
// ============================================================================

using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Runs;

using PvpDuel.Core;
using PvpDuel.Core.Duel;

namespace YouJin.Scripts.Pvp;

/// <summary>一场 PvP 战斗的形态。两种实现完全对等，随便换。</summary>
public enum PvpCombatKind
{
    /// <summary>不打 PvP（保持原版）。</summary>
    Off = 0,

    /// <summary>手速：木桩站场，两边同时出牌、同时结束回合。</summary>
    Speed = 1,

    /// <summary>回合制：一人一回合轮流，没有怪物（内置「PVP 决斗」的机制）。</summary>
    TurnBased = 2,
}

/// <summary>
/// 一次 PvP 开战的「计划」：用哪种形态、进哪个遭遇、进完要不要回到事件。
/// </summary>
/// <remarks>
/// 单独做成一个对象、并且把「改状态」推迟到 <see cref="Apply" />，
/// 是为了让调用方能把 <c>EnterCombatWithoutExitingEvent</c> 留在自己的事件类里
/// （那是 <c>EventModel</c> 的实例方法，外部静态类碰不到），同时形态决策仍然只有一份。
/// </remarks>
public sealed class PvpCombatPlan
{
    internal PvpCombatPlan(
        PvpCombatKind kind,
        EncounterModel? encounter,
        bool resumeAfterCombat,
        string note)
    {
        Kind = kind;
        Encounter = encounter;
        ResumeAfterCombat = resumeAfterCombat;
        Note = note;
    }

    /// <summary>最终采用的形态（可能因为人数不够从回合制降级成手速）。</summary>
    public PvpCombatKind Kind { get; }

    /// <summary>要进的遭遇；<see cref="Kind" /> 是 <see cref="PvpCombatKind.Off" /> 时为 null。</summary>
    public EncounterModel? Encounter { get; }

    /// <summary>战斗结束后要不要回到事件页。</summary>
    public bool ResumeAfterCombat { get; }

    /// <summary>这条计划的一句话说明（打日志用）。</summary>
    public string Note { get; }

    /// <summary>形态可用（<see cref="Kind" /> 不是 Off 且拿到了遭遇）。</summary>
    public bool Ready => Kind != PvpCombatKind.Off && Encounter is not null;

    /// <summary>把形态对应的状态开关落到实处（可重复调用）。</summary>
    public void Apply()
    {
        switch (Kind)
        {
            case PvpCombatKind.Speed:
                PvpCombatKit.ApplySpeed();
                break;

            case PvpCombatKind.TurnBased:
                PvpCombatKit.ApplyTurnBased();
                break;
        }

        Entry.Logger.Info($"PvP 形态：{Note}");
    }
}

/// <summary>PvP 战斗形态的唯一入口：调用方只问它「这次用哪种、进哪个遭遇」。</summary>
public static class PvpCombatKit
{
    /// <summary>回合制用的占位遭遇（怪物会被决斗逻辑在 <c>AddCreature</c> 处拦下）。</summary>
    /// <remarks>
    /// 借 <c>CorpseSlugsWeak</c> 而不是自造空遭遇：那套「进战斗 → 拦怪 → 两人轮流」
    /// 已经在「PVP 决斗」里跑通了，换一个没跑过的遭遇只会多一处不确定。
    /// </remarks>
    public static EncounterModel TurnBasedPlaceholder => ModelDb.Encounter<CorpseSlugsWeak>();

    /// <summary>设置页「PvP 方式」当前选的形态。</summary>
    public static PvpCombatKind FromSettings()
    {
        return YouJinSettings.PvpStyleSetting switch
        {
            PvpStyle.Speed => PvpCombatKind.Speed,
            PvpStyle.TurnBased => PvpCombatKind.TurnBased,
            _ => PvpCombatKind.Off,
        };
    }

    /// <summary>
    /// 能不能用这个形态跑（纯查询，不改状态）。
    /// </summary>
    /// <remarks>
    /// 手速：≥2 人即可（只有一个玩家时没有对手，开打会当场结束）。
    /// 回合制：必须**正好两人**，且决斗的核心模块开着（<see cref="ModuleSwitches.Core" />）。
    /// </remarks>
    public static bool CanRun(PvpCombatKind kind, IRunState? run, out string reason)
    {
        var players = run?.Players.Count ?? 0;

        switch (kind)
        {
            case PvpCombatKind.Speed:
                if (players >= 2)
                {
                    reason = $"{players} 人，可以按手速打";
                    return true;
                }

                reason = $"只有 {players} 人，没有对手";
                return false;

            case PvpCombatKind.TurnBased:
                // 「在场」和「上场」是两件事：擂台赛里可能四个人都在战斗里、只有两位参战
                // （参战者由 DuelSession.SetParticipants 指定）。所以这里只要求至少两人。
                if (players < 2)
                {
                    reason = $"回合制至少要两人，本局 {players} 人";
                    return false;
                }

                if (!ModuleSwitches.Core)
                {
                    reason = "「PVP 决斗」的核心模块没开（设置页 PvP 方式不是回合制）";
                    return false;
                }

                reason = $"{players} 人（参战 2 位）+ 决斗模块可用";
                return true;

            default:
                reason = "PvP 形态=关闭";
                return false;
        }
    }

    /// <summary>
    /// 排一条开战计划。
    /// </summary>
    /// <param name="requested">想用的形态（一般传 <see cref="FromSettings" />；要强制某种就直接写它）。</param>
    /// <param name="run">当前跑局（人数判据用；取不到就按不可用来处理）。</param>
    /// <param name="speedEncounter">手速形态要进的遭遇（就是木桩那个卵）。</param>
    /// <param name="resumeAfterCombat">战斗结束后回不回事件页。</param>
    /// <param name="allowFallback">
    /// 回合制不满足条件时要不要降级成手速。
    /// 挑拨那种「两种都能打」的入口传 true；「PVP 决斗」自己的邀请事件传 false
    /// （那里本来就该打回合制，降级成手速毫无意义）。
    /// </param>
    public static PvpCombatPlan Plan(
        PvpCombatKind requested,
        IRunState? run,
        EncounterModel speedEncounter,
        bool resumeAfterCombat = true,
        bool allowFallback = true)
    {
        var kind = requested;

        if (!CanRun(kind, run, out var why))
        {
            if (allowFallback && kind == PvpCombatKind.TurnBased && CanRun(PvpCombatKind.Speed, run, out var speedWhy))
            {
                kind = PvpCombatKind.Speed;
                why = $"回合制不可用（{why}）→ 退回手速（{speedWhy}）";
            }
            else
            {
                Entry.Logger.Info($"PvP 形态：不开打（{kind}）—— {why}");
                return new PvpCombatPlan(PvpCombatKind.Off, null, false, $"不开打（{why}）");
            }
        }
        else if (kind == PvpCombatKind.TurnBased)
        {
            why = $"回合制：一人一回合轮流，打到有人倒下（{why}）";
        }
        else
        {
            why = $"手速：木桩站场、两边同时出牌（{why}）";
        }

        var encounter = kind == PvpCombatKind.TurnBased ? TurnBasedPlaceholder : speedEncounter;

        return new PvpCombatPlan(kind, encounter, resumeAfterCombat, why);
    }

    /// <summary>手速形态落地：点亮静态开关（布场补丁跑在最前面，必须现在就亮）。</summary>
    internal static void ApplySpeed()
    {
        ArchitectBetrayalEvent.BetrayalCombatActive = true;
    }

    /// <summary>回合制形态落地：关掉手速开关 + 登记「下一场战斗是决斗」。</summary>
    internal static void ApplyTurnBased()
    {
        ArchitectBetrayalEvent.BetrayalCombatActive = false;
        DuelSession.Arm();
    }

    /// <summary>一句话说明现在能用哪些形态（诊断用）。</summary>
    public static string Describe()
    {
        var run = RunManager.Instance?.DebugOnlyGetState();
        var speed = CanRun(PvpCombatKind.Speed, run, out _);
        var turn = CanRun(PvpCombatKind.TurnBased, run, out _);

        return $"PvP 形态：设置={FromSettings()}｜手速可用={speed}｜回合制可用={turn}"
               + $"｜决斗进行中={DuelSession.InDuel}｜占位遭遇={ModelDb.GetId(typeof(CorpseSlugsWeak))}";
    }
}

/// <summary>
/// 给别的 mod 用的 PvP 入口（<c>[ModInterop("youjin", "YouJin.Scripts.Pvp.PvpCombatApi")]</c>）。
/// </summary>
/// <remarks>
/// 只暴露简单类型，和 <c>PvpDuel.Api.DuelApi</c> 的口径一致。典型用法：
/// 自己在某个事件/房间想开一场 PvP，就
/// <c>ArmTurnBased()</c> → 用 <c>ModelDb.GetByIdOrNull&lt;EncounterModel&gt;(PlaceholderEncounterId)</c>
/// 取遭遇 → 走本体的 <c>EnterCombatWithoutExitingEvent</c>。
/// </remarks>
public static class PvpCombatApi
{
    /// <summary>当前设置选中的形态（<c>"Off"</c> / <c>"Speed"</c> / <c>"TurnBased"</c>）。</summary>
    public static string Kind => PvpCombatKit.FromSettings().ToString();

    /// <summary>回合制此刻能不能用（两人 + 决斗模块开着）。</summary>
    public static bool TurnBasedAvailable
        => PvpCombatKit.CanRun(PvpCombatKind.TurnBased, RunManager.Instance?.DebugOnlyGetState(), out _);

    /// <summary>回合制要进的占位遭遇 id（怪物会被自动拦下）。</summary>
    public static string PlaceholderEncounterId => ModelDb.GetId(typeof(CorpseSlugsWeak)).ToString();

    /// <summary>登记「下一场战斗按回合制 PvP 跑」。</summary>
    public static void ArmTurnBased()
    {
        PvpCombatKit.ApplyTurnBased();
        Entry.Logger.Info($"PvP 形态：外部调用登记回合制（{PvpCombatKit.Describe()}）");
    }

    /// <summary>诊断串。</summary>
    public static string Describe() => PvpCombatKit.Describe();
}
