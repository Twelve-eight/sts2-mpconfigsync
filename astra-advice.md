# Astra advice - MpConfigSync

日期: 2026-09-12. 主会话单线. 审查目标不是 "能发一个包", 而是主机配置在所有确定性消费者之前生效, 只影响本会话, 并覆盖读档/重连.

本轮副本构建 0 警告/0 错误. 已执行精确接收源码 + 真实 BaseLib registry/Changed/Save 的隔离探针, 以及真实 ConfigSyncMessage 反序列化. 未运行双端 Steam 会话. 证据: `../astra-advice-evidence/2026-09-12/`.

## P1 MCS-1: 快照晚于第一批配置消费者

位置: `RunManagerInitializeSharedPatch.cs:28-57`, `ConfigSyncMessage.cs:22-33`.

SOURCE, 已对当前 sts2.dll 重新反编译:

- RunManager.SetUpNewMultiplayer:334-343: InitializeShared -> InitializeNewRun -> GenerateRooms.
- InitializeNewRun:522-530: Populate 共享/个人遗物袋, Modifier.OnRunCreated.
- NGame.StartRun:1183-1186: 预加载 -> FinalizeStartingRelics -> Launch.
- Launch:714: SetBufferMessages(false).
- ConfigSyncMessage 保持 ShouldBuffer=true, 所以接收应用不可能在 InitializeShared 内完成.
- Qurious 更早在 SetUpNewMultiplayer prefix Capture -> OnSeedCaptured -> ForSeed 生成.

现有 DEVELOP 的 "InitializeShared 先于生成, 因而配置已经对齐" 把发送时点等同于接收时点. Session 3 证明缓冲安全, 但没有证明效果及时, 结论方向错了.

建议:

1. 在大厅/开局请求链建立 effective snapshot 协议, 明确 session id/revision/hash.
2. 客户端验证并应用完快照, 主机获得完成确认, 才允许进入第一批使用配置的操作. 单靠消息先发/可靠传输/同帧 postfix 不足以形成屏障.
3. host 也冻结同一快照, 不在本局重新读不断变化的偏好.
4. 启动期已消费的 PureSts1Pools/DeterministicPoolOrder/补丁挂载开关不能当普通 runtime 属性同步. 应在开局前检查是否匹配/要求重启, 或为具体消费者提供可靠重建机制.
5. 如果支持的 API 不足以在正确时点应用, 显式阻止不匹配会话比带错误状态启动更安全.

禁止的捷径: 只改 ShouldBuffer=false; 只再发一次; 改成 Save 期待下一局修好; 清缓存后继续用已经生成的 bag; 等 checksum 报错才说同步成功.

验收: 主客机起始 EnableChaosRelics 相反, 预算相反, 启动期键相反; 检查第一个随机消费者之前有效值相同, bag/定义/房间初始状态相同. 先延迟配置包, 再确认双方不提前开局. 真实双端不做完, 不能称 MP safe.

## P1 MCS-2: 接收端没有主机/客户端/会话鉴权

位置: `ConfigSyncApplier.cs:35-44`; `ConfigSyncMessage.cs:70-72`.

Apply 只看本地 Enabled, 把任意 senderId 打成 "from host". 没有 net.Type==Client, 当前主机身份, session/revision 或开局阶段校验.

当前 BaseLib 3.4.5 与实机 3.4.7 的 CustomMessageWrapper 都直接调用 Message.HandleMessage(senderId). ShouldBroadcast=false 只是不转发, 不阻止 host 接收 client 发来的自定义消息. 引擎 NetHostGameService.OnPacketReceived 对已连接对端会投递 handler.

REPRO: 精确 Apply/Scanner/Message 源码, 仅日志入口替换. 没有 RunManager/Client 会话, sender=9999, 注册的设置从 7 改为 99.

建议: 接收端必须验证当前角色/连接/主机来源和对应会话. 注意引擎消息头允许 override senderId, 单纯比较可由载荷覆盖的 senderId 不一定是完整传输认证; 沿实际 transport 身份边界确认. host 拒绝一切 client config snapshot; 非会话/过期/重复 revision 不应用. 保留本地用户 opt-in, 主机不能通过同步 Enabled 强行重开被用户关闭的能力.

验收: host 收到伪配置不变; 非当前主机不变; singleplayer/replay/无局不变; 旧 session 包不变; 合法当前主机快照成功. 不要只测 "我们自己的客户端不会发送".

## P1 MCS-3: no Save 不等于 no file writes

位置: `ConfigSyncApplier.cs:62-73,112-115`.

Changed() 会触发 ConfigChanged. 真实 Qurious RelicsSettingsSubmenu 在 BuildOptions 订阅该事件, 调度 5 秒保存, OnSubmenuHidden/_ExitTree 还会直接 Save. BaseLib 设置页也有保存生命周期.

REPRO: 使用真实 BaseLib ModConfig. 在 ConfigChanged 接入保存订阅者后, Apply 把文件 7 写成 99; Restore 正常完成才写回 7. Apply 自己日志仍输出 "session-scoped, no file writes". 如果中间进程崩溃, 99 已经落盘. 这是间接副作用, 不是仅凭搜索 Save() 可证明的性质.

建议选择并落实一种明确契约:

- 最稳妥: 本地偏好与本局 effective config 分离, 游戏逻辑读 effective, 设置 UI 保存偏好.
- 若必须暂时覆写 static 属性: 需要完整控制这段时期所有保存入口及恢复, 包括 BaseLib/自建 UI/防抖/退出; 仅跳过 Changed 不足以保证用户稍后打开设置页不会保存主机值.
- ConfigReloaded 可以刷新 UI, 但必须区分纯显示刷新与表示用户编辑的 Changed. 不要假设所有订阅者无副作用.

验收: 真正创建过设置页, 开始联机, 收到快照, 等防抖, 打开/关闭页面, 中断进程, 检查文件仍为用户原值. 正常退出恢复一次. 不把 "最后文件一样" 误当 "过程中从未写".

## P1 MCS-4: 重连没有快照

已有记录, 当前仍未解决: 主机重连走 RunLobby.HandleClientRejoinRequestMessage -> GetRejoinMessage, 不再执行 InitializeShared. 客户端执行它但 net.Type!=Host 早退.

建议与 MCS-1 共用同一份被冻结的 session snapshot; 在 rejoin state 交付和客户端任何重建消费者之前完成同步. 只在 PlayerRejoined 事件末尾发包可能还是太晚, 需检查该事件与存档反序列化/内容生成的顺序. 不能把 "已注册事件" 当完成.

验收: 断线后修改本地 cfg, 再重连同局, 恢复的是主机本局有效配置而不是现在偏好. 重连后第一次状态校验一致.

## P2 MCS-5: 网络输入大小未设边界, 应用不是事务

位置: `ConfigSyncMessage.cs:56-67`, `ConfigSyncApplier.cs:49-60,131-173`.

REPRO: 仅 4 字节的 count=-1 触发 ArgumentOutOfRangeException; count=10000 先分配容量 10000 再因包截断失败. 不需要也不应为了证明风险发送超大包.

SOURCE: 每个字段逐个 SetValue, 转换失败只 skip, 可能留下半份有效配置后继续游戏. 转换前还先保存 restore snapshot. 未知 mod/属性与真正的类型错误没有区分是否可安全启动.

建议定义条目数/总字节/字符串上限, 先解析验证整份快照, 判明必需键/版本兼容, 然后原子提交或可回滚提交. 接受自定义 setter 可能有副作用的现实, 不把反射赋值当天然事务. 对影响确定性的失败不能 "部分成功就启动".

验收: 负数, 超限, 截断, 重复键, 未知 mod/属性, 非法值, setter 抛错; 不留半应用状态, 不接受攻击者选择的超额分配.

## 推荐最小状态机

这是建议的设计顺序, 不是要求创造大型框架:

`NoSession -> Preparing(snapshot revision) -> Validated -> Applied -> Running -> Restoring -> NoSession`.

- 所有接收绑定 session/revision.
- Preparing 不允许确定性消费者先运行.
- Running 不允许普通偏好编辑改写本局有效状态.
- Failed validation 不进入 Running.
- Restore 幂等, 不受 Enable 开关影响.
- Rejoin 恢复 Running 的同一快照, 不是一次新采样.

## 保留与不要浪费的工作

- ConfigPropertyScanner 对齐 GetProperties + static/read/write/ConfigIgnore 的筛选有价值. 本轮 fresh BaseLib 与实机版都保留相同核心筛选, 不用再改回 FlattenHierarchy.
- InitializeShared 与 BaseLib 同名 postfix 的注册顺序未发现新问题; 主循环缓冲解释仅支持 "不会边注册边处理", 不能支持 "已经在生成前处理".
- CleanUp 正常结束路径已有逐调用点研究, 可复用, 但它不能补救中途落盘/崩溃.
- 不新增第二套全局 checksum 来掩盖协议没完成; 快照确认是开始条件, 游戏 checksum 是运行后检测, 两者职责不同.

## 交付门

必须含不同初始配置的真实双端新局/读档/重连, 本地文件持久性, 未授权消息拒绝, 至少一个配置驱动的实际遗物池场景. 仅编译/单机加载/收到 N entries 日志都不够.

本轮源码已干净构建, 但没有为这些设计缺陷做产品修复. 建议先与 Qurious 本局状态模型统一, 再继续扩同步类型.
