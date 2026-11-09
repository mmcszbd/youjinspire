// ============================================================================
// 多人专属卡 ×10（只进「多人玩法用的那套卡池」，单人局抽不到）
// ----------------------------------------------------------------------------
//  1 献祭     铁甲战士 技能 罕见 0费 消耗  队友 -5 生命 / 自己 +2 能量 抽 3（升级抽 5）
//  2 拼尽一切 铁甲战士 攻击 稀有 2费 消耗  烧掉全场手牌，每张 10 伤害（升级 13）
//  3 队友先上 静默猎手 能力 稀有 1费       指定一名队友：他 +25% 受伤、自己 -75%（升级 0 费）
//  4 全神贯注 静默猎手 技能 罕见 0费       队友随机弃 1 张，每弃一张自己 +1 能量
//  5 火花四溅 储君     技能 罕见 0费       铸造 13（升级 17），其他人手牌加 1 张碎屑
//  6 借我一用 储君     技能 罕见 0费       队友 +1 脆弱，自己 +1 辉星 +2 能量（升级 +1 辉星）
//  7 暗黑魔法 亡灵契约师 技能 罕见 0费     队友 +8 灾厄，自己 +2 张灵魂（升级 3 张）
//  8 禁忌之力 亡灵契约师 技能 稀有 3费 消耗 队友获得预借时间，自己全部手牌 +1 重放
//  9 输送能量 故障机器人 技能 罕见 1费     队友 -1 力量 -1 敏捷，自己 +3 集中（升级 +4）
// 10 认知正确 故障机器人 能力 稀有 1费     自己集中翻倍，其他人手牌加 2 张伤口（升级 0 费）
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;
using YouJin.Scripts.Powers;

namespace YouJin.Scripts.Cards;

// ── 1 献祭（铁甲战士）───────────────────────────────────────────────────────
[RegisterCard(typeof(IroncladCardPool))]
public sealed class BloodSacrifice : YouJinMultiplayerCard
{
    protected override string Slug => "Sacrifice";

    public override IEnumerable<CardKeyword> CanonicalKeywords =>
        new[] { CardKeyword.Exhaust };

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new HpLossVar(5m),
        new EnergyVar(2),
        new CardsVar(3),
    };

    protected override IEnumerable<IHoverTip> AdditionalHoverTips =>
        new[] { EnergyHoverTip };

    public BloodSacrifice()
        : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        foreach (var mate in Teammates)
        {
            await CreatureCmd.Damage(
                choiceContext, mate, DynamicVars.HpLoss.BaseValue,
                ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, this, cardPlay);
        }

        await PlayerCmd.GainEnergy(DynamicVars.Energy.IntValue, Owner);
        await CardPileCmd.Draw(choiceContext, DynamicVars.Cards.BaseValue, Owner);
    }

    protected override void OnUpgrade() => DynamicVars.Cards.UpgradeValueBy(2m);
}

// ── 2 拼尽一切（铁甲战士）───────────────────────────────────────────────────
[RegisterCard(typeof(IroncladCardPool))]
public sealed class BurnItAll : YouJinMultiplayerCard
{
    protected override string Slug => "Burn, burn, burn";

    public override IEnumerable<CardKeyword> CanonicalKeywords =>
        new[] { CardKeyword.Exhaust };

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new DamageVar(10m, ValueProp.Move),
    };

    public BurnItAll()
        : base(2, CardType.Attack, CardRarity.Rare, TargetType.AnyEnemy)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int burned = 0;

        foreach (var card in PileType.Hand.GetPile(Owner).Cards.ToList())
        {
            await CardCmd.Exhaust(choiceContext, card);
            burned++;
        }

        foreach (var mate in Teammates)
        {
            var player = mate.Player;
            if (player is null)
                continue;

            foreach (var card in PileType.Hand.GetPile(player).Cards.ToList())
            {
                await CardCmd.Exhaust(choiceContext, card);
                burned++;
            }
        }

        if (burned <= 0 || cardPlay.Target is null)
            return;

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .WithHitCount(burned)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .Execute(choiceContext);
    }

    protected override void OnUpgrade() => DynamicVars.Damage.UpgradeValueBy(3m);
}

// ── 3 队友先上（静默猎手）───────────────────────────────────────────────────
// 选择一名队友：那名义受到的敌人伤害 ×1.25，自己受到的敌人伤害 ×0.25。
// 能力本体见 Scripts/powers/TeammateFirstPower.cs（图标套用原版肉盾）。
[RegisterCard(typeof(SilentCardPool))]
public sealed class TeammatesGoFirst : YouJinMultiplayerCard
{
    protected override string Slug => "Teammates go first_";

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new DynamicVar("DamageIncrease", TeammateFirstPower.DamageIncreaseValue),
        new DynamicVar("DamageDecrease", TeammateFirstPower.DamageDecreaseValue),
    };

    public TeammatesGoFirst()
        : base(1, CardType.Power, CardRarity.Rare, TargetType.AnyAlly)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.Target is null)
            return;

        await CreatureCmd.TriggerAnim(Owner.Creature, "PowerUp", Owner.Character.PowerUpAnimDelay);

        // 挂在自己身上，Applier 记住被指定的那名队友（由能力同时改两个人的受伤倍率）。
        await PowerCmd.Apply<TeammateFirstPower>(
            choiceContext, Owner.Creature, 1m, cardPlay.Target, this);
    }

    protected override void OnUpgrade() => EnergyCost.UpgradeBy(-1);
}

// ── 4 全神贯注（静默猎手）───────────────────────────────────────────────────
[RegisterCard(typeof(SilentCardPool))]
public sealed class FullConcentration : YouJinMultiplayerCard
{
    protected override string Slug => "Fully concentrated";

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new EnergyVar(1),
    };

    protected override IEnumerable<IHoverTip> AdditionalHoverTips =>
        new[] { EnergyHoverTip };

    public FullConcentration()
        : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int discarded = 0;

        foreach (var mate in Teammates)
        {
            var player = mate.Player;
            if (player is null)
                continue;

            var hand = PileType.Hand.GetPile(player).Cards.ToList();
            if (hand.Count == 0)
                continue;

            // 用同步 RNG（和原版「未掘宝石」一样的来源），多人才不会分叉。
            var pick = Owner.RunState.Rng.CombatCardSelection.NextItem(hand);
            if (pick is null)
                continue;

            await CardCmd.Discard(choiceContext, pick);
            discarded++;
        }

        if (discarded > 0)
            await PlayerCmd.GainEnergy(discarded * DynamicVars.Energy.IntValue, Owner);
    }

    // 升级：每弃掉一张牌获得的能量 1 → 2
    protected override void OnUpgrade() => DynamicVars.Energy.UpgradeValueBy(1m);
}

// ── 5 火花四溅（储君）───────────────────────────────────────────────────────
[RegisterCard(typeof(RegentCardPool))]
public sealed class SparksFly : YouJinMultiplayerCard
{
    protected override string Slug => "Sparks fly";

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new ForgeVar(13),
    };

    protected override IEnumerable<IHoverTip> AdditionalHoverTips => HoverTipFactory.FromForge();

    public SparksFly()
        : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.TriggerAnim(Owner.Creature, "Cast", Owner.Character.CastAnimDelay);
        await ForgeCmd.Forge(DynamicVars.Forge.BaseValue, Owner, this);

        var debris = new List<CardModel>();
        foreach (var mate in Teammates)
        {
            var player = mate.Player;
            if (player is null)
                continue;

            debris.Add(CombatState.CreateCard<Debris>(player));
        }

        if (debris.Count > 0)
            await CardPileCmd.AddGeneratedCardsToCombat(debris, PileType.Hand, Owner, CardPilePosition.Random);
    }

    protected override void OnUpgrade() => DynamicVars.Forge.UpgradeValueBy(4m);
}

// ── 6 借我一用（储君）───────────────────────────────────────────────────────
[RegisterCard(typeof(RegentCardPool))]
public sealed class LendItToMe : YouJinMultiplayerCard
{
    protected override string Slug => "Lend it to me for use_";

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new PowerVar<FrailPower>(1m),
        new StarsVar(1),
        new EnergyVar(2),
    };

    protected override IEnumerable<IHoverTip> AdditionalHoverTips => new IHoverTip[]
    {
        EnergyHoverTip,
        HoverTipFactory.FromPower<FrailPower>(),
    };

    public LendItToMe()
        : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<FrailPower>(
            choiceContext, Teammates, DynamicVars["FrailPower"].BaseValue, Owner.Creature, this);
        await PlayerCmd.GainStars(DynamicVars.Stars.BaseValue, Owner);
        await PlayerCmd.GainEnergy(DynamicVars.Energy.BaseValue, Owner);
    }

    protected override void OnUpgrade() => DynamicVars.Stars.UpgradeValueBy(1m);
}

// ── 7 暗黑魔法（亡灵契约师）─────────────────────────────────────────────────
[RegisterCard(typeof(NecrobinderCardPool))]
public sealed class DarkMagic : YouJinMultiplayerCard
{
    protected override string Slug => "Dark magic";

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new PowerVar<DoomPower>(8m),
        new CardsVar(2),
    };

    public DarkMagic()
        : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<DoomPower>(
            choiceContext, Teammates, DynamicVars.Doom.BaseValue, Owner.Creature, this);
        await Soul.CreateInHand(Owner, DynamicVars.Cards.IntValue, CombatState);
    }

    protected override void OnUpgrade() => DynamicVars.Cards.UpgradeValueBy(1m);
}

// ── 8 禁忌之力（亡灵契约师）─────────────────────────────────────────────────
[RegisterCard(typeof(NecrobinderCardPool))]
public sealed class TabooForce : YouJinMultiplayerCard
{
    protected override string Slug => "The power of taboo";

    public override IEnumerable<CardKeyword> CanonicalKeywords =>
        new[] { CardKeyword.Exhaust };

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new EnergyVar("ExtraCost", 1),
        new RepeatVar("Replay", 1),
    };

    protected override IEnumerable<IHoverTip> AdditionalHoverTips =>
        new[] { HoverTipFactory.Static(StaticHoverTip.ReplayStatic) };

    public TabooForce()
        : base(3, CardType.Skill, CardRarity.Rare, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.TriggerAnim(Owner.Creature, "Cast", Owner.Character.CastAnimDelay);

        await PowerCmd.Apply<BorrowedTimePower>(
            choiceContext, Teammates, DynamicVars["ExtraCost"].BaseValue, Owner.Creature, this);

        foreach (var card in PileType.Hand.GetPile(Owner).Cards.ToList())
            card.BaseReplayCount += 1;
    }

    // 升级：费用 3 → 2
    protected override void OnUpgrade() => EnergyCost.UpgradeBy(-1);
}

// ── 9 输送能量（故障机器人）─────────────────────────────────────────────────
[RegisterCard(typeof(DefectCardPool))]
public sealed class TransferEnergy : YouJinMultiplayerCard
{
    protected override string Slug => "Transferring energy";

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new PowerVar<StrengthPower>(1m),
        new PowerVar<DexterityPower>(1m),
        new PowerVar<FocusPower>(3m),
    };

    public TransferEnergy()
        : base(1, CardType.Skill, CardRarity.Uncommon, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<StrengthPower>(
            choiceContext, Teammates, -DynamicVars.Strength.BaseValue, Owner.Creature, this);
        await PowerCmd.Apply<DexterityPower>(
            choiceContext, Teammates, -DynamicVars.Dexterity.BaseValue, Owner.Creature, this);
        await PowerCmd.Apply<FocusPower>(
            choiceContext, Owner.Creature, DynamicVars["FocusPower"].BaseValue, Owner.Creature, this);
    }

    // 升级：集中 +3 → +4
    protected override void OnUpgrade() => DynamicVars["FocusPower"].UpgradeValueBy(1m);
}

// ── 10 认知正确（故障机器人）────────────────────────────────────────────────
/// <summary>自己集中翻倍；其他所有玩家的手牌里各塞 2 张伤口。</summary>
[RegisterCard(typeof(DefectCardPool))]
public sealed class CorrectCognition : YouJinMultiplayerCard
{
    /// <summary>给每名队友塞的伤口张数。</summary>
    private const int WoundCount = 2;

    protected override string Slug => "Cognitive correctness";

    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new CardsVar(WoundCount),
    };

    public CorrectCognition()
        : base(1, CardType.Power, CardRarity.Rare, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.TriggerAnim(Owner.Creature, "PowerUp", Owner.Character.PowerUpAnimDelay);

        var focus = Owner.Creature.GetPower<FocusPower>()?.Amount ?? 0m;
        if (focus != 0m)
            await PowerCmd.Apply<FocusPower>(choiceContext, Owner.Creature, focus, Owner.Creature, this);

        var wounds = new List<CardModel>();
        foreach (var mate in Teammates)
        {
            var player = mate.Player;
            if (player is null)
                continue;

            for (var i = 0; i < WoundCount; i++)
                wounds.Add(CombatState.CreateCard<Wound>(player));
        }

        if (wounds.Count > 0)
            await CardPileCmd.AddGeneratedCardsToCombat(wounds, PileType.Hand, Owner, CardPilePosition.Random);
    }

    protected override void OnUpgrade() => EnergyCost.UpgradeBy(-1);
}
