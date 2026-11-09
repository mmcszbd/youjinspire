using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace PvpDuel.Core.Act;

/// <summary>决斗幕的地图收成一小块：把 BOSS 节点挪到初始节点正上方，并把连线重画。</summary>
/// <remarks>
/// <para>
/// 本体 <c>NMapScreen.SetMap</c> 里 BOSS 的坐标是<b>写死</b>的（<c>y = -1980</c>），起始节点在 <c>y = 720</c>，
/// 两者相距 2700px（正好一幕的高度）；中间那一段靠"每行 <c>2325 / (行数 - 1)</c>"铺房间。
/// 决斗幕没有房间，所以光改行数只能让<b>滚动步长</b>变小、改不动那两个点相隔多远 ——
/// 地图看上去依旧又长又空。
/// </para>
/// <para>
/// 这里在本体摆完位、画完线之后，把 BOSS 挪到初始节点上方一点，并把原来那条线删掉按新坐标重画
/// （<c>_paths</c> 里的旧箭头必须先摘掉，否则 <c>DrawPaths</c> 里那次 <c>_paths.Add</c> 会重键报错 ——
/// 之前进不了决斗幕就是踩了这个坑）。只动表现，不碰任何对局数据。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal static class DuelActMapUiPatch
{
    /// <summary>BOSS 相对初始节点的偏移（往上挪一点，两个点就落在同一屏里）。</summary>
    private static readonly Vector2 BossOffset = new(-120f, -140f);

    private static readonly AccessTools.FieldRef<NMapScreen, NMapPoint?> StartingNodeField =
        AccessTools.FieldRefAccess<NMapScreen, NMapPoint?>("_startingPointNode");

    private static readonly AccessTools.FieldRef<NMapScreen, NMapPoint?> BossNodeField =
        AccessTools.FieldRefAccess<NMapScreen, NMapPoint?>("_bossPointNode");

    private static readonly AccessTools.FieldRef<NMapScreen, Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>>>
        PathsField =
            AccessTools.FieldRefAccess<NMapScreen, Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>>>("_paths");

    private static readonly System.Reflection.MethodInfo? DrawPathsMethod =
        AccessTools.Method(typeof(NMapScreen), "DrawPaths");

    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance, ActMap map)
    {
        if (map is not DuelActMap || DrawPathsMethod is null)
        {
            return;
        }

        if (StartingNodeField(__instance) is not { } start || BossNodeField(__instance) is not { } boss)
        {
            return;
        }

        boss.Position = start.Position + BossOffset;

        // 旧连线是按旧坐标画的：先摘掉（连同已生成的箭头贴图），再按新坐标重画一次。
        var key = (start.Point.coord, boss.Point.coord);

        if (PathsField(__instance).Remove(key, out var stale))
        {
            foreach (var dot in stale)
            {
                dot.QueueFree();
            }
        }

        DrawPathsMethod.Invoke(__instance, [start, start.Point]);
    }
}
