// ============================================================================
// 友尽尖塔（youjin）：「建筑师的挑拨 / 背叛」相关的 Harmony 补丁
// ----------------------------------------------------------------------------
// 这个文件现在装三组（入口见下面的 ArchitectBetrayalPatches.Apply）：
//   A 组 · 事件与房间          把原版胜利房间里的 TheArchitect 换成我们的 ArchitectsBetrayalEvent，
//                              并让结算认得出它、把共享事件投票改成多数决、改建筑师结束台词。
//   B 组 · PvP 战斗本体        队友可被指定（卡牌/药水/群体卡/光标四层）、队友显示移到对面、
//                              木桩挪最右、眩晕玩家改成结束回合、出牌上限、退出前拆队伍视图。
//   D 组 · 火堆与偷窃          和背叛事件无关，只是复用这个补丁宿主（历史原因）。
// 存档保护那一组（4 个补丁）已经搬去 ArchitectSaveGuards.cs。
//
// 几个关键切入点（改之前先读这几段注释，都是踩过的坑）：
//   ① EventRoom(EventModel) 构造函数 —— 替换胜利房间。TheArchitect 是 sealed，只能这样换。
//   ② AbstractRoom.IsVictoryRoom —— 换成我们的事件后原版判定为 false，结束画面会变成「失败」。
//   ③ CardModel.IsValidTarget / NTargetManager.AllowedToTargetCreature / PotionModel.IsValidTarget
//      —— 原版永远指不到队友，这三层要一起放开（漏一层就表现得像随机 bug）。
//   ④ CombatState.HittableEnemies —— 群体卡的目标来源；只动它，不动 Enemies（Enemies 决定胜负）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
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
using MegaCrit.Sts2.Core.Saves;

namespace YouJin.Scripts;

/// <summary>补丁安装入口。</summary>
/// <remarks>
/// 这个文件里的补丁早就超出「建筑师的背叛」本身了（火堆、偷窃也借住在这里）。
/// 改造前是一张 30 行的平铺清单，看不出哪几个属于同一件事、出事该先怀疑哪一组。
/// 现在按职责分成四组，每组一个方法 —— **每个补丁仍然是单独 try/catch**，装不上一个不拖累别人，
/// 顺序与改造前一致（彼此独立，本来就没有先后依赖）。
/// C 组（存档保护）的实现已经搬到 <c>ArchitectSaveGuards.cs</c>，这里只留一句转发。
/// </remarks>
internal static class ArchitectBetrayalPatches
{
    /// <summary>装本文件里的全部补丁。</summary>
    public static void Apply()
    {
        var harmony = new Harmony("youjin.architectbetrayal");

        ApplyEventAndRoomPatches(harmony);   // A 组：事件 / 胜利房间替换
        ApplyPvpCombatPatches(harmony);      // B 组：PvP 战斗本体（目标、站位、收场、出牌上限）
        ApplySaveAndProgressGuards(harmony); // C 组：存档保护（脏数据 / 崩溃兜底）
        ApplyRestSiteAndStealPatches(harmony); // D 组：火堆分页 + 「偷窃」三连
    }

    /// <summary>A 组：把胜利房间里的建筑师换成我们的事件，并让结算认得出它。</summary>
    private static void ApplyEventAndRoomPatches(Harmony harmony)
    {
        PatchOne(harmony, typeof(ArchitectRoomReplacementPatch), "替换胜利房间");
        PatchOne(harmony, typeof(VictoryRoomPatch), "胜利房间判定");
        PatchOne(harmony, typeof(MajorityVotePatch), "共享事件多数决");
        PatchOne(harmony, typeof(ArchitectDialoguePatch), "建筑师结束台词");
    }

    /// <summary>
    /// B 组：PvP 战斗本体。
    /// </summary>
    /// <remarks>
    /// 「队友算目标」是三处并行的（卡牌 / 药水 / 群体卡），只装其中一两个会表现得像随机 bug：
    /// 单体能指到人、群体卡却漏掉，或者反过来。改动这里时务必三处一起动。
    /// </remarks>
    private static void ApplyPvpCombatPatches(Harmony harmony)
    {
        PatchOne(harmony, typeof(TeammateTargetingPatch), "队友目标放开");
        PatchOne(harmony, typeof(TeammateUiTargetingPatch), "UI 放开队友目标");
        PatchOne(harmony, typeof(TeammatePotionTargetingPatch), "药水放开队友目标");
        PatchOne(harmony, typeof(TeammateAoeTargetingPatch), "群体卡带上队友");
        PatchOne(harmony, typeof(TeammateEnemySidePatch), "队友显示移到对面");
        PatchOne(harmony, typeof(EggFarRightPatch), "虫子(卵)挪到最右边");
        PatchOne(harmony, typeof(TeammateStunEndsTurnPatch), "眩晕玩家改成结束回合");
        PatchOne(harmony, typeof(BeatingRemnantPvpPatch), "律动残余：PvP 战斗失血上限改成 100");
        PatchOne(harmony, typeof(ArchitectCardLimitPatch), "出牌上限：无参数 CanPlay");
        PatchOne(harmony, typeof(ArchitectCardLimitReasonPatch), "出牌上限：带原因的 CanPlay（UI）");

        // 无木桩形态（手速模式默认形态，见 BarePvpCombat.cs）：
        //   ① 怪物一律不进战斗；② 敌方侧没有单位时自己接管胜负判定。
        // 这两条是从「PVP 决斗」那边搬过来的 —— 有木桩时它们整套让路，
        // 所以旧形态（设置里打开「保留木桩」）不受影响。
        PatchOne(harmony, typeof(BareCombatStripMonstersPatch), "无木桩形态：拦下怪物");
        PatchOne(harmony, typeof(BareCombatKeepAlivePatch), "无木桩形态：接管胜负判定");

        // 旧的 PvpEnemyListPatch（出牌瞬间把队友塞进真实 Enemies）已删除：它会污染真实敌人列表，
        // 所有读 Enemies 的第三方代码都会看到「伪装成敌人的玩家」。现在改用 BetrayalCombatView。

        // 退出路径（2026-10-28 修「放弃本局/回主菜单黑屏」）：
        //   A 档 · RunManager.CleanUp 前把队伍视图全拆了
        //   B 档 · CombatState.RemoveCreature 前按生物还原真 state
        // 详细因果见交接文档末节与 BetrayalCombatView.cs 的 RestoreFor 注释。
        PatchOne(harmony, typeof(BetrayalRunCleanupUninstallPatch), "退出前拆队伍视图（CleanUp）");
        PatchOne(harmony, typeof(BetrayalRemoveCreatureViewPatch), "移除生物前还原真 CombatState");
    }

    /// <summary>C 组：存档保护。让"别人的存档"和"我们自己的脏数据"都炸不到游戏。</summary>
    /// <remarks>实现在 <c>ArchitectSaveGuards.cs</c> —— 那一组和 PvP 无关，单独一个文件更好找。</remarks>
    private static void ApplySaveAndProgressGuards(Harmony harmony) => ArchitectSaveGuards.Apply(harmony);

    /// <summary>
    /// D 组：火堆与「偷窃」。
    /// 和背叛事件无关，只是复用了这个补丁宿主（Entry 里就是调本类的 Apply）。
    /// </summary>
    private static void ApplyRestSiteAndStealPatches(Harmony harmony)
    {
        PatchOne(harmony, typeof(RestSiteOptionsPagerPatch), "火堆选项分页（超过 3 个 → “…”）");

        PatchOne(harmony, typeof(StealCardVerdictPatch), "偷窃：所有人选完后判定结算");
    }

    /// <summary>装一个补丁类（<see cref="Harmony.CreateClassProcessor" /> 只处理带特性的类）。</summary>
    /// <remarks>
    /// 单独 try/catch 是刻意的：任何一个补丁装不上（游戏版本改了方法签名）都不能拖累其它补丁，
    /// 只留一条日志、其它补丁照装。给 internal 是因为 <c>ArchitectSaveGuards</c> 也走这个入口。
    /// </remarks>
    internal static void PatchOne(Harmony harmony, Type type, string label)
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

            // 「建筑师的挑拨」在友尽尖塔和任务模式下都要生效。
            if (!YouJinSettings.ArchitectEventEnabled)
                return;

            // ★ 只在多人局出现：这是一场"投票 → PvP"的收尾玩法，单人没有对手，
            //   选「战斗」会开场即结束（只剩 1 人活着 → 清木桩 → 战斗结束）。
            //   单人保持原版建筑师（和另外三个事件的口径一致：都要求 Players.Count > 1）。
            //   ⚠ 控制台 `event YOUJIN_EVENT_ARCHITECT_BETRAYAL_EVENT` 仍然可以手动跳进来（调试用）。
            var run = RunManager.Instance?.DebugOnlyGetState();
            if (run is null || run.Players.Count <= 1)
                return;

            // 胜利房间的第一站是「建筑师的挑拨」（计分 + 一场手速混战），
            // 挑拨打完再由它自己把房间切给「战战战！！！」。
            var type = typeof(ArchitectProvokeEvent);
            var canonical = ModelDb.GetByIdOrNull<EventModel>(ModelDb.GetId(type));

            // 这里只做查询，绝不能 ModelDb.Inject —— RitsuLib 的自动发现已经把这个
            // 类型登记过了，再 Inject 会让同一个 ID 被 new 两次，AbstractModel 的
            // 构造函数会抛 DuplicateModelException，导致整个游戏启动失败。
            if (canonical is null)
            {
                Entry.Logger.Info("建筑师挑拨：事件还没注册进 ModelDb，保持原版建筑师");
                return;
            }

            __0 = canonical;
            Entry.Logger.Info("建筑师挑拨：胜利房间已替换为挑拨事件");
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

        // 胜利房间里现在可能站两个我们自己造的事件：挑拨（第一站）与战战战（第二站）；
        // 老事件留着只为控制台手动调试，也算胜利房间。
        if (__instance is EventRoom room
            && (room.CanonicalEvent is ArchitectProvokeEvent
                || room.CanonicalEvent is BattleBattleBattleEvent
                || room.CanonicalEvent is ArchitectBetrayalEvent))
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
///
/// 【为什么要按 OwnerId 而不是只认 PlayCardAction】
/// 充能球的**回合结束被动**（闪电/暗球/玻璃球）不是靠"出牌"触发的，
/// 那时候正在跑的动作是 EndPlayerTurnAction —— 它不是 PlayCardAction，
/// 早先那版只会得到 caster = null，于是"谁都不排除"，这份列表里就只有木桩、
/// 没有队友可选（木桩又被判定成不可打击）→ 表现成"球空转 / 球瞄木桩"。
/// 改成和 ⑪ 一样按 action.OwnerId 认人，就能覆盖"结束回合"这条路。
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

            // 正在执行的那个动作是谁发的，就把他找出来，免得他打到自己。
            // （用 OwnerId 而不是只认 PlayCardAction，理由见上面那段注释。）
            Player? caster = null;
            try
            {
                var action = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
                if (action is not null)
                    caster = __instance.Players.FirstOrDefault(player => player.NetId == action.OwnerId);
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
    /// <summary>距右边缘至少留的像素（免得整只贴边）。</summary>
    private const float RightMargin = 40f;

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

            var roomRight = room.GlobalPosition.X + roomWidth;

            var moved = 0;

            foreach (var node in __0)
            {
                if (node is null)
                    continue;

                // ★ 顺手取消它的"判定框"（见方法说明），每次重排布都重新压一遍 ——
                //   节点重建/重排时 MouseFilter 有可能被原版改回去。
                DisableHitboxInput(node);

                var before = node.GlobalPosition;

                // 按**缩放后的立绘半宽**决定能推到多右：
                //   x = 右边缘 - 边距 - 半宽   → 立绘右边缘正好贴着屏幕，不会被裁掉，
                //   同时尽可能靠右（建筑师是 Boss 体型，只看固定边距会推出屏）。
                var halfWidth = EstimateHalfWidth(node);
                // 万一估出来的宽度离谱（Boss 很大），最多按房间宽度的 35% 收，别把它推回中间。
                halfWidth = Mathf.Clamp(halfWidth, 0f, roomWidth * 0.35f);

                var rightEdge = roomRight - RightMargin - halfWidth;

                if (before.X >= rightEdge)
                    continue;

                node.GlobalPosition = new Vector2(rightEdge, before.Y);
                moved++;

                Entry.Logger.Info(
                    $"建筑师的背叛：木桩挪到最右边 {before.X:F0} → {rightEdge:F0}"
                    + $"（房间宽 {roomWidth:F0}，半宽 {halfWidth:F0}）");
            }

            if (moved > 0)
                Entry.Logger.Info($"建筑师的背叛：共 {moved} 只木桩已贴到右侧");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：移动木桩位置失败 {ex}");
        }
    }

    /// <summary>估算立绘**缩放后**的半宽（拿不到就返回 0，退化成只按边距摆）。</summary>
    private static float EstimateHalfWidth(NCreature node)
    {
        try
        {
            if (node.Visuals is not { } visuals || !GodotObject.IsInstanceValid(visuals))
                return 0f;

            if (visuals.Bounds is not { } bounds || !GodotObject.IsInstanceValid(bounds))
                return 0f;

            var scaleX = Mathf.Abs(visuals.Scale.X);
            return bounds.Size.X * 0.5f * scaleX;
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>
    /// 取消判定框：让鼠标**穿透**这个单位。
    ///
    /// ⚠ 只改 `Hitbox.MouseFilter`，**不动它的尺寸** ——
    ///   `GetTopOfHitbox()` / `GetBottomOfHitbox()` 会被特效、飘字之类的地方用来定位，
    ///   把 Size 清零会连带把这些搞坏。而且我们本来就已经在
    ///   `ArchitectBetrayalDummyPatch` 里把卡牌/药水/光标的指定全拦掉了，
    ///   这里只是让鼠标连 hover 都碰不到它。
    /// </summary>
    private static void DisableHitboxInput(NCreature node)
    {
        try
        {
            if (node.Hitbox is { } hitbox && GodotObject.IsInstanceValid(hitbox))
                hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：取消木桩判定框失败（忽略）{ex}");
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
    /// <summary>
    /// 队友移到对面后，**往中线收多少**。
    ///   1.0 = 完整镜像（贴最右，会和 Boss 体型的建筑师撞在一起）
    ///   0.0 = 全部挤在中线上
    /// 取 0.72：比原来往左收了一截，给最右边的建筑师让出空间。
    /// </summary>
    private const float TeammateCompression = 0.72f;

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

                // 完整镜像：x' = 2*centerX - x
                // 再按 TeammateCompression 往中线收：x'' = centerX + (x' - centerX) * c
                var mirrored = centerX * 2f - before.X;
                var targetX = centerX + (mirrored - centerX) * TeammateCompression;

                node.GlobalPosition = new Vector2(targetX, before.Y);

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
                    + $"{before.X:F0} → {targetX:F0}（中线 {centerX:F0}，收拢 {TeammateCompression:P0}）");
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
/// ⑫ 玩家对决里"眩晕玩家"改成"让该玩家结束回合"。
/// ----------------------------------------------------------------------------
/// 起因：原版「吹哨」(Models.Cards.Whistle) 的效果是"造成伤害，然后眩晕目标"，
/// 而眩晕的唯一入口 CreatureCmd.Stun → Creature.StunInternal 第一句就是
///     if (Monster is null) throw new InvalidOperationException("Can't stun a player.");
/// 玩家没有 Monster —— 所以自从我们放开"队友可以当目标"之后，吹哨打队友必定抛异常：
/// 伤害已经打进去了、眩晕这句一炸，卡牌 OnPlay 整段中断，后面的收尾全没了。
///
/// 现在改成：背叛战斗里只要有人要眩晕**玩家**，就把这次眩晕换成
///     PlayerCmd.EndTurn(player, false, null)
/// 也就是"这名玩家立刻结束回合"。这个调法是照抄原版的 ——
/// CreatureCmd.Kill 里对一个死掉的玩家就是这么干的
/// （`PlayerCmd.EndTurn(deadPlayer, false, null)`，见 sts2.dll 的 &lt;Kill&gt;d__14）。
///
/// 两个细节：
///   · Prefix 必须自己把 __result 设成 Task.CompletedTask。原方法返回 Task，
///     跳过原版就是返回 null，而调用方（吹哨的 OnPlay）立刻就会 GetAwaiter()，
///     少这一句就从"抛 Can't stun a player"变成"抛 NullReferenceException"。
///   · 只在 BetrayalCombatActive 时接管；其它场合一律维持原版行为。
/// </summary>
[HarmonyPatch]
internal static class TeammateStunEndsTurnPatch
{
    /// <summary>CreatureCmd 里所有叫 Stun 的重载（原版两个）。</summary>
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (string.Equals(method.Name, nameof(CreatureCmd.Stun), StringComparison.Ordinal))
                yield return method;
        }
    }

    /// <summary>参数用 __0（位置参数），免得猜原方法第一个参数叫什么。</summary>
    private static bool Prefix(Creature __0, ref Task __result)
    {
        try
        {
            if (!ArchitectBetrayalEvent.BetrayalCombatActive)
                return true;

            var player = __0?.Player;
            if (player is null)
                return true; // 真的眩晕怪物 —— 走原版

            // 照抄原版「虚空形态」那张牌的做法（Cards.VoidForm.OnPlay 的最后一句）：
            //     await PowerCmd.Apply<VoidFormPower>(...);
            //     PlayerCmd.EndTurn(card.Owner, false, null);      // ← 就是 false
            // 它就是"打完这张牌直接结束出牌者回合"的，所以眩晕队友也走同一个调用。
            //
            // 那个 bool 的去向核对过：PlayerCmd.EndTurn → SetReadyToEndTurn(玩家, bool, Func)
            //   → 触发 PlayerEndedTurn(玩家, bool)，而唯一的消费点
            //     NEndTurnButton.AfterPlayerEndedTurn 里那个分支
            //     （flag = (还没全员结束 ? 我还能行动 : false) && bool）**只对本地玩家生效**，
            //   对"被眩晕的队友"没有任何影响 —— 两种写法都会真的结束回合。
            //   （对比：结束回合按钮自己走 EndPlayerTurnAction → EndTurn(player, true, null)。）
            var state = player.PlayerCombatState;
            if (state is null)
                return true;

            // 战斗已经在收尾（敌方全灭那种）：结束回合没意义，而且**绝不能**放回原版
            // —— 原版对玩家会抛 "Can't stun a player"。所以这里直接吞掉。
            if (CombatManager.Instance?.IsEnding == true)
            {
                __result = Task.CompletedTask;
                Entry.Logger.Info(
                    $"建筑师的背叛：战斗收尾中，「眩晕玩家」直接忽略（NetId={player.NetId}）");
                return false;
            }

            var alreadyEnded = CombatManager.Instance?.IsPlayerReadyToEndTurn(player) ?? false;
            PlayerCmd.EndTurn(player, false, null);
            __result = Task.CompletedTask;

            Entry.Logger.Info(
                $"建筑师的背叛：把「眩晕玩家」改成「该玩家结束回合」（NetId={player.NetId}，"
                + $"回合 {state.TurnNumber}，之前已结束过={alreadyEnded}）");

            return false;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"建筑师的背叛：眩晕改结束回合失败（按原版处理）{ex}");
            return true;
        }
    }
}
