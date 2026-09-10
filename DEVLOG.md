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