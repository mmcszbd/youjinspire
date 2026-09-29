// ============================================================================
// 友尽尖塔（youjin）：「建筑师的背叛」用到的三处 Harmony 补丁
// ----------------------------------------------------------------------------
//   ① 把原版胜利房间里的 TheArchitect 事件换成我们自己的 ArchitectBetrayalEvent。
//      切入点：RunManager.EnterNextAct() 里那句
//              `EnterRoom(new EventRoom(ModelDb.Event<TheArchitect>()))`
//      最终都会走到 EventRoom(EventModel) 这个构造函数，所以在构造函数入口替换最省事
//      （TheArchitect 是 sealed，继承不了）。
//
//   ② 让「我们的胜利房间」也算胜利房间。
//      AbstractRoom.IsVictoryRoom 的实现是
//          `room is EventRoom e && e.CanonicalEvent is TheArchitect`
//      换成我们的事件后它会变成 false，游戏结束画面就会变成「失败」。
//      这里把「CanonicalEvent 是我们的事件」也判成 true。
//
//   ③ 背叛战斗里允许把队友当成目标。
//      CardModel.IsValidTarget(Creature) 决定「这张卡能不能指向这个生物」：
//          TargetType.AnyEnemy(2) → 要求 creature.Side != 自己主人所在的一侧
//          TargetType.AnyAlly (6) → 要求 creature.Side == 自己主人所在的一侧
//      所以原版永远指不到队友。战斗期间把这个判断后置放宽一次，
//      只对本场战斗生效（ArchitectBetrayalEvent.BetrayalCombatActive）。
//      注意：战斗开始/结束的那一帧边缘情况没有处理，靠 BetrayalCombatActive 开关控制。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Ancients;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace YouJin.Scripts;

/// <summary>补丁安装入口。</summary>
internal static class ArchitectBetrayalPatches
{
    /// <summary>只 patch 本文件里的三个补丁类，不动程序集里其它已有的补丁。</summary>
    public static void Apply()
    {
        // 每个补丁单独 try/catch：任何一个装不上，都不能拖累另外两个。
        var harmony = new Harmony("youjin.architectbetrayal");
        PatchOne(harmony, typeof(ArchitectRoomReplacementPatch), "替换胜利房间");
        PatchOne(harmony, typeof(VictoryRoomPatch), "胜利房间判定");
        PatchOne(harmony, typeof(MajorityVotePatch), "共享事件多数决");
        PatchOne(harmony, typeof(TeammateTargetingPatch), "队友目标放开");
        PatchOne(harmony, typeof(TeammateUiTargetingPatch), "UI 放开队友目标");
        PatchOne(harmony, typeof(ArchitectDialoguePatch), "建筑师结束台词");
        PatchOne(harmony, typeof(TeammateEnemySidePatch), "队友显示移到对面");
        PatchOne(harmony, typeof(EggFarRightPatch), "虫子(卵)挪到最右边");
        PatchOne(harmony, typeof(TeammatePotionTargetingPatch), "药水放开队友目标");
        PatchOne(harmony, typeof(TeammateAoeTargetingPatch), "群体卡带上队友");
        PatchOne(harmony, typeof(PvpEnemyListPatch), "出牌瞬间把队友算成敌人");
    }

    private static void PatchOne(Harmony harmony, Type type, string label)
    {
        try
        {
            harmony.CreateClassProcessor(type).Patch();
            Entry.Logger.Info($"建筑师的背叛：补丁「{label}」安装完成");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：补丁「{label}」安装失败 {ex}");
        }
    }
}

/// <summary>① 把胜利房间里的 TheArchitect 换成我们的事件。</summary>
[HarmonyPatch]
internal static class ArchitectRoomReplacementPatch
{
    /// <summary>
    /// 事件结算完之后，我们要主动进一次「原版建筑师房间」；
    /// 这一次必须**不要**被替换掉，所以进入前把这个开关打开。
    /// （ArchitectDialoguePatch 也用这个开关判断「这次是友尽尖塔引导来的建筑师」。）
    /// </summary>
    public static bool AllowVanillaArchitect;

    private static MethodBase TargetMethod()
        => AccessTools.Constructor(typeof(EventRoom), new[] { typeof(EventModel) });

    private static void Prefix(ref EventModel __0)
    {
        try
        {
            // 我们自己要进原版建筑师房间，放行。
            if (AllowVanillaArchitect)
                return;

            if (__0 is not TheArchitect)
                return;

            if (!YouJinSettings.GameplayEnabled)
                return;

            var type = typeof(ArchitectBetrayalEvent);
            var canonical = ModelDb.GetByIdOrNull<EventModel>(ModelDb.GetId(type));

            // 这里只做查询，绝不能 ModelDb.Inject —— RitsuLib 的自动发现已经把这个
            // 类型登记过了，再 Inject 会让同一个 ID 被 new 两次，AbstractModel 的
            // 构造函数会抛 DuplicateModelException，导致整个游戏启动失败。
            if (canonical is null)
            {
                Entry.Logger.Info("建筑师的背叛：事件还没注册进 ModelDb，保持原版建筑师");
                return;
            }

            __0 = canonical;
            Entry.Logger.Info("建筑师的背叛：胜利房间已替换为自定义事件");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：替换胜利房间失败 {ex}");
        }
    }
}

/// <summary>
/// ② 共享事件的投票改成真正的「多数决」。
/// ----------------------------------------------------------------------------
/// 原版在 host 上收齐所有人的票之后，是这么决定的：
///     var chosen = _multiplayerOptionSelectionRng.NextItem(_playerVotes);
/// —— 从所有票里**随机抽一张**，既不是多数决，也不是 host 说了算。
///
/// 我们不想重写整个 private 方法（里面还有消息广播和一致性检查），
/// 所以用 Prefix 把 _playerVotes 里每个元素都改写成「多数票」，
/// 这样原方法紧接着的 NextItem 必然抽到多数票，广播出去的 optionIndex 就是多数决结果。
/// 于是所有客户端执行的都是同一个选项，「超过一半选战斗」才是真正的多数决。
///
/// 只对「建筑师的背叛」生效 —— _canonicalEvent 不是我们的事件时直接放行，
/// 原版共享事件仍然是原来的随机抽票行为。
/// </summary>
[HarmonyPatch]
internal static class MajorityVotePatch
{
    private static readonly FieldInfo? CanonicalEventField =
        AccessTools.Field(typeof(EventSynchronizer), "_canonicalEvent");

    private static readonly FieldInfo? PlayerVotesField =
        AccessTools.Field(typeof(EventSynchronizer), "_playerVotes");

    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(EventSynchronizer), "ChooseSharedEventOption");

    private static void Prefix(EventSynchronizer __instance)
    {
        try
        {
            if (CanonicalEventField?.GetValue(__instance) is not ArchitectBetrayalEvent)
                return;

            if (PlayerVotesField?.GetValue(__instance) is not List<uint?> votes || votes.Count == 0)
                return;

            var tally = new Dictionary<uint, int>();
            foreach (var vote in votes)
            {
                if (vote is not { } value)
                    continue;

                tally[value] = tally.TryGetValue(value, out var count) ? count + 1 : 1;
            }

            if (tally.Count == 0)
                return;

            // 票最多的选项胜出；平票时取下标小的（0 = 战斗）。
            var winner = tally
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .First().Key;

            for (var i = 0; i < votes.Count; i++)
                votes[i] = winner;

            var detail = string.Join(
                "、",
                tally.OrderBy(pair => pair.Key).Select(pair => $"选项{pair.Key}={pair.Value}票"));

            Entry.Logger.Info($"建筑师的背叛：多人投票（{detail}）→ 采用选项 {winner}");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：多数决改写失败 {ex}");
        }
    }
}

/// <summary>③ 让我们的胜利房间也算「胜利房间」（结算画面才会显示真胜利）。</summary>
[HarmonyPatch(typeof(AbstractRoom), nameof(AbstractRoom.IsVictoryRoom), MethodType.Getter)]
internal static class VictoryRoomPatch
{
    private static void Postfix(AbstractRoom __instance, ref bool __result)
    {
        if (__result)
            return;

        if (__instance is EventRoom room && room.CanonicalEvent is ArchitectBetrayalEvent)
            __result = true;
    }
}

/// <summary>④ 背叛战斗期间允许把队友当成目标（点击后的最终校验）。</summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
internal static class TeammateTargetingPatch
{
    // 参数名必须和原方法一模一样（CardModel.IsValidTarget(Creature target)），
    // 写成别的名字 Harmony 会抛 "Parameter ... not found in method"。
    private static void Postfix(CardModel __instance, Creature target, ref bool __result)
    {
        if (__result)
            return;

        if (!ArchitectBetrayalEvent.BetrayalCombatActive)
            return;

        if (target is null)
            return;

        var mine = __instance.Owner?.Creature;
        if (mine is null || ReferenceEquals(mine, target))
            return;

        // 只放开「自己这一侧」的其他生物（队友）。敌人本来就能被指向。
        if (target.Side != mine.Side)
            return;

        if (!target.IsAlive)
            return;

        __result = true;
    }
}

/// <summary>
/// ⑤ 背叛战斗期间允许把队友当成目标（UI 层：光标能不能落到那个生物身上）。
/// ----------------------------------------------------------------------------
/// 只改 CardModel.IsValidTarget 是不够的 —— 那只管「选中之后能不能打出去」。
/// 玩家拖牌的时候，能不能把光标放到某个生物上，是 NTargetManager 说了算：
///
///     AllowedToTargetNode(node)
///        └─ AllowedToTargetCreature(creature)   ← 这里
///
/// 而 AllowedToTargetCreature 只看两个东西：卡牌的 TargetType 和 Creature.Side
///   · AnyEnemy(2)  → 只有 Side == Enemy 才能被指向
///   · AnyAlly(6)   → 只有自己这一侧才行
/// 所以「选一个敌人」的牌在原版里永远指不到队友。
///
/// 战斗期间把 AnyEnemy 的判断放宽一次：同侧（队友）、活着、不是自己 → 也允许。
/// （自己永远不能指向自己，原版 AnyAlly 分支也是这么做的。）
/// </summary>
[HarmonyPatch(typeof(NTargetManager), "AllowedToTargetCreature")]
internal static class TeammateUiTargetingPatch
{
    // _validTargetsType 是 private 字段；用懒加载，万一字段名变了也只是这次判断失效，
    // 不会在类加载时把整个补丁拖垮。
    private static AccessTools.FieldRef<NTargetManager, TargetType>? _validTargetsType;

    private static void Postfix(NTargetManager __instance, Creature __0, ref bool __result)
    {
        if (__result)
            return;

        if (!ArchitectBetrayalEvent.BetrayalCombatActive)
            return;

        var target = __0;
        if (target is null || target.IsDead)
            return;

        if (GetValidTargetsType(__instance) != TargetType.AnyEnemy)
            return;

        if (target.Side != CombatSide.Player)
            return;

        var player = target.Player;
        if (player is null || LocalContext.IsMe(player))
            return;

        __result = true;
    }

    private static TargetType GetValidTargetsType(NTargetManager manager)
    {
        try
        {
            _validTargetsType ??=
                AccessTools.FieldRefAccess<NTargetManager, TargetType>("_validTargetsType");
            return _validTargetsType(manager);
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：读取卡牌目标类型失败 {ex}");
            return TargetType.None;
        }
    }
}

/// <summary>
/// ⑥ 让建筑师在最后说不同的话。
/// ----------------------------------------------------------------------------
/// TheArchitect.LoadDialogue() 里是这么选对话的：
///     Dialogue = rng.NextItem(DialogueSet.GetValidDialogues(角色, 通关次数, 总胜场, false));
/// 之后 PlayCurrentLine 拿 AncientDialogueLine.LineText 起气泡（TalkCmd.Play），
/// 而 LineText 是可写的 —— 所以这里只在加载完之后换掉**最后一句**：
///     选项一（打过架）→「感谢你们，让我在无聊的生活中看了场好戏。」
///     选项二（不打架）→「那就都去死吧。」
/// 其余台词、攻击动画、真胜利结算全部保持原版。
///
/// 只在「友尽尖塔引导来的建筑师」上生效（AllowVanillaArchitect），
/// 玩家正常玩、或者我们的替换没生效时都不会误改。
/// </summary>
[HarmonyPatch(typeof(TheArchitect), "LoadDialogue")]
internal static class ArchitectDialoguePatch
{
    // Dialogue 属性是 private getter，直接拿 _dialogue 字段更省事。
    private static readonly FieldInfo? DialogueField =
        AccessTools.Field(typeof(TheArchitect), "_dialogue");

    private static void Postfix(TheArchitect __instance)
    {
        try
        {
            if (!ArchitectRoomReplacementPatch.AllowVanillaArchitect)
                return;

            var ending = ArchitectBetrayalEvent.ChosenEnding;
            if (ending == ArchitectBetrayalEvent.Ending.None)
                return;

            if (DialogueField?.GetValue(__instance) is not AncientDialogue dialogue)
                return;

            if (dialogue.Lines is not { Count: > 0 } lines)
                return;

            // 注意：不能改 Lines[^1] —— 对话的最后一句是**玩家**说的
            // （AncientDialogueSpeaker.Character），要改的是建筑师自己那一句。
            // 一句话里建筑师可能说好几句，这里取"最后一句由建筑师说的"。
            AncientDialogueLine? architectLine = null;
            var architectLineIndex = -1;

            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Speaker != AncientDialogueSpeaker.Ancient)
                    continue;

                architectLine = lines[i];
                architectLineIndex = i;
            }

            if (architectLine is null)
            {
                Entry.Logger.Info("建筑师的背叛：这段对话里没有建筑师发言，跳过台词替换");
                return;
            }

            architectLine.LineText = ArchitectBetrayalEvent.ArchitectFinalLine(ending);

            Entry.Logger.Info(
                $"建筑师的背叛：把第 {architectLineIndex} 句（建筑师说的，共 {lines.Count} 句）"
                + $"改成「{ending}」");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：改写建筑师台词失败 {ex}");
        }
    }
}

/// <summary>
/// ⑨ 背叛战斗期间，药水也能指向队友。
/// ----------------------------------------------------------------------------
/// 和卡牌那条补丁同一个思路，只不过判定在 PotionModel 上：
/// 反编译 PotionModel.IsValidTarget 的结果是——
///     AnyEnemy(2) → 要求 creature.Side != 主人所在的一侧
///     AnyAlly(6)  → 要求同侧、活着、而且不能是自己
/// 所以"选一个敌人"的药水原版永远指不到队友。战斗期间把 __result 放宽一次。
/// 参数用 __0（位置参数），免得猜原方法的参数名。
/// </summary>
[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.IsValidTarget))]
internal static class TeammatePotionTargetingPatch
{
    private static void Postfix(PotionModel __instance, Creature __0, ref bool __result)
    {
        if (__result)
            return;

        if (!ArchitectBetrayalEvent.BetrayalCombatActive)
            return;

        var target = __0;
        if (target is null || !target.IsAlive)
            return;

        var mine = __instance.Owner?.Creature;
        if (mine is null || ReferenceEquals(mine, target))
            return;

        if (target.Side != mine.Side)
            return;

        __result = true;
    }
}

/// <summary>
/// ⑩ 背叛战斗期间，群体卡（"对所有敌人"/"随机敌人"那一类）也要打到队友。
/// ----------------------------------------------------------------------------
/// 群体卡不是靠"选目标"的，它们内部统一去问 CombatState.HittableEnemies：
///     HittableEnemies = Enemies.Where(c => c.IsHittable)
/// 反编译确认 Conflagration / DaggerSpray / Thunderclap / Stomp / BouncingFlask……
/// 以及 Hook.ModifyDamage、NTargetManager.ShowMultiCreatureTargetingVisuals 都吃这份列表。
///
/// 所以这里给这个 getter 挂后置补丁：战斗期间把"属于远程玩家"的友方单位（队友本体
/// 和他们的宠物/召唤物）追加进去。于是群体卡会连队友一起打，UI 的多目标高亮也会带上队友。
///
/// 注意这里**只动 HittableEnemies，不动 Enemies**——Enemies 是胜负判定用的
///（CombatManager.IsCombatEnding 看它），动了会让战斗提前结束。
///
/// 【为什么不能用 LocalContext.IsMe 来排除自己】
/// 群体卡的 OnPlay 是在**每个客户端上各跑一遍**的（动作同步 + 确定性重演，
/// 游戏靠 ChecksumTracker 对账）。如果各端按"本地玩家"过滤，A 端算出的目标里有 B、
/// B 端算出的目标里有 A，最终血量就不一样了 —— 直接不同步。
/// 所以这里改成用 ActionExecutor.CurrentlyRunningAction 反查"正在打这张牌的人"：
/// 这个动作在所有客户端上是同一个，排除结果自然一致。
/// 万一拿不到当前动作（纯 UI 预览之类），就谁都不过滤 —— 宁可多算，也绝不让各端结果分叉。
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.HittableEnemies), MethodType.Getter)]
internal static class TeammateAoeTargetingPatch
{
    private static void Postfix(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return;

            if (__instance is null)
                return;

            // 正在执行的那个动作如果是"打牌"，就把它的人找出来，免得他打到自己。
            Player? caster = null;
            try
            {
                caster = (RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction as PlayCardAction)?.Player;
            }
            catch
            {
                // 拿不到就算了，下面会退化成"不过滤"
            }

            List<Creature>? merged = null;

            foreach (var ally in __instance.Allies)
            {
                if (ally is null || !ally.IsAlive)
                    continue;

                // 队友本体走 Player；宠物/召唤物走 PetOwner。
                var owner = ally.Player ?? ally.PetOwner;
                if (owner is null)
                    continue;

                // 出牌的人（以及他自己的宠物）不挨自己的群体卡。
                if (caster is not null && owner.NetId == caster.NetId)
                    continue;

                if (__result.Contains(ally))
                    continue;

                merged ??= new List<Creature>(__result);
                merged.Add(ally);
            }

            if (merged is not null)
                __result = merged;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：群体卡放开队友失败 {ex}");
        }
    }
}

/// <summary>
/// ⑦ 把这块木桩（结实的卵）挪到屏幕最右边。
/// ----------------------------------------------------------------------------
/// 反编译确认的位置来源：NCombatRoom.CreateEnemyNodes 里
///     if (EncounterSlots != null) PositionCreaturesWithSlots(...)   // 遭遇自带场景时按 Marker 摆
///     else                        PositionEnemies(...)              // 否则按数值排布
/// 我们的 BetrayalEggEncounter 没有自定义场景 → EncounterSlots 为 null → 走 PositionEnemies。
/// 而 PositionEnemies 是把整组敌人**居中**排布的，只有一只时会落在偏中间的位置，
/// 正好会跟被搬到敌方一侧的队友挤在一起。
///
/// 所以给 PositionEnemies 挂后置补丁，把它推到右侧边缘（留一点边距）。
/// 注意 PositionEnemies 在方法内部用的是 Control.Position，这里统一用 GlobalPosition 设，
/// 不管它父容器什么变换都能落到屏幕上正确的位置。
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), "PositionEnemies")]
internal static class EggFarRightPatch
{
    /// <summary>距右边缘留的像素（免得整只虫子贴边甚至出屏）。</summary>
    private const float RightMargin = 200f;

    private static void Postfix(List<NCreature> __0)
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return;

            if (__0 is null || __0.Count == 0)
                return;

            if (NCombatRoom.Instance is not { } room)
                return;

            var roomWidth = room.Size.X;
            if (roomWidth <= 1f)
                return;

            var rightEdge = room.GlobalPosition.X + roomWidth - RightMargin;

            var moved = 0;

            foreach (var node in __0)
            {
                if (node is null)
                    continue;

                var before = node.GlobalPosition;
                if (before.X >= rightEdge)
                    continue;

                node.GlobalPosition = new Vector2(rightEdge, before.Y);
                moved++;

                Entry.Logger.Info(
                    $"建筑师的背叛：虫子挪到最右边 {before.X:F0} → {rightEdge:F0}"
                    + $"（房间宽 {roomWidth:F0}）");
            }

            if (moved > 0)
                Entry.Logger.Info($"建筑师的背叛：共 {moved} 只虫子已贴到右侧");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：移动虫子位置失败 {ex}");
        }
    }
}

/// <summary>
/// ⑧ 把「队友」的显示节点搬到对面（敌方那一侧）并水平翻转。
/// ----------------------------------------------------------------------------
/// 纯视觉改动 —— Creature.Side 一个字节都不动，所以：
///   · 胜负判定（CombatManager.IsCombatEnding 只看 Enemies）
///   · 可打击列表（IsHittable / HittableEnemies）
///   · 伤害计算、AI 选目标
/// 全部保持原样。只是"看起来"站在对面。
///
/// 切入点：NCombatRoom.PositionPlayersAndPets(List&lt;NCreature&gt;, float, bool)
/// 它是 **public static**，而且是全游戏唯一给"玩家这一侧"排位置的地方
/// （反编译确认：里面先按 LocalContext.IsMe 把本地玩家插到第 0 位，
///  后面的都是队友，再按网格排布）。挂 Postfix 有两个好处：
///   1. 时机天然正确 —— 它跑的时候节点已经建好、也刚被摆过位置；
///   2. 每次游戏重新排布（加/减单位）都会自动重新应用，不会被覆盖掉。
///
/// 移动方式：拿它排好的 GlobalPosition，按战斗房间的水平中线镜像过去。
/// 这样不用猜敌方的具体坐标，屏幕分辨率变了也对得上。
///
/// 翻转方式：直接把 NCreature.Visuals（NCreatureVisuals : Node2D）的 X 缩放取负。
/// Visuals 是整棵表现子树的根（本体、武器、特效、各种 Marker 都在它下面），
/// 所以一次翻转就能全部翻转，不会出现"身体翻了武器没翻"。
/// 用 -Abs(...) 而不是 *(-1)，是为了幂等 —— 这个方法可能被调用多次。
///
/// 多人同步：不需要。每个客户端本地都有全部参战单位的节点（这也是
/// _isRemotePlayerOrPet 存在的原因），各客户端在同一时机算出同样的结果，
/// 纯确定性，不需要发任何网络消息。
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.PositionPlayersAndPets))]
internal static class TeammateEnemySidePatch
{
    private static void Postfix(List<NCreature> __0)
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return;

            if (__0 is null || __0.Count == 0)
                return;

            if (NCombatRoom.Instance is not { } room)
                return;

            // 以战斗房间的水平中线为镜像轴。
            var centerX = room.GlobalPosition.X + room.Size.X * 0.5f;
            if (centerX <= 1f)
                return;

            var moved = 0;

            foreach (var node in __0)
            {
                if (node is null)
                    continue;

                var creature = node.Entity;
                if (creature is null)
                    continue;

                // 队友本体走 Player；宠物 / 召唤物走 PetOwner（它们的 Player 是 null）。
                // 两者都要处理，否则会出现"人过去了、宠物还留在原地"。
                var owner = creature.Player ?? creature.PetOwner;
                if (owner is null)
                    continue;

                if (LocalContext.IsMe(owner))
                    continue; // 本地玩家和他自己的召唤物留在原位

                var before = node.GlobalPosition;
                node.GlobalPosition = new Vector2(centerX * 2f - before.X, before.Y);

                // 把整棵表现子树镜像过来，面朝队友那边。
                if (node.Visuals is { } visuals)
                {
                    var scale = visuals.Scale;
                    visuals.Scale = new Vector2(-Mathf.Abs(scale.X), scale.Y);
                }

                moved++;
                Entry.Logger.Info(
                    $"建筑师的背叛：{(creature.IsPet ? "队友的召唤物" : "队友")} "
                    + $"NetId={owner.NetId} 移到对面 "
                    + $"{before.X:F0} → {node.GlobalPosition.X:F0}（中线 {centerX:F0}）");
            }

            if (moved > 0)
                Entry.Logger.Info($"建筑师的背叛：共 {moved} 个远程玩家单位已移到敌方一侧");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：移动队友显示节点失败 {ex}");
        }
    }
}

/// <summary>
/// ⑪ 只在"某个玩家正在出牌/用药"的那一瞬间，把队友算进 CombatState.Enemies。
/// ----------------------------------------------------------------------------
/// 为什么还需要这一层：
///   原版群体卡不是统一走一个接口的 ——
///     · Conflagration / DaggerSpray / Thunderclap … 读 HittableEnemies（上面⑩已覆盖）
///     · Hyperbeam / Mirage / FranticEscape 这类直接读 Enemies（⑩覆盖不到）
///   要全都能打到队友，就必须动 Enemies 本身。
///
/// 为什么不能一直动 Enemies：
///   · CombatManager.IsCombatEnding 就是看 Enemies（"敌方全灭 = 战斗结束"），
///     而 ActionExecutor 在**每个动作执行完之后**都会调 CheckWinCondition；
///   · 怪物 AI（Guardbot / Queen / TwoTailedRat…）、EncounterModel.GetNextSlot、
///     CombatRoom.Enemies 这些也都读它。
///   所以这里只在一个**极窄**的时间窗里加料：当前正在执行的动作是某个玩家发起的
///   （CurrentlyRunningAction.OwnerId 能对上一位玩家）。窗口之外 Enemies 原封不动。
///
/// 为什么不会不同步：
///   CurrentlyRunningAction 在所有客户端上是**同一个动作**，用它反查出的"出牌者"也一致，
///   所以各端算出的列表完全相同（之前用 LocalContext.IsMe 那版就是错在这里）。
///   出牌的人自己（和他自己的宠物）会被排除掉，所以群体卡不会打到自己。
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.Enemies), MethodType.Getter)]
internal static class PvpEnemyListPatch
{
    private static void Postfix(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return;

            if (__instance is null)
                return;

            var action = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
            if (action is null)
                return;

            // 找出这次动作是谁发的；对不上玩家（比如怪物行动）就什么都不做。
            var caster = __instance.Players.FirstOrDefault(player => player.NetId == action.OwnerId);
            if (caster is null)
                return;

            List<Creature>? merged = null;

            foreach (var ally in __instance.Allies)
            {
                if (ally is null || !ally.IsAlive)
                    continue;

                // 队友本体走 Player；宠物/召唤物走 PetOwner。
                var owner = ally.Player ?? ally.PetOwner;
                if (owner is null || owner.NetId == caster.NetId)
                    continue;

                if (__result.Contains(ally))
                    continue;

                merged ??= new List<Creature>(__result);
                merged.Add(ally);
            }

            if (merged is not null)
                __result = merged;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：出牌瞬间改写敌方列表失败 {ex}");
        }
    }
}
