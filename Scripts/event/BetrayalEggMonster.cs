// ============================================================================
// 「建筑师的挑拨」战斗里的木桩（**独立怪物类**）
// ----------------------------------------------------------------------------
// 为什么单独一个类 / 为什么不再"借"原版那只卵：
//   · 原来这里 `Title => ModelDb.Monster<ToughEgg>().Title`，名字、身份都跟原版
//     「结实的卵」绑在一起 —— 实测会出现"普通房间/问号房间跑到那只 99999 血的卵"
//     （玩家以为自己遇到的是原版怪）。
//   · 现在按 RitsuLib 的 `ModMonsterTemplate` 模板写成**自成一体的怪物**：
//     自己的 Id、自己的标题（走本地化 `YOUJIN_MONSTER_BETRAYAL_EGG.title`）、
//     自己的血量，只借用原版卵的**外观场景**（`VisualsPath` 只是贴图/骨架路径，不是身份）。
//   · 行为是"永远只出 NOTHING"：不攻击、不移动，纯粹让"玩家互相打"这场战斗成立。
//
// 血量：直接写死 99999（`MinInitialHp/MaxInitialHp`），所以战斗里不需要再改血。
// 皮肤：卵的资源里有多套 skin，不挂一个会显示不出来 —— 这里仍然从 ToughEgg 的
//       私有静态名单里反射读一次（**只读、不写**，最坏情况就是退回默认皮肤）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Godot;

namespace YouJin.Scripts;

/// <summary>
/// 玩家对决里那块「木桩」：外观是原版「结实的卵」，行为是永远不动的靶子。
/// 血量直接就是 99999，所以战斗里不需要再改血。
/// </summary>
// ⚠ 故意**不加** [RegisterMonster]：加了就等于把它登记成"可被随机抽到的 mod 怪物"，
//   结果普通房间 / "?" 房间会跑出这只木桩（99999 × 多人缩放 ≈ 22 万血、还不咬人）。
//   RitsuLib 的 RegisterMonsterAttribute 没有"只登记、不入池"的参数（查过），
//   所以改成：不注册，只在 `BetrayalEggEncounter` 里**就地构造**一份实例给事件战斗用。
public sealed class BetrayalEggMonster : ModMonsterTemplate
{
    /// <summary>卵的皮肤名单（ToughEgg 的私有静态字段，反射读一次；读不到就退回默认皮肤）。</summary>
    private static readonly string[] EggSkinOptions =
        AccessTools.Field(typeof(ToughEgg), "_eggOptions")?.GetValue(null) as string[]
        ?? Array.Empty<string>();

    public override int MinInitialHp => ArchitectBetrayalEvent.DummyHp;

    public override int MaxInitialHp => ArchitectBetrayalEvent.DummyHp;

    /// <summary>
    /// 外观场景 —— 照 RitsuLib 模板的 `AssetProfile` 写法（推荐方式，不需要手动挂脚本）。
    /// 只借原版卵的**场景**（贴图/骨架）：名字和身份都是我们自己的。
    /// </summary>
    public override MonsterAssetProfile AssetProfile => new(
        VisualsScenePath: SceneHelper.GetScenePath("creature_visuals/tough_egg"));

    /// <summary>按模板自动从场景造 visuals（复制即可，不用改）。</summary>
    protected override NCreatureVisuals? TryCreateCreatureVisuals()
        => RitsuGodotNodeFactories.CreateFromScenePath<NCreatureVisuals>(AssetProfile.VisualsScenePath!);

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
