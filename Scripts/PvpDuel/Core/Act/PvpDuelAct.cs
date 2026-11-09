using Godot;

using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Unlocks;

using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using PvpDuel.Core.Entry;
using YouJin.Scripts;

namespace PvpDuel.Core.Act;

/// <summary>「PVP 决斗」额外幕：三幕打完之后追加到本局末尾，内容就是「对决邀请」。</summary>
/// <remarks>
/// 做成额外幕而不是替换本体的结局事件：<c>EnterNextAct</c> 看到 <c>CurrentActIndex &lt; Acts.Count - 1</c>
/// 就自然走 <c>EnterAct</c>，于是三幕打完不再是结局，而是进入这一幕。
/// 美术全部借本体第三幕（见 <see cref="AssetProfile" />），本 mod 不需要出任何关卡图。
/// </remarks>
[RegisterAct]
public sealed class PvpDuelAct : ModActTemplate
{
    private const string VanillaActId = "glory";

    /// <summary>背景、图层、地图底图、休息处、宝箱 Spine 统统指向本体第三幕的目录。</summary>
    public override ActAssetProfile AssetProfile => ContentAssetProfiles.FromVanillaActId(VanillaActId);

    /// <summary>负数 = 永不参与原版随机章节表（本体 ActModel.Index 的约定），只由我们手动追加。</summary>
    public override int Index => -1;

    public override bool IsDefault => false;

    public override bool IsUnlocked(UnlockState unlockState) => true;

    public override string ChestOpenSfx => "event:/sfx/ui/treasure/treasure_act3";

    public override string[] BgMusicOptions => ["event:/music/act3_a1_v1", "event:/music/act3_a2_v1"];

    public override string[] MusicBankPaths => ["res://banks/desktop/act3_a1.bank", "res://banks/desktop/act3_a2.bank"];

    public override string AmbientSfx => "event:/sfx/ambience/act3_ambience";

    public override string ChestSpineSkinNameNormal => "act3";

    public override string ChestSpineSkinNameStroke => "act3_stroke";

    public override Color MapTraveledColor => new("1D1E2F");

    public override Color MapUntraveledColor => new("60717C");

    public override Color MapBgColor => new("819A97");

    // ==================== 内容 ====================

    /// <summary>这一幕没有普通房间，不需要弱怪。</summary>
    protected override int NumberOfWeakEncounters => 0;

    /// <summary>房间数：只是兜底，真正用的地图是 <see cref="DuelActMap" />（只有初始节点和 BOSS 节点）。</summary>
    /// <remarks>
    /// 给 7 而不是 1：<c>StandardActMap</c> / <c>SpoilsActMap</c> 里有 <c>GetRowCount() - 7</c> 这种行号运算，
    /// 行数不足 7 会越界。万一某局带着会把地图整个重造的遗物（SpoilsMap、BigGameHunter），
    /// 给 7 至少能生成出一张合法的普通地图，而不是当场崩。
    /// </remarks>
    protected override int BaseNumberOfRooms => 7;

    /// <summary>这一整幕的事件池只有「对决邀请」。</summary>
    public override IEnumerable<EventModel> AllEvents => [ModelDb.Event<BattleBattleBattleEvent>()];

    /// <summary>
    /// 这一幕的先古之民：借用第三幕那三个。
    /// </summary>
    /// <remarks>
    /// ⚠ 以前这里是空池 <c>[]</c>，当时的理由写的是「空集 → <c>_rooms.Ancient</c> 为 null →
    /// <c>HasAncient</c> 为 false → 不预载先古资源」，看起来安全。
    /// 但当前版本的 <c>RoomSet.Ancient</c> getter 在 <c>_ancient == null</c> 时<b>直接抛异常</b>：
    /// <c>InvalidOperationException("RoomSet.Ancient not set! You must call GenerateRooms")</c>，
    /// 而 <c>ActModel.MapNodeAssetPaths</c> 会读它 —— 于是合流载入决斗局时整局崩：
    /// 客户端断开、主机重开一局（2026-11-02 实测日志里就是这个）。
    /// 所以必须给真实先古，让 <c>GenerateRooms</c> 把 <c>_rooms.Ancient</c> 填上。
    /// </remarks>
    public override IEnumerable<AncientEventModel> AllAncients =>
    [
        ModelDb.AncientEvent<Nonupeipe>(),
        ModelDb.AncientEvent<Tanx>(),
        ModelDb.AncientEvent<Vakuu>(),
    ];

    /// <inheritdoc cref="AllAncients" />
    public override IEnumerable<AncientEventModel> GetUnlockedAncients(UnlockState unlockState) => AllAncients;

    /// <summary>BOSS 沿用第三幕的（玩家在决斗里就分出胜负了，这里只是让地图有合法的 boss 节点）。</summary>
    public override IEnumerable<EncounterModel> BossDiscoveryOrder =>
    [
        ModelDb.Encounter<QueenBoss>(),
        ModelDb.Encounter<TestSubjectBoss>(),
        ModelDb.Encounter<AeonglassBoss>(),
    ];

    /// <summary>遭遇沿用第三幕的一小组：地图上每个房间都会被换成对决事件，留着只是让房间生成有东西可抽。</summary>
    public override IEnumerable<EncounterModel> GenerateAllEncounters() =>
    [
        ModelDb.Encounter<DevotedSculptorWeak>(),
        ModelDb.Encounter<ScrollsOfBitingWeak>(),
        ModelDb.Encounter<TurretOperatorWeak>(),
        ModelDb.Encounter<AxebotsNormal>(),
        ModelDb.Encounter<FrogKnightNormal>(),
        ModelDb.Encounter<GlobeHeadNormal>(),
        ModelDb.Encounter<KnightsElite>(),
        ModelDb.Encounter<MechaKnightElite>(),
        ModelDb.Encounter<SoulNexusElite>(),
        ModelDb.Encounter<QueenBoss>(),
        ModelDb.Encounter<TestSubjectBoss>(),
        ModelDb.Encounter<AeonglassBoss>(),
    ];

    /// <summary>不做「首次遭遇顺序」上的调整（那是给本体前三幕用的教学逻辑）。</summary>
    protected override void ApplyActDiscoveryOrderModifications(UnlockState unlockState)
    {
    }

    /// <summary>房间类型数量：本幕走 <see cref="DuelActMap" /> 自己定点，这张表平时用不到，全给 0 兜底。</summary>
    public override MapPointTypeCounts GetMapPointTypes(Rng mapRng) => new(0, 0) { NumOfElites = 0 };
}
