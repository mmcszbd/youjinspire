# 镜像 PvP · 设计方案

> 分离模式的最终形态：两人各跑自己的单机局，走到同一节点时**不合流**，各自面对一个"对方的镜像"打 PvP。
>
> 最后更新：2026-11-03（合流已删除、镜像阶段 2 前半已落地）

---

## 一、为什么放弃合流

原设计（继承自上游 `pvp_duel`）是「相遇 → 合成存档 → 切会话载入一局新的联机局 → 打完 → 回程」。这条路在实测里崩了三次，根因逐层剥出来是：

| 崩溃 | 现象 | 根因 | 状态 |
|---|---|---|---|
| ① | `RoomSet.Ancient not set! You must call GenerateRooms` | `PvpDuelAct.AllAncients => []`，而 0.111 之后 `RoomSet.Ancient` 不再容忍 null | 已修（借第三幕三个古代事件） |
| ② | `链路=未保活` → `联机传输断开：Quit` | `SplitMerge` 调 `CleanUp()` 触发主动断开，而 `ShouldSuppressDisconnect` 依赖的 `Armed` 被 `ModuleSplitKeepAlive`（默认 false）挡住 | 已修（`SplitKeepAlive => SplitMode` 强制） |
| ③ | 合流成功后 4 秒主机被拉回单机局，客户端留在决斗局 → 掉线 | **我们自己加的测试探针** `if (ModuleSwitches.SplitRebuildTest) { RunRebuilder.Schedule(4.0); }` | 已删 |

但更根本的问题在于：**上游文档自己就把这条路标成了最大未知数。**

> `docs/SPLIT_MODE.md` 第 166-167 行：
> 5. **切会话（L2）**：本地退局 → 主机 `LoadRunLobby` → 客户端 load-join（`RunSessionState.InLoadedLobby`）→ `SetUpSavedMultiplayer`。⚠ **这条是最大的未知数**：要在**已有连接**上驱动 load-join，而不是断线重连（**退路：相遇时干脆重连一次**）。

而且上游的文件表只列到 `DuelSaveBuilder.cs`（合流第二步），`SplitMerge.cs` 根本不在表里 —— 我们工程里的 `SplitMerge` / `RunRebuilder` 是自己补的，**上游也没跑通过**。

### 结论

镜像方案把两件最难的事（切会话、拆局重建）整个删掉，换成两件难但可控的事（回合协议、结果同步）。

- **不需要** `CleanUp` / `LoadRunLobby` / `SetUpSavedMultiplayer`
- **不需要** 合成存档、拆局、回程
- 两端各自留在自己的单机局里，只是同时多开一场战斗

---

## 二、核心思路

```
玩家 A 的单机局                 玩家 B 的单机局
─────────────                 ─────────────
A 打自己的地图                  B 打自己的地图
    ↓ 走到节点 (act1, 3, 5)         ↓ 走到节点 (act1, 3, 5)
    ↓ 相遇检测命中                  ↓ 相遇检测命中
A 进战斗：                      B 进战斗：
  敌人 = B 的镜像                 敌人 = A 的镜像
  血量 = B 的当前血量             血量 = A 的当前血量
    ↓                              ↓
A 的回合：A 算伤害 → 打到自己本机镜像 → 结果发给 B
B 的回合：B 算伤害 → 打到自己本机镜像 → 结果发给 A
    ↓                              ↓
收到对方结果 → 应用到自己的角色身上（掉血/格挡）
    ↓                              ↓
任一方镜像血量归零 → 上报主机 → 主机裁定 → 广播
    ↓                              ↓
各自结算，回到自己的地图继续
```

**关键点：伤害是"各算各的"，不是"同步战斗状态"。**

我打你，是我在这边算"我造成了 12 点伤害"，然后把 `12` 这个数字发给你，你自己扣血。你的遗物、你的能力、你的格挡，全在你本机生效。

---

## 三、设计定稿（D1–D11）

| 编号 | 议题 | 定稿 |
|---|---|---|
| D1 | 权威归属 | **各管自己挨的打，主机只做裁决** |
| D2 | 死亡裁决 | **任一方血量归零 → 上报主机 → 主机校验 → 广播裁定** |
| D3 | 手牌与能量 | **完全各自本地，不同步**（另加"手牌数量 + 能量"纯显示同步） |
| D4 | 遗物与能力 | **不复制，只同步结果**；但把对方能力列表同步过来挂在镜像上**纯展示**（不参与计算） |
| D5 | 出牌反馈 | 同步「牌 id + 目标 + 数值」，本地播通用动画 |
| D6 | 回合交接 | **令牌协议，不设超时**（卡住就卡住，靠 D9 兜底；日志每分钟打一行"等待对方出牌已 N 秒"） |
| D7 | 触发点 | 复用 `SplitMeeting.ShouldTriggerMeeting()` |
| D8 | 状态漂移校验 | 每次 `TurnEnd` 带 `{我的血量, 对方血量, 回合序号}`，不一致**以主机为准强制对齐** |
| D9 | 断线 | 任一方断开 → 本机**立即**结束这场 PvP，判对方胜，回地图 |
| D10 | 结算 | 金币/遗物转移靠**发消息让对方本机自己扣**；输者 500 血木桩**纯本地**加一场 |
| D11 | 人数 | 镜像 PvP **先只支持两人** |

### D1 为什么不让主机算一切

主机算一切 = 每个动作两次往返（客户端上报 → 主机计算 → 回传），手感烂。

主机只做裁决 = 只有"谁死了"这一处需要拍板，其余都是本地即时响应。

### D2 为什么不接受单方宣告

一方血量归零时，如果直接信它，网络抖一下就会误判。必须主机校验两边血量再广播。

### D3 为什么手牌不同步

同步手牌等于同步整个 `CombatState`，那又回到合流那条路上了。只同步**结果**就够。

代价是看不到对方有几张手牌 —— 所以补一个"手牌数量 + 能量"的纯显示同步，成本极低。

### D4 为什么不同步遗物

你身上的"每回合 +2 力量"影响的是**你打出去的伤害**，而你算结果时已经把它算进去了。对方只需要接收"我掉了 12 血"这个数字。

### D6 为什么不做超时

分离模式本来就没法强退，做一个"超时判负"反而会出现"我切出去倒杯水回来就输了"。卡住就卡住，靠 D9（对方断开）兜底。

### D8 为什么必须有对账

这是整个方案唯一可能"越跑越错"的地方。一次丢包就会让两边血量永久错开，而且越打越离谱。每个回合结束对一次账，不一致就强制对齐。

### D10 的取舍

金币/遗物转移必须由对方本机执行（我扣不了你的钱）。理论上可以作弊 —— 单机 mod，接受这个代价。

---

## 四、阶段拆分

| 阶段 | 内容 | 状态 |
|---|---|---|
| **阶段 1** | 镜像战斗骨架 —— 分离期能不能在自己局里开一场战斗 | ✅ 已验证 |
| **阶段 2 前半** | 镜像血量来自对方 | ✅ 已落地待测 |
| **阶段 2 后半** | 回合令牌 + 出牌同步（`MirrorSession` + `TurnEndMsg` + `CardPlayedMsg`） | ⬜ 未开始 |
| **阶段 3** | 结算（死亡裁决、金币/遗物转移、500 血木桩） | ⬜ 未开始 |
| **阶段 4** | 接入 `SplitMeeting.ShouldTriggerMeeting()` | ✅ 已接（提前做了） |

### 阶段 1 验证结果

```
[SplitMode] 镜像战斗：已进战斗｜玩家侧 1 人｜盟友 1｜敌人 2（CORPSE_SLUG 25/25, CORPSE_SLUG 27/27）
```

结论：分离期（本机 `RunState.Players` 只有 1 人）**能**开出战斗，敌人正常入场。核心假设成立。

### 阶段 2 前半实现要点

`Scripts/PvpDuel/Core/SplitMode/Mirror/MirrorCombat.cs`

- 血量来源：`SplitPresencePayload.HpPercent`（分离期唯一同步过的对方状态）
- 换算：`HpBaseline = 100`，`peerHp = clamp(peer.HpPercent, 1, 100) * 100 / 100`
- 只留第一只怪当镜像，其余 `CreatureCmd.Kill(extra, force: true)`
- `CreatureCmd.SetMaxHp(mirror, HpBaseline)` + `CreatureCmd.SetCurrentHp(mirror, peerHp)`

**已知局限**：还是软泥外观，血量是百分比换算（非绝对值）。

---

## 五、关键技术约束（反编译确证）

### 进战斗的唯一干净入口

```csharp
RunManager.EnterRoomDebug(
    RoomType.Monster,                       // 不能传 RoomType.Combat（枚举里没有）
    MapPointType.Unassigned,
    ModelDb.Encounter<CorpseSlugsWeak>().ToMutable(),   // ⚠ 必须 ToMutable()
    showTransition: true)
```

`RunManager.EnterRoomDebug` 在 `D:\sts2_il/MegaCrit.Sts2.Core.Runs/RunManager.cs:1091`，内部 `ClearScreens()` + `ExitCurrentRooms()` + `CombatStateSynchronizer.StartSync()` + 建 `CombatRoom`。

**不能用 `EnterCombatWithoutExitingEvent`** —— 那是 `EventModel` 的实例方法，外部静态类碰不到（见 `Scripts/pvp/PvpCombatKit.cs:58-59`）。

### ⚠ 必须 `.ToMutable()`

`ModelDb.Encounter<T>()` 给的是**规范实例**（canonical，全局共用），而 `CombatRoom` 构造第一句 `encounter.AssertMutable()` 要一份属于本场的可变副本。

不 `.ToMutable()` 会抛：

```
CanonicalModelException: Canonical model of type ...CorpseSlugsWeak used in incorrect place.
  at MegaCrit.Sts2.Core.Models.AbstractModel.AssertMutable()
  at MegaCrit.Sts2.Core.Rooms.CombatRoom..ctor(EncounterModel encounter, IRunState runState)
```

游戏自己也是这么干的 —— `RunManager.cs:952`：
```csharp
new CombatRoom((model as EncounterModel) ?? State.Act.PullNextEncounter(roomType).ToMutable(), State)
```

### `Creature.Side` 是 private

没法把队友真的变成敌人。现有做法是拿原版 `BattlewornDummyEventV1Encounter` 里的 `BattleFriendV1` 当木桩，开战时用 `CreatureCmd.SetMaxHp` / `CreatureCmd.SetCurrentHp` 改血量（两个都不需要 `PlayerChoiceContext`）。

### `BarePvpCombat` 不会拦我们的镜像怪

`Scripts/event/BarePvpCombat.cs:79-103` 的拦怪条件：

```csharp
if (__0 is null || __0.IsPlayer || __0.PetOwner is not null) return true;
if (!PvpBareCombat.Active(__instance)) return true;
```

而 `PvpBareCombat.Active` 要求 `state.Players.Count == 2`，分离期只有 1 人**恒为假** → 不会拦。

`BareCombatKeepAlivePatch` 同理不生效 —— 镜像死了战斗会正常结束，正好是我们要的。

### 其他 API 事实

| 类型 | 位置 | 备注 |
|---|---|---|
| `RoomType` 枚举 | `D:\sts2_il/MegaCrit.Sts2.Core.Rooms/RoomType.cs:32` | `Unassigned, Monster, Elite, Boss, Treasure, Shop, Event, RestSite, Map` |
| `CombatRoom(EncounterModel, IRunState?)` | `D:\sts2_il/MegaCrit.Sts2.Core.Rooms/CombatRoom.cs:80` | |
| `CombatRoom.CombatState { get; }` | 同文件 L41 | |
| `CombatState.Enemies` | `CombatState.cs:66` | `Allies`(61) / `Creatures`(71) / `PlayerCreatures`(76) / `Players`(81) / `HittableEnemies`(143) |
| `SplitPresencePayload` | `Scripts/PvpDuel/Core/SplitMode/Presence/SplitPresenceChannel.cs:16-23` | 字段：`ActIndex, MapColumn, MapRow, HasCoord, Activity, HpPercent, Seq` |
| `PresenceLedger.TryFindMeeting` | `Scripts/PvpDuel/Core/SplitMode/Presence/PresenceLedger.cs:139` | `out ulong peerNetId, out SplitPresencePayload peerPayload` |

---

## 六、已完成的代码改动

### 删除清单（合流整套，共 714 行）

| 文件 | 行数 |
|---|---|
| `Scripts/PvpDuel/Core/SplitMode/Meeting/SplitMerge.cs` | 254 |
| `Scripts/PvpDuel/Core/SplitMode/Meeting/SplitMergeChannel.cs` | 88 |
| `Scripts/PvpDuel/Core/SplitMode/Meeting/DuelSaveBuilder.cs` | 107 |
| `Scripts/PvpDuel/Core/SplitMode/Meeting/SplitSnapshotChannel.cs` | 99 |
| `Scripts/PvpDuel/Core/SplitMode/Rebuild/RunRebuilder.cs` | 166 |

`Rebuild/` 目录一并删除。

### 重写

- `Scripts/PvpDuel/Core/SplitMode/Meeting/SplitMeeting.cs`：238 行 → 约 110 行
  - 保留：`ShouldTriggerMeeting()`、`NoteTriggered()`、`Reset()`、`NodeKey(SplitPresencePayload)`
  - 删除：`Pump` / `TrySendSnapshot` / `OnPeerSnapshot` / `TryBuildDuelSave` / `PeerSnapshot` / `_peerSnapshot` / `_builtDuelSave` / `_localPlayerJson` / `ResendSeconds`

### 新增

- `Scripts/PvpDuel/Core/SplitMode/Mirror/MirrorCombat.cs` —— 镜像战斗本体
- `Scripts/PvpDuel/Core/SplitMode/Mirror/SplitMirrorTrigger.cs` —— 相遇 → 进镜像

### 改枚举

- `Scripts/PvpDuel/Core/SplitMode/SplitStage.cs`：`Off/SoloRun/Meeting/DuelRun/AfterRun` → **`Off/SoloRun/Mirror`**
  - `Describe()` 加 `Mirror => "镜像 PvP（打对方镜像）"`

### 改装配

- `Scripts/PvpDuel/Core/SplitMode/Link/SplitLink.cs:183-188`

```csharp
// 旧
PresencePublisher.Pump(delta, transport);
SplitMeeting.Pump(delta, transport);
SplitMerge.Pump(delta, transport);
RunRebuilder.Tick(delta);
MapPeerMarker.Pump();

// 新
PresencePublisher.Pump(delta, transport);
SplitMirrorTrigger.Pump();
MapPeerMarker.Pump();
```

---

## 七、已知坑（踩过，别再踩）

| 坑 | 表现 | 处理 |
|---|---|---|
| `RoomType.Combat` | 编译报不存在 | 枚举里没有 Combat，用 `RoomType.Monster` |
| 少 `ToMutable()` | 运行时 `CanonicalModelException` | 见第五节 |
| `TaskHelper` 找不到 | `CS0103` | `using MegaCrit.Sts2.Core.Helpers;` |
| `PlayerCmd` 找不到 | `CS0246` | `using MegaCrit.Sts2.Core.Commands;` |
| `RunManager.State` 不公开 | 编译错 | 用 `RunManager.Instance.IsInProgress` |
| `perl -pi` 批量改后 `edit` 失败 | `file changed since it was read` | 先重读再 `edit` |
| 游戏开着 build | `MSB3021` | 先关游戏 |
| `ilspycmd` 不带 `-r` | 反编译产物 126 个编译错（`LayoutPreset`、`AutowrapMode` 等裸短名） | 加 `-r "D:\Slay the Spire 2\data_sts2_windows_x86_64"` |

---

## 八、待定

1. **外观**：现在镜像还是软泥。要像对方得动 `MonsterModel.Title`（自定义 MonsterModel 或换遭遇），工作量未知
2. **血量绝对值**：现在只有 `HpPercent`。要精确得给 `SplitPresencePayload` 加 `CurrentHp` / `MaxHp` 两个字段（两端要同步升级）
3. **D4 的能力展示**：对方能力列表怎么同步、怎么挂到镜像上纯展示
4. **D3 的显示同步**：手牌数量 + 能量怎么传
5. **D9 断线检测**：怎么判"对方断了"（presence 超时？）
6. **D10 结算的作弊面**：接受，但要不要留个日志方便事后对账

---

## 九、部署与验证

### 三处部署位置

```
① D:\Slay the Spire 2\mods\YouJinSpire
② D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\YouJinSpire
③ D:\SteamLibrary\steamapps\workshop\content\2868840\3807296531   ← 游戏实际加载这份
```

`youjin.csproj` 的 AfterBuild 只自动同步 ①②，**③ 必须手动拷**。

### 验证日志（`%APPDATA%\SlayTheSpire2\logs\godot.log`）

| 日志 | 意思 |
|---|---|
| `[SplitMode] 相遇 → 进镜像战斗` | 触发命中 |
| `[SplitMode] 镜像战斗：开局完成｜镜像 XXX nn/100` | 成功 |
| `[SplitMode] 镜像战斗失败：…` | 报错全文 |
| `合流：…` | 老路还在跑（装配没生效） |

### 当前版本

- dll `432640 B` / sha `e7115c0653a` / 2026-11-03 09:16
- 三处已同步
