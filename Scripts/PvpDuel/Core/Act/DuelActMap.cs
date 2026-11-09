using MegaCrit.Sts2.Core.Map;

namespace PvpDuel.Core.Act;

/// <summary>决斗幕的地图：只有初始节点（先古房）和 BOSS 节点 —— 两个点进去都是「对决邀请」。</summary>
/// <remarks>
/// <para>
/// <b>起始节点绝不能放进网格</b>：<c>NMapScreen.SetMap</c> 会先在网格循环里画一遍边、
/// 再另外拿起始节点画一遍（<c>DrawPaths(_startingPointNode, map.StartingMapPoint)</c>），
/// 放进网格就会把同一条边加两次，<c>_paths.Add</c> 当场抛
/// <c>ArgumentException: An item with the same key has already been added</c>，整幕进不去。
/// 本体也是把起始点摆在网格外的 row 0。
/// </para>
/// <para>
/// <b>网格里一行房间都不放</b>：地图上就只剩「初始 + BOSS」两个点。网格高度只用来调地图长度，
/// 取多少见 <see cref="Rows" />（行数太少反而会把地图拉长，因为间距是 <c>2325 / (行数 - 1)</c>）。
/// </para>
/// <para>
/// 起始节点的类型照本体惯例标 Ancient（本体每一幕第一个节点都是先古房）；点哪个房间都不是问题，
/// 因为决斗幕里的房间会被 <c>DuelActCreateRoomPatch</c> 统一换成「对决邀请」。
/// </para>
/// </remarks>
internal sealed class DuelActMap : ActMap
{
    private const int Columns = 7;

    /// <summary>起始点 / BOSS 点所在的列（本体也是 GetColumnCount() / 2）。</summary>
    private const int CenterColumn = 3;

    /// <summary>
    /// 网格高度（只影响地图的"长度"，因为我们一行房间都不放）。
    /// </summary>
    /// <remarks>
    /// 本体的地图尺寸是写死的：起始节点固定 <c>y = 720</c>、BOSS 节点固定 <c>y = -1980</c>、
    /// 每行间距 <c>_distY = 2325 / (行数 - 1)</c>。
    /// 行数太少反而把地图拉长：2 行时 <c>_distY = 2325</c>，从初始滚到 BOSS 要 <c>2 × 2325 = 4650</c> 像素；
    /// 取本体一幕的行数（13 个房间 + 1 = 14）时 <c>_distY = 179</c>，滚动距离降到 <c>14 × 179 ≈ 2500</c>，
    /// 正好是本体一幕的高度，也就是<b>在不改 UI 的前提下能到的下限</b>
    /// （再短就得去改 <c>NMapScreen</c> 里写死的 BOSS 坐标，那属于表现层 hack）。
    /// 下限 2 行是 <c>_distY</c> 公式本身的要求（1 行会除以 0）。
    /// </remarks>
    private const int Rows = 14;

    /// <summary>
    /// BOSS 节点的行号：就摆在初始节点上面一格。
    /// </summary>
    /// <remarks>
    /// 本体把 BOSS 当成"最后一排"（<c>new MapPoint(col, GetRowCount())</c>），于是摄像机要滚满一整幕才能到它。
    /// 决斗幕没有房间，把 BOSS 放在最后一排只会让地图看起来又长又空 —— 行号决定摄像机滚多远，
    /// 所以这里只给它一格（配合 <c>DuelActMapUiPatch</c> 把它的坐标也挪到初始节点上方）。
    /// </remarks>
    private const int BossRow = 1;

    private readonly MapPoint?[,] _grid = new MapPoint?[Columns, Rows];

    public DuelActMap(bool hasSecondBoss)
    {
        // 起始节点标成「问号房」而不是本体惯例的 Ancient：这一幕没有先古之民，
        // 标成 Ancient 会让人以为这里有个先古房（而且万一哪天绕过了 CreateRoom 那层替换，
        // Ancient 会走去 PullAncient()，幕里没有先古就直接抛）。问号房最稳：图标是问号，
        // 真被绕过去也只会按普通房间抽一个，不会碰到先古。
        StartingMapPoint = new MapPoint(CenterColumn, 0)
        {
            PointType = MapPointType.Unknown,
            CanBeModified = false,
        };

        // BOSS 摆在网格外（网格一行房间都不放），行号见 BossRow。
        BossMapPoint = new MapPoint(CenterColumn, BossRow)
        {
            PointType = MapPointType.Boss,
            CanBeModified = false,
        };

        // 没有房间行，所以初始节点直接连 BOSS：进完初始房后 BOSS 就是唯一可去的点。
        StartingMapPoint.AddChildPoint(BossMapPoint);

        if (hasSecondBoss)
        {
            SecondBossMapPoint = new MapPoint(CenterColumn, BossRow + 1)
            {
                PointType = MapPointType.Boss,
                CanBeModified = false,
            };

            BossMapPoint.AddChildPoint(SecondBossMapPoint);
        }
    }

    public override MapPoint BossMapPoint { get; }

    public override MapPoint StartingMapPoint { get; }

    public override MapPoint? SecondBossMapPoint { get; }

    protected override MapPoint?[,] Grid => _grid;
}
