using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace PvpDuel.Core.SplitMode;

/// <summary>在地图上把对端画出来：一个小标记贴着「对端所在的那个节点」。</summary>
/// <remarks>
/// <para>
/// 位置不自己算：<c>NMapScreen</c> 里有一张 <c>MapCoord → NMapPoint</c> 的表（<c>_mapPointDictionary</c>），
/// 拿到那个节点之后把标记<b>挂成它的子节点</b>即可——地图滚动、缩放、重画都自动跟着走，不用碰任何坐标换算。
/// </para>
/// <para>纯本机表现：不参与判定，开关一关就整块不显示。</para>
/// </remarks>
internal static class MapPeerMarker
{
    private static readonly AccessTools.FieldRef<NMapScreen, Dictionary<MapCoord, NMapPoint>>? MapPointsField =
        AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary") is null
            ? null
            : AccessTools.FieldRefAccess<NMapScreen, Dictionary<MapCoord, NMapPoint>>("_mapPointDictionary");

    /// <summary>标记相对所在节点的偏移（往右上挪一点，免得压住节点本身）。</summary>
    private static readonly Vector2 Offset = new(26f, -24f);

    private static Label? _marker;
    private static Node? _parent;
    private static bool? _lastMeeting;

    /// <summary>由每帧的泵调用（幂等：地图没开 / 开关关着 / 拿不到坐标就收起来）。</summary>
    public static void Pump()
    {
        if (!ModuleSwitches.SplitIcon || MapPointsField is null)
        {
            Hide();
            return;
        }

        if (!TryGetPeerOnThisMap(out var peerNetId, out var payload, out var coord))
        {
            Hide();
            return;
        }

        if (NMapScreen.Instance is not { } screen || !screen.IsOpen)
        {
            Hide();
            return;
        }

        if (!MapPointsField(screen).TryGetValue(coord, out var pointNode) || pointNode is null)
        {
            // 对端在另一幕（或者地图还没建好）：先不画。
            Hide();
            return;
        }

        var marker = EnsureMarker(pointNode);
        var meeting = PresenceLedger.TryFindMeeting(out _, out _);

        if (_lastMeeting != meeting)
        {
            _lastMeeting = meeting;
            ModInfo.Info(meeting
                ? "[SplitMode] 地图标记：对端就在同一节点（相遇）"
                : "[SplitMode] 地图标记：对端在别的节点");
        }

        marker.Visible = true;
        marker.Text = meeting
            ? $"⚔ 相遇 {payload.HpPercent}%"
            : $"◆ 对端 {payload.HpPercent}%";
        marker.AddThemeColorOverride("font_color", meeting
            ? new Color(1f, 0.85f, 0.35f)
            : new Color(0.55f, 0.9f, 1f));
        marker.TooltipText = meeting
            ? $"对端 {peerNetId} 正在同一节点"
            : $"对端 {peerNetId} 在这个节点";
    }

    /// <summary>对端坐标（必须和本机在同一幕，否则那张图上没有这个点）。</summary>
    private static bool TryGetPeerOnThisMap(
        out ulong peerNetId,
        out SplitPresencePayload payload,
        out MapCoord coord)
    {
        peerNetId = 0;
        payload = default;
        coord = default;

        if (RunManager.Instance?.DebugOnlyGetState() is not { } state
            || !PresenceLedger.TryGetFirstPeer(out peerNetId, out payload)
            || !payload.HasCoord
            || payload.ActIndex != state.CurrentActIndex)
        {
            return false;
        }

        coord = new MapCoord(payload.MapColumn, payload.MapRow);

        return true;
    }

    private static Label EnsureMarker(Node parent)
    {
        if (_marker is not null && GodotObject.IsInstanceValid(_marker) && ReferenceEquals(_parent, parent))
        {
            return _marker;
        }

        Hide();

        _marker = new Label
        {
            Name = "PvpDuelPeerMarker",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 50,
            Position = Offset,
        };

        _marker.AddThemeFontSizeOverride("font_size", 14);
        _marker.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        _marker.AddThemeConstantOverride("outline_size", 4);

        parent.AddChildSafely(_marker);
        _parent = parent;

        ModInfo.LogOnce("地图标记：已把对端标记挂到地图节点上");

        return _marker;
    }

    private static void Hide()
    {
        if (_marker is not null && GodotObject.IsInstanceValid(_marker))
        {
            _marker.QueueFree();
        }

        _marker = null;
        _parent = null;
    }
}
