# sts2-mpconfigsync - DEVELOP.md

主机联机开局时自动下发 mod 配置,消除"两端配置文件不同导致的联机分歧".

状态: v0.1.0 已实现并发布 (2026-09-10; workshop fileid 3799210379).
实机双端验证未执行 - 见 4 节与 DEVLOG Session 1 的"未验证"清单.

## 1. 问题

- 引擎 MP 校验和机制 (ChecksumTracker) 对每步动作比对两端全状态. 任何读配置的 mod
  逻辑,若两端配置值不同,计算出的状态就不同 -> StateDivergence -> 断线.
- 我们已遇到的实际分歧: mod 来源不同 (已解决, 全员工坊), 丢包 (引擎问题). 配置不同
  是下一个高危源: Spire1 的 EnableSts1Content/PureSts1Pools, Relics 的词条档位等
  都直接改变生成内容.
- BaseLib 配置无任何联机同步: 配置文件是本地 JSON (mod_configs/<RootNamespace>.cfg),
  两端各读各的.

## 2. 结论性事实 (全部来自反编译, 证据见 research)

### 引擎侧 (sts2.dll, v0.111)

- `MessageTypes.Initialize` 扫描 mod 程序集内的 `INetMessage` 子类型 -> mod 可以
  定义自定义网络消息 (MessageTypes.cs).
- `INetGameService` (`RunManager.Instance.NetService`): `SendMessage<T>(T)` 广播 /
  `SendMessage<T>(T, ulong)` host 定向; `RegisterMessageHandler<T>`; `Type`
  (Singleplayer/Host/Client/Replay) (INetGameService.cs).
- `RunManager.SetUpNewMultiplayer(state, StartRunLobby lobby, ..)`: 大厅的同一个
  `NetService` 实例带进 run; `InitializeShared(netService, ..)` 是 run 会话起点
  (RunManager.cs).
- Lobby 阶段 (StartNewMultiplayerRun 之前) 也有消息流: RunLobby 用同一 NetService
  注册自己的处理器 (RunLobby.cs:70-76).
- AA 的先例: 用自定义 ModifierModel (ChaosPoolSnapshotModifier) 当配置载体, 大厅
  修饰符列表原生同步 (LobbyModifiersChangedMessage), 两端在
  NGame.StartNewMultiplayerRun prefix 读取 (AAW 反编译).

### BaseLib 侧 (v3.4.5, .tmp/baselib-decomp/)

- 配置 = `SimpleModConfig` 静态属性 + `ModConfigRegistry.Register(modId, cfg)`
  (ModConfig.cs / ModConfigRegistry.cs).
- `ModConfigRegistry.GetAll()` 公开枚举所有 mod 的配置; `ConfigProperties` 是
  protected, 但静态属性 + 无 [ConfigIgnore] 的过滤规则可以反射复刻.
- 值序列化天然是字符串: `TypeDescriptor` invariant 转换器 (与 .cfg 落盘同一语义).
- 写入 = `property.SetValue(null, v)` + `config.Changed()` (刷 UI) + `config.Save()`
  (原子落盘) - 设置界面就这么干, 我们照抄.
- **MP 桥已存在**: `BaseLib.Abstracts.ICustomMessage` + `CustomMessageWrapper`.
  mod 实现 `ICustomMessage` (Serialize/Deserialize/HandleMessage), BaseLib 在
  `RunManager.InitializeShared` postfix 自动注册, FullName 哈希做确定性消息 id,
  两端只要都装了这个 mod 就能互通 (CustomMessageWrapper.cs, RunManagerPatches.cs).
- `ICustomMessage` 默认 `ShouldBuffer=true` (缓冲到处理器就绪), `Reliable` 传输.

## 3. 设计

### 载体: BaseLib ICustomMessage (方案 A)

否决的方案 B (ModifierModel 载体, AA 路线): 修饰符会在自定义难度 UI 里显示, 侵入
体验; 且每次 host 改修饰符要重新注入; SavedProperties 只能带 int/bool/string 等标
量, 装整个配置快照只能塞 JSON 字符串, 脆. 方案 A 零 UI 污染, 全保真, BaseLib 自己
维护传输层.

### 消息: ConfigSyncMessage : ICustomMessage

```csharp
// 载荷: 每条 = modId + 属性名 + invariant 字符串值 (与 .cfg 落盘格式同语义)
// PacketWriter 序列化: count:int, 然后每条 3 个 string.
public bool ShouldBroadcast => false;   // host 定向发送, 不经 host 中继广播
public bool ShouldBuffer => true;       // 客户端处理器就绪前缓冲 (默认)
public NetTransferMode Mode => NetTransferMode.Reliable;
```

### 同步时机: host 在 RunManager.InitializeShared postfix 发送

- Harmony postfix on `RunManager.InitializeShared`, 在 BaseLib 的同名 postfix
  (Harmony 优先级默认 400 顺序不定) 之后或之前都行 - 我们不依赖 wrapper 注册
  完成的顺序? 依赖: 消息要经过 CustomMessageWrapper 收发, host 端发送只需要
  `RunManager.Instance.NetService` 就绪 + CustomMessageWrapper.Initialize()
  (启动时已跑); host SEND 不需要 handler 注册. 但客户端 HANDLE 需要 BaseLib 的
  wrapper handler 已注册 (InitializeShared postfix 内).
- 所以: host 发送可以就在我们自己的 InitializeShared postfix (此时 NetService
  已赋值). 客户端处理时 BaseLib postfix 可能尚未跑 (顺序不定) -> ShouldBuffer
  兜底? 不对 - buffer 的是 NetMessageBus 层, handler 未注册时
  SendMessageToAllHandlers 直接 Log.Error 丢弃 ("no message handlers
  registered"). 危险!
- **解决**: 我们的 postfix 里直接注册自己的 `RegisterMessageHandler<
  CustomMessageWrapper>` 不需要 - BaseLib 的 postfix 一定也在同一次
  InitializeShared 调用内执行 (所有 postfix 都跑完后才继续). NetMessageBus 的
  handler 字典在全部 postfix 跑完后才收到消息? 不 - 消息到达是异步的 (网络线程
  -> Update 泵). InitializeShared 同步执行完毕前不会有 Update. 因此只要 host 的
  SEND 在 InitializeShared 内任意位置, 客户端的 BaseLib 注册在任何 postfix 内,
  两端 InitializeShared 都返回后才第一次 Update 泵消息 -> 无竞态. (引擎 Update
  泵是主循环驱动, run 初始化在主循环内完成.)
- 发送目标: `SendMessage(msg)` (广播给全部 client). 客户端不发任何东西
  (server-authoritative).
- Singleplayer/Replay: Type 检查, 不发送不处理.

### 应用 (客户端 HandleMessage)

对每条 (modId, prop, value):
1. `ModConfigRegistry.Get(modId)` - null (本端没这个 mod) -> 跳过 (mod 集合差异已
   由引擎 mod 校验处理).
2. 反射取该 config 类型的静态属性 (静态+可读写+无 ConfigIgnore, 复刻
   CheckConfigProperties 过滤), 找 prop -> 没找到 (版本不同, 属性已改名) -> 跳过
   并记日志.
3. `TypeDescriptor.GetConverter(type).ConvertFromInvariantString(value)` -> 异常
   跳过 (类型不匹配保护).
4. `property.SetValue(null, obj)`.
5. 全部应用后: 每个受影响 config 调 `Changed()` + `ConfigReloaded()` (刷 UI 行).
   **不调 `Save()`** - 覆盖只存在于内存, 运行结束时由 RunManager.CleanUp postfix
   把用户自己的值原样写回 (会话级语义, 用户配置文件永不被改写). 本条为
   commit 772958af 的最终设计; 早期草稿写的"落盘"已废弃.

### 范围

v1 同步: 所有注册配置的全部可同步属性. 潜在的本地偏好 (如纯表现开关) 后续用
`[ConfigIgnore]` 或本 mod 自己的排除清单处理 - 先保正确性 (宁可过度同步).

## 4. 验证计划

- 构建 + 部署 (dotnet build -> mods/).
- 单机冒烟: 游戏启动, mod 加载日志无错, 配置界面出现 MpConfigSync 条目.
- 联机冒烟: 两端 (真实 Steam 联机) 开一局, host 改一个配置值 (如 Relics 档位),
  client 端日志应出现 `Config sync applied: N entries` 且不再报 StateDivergence.
  观察点是**会话内行为**, 不是配置文件内容: 本 mod 不写盘 (见 3.5 节),
  client 的 cfg 文件保持不变是预期结果. (需要用户实机配合; 截至 2026-09-12 未执行 -
  2026-09-10 那次事故只有一端装了本 mod, 接收路径从未在真实对端跑过.)

## 5. 风险

- Harmony postfix 顺序: 若未来引擎把 Update 泵挪进 InitializeShared, host 发送可
  能早于客户端 BaseLib 注册. 兜底: ShouldBuffer=true (引擎 NetMessageBus 缓冲).
- 未知 mod 的怪异配置类型 (SimpleModConfig 只支持 bool/int/float/double/string/
  enum/Color, 都有 invariant 转换器) - 转换失败跳过, 不崩.
- 配置应用时机晚于 mod 初始化 (initializer 启动时就读配置的逻辑拿到的是本地值):
  我们的 mod 都在 run 生成时才读配置 (Spire1 gate / Relics 生成都在 run 内),
  InitializeShared 先于任何 run 内容生成 -> 安全. 已知例外: 无.

## 6. 配置盘点结论 (research-mod-configs.md 摘要)

四 mod 共 16 个 cfg 键 + Spire1 character.txt(非 cfg, 仅本地选单可见性, 不同步).
关键事实:

- **Tier1 确定性键 8 个**: Relics EnableChaosRelics/ChaosRelicMultiplier, Perfect
  EnablePerfect, Spire1 PureSts1Pools, ChaosBridge EnableForAllModdedCharacters/
  DefaultPoolMode/PerCharacterOverrides/DeterministicPoolOrder.
- **Spire1 四个 EnableSts1* 是死开关** (零消费者), 保险同步.
- **启动期读取的键** (PureSts1Pools, DeterministicPoolOrder, IgnoreMpModDifferences
  补丁挂载分支): 内存 static 写入对它们无效 (要下次启动), 同步须落盘 cfg.
- 池构成类键 mid-run 改内存 = 主动制造分歧; 但我们在 InitializeShared (run 生成前)
  应用, 时序安全.
- scout 建议: host 全量下发所有键 (含 Tier2/3), 一致性最大化, 零分支逻辑.

## 7. 实现修订 (结合盘点)

- 同步范围: 全量 (所有 ModConfigRegistry.GetAll() 配置的全部可同步属性). 简单,
  正确, 覆盖未来新增键.
- 应用 = SetValue(null,v) + Changed()/ConfigReloaded() (刷 UI). 不落盘; 运行结束
  时 CleanUp postfix 恢复用户原值.
- 时序: InitializeShared 先于 GenerateRooms/InitializeNewRun 的任何内容生成 ->
  池构成键在本局就已对齐.
- **已知缺口**: 启动期读取的键 (Spire1 PureSts1Pools / DeterministicPoolOrder,
  IgnoreMpModDifferences 的挂载分支) 在收到同步消息时已被消费, 内存 SetValue 对
  它们无效; 而会话级设计又移除了早期"落盘使下局对齐"的兜底, 因此这些键在联机中
  永不生效. 需要在两案之间明确取舍: (a) 接受并在文档声明, 或 (b) 为这批键开一个
  显式落盘白名单. 未决.
- 客户端回发确认 (v1 不做): 失配检测靠引擎现有 ChecksumTracker - 若配置仍不同
  导致状态差, 引擎自己会报 StateDivergence, 我们不需要重复造.