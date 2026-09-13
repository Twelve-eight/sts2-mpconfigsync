# DEVLOG - sts2-mpconfigsync

## Session 1 (2026-09-10): 立项与 v0.1.0 实现

任务: 用户要求"主机开始游戏时自动下发配置,防止配置文件不同造成的分歧".

### 研究结论 (全部反编译取证)

- BaseLib 配置栈: SimpleModConfig 静态属性 + ModConfigRegistry.Register;
  落盘 mod_configs/<RootNamespace>.cfg (JSON 字典 invariant 字符串);
  ModConfigRegistry.GetAll() 公开枚举; 写入 = SetValue + Changed() + Save()
  (设置 UI 同款调用链). 报告: .tmp/research-baselib-config.md.
- 引擎 MP: mod 可定义 INetMessage 子类型 (MessageTypes.Initialize 扫描 mod 程序集);
  INetGameService.SendMessage 广播/定向; NetMessageBus 只在 Update 泵投递 =>
  InitializeShared 内发送与处理器注册无竞态. 报告: DEVELOP.md Sec 2.
- BaseLib 已有 MP 桥: ICustomMessage + CustomMessageWrapper (跨 mod 自动发现,
  FullName 哈希 id, RunManager.InitializeShared postfix 注册/注销).
  CustomLinkedRewardChoiceMessage 是现成参考实现.
- AA 的另一条路 (ModifierModel 载体, 大厅修饰符原生同步) 被否决: 污染自定义难度 UI.
- 四 mod 配置盘点: 16 个 cfg 键, Tier1 确定性键 8 个 (Relics EnableChaosRelics/
  Multiplier, Perfect EnablePerfect, Spire1 PureSts1Pools, ChaosBridge 四键);
  Spire1 四个 EnableSts1* 死开关; character.txt 非 cfg 不同步 (仅本地选单).
  报告: .tmp/research-mod-configs.md (14KB).

### v0.1.0 实现

- mod/MpConfigSyncCode/: MainFile (initializer+PatchAll), ConfigSyncMessage
  (ICustomMessage, 扁平 (modId, prop, invariant value) x N, ShouldBroadcast=false,
  Reliable), ConfigSyncApplier (receiver: 逐条 resolve->convert->SetValue, 失败跳过;
  收尾 Changed()+ConfigReloaded()+Save()), ConfigPropertyScanner (复刻
  CheckConfigProperties 过滤), RunManagerInitializeSharedPatch (host-only:
  NetGameType.Host + Enabled 双门, 快照全量下发), MpConfigSyncConfig (Enabled 开关,
  双端 opt-in 语义).
- csproj 按 Perfect 模板 (Publicize sts2, BaseLib 3.4.5 NuGet, PckPacker, 部署
  targets); 部署 mods/MpConfigSync/ 完成.

### 验证 (静态, 实机待用户联机)

- dotnet build: 0 警告 0 错误; dll+pck+json 部署到 mods/; pck 内含 zhs+eng
  settings_ui (字节级确认 MPCONFIGSYNC- 键).
- ilspycmd 确认 ConfigSyncMessage : ICustomMessage 在产物 dll 中; FullName
  MpConfigSync.MpConfigSyncCode.ConfigSyncMessage 进 wrapper 哈希表.
- 竞态链人工复核: NetService 在 InitializeShared 首行赋值; NetMessageBus 投递
  仅在 Update 泵 (调用栈返回后); BaseLib wrapper 注册同方法 postfix; 客户端
  readyForBroadcasting 在 lobby 期置位 (RunLobby:118) 早于 run 启动.
- 未验证 (需实机): 真实双端联机下发/落盘 (用户下次联机时观察 mod_configs/
  <mod>.cfg 被改写 + 日志 "Config sync applied: N entries").

### 已知限制

- 启动期读取的键 (PureSts1Pools / DeterministicPoolOrder) 本局不生效 (static
  已被读取), 落盘后下局生效 - DEVELOP.md Sec 7 记录.
- 双端都要装 MpConfigSync (ICustomMessage 类型须两端可反序列化); 单端装则无
  效但无害 (无 handler 只是 host 发出的消息被静默丢).
- 不同步: BaseLib 自身 BaseLibConfig (音量/日志), Spire1 character.txt.
  v1 策略 = 全量同步其余所有注册配置 (含死开关), 简单正确.
## Session 2 - 2026-09-12 (文档对齐 + 侦察)

### 文档漂移修正
DEVELOP.md 有三处描述的是已被 commit 772958af 取代的设计, 已改:
1. 第 5 行状态行: "设计完成, 实现中" -> v0.1.0 已实现并发布 (fileid 3799210379).
2. 3.5 节应用步骤: 删掉 "Save() (落盘, 原子)", 改为明确声明不落盘 + CleanUp 恢复.
3. 第 4 节联机冒烟的观察点: 原文写 "client 端 cfg 落盘值应变成 host 值", 在会话级
   设计下不可能发生; 改为观察日志的 "Config sync applied: N entries" 与不再出现
   StateDivergence.
4. 第 7 节修订: 新增"已知缺口"条目 - 启动期读取的键 (PureSts1Pools /
   DeterministicPoolOrder / IgnoreMpModDifferences 挂载分支) 在收到消息时已被消费,
   内存 SetValue 无效, 而会话级设计移除了落盘兜底, 所以这些键在联机中永不生效.
   需要明确取舍 (接受并声明 / 开落盘白名单), 未决.

### 未做的验证 (保持未验证状态)
真实双端联机的接收路径仍未执行过. 2026-09-10 那次事故只有一端装了本 mod, 对端把
消息静默丢弃, 因此 apply 路径从未在真实对端运行. 本次没有第二个客户端, 无法补齐.

## Session 3 - 2026-09-12 (第 8 步: 三缺陷修复 + 两项确认)

来源: HANDOFF-2026-09-12.md §3.3 (三项已确认缺陷 + 两项"另需确认").
范围由用户裁定: **只做 MpConfigSync 三缺陷**, 四个跨仓库审查 (Heartshake /
MpConfigSync / Perfect / ChaosBridge) 不做.

### 缺陷 #1 - 注释与代码矛盾 (MainFile.cs)

原注释声称"逐类型 try/catch, 绝不用 PatchAll", 代码实际是
`harmony.PatchAll(assembly)` 外包**一个** try/catch —— 任何一个补丁类抛异常,
整批补丁全部不生效, 日志只有一行.

改为 `ApplyPatches()`: 遍历 `assembly.GetTypes()`, 用 `HasHarmonyPatch(Type)`
(= 带 `[HarmonyPatch]` 且非抽象非泛型) 预筛, 每个类型单独
`harmony.CreateClassProcessor(type).Patch()`, 各自 try/catch.
日志: 每类 `patched N method(s) via {FullName}`; 失败类
`Harmony patch class {FullName} failed (continuing with the remaining classes): {e}`;
末尾汇总 `Harmony: N method(s) patched across M type(s), K patch class(es) failed`.
新增 `using System.Collections.Generic;`.

### 缺陷 #2 - ConfigPropertyScanner.Scan 与它声称复刻的方法语义不一致

原实现 `GetProperties(Public | Static | FlattenHierarchy)`, 注释说复刻 BaseLib 的
`ModConfig.CheckConfigProperties`. 差别就在 `FlattenHierarchy`: 它会把**继承来的**
public static 属性也返回, 而 BaseLib 的 `GetProperties()` (默认 DeclaredOnly) 不会
—— 于是扫描器会把 BaseLib 根本不持久化的属性也纳入快照/下发/恢复, 客户端在
SetValue 一个"本不该同步"的属性.

改为逐条对齐 `BaseLib/Config/ModConfig.cs:143-153`:

    foreach (PropertyInfo property in configType.GetProperties())
    {
        if (property.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
        if (!property.CanRead || !property.CanWrite) continue;
        if (property.GetMethod?.IsStatic != true) continue;
        result.Add(property);
    }

注释里写明对齐依据与"为什么 FlattenHierarchy 是错的".

### 缺陷 #3 - ShouldBuffer 用 ICustomMessage 默认值 (刻意保留 + 文档化)

`ConfigSyncMessage` 没有覆写 `ShouldBuffer`, 保持接口默认 `true`.
**判定: 这是正确行为, 不是遗漏** —— 只补文档, 不改代码. 证据链 (引擎 v0.111):

- 开缓冲: `StartRunLobby.cs:498` / `LoadRunLobby.cs:320` `SetBufferMessages(true)`,
  在 run 启动流程早期, 早于 `InitializeShared`.
- 发送: 本 mod 在 `RunManager.InitializeShared` postfix 发送快照.
- 释放: `RunManager.Launch()` (`RunManager.cs:711-717`) `SetBufferMessages(false)`
  -> `NetMessageBus.SetBufferMessages` (`NetMessageBus.cs:107-125`) 逐条
  `SendMessageToAllHandlers`. `Launch()` 由 `NGame.LoadRun` (`NGame.cs:1168`) /
  `NGame.StartRun` (`NGame.cs:1186`) 调用, 在 `SetUp*` (即 InitializeShared) 之后.

所以接收端是在 `Launch()` 时刻统一 apply, 不会插进 `InitializeShared` 正在构建
同步器的过程中. 覆写成 `false` 反而会在初始化中途投递, 严格更差.
`<para>` 块已写入 `ConfigSyncMessage` 的 XML 注释.

### 另需确认 #1 - CleanUp 恢复路径是否覆盖全部结束方式  [已验证: 覆盖]

读 `RunManager.CleanUp` (`RunManager.cs:1569-1616`) 与全部调用点, 逐条:

| 结束方式 | 路径 | 到 CleanUp |
|---|---|---|
| 胜利 / 死亡 | GameOverScreen -> `NGame.ReturnToMainMenuAfterRun` -> `ReturnToMainMenu` -> `CleanUp()` (`NGame.cs:1075-1081`) | 是 |
| 胜利且解锁新纪元 | GameOverScreen -> `NGame.GoToTimelineAfterRun` -> `GoToTimeline` -> `CleanUp()` (`NGame.cs:1067-1073`) | 是 |
| 局内放弃 (单机) | `RunManager.Abandon()` -> `AbandonInternal()` -> `GuaranteeKillAllPlayers()` -> 死亡 -> 上一条 | 是 (经死亡) |
| 局内放弃 (联机) | `RunLobby.AbandonRun()` -> 广播 `RunAbandonedMessage` -> 两端各自 `IRunLobbyListener.RunAbandoned()` -> `AbandonInternal()` -> 死亡 | 是 (经死亡) |
| 暂停菜单"保存并退出" | `NPauseMenu.CloseToMenu()` -> `NGame.ReturnToMainMenu()` -> `CleanUp()` | 是 |
| 暂停菜单"断开" (已断开时) | `NGame.ReturnToMainMenuAfterRun()` -> `CleanUp()` | 是 |
| 本端掉线 (主机离开等) | `RunManager.LocalPlayerDisconnected(info)` -> 若非 QuitGameOver / 非 IsAbandoned / 非 GameOver -> `ReturnToMainMenuWithError` -> `NGame.ReturnToMainMenuAfterRun()` -> `CleanUp()` | 是 (三个例外恰好都是已有别的路径通向 CleanUp 的情形) |
| 关窗口 / 退出进程 | `NRun._Notification(1006 = NOTIFICATION_PREDELETE)` -> `CleanUp(graceful:false)` (`NRun.cs:204-210`) | 是 |
| Steam 覆盖层加入好友局 (局内) | `SteamJoinCallbackHandler.cs:84` -> `NGame.ReturnToMainMenu()` -> `CleanUp()` | 是 |
| 主菜单继续存档失败 | `NMainMenu.cs:717` -> `CleanUp()` | 是 |
| 主菜单放弃存档局 (无活动会话) | `NMainMenu.AbandonRun()` / `NMultiplayerSubmenu.TryAbandonMultiplayerRun()` | 不经过 —— 但此时 `State == null`, `CleanUp` 本身首行就 return; 无会话即无内存覆盖 |

关键补充: 覆盖只可能在 `State != null` 期间存在. 覆盖由接收端 apply 产生, 而 apply
只发生在 `Launch()` 释放缓冲之后, `Launch()` 必然晚于 `InitializeShared`
(`RunManager.cs:466-469` 里 `State == null` 会直接抛). 所以不存在"覆盖还活着但
CleanUp 因 `State == null` 提前返回"的窗口.
**结论: 恢复路径覆盖全部结束方式, `RunManagerCleanUpPatch.cs` 注释的声明成立.**

### 另需确认 #2 - 与 BaseLib 后置补丁的顺序无关性  [已验证: 无关]

同一方法 `RunManager.InitializeShared` 上有两个 postfix: 本 mod 的 (发送快照) 与
BaseLib `RunManagerPatches.InitializeCustomMessageHandlers`
(`CustomMessagePatches.cs:12-18`, 注册 `CustomMessageWrapper` 处理器).
两者顺序由 Harmony 补丁时序决定, 不确定 —— 但无关:

1. 发送端只需要三件东西, 全部在**开机期**就绪, 与补丁顺序无关:
   - `CustomMessageWrapper.Initialize()` 填充 `CustomMessageToId`
     (`Abstracts/CustomMessage.cs:38-51`), 由 `PostModInitPatch.EarlyPostInit` 调用
     (`Patches/PostModInitPatch.cs:32-65`, 挂在 `LocManager.Initialize` 前缀);
   - `MessageTypes.Initialize()` 建类型缓存, 且 BaseLib 的 `AdjustCustomMessageKeys`
     后置写入 wrapper id (`CustomMessagePatches.cs:29-86`), 由
     `OneTimeInitialization.ExecuteEssential` 调用 (`OneTimeInitialization.cs:84`);
   - `NetService` 在 `InitializeShared` **第 2 条语句**赋值 (`RunManager.cs:470`),
     任何 postfix 都晚于它.
2. 投递不发生在 `InitializeShared` 里. `NetMessageBus` 只在 `Update()` 泵时投递
   (接收端 buffering=true 时先入 `_bufferedMessages`), 主机端也不发给自己.
   真正投递要等 `NRun._Process` -> `NetService.Update()` (`NRun.cs:199-202`),
   而 `NRun` 在 `NGame.cs:1187` 由 `Launch()` **之后**创建. 所以"BaseLib 注册
   handler"只要早于投递即可 —— 它在 `InitializeShared` 内, 必然早于投递.
3. 顺带核实主机广播门: `NetHostGameService.SendMessage<T>(T)` 只发给
   `readyForBroadcasting == true` 的 peer (`NetHostGameService.cs:114-130`).
   该标志由 `StartRunLobby.cs:272` / `LoadRunLobby.cs:204` / `RunLobby.cs:118`
   在大厅握手完成时置位, 都早于 run 启动. 所以 `InitializeShared` 时全部 peer 已就绪.

**结论: 顺序论证成立, 且由源码而非推理确认.**

### 构建

`dotnet-env.py "G:/omp works/sts2-mpconfigsync/mod" build MpConfigSync.csproj -c Debug`
-> `MpConfigSync.dll` + `PCK packed`, `0 个警告 / 0 个错误`.

### 本次新增的已知限制 (记录, 不在本次范围)

- **重连 (rejoin) 的对端拿不到快照.** 主机侧重连走
  `RunLobby.HandleClientRejoinRequestMessage` -> `GetRejoinMessage()`
  (`RunManager.cs:1745-1752`), 不重跑 `InitializeShared`, 所以本 mod 的 postfix
  不触发; 重连客户端自己走 `SetUpSavedMultiplayer` -> `InitializeShared`
  (`RunManager.cs:383-392`), postfix 虽执行但 `net.Type != Host` 早退, 不会误发.
  结果: 重连端本局用本地配置. 修法可选 (重连响应里捎带快照, 或主机监听
  `PlayerRejoined` 事件后单发), 需先与"会话级 + 不落盘"的设计约束对齐, 未决.
- 缺陷 #3 已文档化, 但"双端联机真实 apply 路径"仍未实机验证 (与 Session 2 同).

---

## 2026-09-12 (夜) astra-advice MCS-1/2/3/4/5 修复 (主会话单线)

### 架构改动

- **推送时点 (MCS-1)**: 新增 `Patches/LobbySnapshotPushPatches.cs` —— 主机在
  `StartRunLobby.BeginRunForAllPlayers` (新局) / `LoadRunLobby.TryBeginRunForAllPlayers`
  (读档建房) / `RunLobby.HandleClientRejoinRequestMessage` (重连, MCS-4) 三个
  **prefix** 推送快照。屏障依据: 引擎对这些方法体里的 begin-run/rejoin-response
  消息与本 mod 的快照走**同一条可靠有序通道**, prefix 发送先于 begin 消息到达,
  客户端在自身 SetUpNewMultiplayer → InitializeShared → Populate 之前应用配置。
  `InitializeShared` postfix 保留为兜底重申 (对漏收 lobby 包的客户端在 Launch 前
  收敛; 单靠它不构成 MCS-1, 注释如实改写)。`ConfigSyncMessage.ShouldBuffer=false`
  (原注释的"缓冲到 Launch 更安全"混淆了"不与 synchronizer 竞争"与"早于首个消费者")。
- **接收端鉴权 (MCS-2)**: `MpNetSession.AuthorizeSnapshot` —— 必须有活跃已连接
  service, 本机角色为 Client, 且 senderId == `NetClientGameService.HostNetId`
  (与引擎握手 `HandshakeMessageReceived` 同款传输层身份判据, 载荷可伪造的
  sender 字段不参与)。Host/单机/回放一律拒绝 (ShouldBroadcast=false 只是不转发,
  不阻止 host 收到 client 消息——旧代码把这当"收不到")。
- **大厅期 service 捕获**: `Patches/LobbySnapshotBarrierPatches.cs` 对三个 lobby
  构造器 postfix 记录 `INetGameService` (RunManager.NetService 在 InitializeRunLobby
  才赋值, 大厅期为 null; lobby 与 run 共享同一 service 对象)。
- **文件写保护 (MCS-3)**: Apply 路径不再触发 `Changed()` (那是"用户编辑"信号,
  会驱动 Qurious/BaseLib 的防抖/关页/退出保存); 只发 `ConfigReloaded()` 刷新 UI。
  纵深防御: 首次应用前把触及 mod 的 `mod_configs/*.cfg` 快照字节 + 设只读,
  任何第三方订阅者中途落盘都会失败; CleanUp 时解除只读并**逐字节还原**。
  中途崩溃也不再污染用户文件。
- **事务化 + 边界 (MCS-5)**: Deserialize 拒绝条目数 ∉[0,512] 与超长字符串
  (不再按攻击者给的 count 预分配); Apply 先全量 resolve+convert, 任一**已存在
  属性**转换失败 → 整包拒绝 (不留半应用状态); 未知 mod/属性 = 版本偏差, 容忍并
  记录 (本地无该 mod 即无消费者); setter 抛错逐条记录不中断开局。

### 验证

- 隔离构建 0 警告 0 错误, 已部署实机。
- 引擎事实全部对 dllsrc 复核: 闸门顺序/BeginRunForAllPlayers(:441)/
  TryBeginRunForAllPlayers(:291)/RunLobby 重连 handler(:103 发送 rejoin response)/
  HostNetId(NetClientGameService:31)。
- **未验证边界 (需真实双端)**: ①通道有序性在实机 Steam 会话中的表现
  (设计上 prefix→begin 顺序成立, 未实测); ②设置页开着时第三方订阅者写只读
  文件的实际行为 (预期失败+日志); ③重连后首状态校验; ④CustomMessageWrapper
  在 lobby 阶段发送是否需要额外初始化 (兜底路径已覆盖失败情形)。
  按审查交付门, 未跑双端前不称 MP safe。

### 保留

- ConfigPropertyScanner / CleanUp 恢复路径 / InitializeShared 与 BaseLib 的
  注册顺序 (审查确认无问题) 未动。

---

## 附: 会话输入与工作顺序 (2026-09-12~13, 全量见 docs/session-log-2026-09-12-13.md)

与本仓库直接相关的用户输入序列:
1. (astra-advice 项 3/4, 用户原始需求「配置下发覆盖任何进入房间,只应用于本局」)
   → 大厅屏障+鉴权+文件冻结+事务 (4da0efb)。
2. GPT6-Astra 二轮 live log 证据: 「0 method(s) patched」与探针 8/8 矛盾 →
   **静态类 IsAbstract 过滤跳过全部补丁类** (所有 MCS 补丁从未挂载) — 修复 (506988c)。
3. 同复审: 大厅推送被 NetMessageBus 丢弃 (handler 未注册) → lobby 早注册 + InitializeShared
   兜底反注册 (引擎不去重) (506988c)。
教训要点: L5 静态类 IsAbstract / L9 消息总线丢弃与注册去重 / L3 "0 错误"可以是 bug 本身。

## 2026-09-14 astra 第三轮审查交接记录

### 审查证据截点
- 隔离构建工作区: `G:\\omp works\\.tmp\\astra-review-20260914-1789319234062`.
- 正式证据: `G:\\omp works\\astra-advice-evidence\\2026-09-14\\`.
- 当前构建 `MpConfigSync.dll` SHA256: `ba03e9582bf65b07351b9f14ede4fe5772cfdd0fec83d0db99883769147328b8`.
- 产品私有 `MainFile.ApplyPatches` 隔离调用结果: 23 types, 8 patch classes, 8 engine methods, 0 failed classes.
- `sync-probe-v2` 当前隔离输出: 19 个 PASS 行, 覆盖鉴权, 转换失败整包拒绝, count 限制, cfg 只读冻结和直接恢复. 未覆盖真实 CleanUp, setter/file 故障回滚, 中断和双端 transport.

### 当前仍未闭合
- lobby constructor 早注册源码存在且生产扫描器隔离安装成功, 但未运行真实 packet 派发, 新局/读档/加入/rejoin 首消费者时序和 BaseLib handler 反注册后的真实数量.
- setter 失败, 文件保护或恢复失败, 进程终止时的整体事务安全仍未证明.

### 交接恢复动作
- 本轮没有产品源码修改, 没有操作游戏, 没有部署或 push.
- 当前 `git status` 另外观察到 `mod/MpConfigSync/MpConfigSync.dll`, `.json`, `.pdb` 三个未跟踪生成物. 不清理, 不回滚, 不加入本轮文档提交; 归属需下一轮先确认.
- 当前 advice 修改未提交. 下一轮先复核本 DEVLOG 与 `astra-advice-evidence/2026-09-14/handoff-state.json`, 再决定是否把 advice/DEVLOG 分开提交.
详见 docs/session-log-2026-09-12-13.md 第二节。

---

## 2026-09-14 首次实机加载 + 破解版双开测试环境 (阶段 1-2 完成)

- **MCS 首次在真实游戏里加载**: I: 副本 (Goldberg, v0.111.0) 与其克隆副本 B, 日志均为
  `Harmony: 8 method(s) patched across 23 type(s), 0 patch class(es) failed` —— astra 第三轮
  "生产扫描器已修复但未实机加载" 的缺口就此闭合 (实机挂载证据)。部署的是本仓重建版
  (SHA256 879DF477…, 源码 = 当前 HEAD)。
- 测试环境: A = `I:\Slay the Spire 2\` (Goldberg), B = `G:\omp works\sts2-test-client-B\`
  (完整克隆, 独立 Goldberg 身份 TEST_B / local_steam_id 76561199520000001)。mod 集 = 51 个
  真实会话镜像, 两副本逐字节一致。完整报告: `docs/mcs-cracked-mp-test-2026-09-14.md`。
- 环境修复两处 (与 MCS 无关但记录): Mesugaki 换工坊 0.1.2; 测试副本 Perfect.json 补
  ActsFromThePast 依赖 (加载顺序敏感的可选依赖, 加载器拓扑排序后 Perfect 初始化成功)。
- 阶段 3 待执行: 双开大厅 → 配置下发/本局隔离/读档建房/rejoin 场景矩阵 (报告 §阶段 3)。

## 2026-09-14 阶段 3 完成: 三条推送路径 + 恢复矩阵真实双端验证
- Goldberg 双开 (A 主机 / B 客户端) 完整跑通: start-lobby begin、load-lobby begin、initialize-shared backstop 三条推送路径全部触发 (227 entries); 文件冻结拦截了客户端防抖保存 (BaseLib Access denied = 按设计); 断连场景下 13 个 cfg 逐一恢复到会话前字节; 0.5.7 同款 LastRunSeed 续档思路在 Qurious 侧真实读档局验证 (snapshot kept)。
- 事故记录: QuickLink 回退在 GSE 下失败 (socket 重建后好友 lobby 信息不刷新 → 客户端自动重连未发生 → 黑屏遮罩残留) —— 环境限制判定, 与 MCS 无关; 详见 docs/mcs-cracked-mp-test-2026-09-14.md。
- 进房根因: 破解包 GSE 的 steam_appid=2963800 与引擎硬编码 2868840 不符 → 好友过滤器丢弃一切好友; 改 2868840 后进房正常。account_steamid/listen_port 键位经 DLL 字面量邻域验证。
- 待办移交: 接收端鉴权 (MCS-2)、事务边界 (MCS-3 加固)、真实 Steam 环境复测 rejoin。
