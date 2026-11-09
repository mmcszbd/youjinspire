// ============================================================================
// 友尽尖塔（youjin）：商店药水「被祝福的泉水」
// ----------------------------------------------------------------------------
// 规则：
//   1. 普通稀有药水：跟着药水池子随机出现（商店 / 奖励都可能抽到）；
//      商店里售价固定 150（见 BlessedSpringShopPatch 的定价补丁）；
//   2. 使用后立即触发：从卡组里选一张**诅咒**删除；
//      「永恒(Eternal)」诅咒不能删，所以不会出现在可选列表里；
//   3. 牌组里没有可删的诅咒 → 什么都不发生（连选择界面都不弹）。
//
// 反编译踩点：
//   · 药水随机池 PotionFactory.GetPotionOptions(player) 只读两个来源：
//       player.Character.PotionPool（角色池）+ 共享药水池。
//     所以只要把药水注册进**原版 Token 池**（TokenPotionPool，永远不会被随机抽到），
//     它就绝不会出现在战斗掉落/奖励里，只能由我们在商店里手动塞进去 —— 天然满足"只能在商店出售"。
//     （注意：不能自己造一个池子又不管注册 —— PotionModel.get_Pool() 会执行
//       AllPotionPools.First(pool => pool.Contains(this))，找不到就抛
//       "Sequence contains no matching element"，药水弹窗 _Ready 直接崩，
//       表现就是"点开药水后卡在使用/丢弃界面关不掉"。）
//   · 药水可用时机：PotionUsage.AnyTime(2) 表示随时可用（原版 FruitsJuice / BloodPotion 就是这么写的）；
//     TargetType 用 Self(1)：效果只作用于使用者自己，不该让你去点队友
//     （2026-10-28 用户报「加了两个药水怎么都只能对自己用」——泉水本来就是对使用者自己生效，
//       之前挂 AnyPlayer 会先弹选人、点了队友却还是删自己的诅咒，改成 Self 让界面和行为一致）。
//   · 使用入口：PotionModel.OnUse(ctx, target)（protected virtual）—— 用掉药水的瞬间就会跑这里。
//   · 删牌界面：CardSelectCmd.FromDeckForRemoval(player, prefs, filter)，第三个参数是自定义筛选，
//     原版传 null（所有牌都能删），我们传"是诅咒且不带 Eternal"。
//   · 图标：借用观者 mod（Watcher）里「瓶装奇迹 BottledMiracle」的贴图当占位符。
//     那两张 .tres 是打包好的药水贴图，路径我从 Watcher.pck 的文件表里确认过。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.PotionPools;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace YouJin.Scripts;

/// <summary>商店药水：被祝福的泉水（删除一张诅咒）。</summary>
[RegisterPotion(typeof(TokenPotionPool))]
public sealed class BlessedSpringPotion : ModPotionTemplate
{
    /// <summary>售价（真正的定价在 BlessedSpringPotionEntry.CalcCost 里）。</summary>
    public const int Price = 150;

    /// <summary>稀有度只影响图鉴配色之类；因为不在任何共享池里，抽奖抽不到它。</summary>
    public override PotionRarity Rarity => PotionRarity.Rare;

    /// <summary>随时可用（不限于战斗）。</summary>
    public override PotionUsage Usage => PotionUsage.AnyTime;

    /// <summary>
    /// 只对自己：不弹选人界面。
    /// 效果本来就是删"使用者自己"的诅咒（OnUse 里从来没看 target），
    /// 之前挂 AnyPlayer 会在多人战斗里让你先点一个玩家、点了队友也没用 —— 纯误导。
    /// （原版反面例子：BloodPotion / FruitJuice 那种 AnyPlayer 是真的对目标生效，我们不是。）
    /// </summary>
    public override TargetType TargetType => TargetType.Self;

    /// <summary>
    /// 图标：优先 youjin/images/potions/blessed_spring.png（+ _outline.png），
    /// 没有就用占位图（观者 mod 的「瓶装奇迹」）。
    /// </summary>
    public override PotionAssetProfile AssetProfile => PotionArt.Resolve("blessed_spring");

    /// <summary>喝下去立刻触发：删掉一张诅咒。</summary>
    protected override async Task OnUse(PlayerChoiceContext choiceContext, Creature? target)
    {
        try
        {
            // 药水归属人：原版药水自身带 Owner（用它能拿到玩家）
            var player = Owner;
            if (player is null || !YouJinSettings.GameplayEnabled)
                return;

            if (!player.Deck.Cards.Any(IsRemovableCurse))
            {
                Entry.Logger.Info("被祝福的泉水：牌组里没有可删除的诅咒，无事发生");
                return;
            }

            var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1)
            {
                Cancelable = true,
                RequireManualConfirmation = true,
            };

            // 用带 choiceContext 的选择接口：药水效果本身就在一个 choice 流程里跑，
            // FromDeckForRemoval 那种"脱手"的选择（原版给奖励/事件用）在药水里会把流程卡死。
            var curses = player.Deck.Cards.Where(IsRemovableCurse).ToList();
            var chosen = await CardSelectCmd.FromSimpleGrid(choiceContext, curses, player, prefs);
            var card = chosen?.FirstOrDefault();
            if (card is null)
            {
                Entry.Logger.Info("被祝福的泉水：没有选择任何诅咒，无事发生");
                return;
            }

            await CardPileCmd.RemoveFromDeck(card, false);
            Entry.Logger.Info($"被祝福的泉水：删除了诅咒 {card.Id.Entry}");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"被祝福的泉水：结算失败 {ex}");
        }
    }

    /// <summary>能删的只有"诅咒"，而且不能带永恒（Eternal = 不可移除）。</summary>
    private static bool IsRemovableCurse(CardModel card)
        => card.Type == CardType.Curse
           && !card.Keywords.Contains(CardKeyword.Eternal);

    /// <summary>把名字/描述注入 potions 表（商店里显示名字要用，由 Entry 提前调一次）。</summary>
    /// <param name="force">true = 覆盖式（LocManager 表加载完之后由 LateLocalization 调，改文案不用重导 PCK）。</param>
    public static void EnsureLocalized(bool force = false)
    {
            var id = ModelDb.GetId(typeof(BlessedSpringPotion))?.Entry;
            if (string.IsNullOrEmpty(id))
                return;
        LocInject.Merge("potions", $"{id}.title", force, chinese => new Dictionary<string, string>
        {
            [$"{id}.title"] = chinese ? "被祝福的泉水" : "Blessed Spring",
            [$"{id}.description"] = chinese
                ? $"从[gold]卡组[/gold]中选择一张[red]诅咒[/red]删除（永恒诅咒无法删除）。"
                  + $"售价 [blue]{Price}[/blue] 金币。"
                : "Remove a Curse from your deck (Eternal curses can't be removed).",
        }, "被祝福的泉水");
    }
}
