// ============================================================================
// 能力：队友先上（TeammateFirstPower）
// ----------------------------------------------------------------------------
// 由「队友先上」打出后获得，挂在【自己】身上，Applier = 被指定的那名队友：
//   * 自己     受到的敌人伤害 ×0.25（少受 75%）
//   * 被指定队友 受到的敌人伤害 ×1.25（多受 25%）
//
// 反编译依据（0.111.0）：
//   * Hook.ModifyDamageInternal -> runState.IterateHookListeners(combatState)
//     会把**全场所有生物的能力**都过一遍（CombatState.IterateHookListeners 里
//     list.AddRange(creature.Powers)），并带上 target 参数，
//     所以一个挂在自身上、靠 Applier 记住队友的能力就能同时改两边。
//   * PowerModel 文本键 = "powers" 表里的 {Id.Entry}.title / .description / .smartDescription
//     模组能力的 Id.Entry = YOUJIN_POWER_<类名大写下划线>
//   * 图标：IconPath 默认 = res://images/atlases/power_atlas.sprites/<entry 小写>.tres
//     这里直接用原版肉盾的图标（PowerAssetProfile 覆盖）。
// ============================================================================

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts.Powers;

[RegisterPower]
public sealed class TeammateFirstPower : ModPowerTemplate
{
    /// <summary>自己受伤倍率（0.25 = 少受 75%）。</summary>
    public const decimal DamageDecreaseValue = 0.25m;

    /// <summary>被指定队友受伤倍率（1.25 = 多受 25%）。</summary>
    public const decimal DamageIncreaseValue = 1.25m;

    /// <summary>原版肉盾的小图标（能力条 / 悬浮提示用）。</summary>
    private const string TankIconPath = "res://images/atlases/power_atlas.sprites/tank_power.tres";

    /// <summary>原版肉盾的大图标（详情页用）。</summary>
    private const string TankBigIconPath = "res://images/powers/tank_power.png";

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override PowerInstanceType InstanceType => PowerInstanceType.Instanced;

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new DynamicVar("DamageDecrease", DamageDecreaseValue),
        new DynamicVar("DamageIncrease", DamageIncreaseValue),
    };

    public override PowerAssetProfile AssetProfile => new PowerAssetProfile(
        IconPath: TankIconPath,
        BigIconPath: TankBigIconPath);

    public override decimal ModifyDamageMultiplicative(
        Creature? target, decimal amount, ValueProp props, Creature? dealer,
        CardModel? cardSource, CardPlay? cardPlay)
    {
        if (target is null || !props.IsPoweredAttack())
            return 1m;

        if (target == Owner)
            return DamageDecreaseValue;

        if (Applier is not null && target == Applier)
            return DamageIncreaseValue;

        return 1m;
    }
}
