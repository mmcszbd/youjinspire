// ============================================================================
// 多人专属卡：基类 + 卡图/本地化工具
// ----------------------------------------------------------------------------
// 这些卡的定位和原版「午夜(Midnight)」「肉盾(Tank)」「相信你(BelieveInYou)」一样：
//   * 注册进角色的常规卡池（[RegisterCard(typeof(XxxCardPool))]）
//   * 但 MultiplayerConstraint = MultiplayerOnly —— 单人局抽不到、图鉴里标多人
// 单人局里「队友相关」的部分自然空过（没有队友），自己那部分照常生效。
//
// 反编译依据（游戏 0.111.0）：
//   * CardModel.Title/Description -> LocString("cards", Id.Entry + ".title"/".description")
//   * 模组内容的 Id.Entry = "<MOD>_<CATEGORY>_<TYPE_NAME_UPPER_SNAKE>"
//     （RitsuLib 的 ModelDbModdedEntryPatch + ModContentRegistry.GetFixedPublicEntry）
//     例：BlessedSpringPotion -> YOUJIN_POTION_BLESSED_SPRING_POTION
//     所以本文件里的类 X 的键就是 YOUJIN_CARD_<X 的大写下划线>
//   * 本地化表名 = pck 里 youjin/localization/<lang>/cards.json 的文件名（"cards"）
//     RitsuLib/ModManager.GetModdedLocTables 会把它并进原版 cards 表。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts.Cards;

/// <summary>卡图路径：res://youjin/images/cards/&lt;slug&gt;.png（放进去后要重导 PCK）。</summary>
internal static class CardArt
{
    public static CardAssetProfile Portrait(string slug)
        => new CardAssetProfile(PortraitPath: $"res://youjin/images/cards/{slug}.png");
}

/// <summary>本 mod 所有「多人专属卡」的公共基类。</summary>
public abstract class YouJinMultiplayerCard : ModCardTemplate
{
    protected YouJinMultiplayerCard(int baseCost, CardType type, CardRarity rarity, TargetType target)
        : base(baseCost, type, rarity, target)
    {
    }

    /// <summary>单人局里抽不到；和原版「午夜」一致。</summary>
    public override CardMultiplayerConstraint MultiplayerConstraint => CardMultiplayerConstraint.MultiplayerOnly;

    public override CardAssetProfile AssetProfile => CardArt.Portrait(Slug);

    /// <summary>卡图文件名（不带 .png）。</summary>
    protected abstract string Slug { get; }

    /// <summary>除自己以外的、活着的队友（单人局为空）。</summary>
    protected IReadOnlyList<Creature> Teammates => GetTeammates();

    private IReadOnlyList<Creature> GetTeammates()
    {
        try
        {
            var combat = CombatState;
            var me = Owner?.Creature;
            if (combat is null || me is null)
                return Array.Empty<Creature>();

            return combat.GetTeammatesOf(me)
                .Where(c => c is not null && c != me && c.IsAlive && c.IsPlayer)
                .ToList();
        }
        catch
        {
            return Array.Empty<Creature>();
        }
    }
}

/// <summary>
/// 运行时兜底本地化：pck 里的 cards.json 是主渠道，这里再按运行时算出来的 Id.Entry 补一遍，
/// 防止键名/表名对不上导致卡面显示成键名。
/// </summary>
internal static class MpCardLocalization
{
    private static readonly Dictionary<string, (string Zh, string En)> Titles = new(StringComparer.Ordinal)
    {
        ["BLOOD_SACRIFICE"] = ("献祭", "Blood Sacrifice"),
        ["BURN_IT_ALL"] = ("拼尽一切", "Burn It All"),
        ["TEAMMATES_GO_FIRST"] = ("队友先上", "Teammates Go First"),
        ["FULL_CONCENTRATION"] = ("全神贯注", "Full Concentration"),
        ["SPARKS_FLY"] = ("火花四溅", "Sparks Fly"),
        ["LEND_IT_TO_ME"] = ("借我一用", "Lend It To Me"),
        ["DARK_MAGIC"] = ("暗黑魔法", "Dark Magic"),
        ["TABOO_FORCE"] = ("禁忌之力", "Forbidden Power"),
        ["TRANSFER_ENERGY"] = ("输送能量", "Transfer Energy"),
        ["CORRECT_COGNITION"] = ("认知正确", "Correct Cognition"),
    };

    /// <summary>类名 -> 中文描述 / 英文描述（动态变量用原版同样的 SmartFormat 写法）。</summary>
    private static readonly Dictionary<string, (string Zh, string En)> Descriptions = new(StringComparer.Ordinal)
    {
        ["BLOOD_SACRIFICE"] = (
            "[gold]消耗[/gold]。\n所有队友失去{HpLoss:diff()}点生命。\n获得{Energy:energyIcons()}，抽{Cards:diff()}张牌。",
            "[gold]Exhaust[/gold].\nAll teammates lose {HpLoss:diff()} HP.\nGain {Energy:energyIcons()} and draw {Cards:diff()} cards."),
        ["BURN_IT_ALL"] = (
            "[gold]消耗[/gold]。\n[gold]消耗[/gold]你和所有队友的[gold]手牌[/gold]。\n每张被[gold]消耗[/gold]的牌造成{Damage:diff()}点伤害。",
            "[gold]Exhaust[/gold].\n[gold]Exhaust[/gold] your hand and every teammate's hand.\nDeal {Damage:diff()} damage for each card exhausted."),
        ["TEAMMATES_GO_FIRST"] = (
            "选择一名队友。\n你受到的敌人伤害减少{DamageDecrease:percentLess()}%。\n该队友受到的敌人伤害增加{DamageIncrease:percentMore()}%。",
            "Choose a teammate.\nYou take {DamageDecrease:percentLess()}% less damage from enemies.\nThat teammate takes {DamageIncrease:percentMore()}% more damage from enemies."),
        ["FULL_CONCENTRATION"] = (
            "所有队友随机[gold]弃[/gold]一张牌。\n每有一张牌被弃掉，获得{Energy:energyIcons()}。",
            "Each teammate randomly [gold]discards[/gold] a card.\nGain {Energy:energyIcons()} for each card discarded."),
        ["SPARKS_FLY"] = (
            "[gold]铸造[/gold]{Forge:diff()}。\n其他所有玩家的[gold]手牌[/gold]里加入一张[gold]碎屑[/gold]。",
            "[gold]Forge[/gold] {Forge:diff()}.\nAdd a [gold]Debris[/gold] to every other player's hand."),
        ["LEND_IT_TO_ME"] = (
            "所有队友获得{FrailPower:diff()}层[gold]脆弱[/gold]。\n获得{Stars:starIcons()}和{Energy:energyIcons()}。",
            "All teammates gain {FrailPower:diff()} [gold]Frail[/gold].\nGain {Stars:starIcons()} and {Energy:energyIcons()}."),
        ["DARK_MAGIC"] = (
            "所有队友获得{DoomPower:diff()}层[gold]灾厄[/gold]。\n将{Cards:diff()}张[gold]灵魂[/gold]加入你的[gold]手牌[/gold]。",
            "All teammates gain {DoomPower:diff()} [gold]Doom[/gold].\nAdd {Cards:diff()} [gold]Soul[/gold] cards to your hand."),
        ["TABOO_FORCE"] = (
            "[gold]消耗[/gold]。\n所有队友获得[gold]预借时间[/gold]：本回合卡牌耗能增加{ExtraCost:energyIcons()}。\n你[gold]手牌[/gold]中的所有牌获得[gold]重放[/gold]{Replay:diff()}。",
            "[gold]Exhaust[/gold].\nAll teammates gain [gold]Borrowed Time[/gold]: cards cost {ExtraCost:energyIcons()} more this turn.\nAll cards in your hand gain [gold]Replay[/gold] {Replay:diff()}."),
        ["TRANSFER_ENERGY"] = (
            "所有队友失去{StrengthPower:diff()}点[gold]力量[/gold]和{DexterityPower:diff()}点[gold]敏捷[/gold]。\n获得{FocusPower:diff()}点[gold]集中[/gold]。",
            "All teammates lose {StrengthPower:diff()} [gold]Strength[/gold] and {DexterityPower:diff()} [gold]Dexterity[/gold].\nGain {FocusPower:diff()} [gold]Focus[/gold]."),
        ["CORRECT_COGNITION"] = (
            "使你的[gold]集中[/gold]翻倍。\n其他所有玩家的[gold]手牌[/gold]里加入{Cards:diff()}张[gold]伤口[/gold]。",
            "Double your [gold]Focus[/gold].\nAdd {Cards:diff()} [gold]Wound[/gold] cards to every other player's hand."),
    };

    /// <summary>本 mod 自己写的能力：类名 -> 中英标题 / 描述。</summary>
    private static readonly Dictionary<string, (string Zh, string En)> PowerTitles = new(StringComparer.Ordinal)
    {
        ["TEAMMATE_FIRST_POWER"] = ("队友先上", "Teammates Go First"),
    };

    private static readonly Dictionary<string, (string Zh, string En)> PowerDescriptions = new(StringComparer.Ordinal)
    {
        ["TEAMMATE_FIRST_POWER"] = (
            "受到的敌人伤害减少{DamageDecrease:percentLess()}%。\n指定的队友受到的敌人伤害增加{DamageIncrease:percentMore()}%。",
            "Take {DamageDecrease:percentLess()}% less damage from enemies.\nThe chosen teammate takes {DamageIncrease:percentMore()}% more damage from enemies."),
    };

    /// <summary>
    /// ★ 真正生效的那次注入。由 `LateLocalizationPatches` 挂在 `LocManager.SetLanguage` / `Initialize`
    /// 的**后置补丁**上调用 —— 那时本地化表才刚加载完。
    ///
    /// `force=true` 会**覆盖** PCK 里那份 cards.json / powers.json 的旧文本，所以
    /// **改卡牌/能力文案只要重新编译就行，不用重新导出 PCK**。
    ///
    /// （`Entry.Init` 那会儿 LocManager 还没建好 —— 初始化顺序是
    /// `OneTimeInitialization.ExecuteVeryEarly`（跑 mod 初始化）→ `ExecuteEssential`（`LocManager.Initialize`），
    /// 所以在 Init 里调这个必然是空转。别再往 Init 里塞本地化注入了。）
    /// </summary>
    public static void ApplyOverrides() => Merge(force: true);

    private static void Merge(bool force)
    {
        try
        {
            var loc = MegaCrit.Sts2.Core.Localization.LocManager.Instance;
            if (loc is null)
                return;

            bool zh = (loc.Language ?? string.Empty)
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            var count = InjectCardTable(loc, zh, force) + InjectPowerTable(loc, zh, force);
            Entry.Logger.Info($"[youjin] 卡牌/能力文本注入 {count} 条（lang={loc.Language}, 覆盖={force}）");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[youjin] 卡牌/能力文本注入失败 {ex}");
        }
    }

    private static int InjectCardTable(MegaCrit.Sts2.Core.Localization.LocManager loc, bool zh, bool force)
    {
        try
        {
            var table = loc.GetTable("cards");
            if (table is null)
                return 0;

            var merge = new Dictionary<string, string>();
            foreach (var type in CardTypes())
            {
                string entry;
                try
                {
                    entry = ModelDb.GetId(type)?.Entry ?? string.Empty;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry))
                    continue;

                if ((force || !table.HasEntry($"{entry}.title")) && Titles.TryGetValue(type.Name, out var title))
                    merge[$"{entry}.title"] = zh ? title.Zh : title.En;

                if ((force || !table.HasEntry($"{entry}.description"))
                    && Descriptions.TryGetValue(type.Name, out var desc))
                    merge[$"{entry}.description"] = zh ? desc.Zh : desc.En;
            }

            if (merge.Count > 0)
                table.MergeWith(merge);

            return merge.Count;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[youjin] 卡牌文本注入失败 {ex}");
            return 0;
        }
    }

    private static int InjectPowerTable(MegaCrit.Sts2.Core.Localization.LocManager loc, bool zh, bool force)
    {
        try
        {
            var table = loc.GetTable("powers");
            if (table is null)
                return 0;

            var merge = new Dictionary<string, string>();
            foreach (var type in typeof(MpCardLocalization).Assembly.GetTypes())
            {
                if (!type.IsClass || type.IsAbstract
                    || !typeof(MegaCrit.Sts2.Core.Models.PowerModel).IsAssignableFrom(type))
                    continue;

                string entry;
                try
                {
                    entry = ModelDb.GetId(type)?.Entry ?? string.Empty;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry))
                    continue;

                if ((force || !table.HasEntry($"{entry}.title")) && PowerTitles.TryGetValue(type.Name, out var title))
                    merge[$"{entry}.title"] = zh ? title.Zh : title.En;

                if (PowerDescriptions.TryGetValue(type.Name, out var desc))
                {
                    if (force || !table.HasEntry($"{entry}.description"))
                        merge[$"{entry}.description"] = zh ? desc.Zh : desc.En;
                    if (force || !table.HasEntry($"{entry}.smartDescription"))
                        merge[$"{entry}.smartDescription"] = zh ? desc.Zh : desc.En;
                }
            }

            if (merge.Count > 0)
                table.MergeWith(merge);

            return merge.Count;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[youjin] 能力文本注入失败 {ex}");
            return 0;
        }
    }

    /// <summary>本程序集里所有继承了 YouJinMultiplayerCard 的具体类型。</summary>
    private static IEnumerable<Type> CardTypes()
        => typeof(MpCardLocalization).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(YouJinMultiplayerCard).IsAssignableFrom(t));
}
