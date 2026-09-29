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
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>
/// 玩家对决里那块「木桩」：外观是原版「结实的卵」，行为是永远不动的靶子。
/// 血量直接就是 99999，所以战斗里不需要再改血。
/// </summary>
[RegisterMonster]
public sealed class BetrayalEggMonster : ModMonsterTemplate
{
    /// <summary>卵的皮肤名单（ToughEgg 的私有静态字段，反射读一次）。</summary>
    private static readonly string[] EggSkinOptions =
        AccessTools.Field(typeof(ToughEgg), "_eggOptions")?.GetValue(null) as string[]
        ?? Array.Empty<string>();

    /// <summary>名字直接用原版「结实的卵」的本地化文本。</summary>
    public override LocString Title => ModelDb.Monster<ToughEgg>().Title;

    public override int MinInitialHp => ArchitectBetrayalEvent.DummyHp;

    public override int MaxInitialHp => ArchitectBetrayalEvent.DummyHp;

    /// <summary>借用卵的模型（等价于 MonsterModel 的默认推导，只是换成 tough_egg）。</summary>
    protected override string VisualsPath => SceneHelper.GetScenePath("creature_visuals/tough_egg");

    /// <summary>永远只出 NOTHING，什么都不做。</summary>
    protected override MonsterMoveStateMachine GenerateMoveStateMachine()
    {
        var nothing = new MoveState(
            "NOTHING",
            NothingMove,
            new AbstractIntent[] { new HiddenIntent() });

        nothing.FollowUpState = nothing;

        return new MonsterMoveStateMachine(new List<MonsterState> { nothing }, nothing);
    }

    /// <summary>
    /// 照抄 ToughEgg 的皮肤逻辑：卵的资源里有多套 skin，不挂一个会显示不出来。
    /// （拿不到名单时保持空实现，让默认皮肤生效。）
    /// </summary>
    public override void SetupSkins(MegaSprite sprite, MegaSkeleton skeleton)
    {
        if (EggSkinOptions.Length == 0)
            return;

        var skinName = Rng.NextItem(EggSkinOptions);
        if (skinName is null)
            return;

        var eggSkin = skeleton.GetData().FindSkin(skinName);
        if (eggSkin is null)
            return;

        var skin = sprite.NewSkin("custom-skin");
        skin.AddSkin(eggSkin);
        skeleton.SetSkin(skin);
        skeleton.SetSlotsToSetupPose();
    }

    private static Task NothingMove(IReadOnlyList<Creature> creatures) => Task.CompletedTask;
}

/// <summary>
/// 只放上面那只「卵木桩」的遭遇。不产任何奖励。
/// 它只在这场事件战斗里被手动引用（EventModel.EnterCombatWithoutExitingEvent），
/// 不会进任何一层的随机遭遇池 —— 各层的遭遇表是硬编码在 ActModel.GenerateAllEncounters 里的。
/// </summary>
[RegisterGlobalEncounter]
public sealed class BetrayalEggEncounter : ModEncounterTemplate
{
    /// <summary>普通战斗遭遇（和原版 BattlewornDummyEventV1Encounter 一样）。</summary>
    public override MegaCrit.Sts2.Core.Rooms.RoomType RoomType
        => MegaCrit.Sts2.Core.Rooms.RoomType.Monster;

    /// <summary>这场遭遇可能出现的怪物（图鉴/预加载用）。</summary>
    public override IEnumerable<MonsterModel> AllPossibleMonsters
        => new[] { ModelDb.Monster<BetrayalEggMonster>() };

    /// <summary>不给金币/卡牌奖励。</summary>
    public override bool ShouldGiveRewards => false;

    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters()
        => new (MonsterModel, string?)[]
        {
            (ModelDb.Monster<BetrayalEggMonster>().ToMutable(), null),
        };
}
