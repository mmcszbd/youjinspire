// ============================================================================
// 友尽尖塔（youjin）：「建筑师的背叛」里那块木桩
// ----------------------------------------------------------------------------
// 原来是直接复用原版的 BattlewornDummyEventV1Encounter（训练木偶 BattleFriendV1）。
// 现在改成「结实的卵 ToughEgg」的**模型**，但仍然是无害木桩。
//
// 为什么不直接放 ToughEgg：
//   ToughEgg 是个真怪物 —— 它的招式是 HATCH_MOVE（召唤孵化）→ NIBBLE_MOVE（咬人），
//   直接拿来用，这场玩家对决里会多出一只会孵蛋、会咬人的蛋。
//   所以这里自己写一个怪物：**外表用卵的模型，行为还是木桩**（只有一个 NOTHING 招式）。
//
// 反编译确认的几个点：
//   · MonsterModel.VisualsPath 的默认实现就是
//       SceneHelper.GetScenePath("creature_visuals/" + Id.Entry.ToLowerInvariant())
//     ToughEgg 没有覆写它，所以卵的模型路径就是
//       res://scenes/creature_visuals/tough_egg.tscn
//   · MonsterModel.SetupSkins 基类是**空实现**；ToughEgg 覆写它，从私有静态
//     字段 _eggOptions 里随机挑一个 skin 挂上去。我们照抄这段逻辑（skin 名单用
//     反射拿），否则可能挂到默认皮肤上、甚至看不见。
//   · 怪物的招式表：new MonsterMoveStateMachine(IEnumerable<MonsterState>, MonsterState)，
//     其中 MoveState(string, Func<IReadOnlyList<Creature>,Task>, AbstractIntent[])，
//     FollowUpState 指回自己 = 永远只出这一招（原版 BigDummy / Architect 都是这么写的）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Models.Acts;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>
/// 只放上面那只「卵木桩」的遭遇。不产任何奖励。
/// 它只在这场事件战斗里被手动引用（EventModel.EnterCombatWithoutExitingEvent）。
///
/// ⚠ 注册方式很关键（实测踩过）：
///   · 原来用的是 `[RegisterGlobalEncounter]` —— 它会进一张**全局遭遇表**，
///     而"?"房间生成随机战斗时看起来会**直接读那张表、绕过 `IsValidForAct`** ✗
///     → 结果普通房间/问号房间会跑出那只 99999 血的卵。
///   · 现在改成 `[RegisterActEncounter]`（RitsuLib 模板里的写法）+ `IsValidForAct => false`：
///     不再进全局表 ✓，而在所属层的表里又会被 `IsValidForAct` 否决 ✓ → 永远抽不到 ✓。
///   · 我们事件里那句 `ModelDb.Encounter&lt;BetrayalEggEncounter&gt;()` 照常取得到 ✓（注册还在 ✓）。
/// </summary>
[RegisterActEncounter(typeof(Glory))]
public sealed class BetrayalEggEncounter : ModEncounterTemplate
{
    /// <summary>
    /// ⚠ 这里是"能不能被普通房间抽到"的**第三道锁**：
    /// 我们这场战斗**只由事件自己开**（`EnterCombatWithoutExitingEvent`），
    /// 不需要被任何普通小怪房间采用 —— 所以声明成 `Event` 而不是 `Monster`。
    /// （声明 `Monster` 就等于告诉游戏"这是可以放进小怪房间的遭遇" ✗。）
    ///
    /// 【版本记录 · 已回退到 10-03 15:34 的状态】
    ///   · 10-03 15:34（以及更早）：这一行是 `Event` —— 事件战斗的背景/结算/流程正常 ✓
    ///   · 10-03 15:44：为了修"游戏打不开"，改成 `Monster`。
    ///     原因：游戏的"已发现内容"是按 RoomType 归档的，写成 Event 会让这个遭遇 id
    ///     落进 `progress.save` 的 `discovered_events`，而启动读档把那份列表当
    ///     `EventModel` 用 → InvalidCastException → `NGame.GameStartup()` 中断 →
    ///     **游戏打不开**（异常本身没错，是那份列表被写脏了）。
    ///     但改成 Monster 之后，**背叛事件那场战斗会黑屏**（事件房的流程不再成立）✗。
    ///   · 现在（10-04）：按需求回退成 `Event`，优先保证事件战斗能打 ✓。
    ///     那个"存档被写脏"的问题改用**运行期兜底**处理（见 Entry.cs 里的说明），
    ///     不再靠改这里来规避。
    /// </summary>
    public override MegaCrit.Sts2.Core.Rooms.RoomType RoomType
        => MegaCrit.Sts2.Core.Rooms.RoomType.Event;

    /// <summary>
    /// ★ 关键：**任何一层都不许把这个遭遇算进随机房**。
    ///
    /// 原来只有 `[RegisterGlobalEncounter]` + `RoomType.Monster` 两句话，
    /// 结果游戏在小怪房间里会把它当成可选遭遇抽出来 —— 玩家在普通房间遇到一只
    /// 「卵/幼虫」，血量还是木桩那份（99999 × 多人缩放 ≈ 23999）、打它也不咬人。
    ///
    /// `ModEncounterTemplate.IsValidForAct(ActModel)` 就是干这个的官方插槽
    /// （和 `ShouldAllowHitting` 一样是留给 mod 的开放判据）：返回 false = 这一层不采用它。
    /// 全返回 false 之后，它仍然登记在 ModelDb 里 —— 我们事件里那句
    /// `ModelDb.Encounter&lt;BetrayalEggEncounter&gt;()` 照常取得到，只是永远不会被随机抽到。
    /// </summary>
    public override bool IsValidForAct(MegaCrit.Sts2.Core.Models.ActModel act) => false;

    /// <summary>
    /// 那一只木桩的**规范实例**。
    ///
    /// ⚠ 千万不要写成 `new BetrayalEggMonster()` —— 实测炸过，而且是黑屏级别的事故：
    ///   `AbstractModel` 的构造函数会往 ModelDb 里登记"ID → 类型"，
    ///   而 RitsuLib 的自动发现**已经**把 `MONSTER.BETRAYAL_EGG_MONSTER` 登记过了，
    ///   我们再自己 new 一次就会抛：
    ///     DuplicateModelException: ModelDb already contains ID MONSTER.BETRAYAL_EGG_MONSTER ...
    ///     "You have called a constructor on an AbstractModel. Use ModelDb instead."
    ///   这个异常发生在 `GenerateMonsters()`（战斗布场阶段）→
    ///   客户端和主机都进不去战斗 → 表现就是**选完"战斗"直接黑屏**。
    ///   改成走 ModelDb 取规范实例就没事了（它不会再登记一次）。
    ///
    /// 注意：这里只是"按 ID 取实例"，并不会把它塞进任何**怪物抽取池**，
    /// 所以普通房间/问号房间依然抽不到它 ✓。
    /// </summary>
    private static BetrayalEggMonster Dummy => ModelDb.Monster<BetrayalEggMonster>();

    /// <summary>这场遭遇可能出现的怪物（图鉴/预加载用）。</summary>
    public override IEnumerable<MonsterModel> AllPossibleMonsters
        => new[] { (MonsterModel)Dummy };

    /// <summary>不给金币/卡牌奖励。</summary>
    public override bool ShouldGiveRewards => false;

    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters()
        => new (MonsterModel, string?)[]
        {
            (Dummy.ToMutable(), null),
        };
}
