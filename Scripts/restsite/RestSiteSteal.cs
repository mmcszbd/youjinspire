// ============================================================================
// 友尽尖塔（youjin）：火堆「假寐」
// ----------------------------------------------------------------------------
// 规则：
//   1. 多人局里，火堆选项里一开始就有「假寐」，**一直是亮的，随时能点**；
//   2. 点它进入"选人"状态（和原版「治愈」给队友回血完全同一套交互）：
//      鼠标点队友角色来选定目标；
//   3. 选定后：偷走该队友 30% 的金币，同时自己回复 25% 生命上限；
//      （贪婪模式开着时"获得金币"+20%，所以小偷实际到手 36%，被偷的人只掉 30%）
//   4. 取消选择就什么也不发生（原版也是这个行为，这次火堆行动算跳过）；
//   5. 单人局不出现这个选项。
//
// 反编译踩点（全部确认过，照着原版 MendRestSiteOption 抄的）：
//   · 选项在 RestSiteSynchronizer.BeginRestSite() → RestSiteOption.Generate(player)
//     → Hook.ModifyRestSiteOptions → 模型的 TryModifyRestSiteOptions(player, options) 里加入。
//   · "选队友"用的是通用指向选择器 NTargetManager：
//       StartTargeting(TargetType.AnyPlayer, 起点坐标, TargetMode.ClickMouseToTarget,
//                      Func<bool> 是否取消, Func<Node,bool> 哪些节点可指)
//       await SelectionFinished() → 返回被点中的节点
//     这个选择要在所有客户端同步：先 ReserveChoiceId，本机玩家选完 SyncLocalChoice，
//     所有客户端再 WaitForRemoteChoice 拿结果（Mend 就是这么做的）。
//   · 只能指队友：AllowHoveringNode 里把 Owner 自己过滤掉。
//   · 节点 → 玩家：NRestSiteCharacter / NMultiplayerPlayerState 都带 Player 属性。
//   · 取消时 Mend 会 Sync 一个"空结果"（PlayerChoiceResult.FromPlayerId(null)），
//     这样远端不会一直等；然后 OnSelect 返回 false。
//   · 文本运行时注入 rest_site_ui 表，不用重新导出 PCK；
//     图标先用占位符（把原版金币贴图登记到我们选项本该用的图标路径下）。
// ============================================================================

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace YouJin.Scripts;

/// <summary>火堆「假寐」的逻辑。订阅一局游戏级钩子（HookType.Run）。</summary>
[RegisterSingleton]
public class NapRestSiteModel : HookedSingletonModel
{
    /// <summary>
    /// 偷取比例：目标身上金币的 30%（向下取整，不足 1 金币就当没钱）。
    /// 注意：贪婪模式开启时"获得金币"整体 +20%（见 CombatLootSplitter.ModifyGoldGained），
    /// 所以小偷实际到手是 30% × 1.2 = 36%，而被偷的人只掉 30%。
    /// </summary>
    public const double StealPercent = 0.30;

    /// <summary>回复比例（生命上限的 25%）。</summary>
    public const double HealPercent = 0.25;

    /// <summary>选项 id；本地化键是 OPTION_{OptionId}.name / .description。</summary>
    public const string OptionId = "YOUJIN_NAP";

    private const string LocTableName = "rest_site_ui";

    public static NapRestSiteModel? Instance { get; private set; }

    /// <summary>本次火堆里已经假寐过的玩家，防止重复结算。</summary>
    private readonly HashSet<ulong> _napped = new();

    public NapRestSiteModel() : base(HookType.Run)
    {
        Instance = this;
        EnsureLocalized();
    }

    /// <summary>换火堆就清一次记录。</summary>
    public override Task AfterRoomEntered(AbstractRoom room)
    {
        if (room is RestSiteRoom)
            _napped.Clear();

        return Task.CompletedTask;
    }

    /// <summary>火堆选项生成时把「假寐」塞进去（多人局才加，加入时就是可点的）。</summary>
    public override bool TryModifyRestSiteOptions(Player player, ICollection<RestSiteOption> options)
        => YouJinRestSiteOptions.TryAdd(player, options, OptionId, "假寐", () =>
        {
            EnsureLocalized();
            options.Add(new NapRestSiteOption(player));
        });

    /// <summary>
    /// 假寐结算：偷走选定队友 30% 的金币，再回复自身 25% 生命上限。
    /// 所有客户端都会跑同样的逻辑，纯确定，不会不同步。
    /// </summary>
    public async Task NapAsync(Player self, Player target)
    {
        var run = CurrentRunState ?? self.RunState;
        if (run is null)
            return;

        // 安全网：同一次火堆里同一个人只结算一次
        if (!_napped.Add(self.NetId))
            return;

        if (target.NetId != self.NetId)
        {
            var amount = (int)Math.Floor(target.Gold * StealPercent);
            if (amount > 0)
            {
                await PlayerCmd.LoseGold(amount, target, GoldLossType.Stolen);
                await PlayerCmd.GainGold(amount, self, false);
                Entry.Logger.Info($"假寐：NetId={self.NetId} 从 NetId={target.NetId} 偷走 {amount} 金币");
            }
            else
            {
                Entry.Logger.Info($"假寐：NetId={target.NetId} 身上没钱，只回血");
            }
        }

        var maxHp = self.Creature?.MaxHp ?? 0;
        var heal = (int)Math.Floor(maxHp * HealPercent);
        if (heal > 0 && self.Creature is not null)
        {
            await CreatureCmd.Heal(self.Creature, heal, true);
            Entry.Logger.Info(
                $"假寐：NetId={self.NetId} 回复 {heal} 点生命（上限 {maxHp} 的 {HealPercent * 100:0}%）");
        }
    }

    /// <summary>
    /// 用原版那套指向选择来选一名队友。选不到（取消 / 没法选）就返回 null。
    /// 流程和 MendRestSiteOption.OnSelect 一致：所有人都在这个协程里参与同步。
    /// </summary>
    public static Task<Player?> ChooseTargetPlayerAsync(Player owner, RestSiteOption option)
        => TeammatePicker.ChooseSyncedAsync(
            owner,
            TeammatePicker.OriginFor(NRestSiteRoom.Instance, option),
            focusController: true,
            room: NRestSiteRoom.Instance,
            tag: "假寐");

    /// <summary>把「假寐」的名字和描述注入原版 rest_site_ui 表（当前语言）。</summary>
    private static void EnsureLocalized()
    {
            var percent = (int)(HealPercent * 100);
            var stealPercent = (int)(StealPercent * 100);
        LocInject.Merge(LocTableName, $"OPTION_{OptionId}.name", chinese => new Dictionary<string, string>
        {
            [$"OPTION_{OptionId}.name"] = chinese ? "假寐" : "Catnap",
            [$"OPTION_{OptionId}.description"] = chinese
                ? $"点击一名队友：偷走他 [blue]{stealPercent}%[/blue] 的金币，"
                  + $"并回复自身 [blue]{percent}%[/blue] 生命上限。"
                : $"Pick a teammate: steal {stealPercent}% of their gold and heal "
                  + $"{percent}% of your max HP.",
        }, "假寐");
    }
}

/// <summary>火堆里的「假寐」按钮。</summary>
public class NapRestSiteOption : YouJinRestSiteOption
{
    public NapRestSiteOption(Player owner) : base(owner)
    {
    }

    public override string OptionId => NapRestSiteModel.OptionId;

    protected override async Task<bool> ActivateAsync()
    {
        var model = NapRestSiteModel.Instance;
        if (model is null)
            return false;

        var target = await NapRestSiteModel.ChooseTargetPlayerAsync(Owner, this);
        if (target is null)
            return false;

        await model.NapAsync(Owner, target);
        return true;
    }
}
